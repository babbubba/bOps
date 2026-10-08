// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>Runtime-owned bounded validation for ADR-0050 typed evidence facts.</summary>
internal static class EvidenceFacts
{
    internal const int MaximumFactsPerToolCall = 16;
    internal const int MaximumIdentifierLength = 128;
    internal const int MaximumValueTextLength = 1024;
    internal const int MaximumPathListItems = 16;
    internal const int MaximumSerializedBytes = 4096;

    internal static string? Validate(IReadOnlyList<EvidenceFact>? facts)
    {
        if (facts is null)
        {
            return "facts must not be null.";
        }

        if (facts.Count > MaximumFactsPerToolCall)
        {
            return $"facts has {facts.Count} entries; at most {MaximumFactsPerToolCall} are allowed.";
        }

        if (JsonSerializer.SerializeToUtf8Bytes(facts).Length > MaximumSerializedBytes)
        {
            return $"facts exceeds the {MaximumSerializedBytes}-byte limit.";
        }

        foreach (var fact in facts)
        {
            if (fact is null)
            {
                return "facts contains a null entry.";
            }

            if (string.IsNullOrWhiteSpace(fact.Type) || fact.Type.Length > MaximumIdentifierLength)
            {
                return $"fact type must be non-empty and at most {MaximumIdentifierLength} characters.";
            }

            if (string.IsNullOrWhiteSpace(fact.Key) || fact.Key.Length > MaximumIdentifierLength)
            {
                return $"fact key must be non-empty and at most {MaximumIdentifierLength} characters.";
            }

            if (fact.Value is null || fact.ValueType == ToolParameterType.Enum || !Enum.IsDefined(fact.ValueType))
            {
                return "fact value type is not supported.";
            }

            var value = ToolArguments.FromJson(new JsonObject { ["value"] = fact.Value.DeepClone() });
            var parameter = new ToolParameter("value", fact.ValueType, "Evidence fact value.")
            {
                MaxLength = MaximumValueTextLength,
                MaxItems = MaximumPathListItems,
            };
            if (ArgumentSchema.Validate([parameter], value) is { } violation)
            {
                return $"fact value is invalid: {violation.Message}";
            }
        }

        return null;
    }
}
