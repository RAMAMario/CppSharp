﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using CppSharp.AST;
using CppSharp.AST.Extensions;
using Type = CppSharp.AST.Type;

namespace CppSharp.Passes
{
    public class GetterSetterToPropertyPass : TranslationUnitPass
    {
        static GetterSetterToPropertyPass()
        {
            LoadVerbs();
        }

        private static void LoadVerbs()
        {
            var assembly = Assembly.GetAssembly(typeof(GetterSetterToPropertyPass));
            using (var resourceStream = GetResourceStream(assembly))
            {
                using (var streamReader = new StreamReader(resourceStream))
                    while (!streamReader.EndOfStream)
                        verbs.Add(streamReader.ReadLine());
            }
        }

        private static Stream GetResourceStream(Assembly assembly)
        {
            var resources = assembly.GetManifestResourceNames();

            if (resources.Count() == 0)
                throw new Exception("Cannot find embedded verbs data resource.");

            // We are relying on this fact that there is only one resource embedded.
            // Before we loaded the resource by name but found out that naming was
            // different between different platforms and/or build systems.
            return assembly.GetManifestResourceStream(resources[0]);
        }

        public GetterSetterToPropertyPass()
        {
            VisitOptions.VisitClassBases = false;
            VisitOptions.VisitClassFields = false;
            VisitOptions.VisitClassProperties = false;
            VisitOptions.VisitClassMethods = false;
            VisitOptions.VisitNamespaceEnums = false;
            VisitOptions.VisitNamespaceTemplates = false;
            VisitOptions.VisitNamespaceTypedefs = false;
            VisitOptions.VisitNamespaceEvents = false;
            VisitOptions.VisitNamespaceVariables = false;
            VisitOptions.VisitFunctionParameters = false;
            VisitOptions.VisitTemplateArguments = false;
        }

        public override bool VisitClassDecl(Class @class)
        {
            if (!base.VisitClassDecl(@class))
                return false;

            ProcessProperties(@class, GenerateProperties(@class));
            return false;
        }

        protected virtual IEnumerable<Property> GenerateProperties(Class @class)
        {
            var newProperties = new List<Property>();
            foreach (Method method in @class.Methods.Where(
                m => !m.IsConstructor && !m.IsDestructor && !m.IsOperator && m.IsGenerated &&
                    m.SynthKind != FunctionSynthKind.DefaultValueOverload &&
                    m.SynthKind != FunctionSynthKind.ComplementOperator &&
                    !m.ExcludeFromPasses.Contains(typeof(GetterSetterToPropertyPass))))
            {
                if (IsGetter(method))
                {
                    string name = GetPropertyName(method.Name);
                    QualifiedType type = method.OriginalReturnType;
                    Property property = GetProperty(method, name, type);
                    if (!property.HasGetter)
                    {
                        property.GetMethod = method;
                        property.QualifiedType = method.OriginalReturnType;
                        newProperties.Add(property);
                    }
                    else
                        method.GenerationKind = GenerationKind.Generate;
                    continue;
                }
                if (IsSetter(method))
                {
                    string name = GetPropertyNameFromSetter(method.Name);
                    QualifiedType type = method.Parameters.First(p => p.Kind == ParameterKind.Regular).QualifiedType;
                    Property property = GetProperty(method, name, type, true);
                    property.SetMethod = method;
                    newProperties.Add(property);
                }
            }

            return CleanUp(@class, newProperties);
        }

        private IEnumerable<Property> CleanUp(Class @class, List<Property> newProperties)
        {
            if (!Options.UsePropertyDetectionHeuristics)
                return newProperties;

            for (int i = newProperties.Count - 1; i >= 0; i--)
            {
                Property property = newProperties[i];
                if (property.HasSetter)
                    continue;

                string firstWord = GetFirstWord(property.GetMethod.Name);
                if (firstWord.Length < property.GetMethod.Name.Length &&
                    Match(firstWord, new[] { "get", "is", "has" }))
                    continue;

                if (Match(firstWord, new[] { "to", "new" }) ||
                    verbs.Contains(firstWord))
                {
                    property.GetMethod.GenerationKind = GenerationKind.Generate;
                    @class.Properties.Remove(property);
                    newProperties.RemoveAt(i);
                }
            }

            return newProperties;
        }

