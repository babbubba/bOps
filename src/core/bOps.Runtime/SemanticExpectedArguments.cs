// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>ADR-0050's bounded, manifest-typed expected-argument validation and subset equality.</summary>
internal static class SemanticExpectedArguments
{
    internal const int MaximumConstraintCount = 8;
    internal const int MaximumSerializedBytes = 1024;

    internal static string? Validate(ToolManifest manifest, ToolArguments expected)
    {
        var values = expected.ToJson();
        if (values.Count > MaximumConstraintCount)
        {
            return $"expectedArguments has {values.Count} properties; at most {MaximumConstraintCount} are allowed.";
        }

        if (JsonSerializer.SerializeToUtf8Bytes(values).Length > MaximumSerializedBytes)
        {
            return $"expectedArguments exceeds the {MaximumSerializedBytes}-byte limit.";
        }

        var parameters = manifest.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (!parameters.TryGetValue(name, out var parameter))
            {
                return $"expectedArguments names undeclared parameter '{name}'.";
            }

            if (parameter.Sensitive)
            {
                return $"expectedArguments may not constrain sensitive parameter '{name}'.";
            }

            if (value is null)
            {
                return $"expectedArguments parameter '{name}' may not be null.";
            }

            var one = ToolArguments.FromJson(new JsonObject { [name] = value.DeepClone() });
            if (ArgumentSchema.Validate([parameter with { Required = true }], one) is { } violation)
            {
                return $"expectedArguments parameter '{name}' is invalid: {violation.Message}";
            }
        }

        return null;
    }

    internal static bool Matches(ToolManifest manifest, ToolArguments? expected, ModelToolCall call)
    {
        if (expected is null)
        {
            return true;
        }

        var constraints = expected.ToJson();
        var actual = call.Arguments.ToJson();
        var parameters = manifest.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);
        foreach (var (name, expectedValue) in constraints)
        {
            if (expectedValue is null
                || !actual.TryGetPropertyValue(name, out var actualValue)
                || actualValue is null
                || !parameters.TryGetValue(name, out var parameter)
                || !TypedEquals(parameter.Type, expectedValue, actualValue))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool HasDiscriminator(ToolManifest manifest, ToolArguments left, ToolArguments right)
    {
        var leftValues = left.ToJson();
        var rightValues = right.ToJson();
        var parameters = manifest.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);
        foreach (var (name, leftValue) in leftValues)
        {
            if (leftValue is not null
                && rightValues.TryGetPropertyValue(name, out var rightValue)
                && rightValue is not null
                && parameters.TryGetValue(name, out var parameter)
                && !TypedEquals(parameter.Type, leftValue, rightValue))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TypedEquals(ToolParameterType type, JsonNode left, JsonNode right) => type switch
    {
        ToolParameterType.String or ToolParameterType.Path or ToolParameterType.Duration or ToolParameterType.Enum =>
            TryString(left, out var leftText) && TryString(right, out var rightText)
            && string.Equals(leftText, rightText, StringComparison.Ordinal),
        ToolParameterType.Integer =>
            left is JsonValue leftInteger && right is JsonValue rightInteger
            && leftInteger.TryGetValue<int>(out var leftInt) && rightInteger.TryGetValue<int>(out var rightInt)
            && leftInt == rightInt,
        ToolParameterType.Number =>
            TryNumber(left, out var leftNumber) && TryNumber(right, out var rightNumber) && leftNumber.Equals(rightNumber),
        ToolParameterType.Boolean =>
            left is JsonValue leftBoolean && right is JsonValue rightBoolean
            && leftBoolean.TryGetValue<bool>(out var leftBool) && rightBoolean.TryGetValue<bool>(out var rightBool)
            && leftBool == rightBool,
        ToolParameterType.PathList => PathListsEqual(left, right),
        _ => false,
    };

    private static bool TryString(JsonNode node, out string? value)
    {
        value = null;
        return node.GetValueKind() == JsonValueKind.String
            && node is JsonValue json
            && json.TryGetValue(out value);
    }

    private static bool TryNumber(JsonNode node, out double value)
    {
        value = default;
        return node is JsonValue json
            && double.TryParse(json.ToJsonString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool PathListsEqual(JsonNode left, JsonNode right)
    {
        if (left is not JsonArray leftPaths || right is not JsonArray rightPaths || leftPaths.Count != rightPaths.Count)
        {
            return false;
        }

        for (var i = 0; i < leftPaths.Count; i++)
        {
            if (leftPaths[i] is null || rightPaths[i] is null
                || !TryString(leftPaths[i]!, out var leftPath) || !TryString(rightPaths[i]!, out var rightPath)
                || !string.Equals(leftPath, rightPath, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
