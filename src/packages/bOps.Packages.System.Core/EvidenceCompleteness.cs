// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// Derives the typed <see cref="ToolResultCompleteness"/> (HARDEN-6, ADR-0022) of a system tool result from the completeness
/// fields the tool's own formatter already emitted: <c>complete</c>, <c>partial</c>, <c>truncated</c>, <c>status</c>, <c>exists</c>
/// and <c>rootFound</c>. The JSON stays the single source of truth, so the typed value and the legacy fields cannot diverge, and
/// the derivation happens here, inside the package that wrote the JSON; the runtime only ever reads the typed value.
/// </summary>
/// <remarks>
/// Precedence, first match wins: <c>complete: true</c> is <see cref="ToolResultCompleteness.Complete"/>; <c>partial: true</c> is
/// <see cref="ToolResultCompleteness.Partial"/>; a <c>status</c> of <c>unavailable</c> or <c>unsupported</c>, or a subject that
/// does not exist (<c>exists</c> or <c>rootFound</c> false), is <see cref="ToolResultCompleteness.Unavailable"/>;
/// <c>complete: false</c>, <c>truncated: true</c> or a <c>status</c> of <c>partial</c> is <see cref="ToolResultCompleteness.Partial"/>;
/// any other recognised field is <see cref="ToolResultCompleteness.Complete"/>. Output with none of these fields stays
/// <see cref="ToolResultCompleteness.Unspecified"/>.
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

        if (root is null)
        {
            return ToolResultCompleteness.Unspecified;
        }

        var complete = Flag(root, "complete");
        var partial = Flag(root, "partial");
        var truncated = Flag(root, "truncated");
        var exists = Flag(root, "exists");
        var rootFound = Flag(root, "rootFound");
        var status = root["status"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        if (complete == true)
        {
            return ToolResultCompleteness.Complete;
        }

        if (partial == true)
        {
            return ToolResultCompleteness.Partial;
        }

        if (status is "unavailable" or "unsupported" || exists == false || rootFound == false)
        {
            return ToolResultCompleteness.Unavailable;
        }

        if (complete == false || truncated == true || status == "partial")
        {
            return ToolResultCompleteness.Partial;
        }

        return partial is not null || truncated is not null || status is not null
            ? ToolResultCompleteness.Complete
            : ToolResultCompleteness.Unspecified;
    }

    private static bool? Flag(JsonObject root, string name) =>
        root[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
}