        private static Property GetProperty(Method method, string name,
            QualifiedType type, bool isSetter = false)
        {
            Type underlyingType = GetUnderlyingType(type);
            Class @class = (Class) method.Namespace;
            Property property = @class.Properties.Find(
                p => p.Field == null &&
                    (p.Name == name ||
                     (isSetter && p.HasGetter &&
                      GetReadWritePropertyName(p.GetMethod, name) == name)) &&
                    ((p.HasGetter &&
                      GetUnderlyingType(p.GetMethod.OriginalReturnType).Equals(underlyingType)) ||
                     (p.HasSetter &&
                      GetUnderlyingType(p.SetMethod.Parameters[0].QualifiedType).Equals(underlyingType)))) ??
                new Property { Name = name, QualifiedType = type };

            if (property.Namespace == null)
            {
                property.Namespace = method.Namespace;
                property.Access = method.Access;
                @class.Properties.Add(property);
            }
            else
            {
                property.Access = (AccessSpecifier) Math.Max(
                    (int) (property.GetMethod ?? property.SetMethod).Access,
                    (int) method.Access);
            }

            property.Name = property.OriginalName = name;
            method.GenerationKind = GenerationKind.Internal;
            if (method.ExplicitInterfaceImpl != null)
                property.ExplicitInterfaceImpl = method.ExplicitInterfaceImpl;
            return property;
        }

        private static void ProcessProperties(Class @class, IEnumerable<Property> newProperties)
        {
            foreach (Property property in newProperties)
            {
                ProcessOverridden(@class, property);

                if (!property.HasGetter)
                {
                    if (property.HasSetter)
                        property.SetMethod.GenerationKind = GenerationKind.Generate;
                    @class.Properties.Remove(property);
                    continue;
                }
                if (!property.HasSetter &&
                    @class.GetOverloads(property.GetMethod).Any(
                        m => m != property.GetMethod && !m.Ignore))
                {
                    property.GetMethod.GenerationKind = GenerationKind.Generate;
                    @class.Properties.Remove(property);
                    continue;
                }

                RenameConflictingMethods(@class, property);
                CombineComments(property);
            }
        }

        private static void ProcessOverridden(Class @class, Property property)
        {
            if (!property.IsOverride)
                return;

            Property baseProperty = GetBaseProperty(@class, property);
            if (baseProperty == null)
            {
                if (property.HasSetter)
                {
                    property.SetMethod.GenerationKind = GenerationKind.Generate;
                    property.SetMethod = null;
                }
                else
                {
                    property.GetMethod.GenerationKind = GenerationKind.Generate;
                    property.GetMethod = null;
                }
            }
            else if (!property.HasGetter && baseProperty.HasSetter)
                property.GetMethod = baseProperty.GetMethod;
            else if (!property.HasSetter || !baseProperty.HasSetter)
            {
                if (property.HasSetter)
                    property.SetMethod.GenerationKind = GenerationKind.Generate;
                property.SetMethod = baseProperty.SetMethod;
            }
        }

        private static Property GetBaseProperty(Class @class, Property @override)
        {
            foreach (var @base in @class.Bases)
            {
                Class baseClass = @base.Class.OriginalClass ?? @base.Class;
                Property baseProperty = baseClass.Properties.Find(p =>
                    (@override.GetMethod?.IsOverride == true &&
                     @override.GetMethod.BaseMethod == p.GetMethod) ||
                    (@override.SetMethod?.IsOverride == true &&
                     @override.SetMethod.BaseMethod == p.SetMethod) ||
                    (@override.Field != null && @override.Field == p.Field));
                if (baseProperty != null)
                    return baseProperty;

                baseProperty = GetBaseProperty(@base.Class, @override);
                if (baseProperty != null)
                    return baseProperty;
            }
            return null;
        }

