// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// The one validator of arguments against a declared <see cref="ToolParameter"/> schema (rule S2, ADR-0022 HARDEN-6, ADR-0044
/// section 9.2): a tool's arguments against <see cref="ToolManifest.Parameters"/> and a Capability's input against
/// <see cref="CapabilityManifest.InputSchema"/> go through the same code. Unknown names are rejected, required ones must be
/// present and non-null, values must have the JSON-native type, and <c>AllowedValues</c>, <c>Minimum</c>/<c>Maximum</c>,
/// <c>MinLength</c>/<c>MaxLength</c> and <c>MinItems</c>/<c>MaxItems</c> hold. Nothing is coerced, clamped or guessed, and
/// nothing throws: a schema that is itself unusable is a violation, not an exception.
/// </summary>
/// <remarks>
/// A message names the parameter and the violated constraint. It carries the submitted value only for a parameter that is not
/// <see cref="ToolParameter.Sensitive"/>, and then only a number: a sensitive value, and its length, never appear in it.
/// </remarks>
internal static class ArgumentSchema
{
    /// <summary>Why arguments do not conform: the parameter concerned when there is one, and a bounded message.</summary>
    internal sealed record Violation(string? Parameter, string Message);

    /// <summary>Validates <paramref name="arguments"/> against <paramref name="schema"/>; <c>null</c> when they conform.</summary>
    internal static Violation? Validate(IReadOnlyList<ToolParameter> schema, ToolArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(arguments);

        var declared = new Dictionary<string, ToolParameter>(StringComparer.Ordinal);
        foreach (var parameter in schema)
        {
            if (parameter is null || parameter.Name is null || !declared.TryAdd(parameter.Name, parameter))
            {
                return new Violation(null, "The declared parameter schema is not usable: a parameter is null, unnamed or declared twice.");
            }
        }

        var supplied = arguments.ToJson();
        foreach (var (name, _) in supplied)
        {
            if (!declared.ContainsKey(name))
            {
                return new Violation(name, $"Unknown argument '{name}'.");
            }
        }

        foreach (var parameter in schema.Where(p => p.Required))
        {
            if (!supplied.TryGetPropertyValue(parameter.Name, out var requiredValue) || requiredValue is null)
            {
                return new Violation(parameter.Name, $"Missing required argument '{parameter.Name}'.");
            }
        }

        foreach (var parameter in schema)
        {
            if (!supplied.TryGetPropertyValue(parameter.Name, out var value) || value is null)
            {
                continue;
            }

            if (!HasDeclaredType(parameter.Type, value))
            {
                return new Violation(parameter.Name, $"Argument '{parameter.Name}' is not a valid {parameter.Type}.");
            }

            if (parameter.AllowedValues is { Count: > 0 }
                && value is JsonValue allowedValue
                && allowedValue.TryGetValue<string>(out var text)
                && !parameter.AllowedValues.Contains(text, StringComparer.Ordinal))
            {
                return new Violation(parameter.Name, $"Argument '{parameter.Name}' is not one of the allowed values.");
            }

            if (ViolatedConstraint(parameter, value) is { } violation)
            {
                return new Violation(parameter.Name, violation);
            }
        }

        return null;
    }

    /// <summary>
    /// ADR-0022 (HARDEN-6): a constraint must fit the parameter type it is declared on and be consistent with its counterpart.
    /// The registration check of both tool parameters and Capability input schemas; <c>null</c> when consistent.
    /// </summary>
    internal static string? DescribeInvalidConstraint(ToolParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        if ((parameter.Minimum is not null || parameter.Maximum is not null) && parameter.Type is not (ToolParameterType.Integer or ToolParameterType.Number))
        {
            return "Minimum/Maximum apply only to Integer and Number";
        }

        if ((parameter.MinLength is not null || parameter.MaxLength is not null) && parameter.Type is not (ToolParameterType.String or ToolParameterType.Path))
        {
            return "MinLength/MaxLength apply only to String and Path";
        }

        if ((parameter.MinItems is not null || parameter.MaxItems is not null) && parameter.Type != ToolParameterType.PathList)
        {
            return "MinItems/MaxItems apply only to PathList";
        }

        if ((parameter.Minimum is { } minimum && !double.IsFinite(minimum)) || (parameter.Maximum is { } maximum && !double.IsFinite(maximum)))
        {
            return "Minimum and Maximum must be finite numbers";
        }

        if (parameter.MinLength < 0 || parameter.MaxLength < 0 || parameter.MinItems < 0 || parameter.MaxItems < 0)
        {
            return "lengths and item counts cannot be negative";
        }

        if (parameter.Minimum > parameter.Maximum || parameter.MinLength > parameter.MaxLength || parameter.MinItems > parameter.MaxItems)
        {
            return "a minimum exceeds its maximum";
        }

        return null;
    }

