// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;

namespace bOps.Architecture.Tests;

/// <summary>
/// Describes the public surface of an assembly as text, one line per type, member and enum value, in a stable order. The frozen
/// 1.0 surface of <c>bOps.Abstractions</c> is kept as such a description (<c>Snapshots/abstractions-1.0.txt</c>), taken from the
/// V1.0 close-out build, and every line of it must still be true of the current assembly: nothing removed, retyped or renumbered
/// (ADR-0022). Adding is allowed. This replaces a manual reflection dump: the V1.2-B check found an enum renumbered that a
/// name-based comparison of the V1.1-H kind had missed, so enum values are part of the description.
/// </summary>
internal static class PublicSurface
{
    private const BindingFlags Members =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>The lines describing <paramref name="assembly"/>'s public surface. A line starts with the type it belongs to.</summary>
    internal static IReadOnlyList<string> Describe(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var lines = new List<string>();
        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var name = Name(type);
            lines.Add($"{name} | kind {Kind(type)}");
            if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(Enum))
            {
                lines.Add($"{name} | base {Name(baseType)}");
            }

            foreach (var iface in type.GetInterfaces().OrderBy(Name, StringComparer.Ordinal))
            {
                lines.Add($"{name} | implements {Name(iface)}");
            }

            foreach (var attribute in type.GetCustomAttributesData().Select(a => a.AttributeType.FullName).Where(n => n is not null).OrderBy(n => n, StringComparer.Ordinal))
            {
                if (attribute is "System.FlagsAttribute")
                {
                    lines.Add($"{name} | attribute {attribute}");
                }
            }

            foreach (var member in type.GetMembers(Members))
            {
                if (Describe(member) is { } described)
                {
                    lines.Add($"{name} | {described}");
                }
            }
        }

        return lines.Distinct(StringComparer.Ordinal).OrderBy(l => l, StringComparer.Ordinal).ToList();
    }

    private static string? Describe(MemberInfo member)
    {
        switch (member)
        {
            case FieldInfo field when field.DeclaringType!.IsEnum && field.IsStatic:
                return $"enum-value {field.Name} = {Convert.ToInt64(field.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture)}";
            case FieldInfo field when (field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly) && !field.Name.Contains('<', StringComparison.Ordinal):
                return $"field {Modifiers(field.IsStatic, field.IsLiteral || field.IsInitOnly)}{Name(field.FieldType)} {field.Name}";
            case ConstructorInfo ctor when ctor.IsPublic || ctor.IsFamily || ctor.IsFamilyOrAssembly:
                return $"ctor({string.Join(", ", ctor.GetParameters().Select(p => Name(p.ParameterType)))})";
            case MethodInfo method when IsExposed(method) && !method.IsSpecialName && !method.Name.Contains('<', StringComparison.Ordinal):
                return $"method {Modifiers(method.IsStatic, method.IsAbstract)}{Name(method.ReturnType)} {method.Name}{Generics(method)}({string.Join(", ", method.GetParameters().Select(p => Name(p.ParameterType)))})";
            case PropertyInfo property when (property.GetMethod is { } g && IsExposed(g)) || (property.SetMethod is { } s && IsExposed(s)):
                var getter = property.GetMethod is { } get && IsExposed(get) ? "get;" : string.Empty;
                var setter = property.SetMethod is { } set && IsExposed(set) ? "set;" : string.Empty;
                return $"property {Name(property.PropertyType)} {property.Name}({string.Join(", ", property.GetIndexParameters().Select(p => Name(p.ParameterType)))}) {{{getter}{setter}}}";
            case EventInfo evt when evt.AddMethod is { } add && IsExposed(add):
                return $"event {Name(evt.EventHandlerType!)} {evt.Name}";
            default:
                return null;
        }
    }

    private static bool IsExposed(MethodBase method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static string Modifiers(bool isStatic, bool other) => (isStatic ? "static " : string.Empty) + (other ? "fixed " : string.Empty);

    private static string Generics(MethodInfo method) =>
        method.IsGenericMethodDefinition ? $"<{string.Join(",", method.GetGenericArguments().Select(a => a.Name))}>" : string.Empty;

    private static string Kind(Type type) =>
        type.IsEnum ? "enum"
        : type.IsInterface ? "interface"
        : type.IsValueType ? "struct"
        : type.IsAbstract && type.IsSealed ? "static class"
        : type.IsAbstract ? "abstract class"
        : type.IsSealed ? "sealed class"
        : "class";

    private static string Name(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsArray)
        {
            return Name(type.GetElementType()!) + "[]";
        }

        if (type.IsByRef)
        {
            return Name(type.GetElementType()!) + "&";
        }

        var builder = new StringBuilder(type.IsNested ? Name(type.DeclaringType!) + "+" : type.Namespace is { Length: > 0 } ns ? ns + "." : string.Empty);
        builder.Append(type.Name);
        if (type.IsGenericType)
        {
            builder.Append('<').Append(string.Join(",", type.GetGenericArguments().Select(Name))).Append('>');
        }

        return builder.ToString();
    }
}
