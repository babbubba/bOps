// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Storage.Core;

/// <summary>
/// Derives the typed <see cref="ToolResultCompleteness"/> (HARDEN-6, ADR-0022) of a storage tool result from the completeness
/// fields its own formatter already emitted: the top-level <c>truncated</c> flag and, for <c>storage.health</c>, the per-device
/// <c>partial</c> flag. The JSON stays the single source of truth, so the typed value and the legacy fields cannot diverge.
/// </summary>
/// <remarks>
/// A result that was cut by its row or byte bound, or that contains a device whose evidence is <c>partial</c>, is
/// <see cref="ToolResultCompleteness.Partial"/>; a result that states neither is <see cref="ToolResultCompleteness.Complete"/>;
/// output without a <c>truncated</c> flag stays <see cref="ToolResultCompleteness.Unspecified"/>.
/// </remarks>
internal static class EvidenceCompleteness
{
    /// <summary>A successful result for <paramref name="output"/>, carrying the completeness its own fields state.</summary>
    internal static ToolCallResult Success(string output) =>
        ToolCallResult.Success(output) with { Completeness = FromOutput(output) };

    /// <summary>The completeness stated by the completeness fields of <paramref name="output"/>.</summary>
    internal static ToolResultCompleteness FromOutput(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return ToolResultCompleteness.Unspecified;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(output) as JsonObject;
        }
        catch (JsonException)
        {
            return ToolResultCompleteness.Unspecified;
        }

        if (root?["truncated"] is not JsonValue truncatedValue || !truncatedValue.TryGetValue<bool>(out var truncated))
        {
            return ToolResultCompleteness.Unspecified;
        }

        var partialDevice = root["devices"] is JsonArray devices
            && devices.OfType<JsonObject>().Any(device => device["partial"] is JsonValue flag && flag.TryGetValue<bool>(out var partial) && partial);

        return truncated || partialDevice ? ToolResultCompleteness.Partial : ToolResultCompleteness.Complete;
    }
}