    /// <summary>
    /// ADR-0044 section 9.2 (review N-7): whether a Capability's input schema is internally valid, checked when its Skill
    /// registers so no active Capability carries one that cannot be rendered or satisfied. Refuses a missing schema, a null or
    /// blank-named parameter, a name declared twice (ordinal), an undefined type, an <c>Enum</c> with no allowed values, and
    /// every constraint inconsistency <see cref="DescribeInvalidConstraint"/> refuses. <c>null</c> when valid.
    /// </summary>
    internal static string? DescribeInvalidInputSchema(IReadOnlyList<ToolParameter>? schema)
    {
        if (schema is null)
        {
            return "it declares no input schema (an empty list declares none)";
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < schema.Count; i++)
        {
            var parameter = schema[i];
            if (parameter is null)
            {
                return $"input parameter {i} is null";
            }

            if (string.IsNullOrWhiteSpace(parameter.Name))
            {
                return $"input parameter {i} has a blank name";
            }

            if (!names.Add(parameter.Name))
            {
                return $"input parameter '{Bounded(parameter.Name)}' is declared more than once";
            }

            if (!Enum.IsDefined(parameter.Type))
            {
                return $"input parameter '{Bounded(parameter.Name)}' has an undefined type";
            }

            if (parameter.Type == ToolParameterType.Enum && parameter.AllowedValues is not { Count: > 0 })
            {
                return $"input parameter '{Bounded(parameter.Name)}' is an Enum with no allowed values";
            }

            if (parameter.AllowedValues is { } allowed && allowed.Any(value => value is null))
            {
                return $"input parameter '{Bounded(parameter.Name)}' lists a null allowed value";
            }

            if (DescribeInvalidConstraint(parameter) is { } problem)
            {
                return $"input parameter '{Bounded(parameter.Name)}' ({parameter.Type}) declares invalid constraints: {problem}";
            }
        }

        return null;
    }

    private static bool HasDeclaredType(ToolParameterType type, JsonNode value) => type switch
    {
        ToolParameterType.String or ToolParameterType.Path or ToolParameterType.Duration or ToolParameterType.Enum =>
            value.GetValueKind() == JsonValueKind.String,
        ToolParameterType.Integer => value is JsonValue integer && integer.TryGetValue<int>(out _),
        ToolParameterType.Number => value is JsonValue number && number.TryGetValue<double>(out _),
        ToolParameterType.Boolean => value.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        ToolParameterType.PathList => value is JsonArray paths
            && paths.All(path => path is not null && path.GetValueKind() == JsonValueKind.String),
        _ => false,
    };

    /// <summary>
    /// Checks one already type-checked argument against the typed constraints of its parameter (ADR-0022). The message names the
    /// argument and the bound so a caller can correct it; for a parameter that is not sensitive it also says the number or length
    /// it found. It is culture-invariant and the value is never clamped.
    /// </summary>
    private static string? ViolatedConstraint(ToolParameter parameter, JsonNode value)
    {
        var name = parameter.Name;
        var sensitive = parameter.Sensitive;
        switch (parameter.Type)
        {
            case ToolParameterType.Integer or ToolParameterType.Number when value is JsonValue numeric && TryReadNumber(numeric, out var number):
                if (parameter.Minimum is { } minimum && number < minimum)
                {
                    return sensitive
                        ? FormattableString.Invariant($"Argument '{name}' is below minimum {minimum}.")
                        : FormattableString.Invariant($"Argument '{name}' = {number} is below minimum {minimum}.");
                }

                if (parameter.Maximum is { } maximum && number > maximum)
                {
                    return sensitive
                        ? FormattableString.Invariant($"Argument '{name}' exceeds maximum {maximum}.")
                        : FormattableString.Invariant($"Argument '{name}' = {number} exceeds maximum {maximum}.");
                }

                break;
            case ToolParameterType.String or ToolParameterType.Path when value is JsonValue textual && textual.TryGetValue<string>(out var text):
                if (parameter.MinLength is { } minLength && text.Length < minLength)
                {
                    return sensitive
                        ? FormattableString.Invariant($"Argument '{name}' is shorter than the minimum length {minLength}.")
                        : FormattableString.Invariant($"Argument '{name}' length {text.Length} is below minimum {minLength}.");
                }

                if (parameter.MaxLength is { } maxLength && text.Length > maxLength)
                {
                    return sensitive
                        ? FormattableString.Invariant($"Argument '{name}' is longer than the maximum length {maxLength}.")
                        : FormattableString.Invariant($"Argument '{name}' length {text.Length} exceeds maximum {maxLength}.");
                }

                break;
            case ToolParameterType.PathList when value is JsonArray items:
                if (parameter.MinItems is { } minItems && items.Count < minItems)
                {
                    return FormattableString.Invariant($"Argument '{name}' contains {items.Count} items, below minimum {minItems}.");
                }

                if (parameter.MaxItems is { } maxItems && items.Count > maxItems)
                {
                    return FormattableString.Invariant($"Argument '{name}' contains {items.Count} items, exceeding maximum {maxItems}.");
                }

                break;
        }

        return null;
    }

    /// <summary>Reads a JSON number whatever backs the node: a parsed element or a CLR value a caller built.</summary>
    private static bool TryReadNumber(JsonValue value, out double number) =>
        double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number);

    private static string Bounded(string name) => name.Length <= 128 ? name : name[..128];
}