        private static void RenameConflictingMethods(Class @class, Property property)
        {
            foreach (var method in @class.Methods.Where(
                m => m.IsGenerated && m.Name == property.Name))
            {
                var oldName = method.Name;
                method.Name = $@"get{char.ToUpperInvariant(method.Name[0])}{
                    method.Name.Substring(1)}";
                Diagnostics.Debug("Method {0}::{1} renamed to {2}",
                    method.Namespace.Name, oldName, method.Name);
            }
            foreach (var @event in @class.Events.Where(
                e => e.Name == property.Name))
            {
                var oldName = @event.Name;
                @event.Name = $@"on{char.ToUpperInvariant(@event.Name[0])}{
                    @event.Name.Substring(1)}";
                Diagnostics.Debug("Event {0}::{1} renamed to {2}",
                    @event.Namespace.Name, oldName, @event.Name);
            }
        }

        private static string GetReadWritePropertyName(INamedDecl getter, string afterSet)
        {
            string name = GetPropertyName(getter.Name);
            if (name != afterSet && name.StartsWith("is", StringComparison.Ordinal) &&
                name != "is")
            {
                name = char.ToLowerInvariant(name[2]) + name.Substring(3);
            }
            return name;
        }

        private static Type GetUnderlyingType(QualifiedType type)
        {
            TagType tagType = type.Type as TagType;
            if (tagType != null)
                return type.Type;
            // TODO: we should normally check pointer types for const; 
            // however, there's some bug, probably in the parser, that returns IsConst = false for "const Type& arg"
            // so skip the check for the time being
            PointerType pointerType = type.Type as PointerType;
            return pointerType != null ? pointerType.Pointee : type.Type;
        }

        private static void CombineComments(Property property)
        {
            Method getter = property.GetMethod;
            if (getter.Comment == null)
                return;

            var comment = new RawComment
            {
                Kind = getter.Comment.Kind,
                BriefText = getter.Comment.BriefText,
                Text = getter.Comment.Text
            };
            if (getter.Comment.FullComment != null)
            {
                comment.FullComment = new FullComment();
                comment.FullComment.Blocks.AddRange(getter.Comment.FullComment.Blocks);
                Method setter = property.SetMethod;
                if (getter != setter && setter?.Comment != null)
                {
                    comment.BriefText += Environment.NewLine + setter.Comment.BriefText;
                    comment.Text += Environment.NewLine + setter.Comment.Text;
                    comment.FullComment.Blocks.AddRange(setter.Comment.FullComment.Blocks);
                }
            }
            property.Comment = comment;
        }

        private static string GetPropertyName(string name)
        {
            var firstWord = GetFirstWord(name);
            if (Match(firstWord, new[] { "get" }) && name != firstWord &&
                !char.IsNumber(name[3]))
            {
                if (char.IsLower(name[0]))
                {
                    if (name.Length == 4)
                    {
                        return char.ToLowerInvariant(
                            name[3]).ToString(CultureInfo.InvariantCulture);
                    }
                    return char.ToLowerInvariant(
                        name[3]).ToString(CultureInfo.InvariantCulture) +
                                    name.Substring(4);
                }
                return name.Substring(3);
            }
            return name;
        }

        private static string GetPropertyNameFromSetter(string name)
        {
            var nameBuilder = new StringBuilder(name);
            string firstWord = GetFirstWord(name);
            if (firstWord == "set" || firstWord == "set_")
                nameBuilder.Remove(0, firstWord.Length);
            if (nameBuilder.Length == 0)
                return nameBuilder.ToString();

            nameBuilder.TrimUnderscores();
            if (char.IsLower(name[0]) && !char.IsLower(nameBuilder[0]))
                nameBuilder[0] = char.ToLowerInvariant(nameBuilder[0]);
            return nameBuilder.ToString();
        }

        private bool IsGetter(Method method) =>
            !method.IsDestructor &&
            !method.OriginalReturnType.Type.IsPrimitiveType(PrimitiveType.Void) &&
            !method.Parameters.Any(p => p.Kind != ParameterKind.IndirectReturnType);

        private static bool IsSetter(Method method)
        {
            Type returnType = method.OriginalReturnType.Type.Desugar();
            return (returnType.IsPrimitiveType(PrimitiveType.Void) ||
                returnType.IsPrimitiveType(PrimitiveType.Bool)) &&
                method.Parameters.Count(p => p.Kind == ParameterKind.Regular) == 1;
        }

        private static bool Match(string prefix, IEnumerable<string> prefixes)
        {
            return prefixes.Any(p => prefix == p || prefix == p + '_');
        }

        private static string GetFirstWord(string name)
        {
            var firstWord = new List<char> { char.ToLowerInvariant(name[0]) };
            for (int i = 1; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsLower(c))
                {
                    firstWord.Add(c);
                    continue;
                }
                if (c == '_')
                {
                    firstWord.Add(c);
                    break;
                }
                if (char.IsUpper(c))
                    break;
            }
            return new string(firstWord.ToArray());
        }

        private static readonly HashSet<string> verbs = new HashSet<string>();
    }
}
