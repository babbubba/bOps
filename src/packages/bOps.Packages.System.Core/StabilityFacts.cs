// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// The typed evidence facts <c>system.stability</c> emits beside its output (ADR-0050; ADR-0052 AF-04): for each category the call
/// observed at least one record of, how many records its sources contributed in the window. The projection reads the tool's own
/// formatted categories, so a fact can never disagree with the JSON. A count is a lower bound when the category's status is
/// <c>partial</c> or the result is truncated, and the runtime presents it under the result's own completeness; a category that is
/// unavailable, not applicable, not collected or observed zero times yields no fact at all, so absence is never a fact.
/// </summary>
public static class StabilityFacts
{
    /// <summary>The fact type: the records a category contributed in the window. The key is the category's wire name.</summary>
    public const string CategoryCountType = "system.stability.category-count";

    /// <summary>The fact type: the look-back window the counts cover, in days.</summary>
    public const string WindowDaysType = "system.stability.window-days";

    /// <summary>The one key of the <see cref="WindowDaysType"/> fact.</summary>
    public const string WindowDaysKey = "window";

    /// <summary>The facts of one successful call: <paramref name="output"/> is the JSON <see cref="StabilityFormatting.Format"/> produced for <paramref name="query"/>.</summary>
    public static IReadOnlyList<EvidenceFact> From(string output, StabilityQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        JsonArray? categories;
        try
        {
            categories = (JsonNode.Parse(output) as JsonObject)?["categories"] as JsonArray;
        }
        catch (JsonException)
        {
            return [];
        }

        var facts = new List<EvidenceFact>();
        foreach (var category in categories?.OfType<JsonObject>() ?? [])
        {
            if (category["applicability"]?.GetValue<string>() != "applicable"
                || category["status"]?.GetValue<string>() is not ("available" or "partial")
                || category["count"] is not JsonValue count || !count.TryGetValue<int>(out var records) || records <= 0
                || category["category"]?.GetValue<string>() is not { Length: > 0 } name
                || name.Length > EvidenceFactBounds.MaximumIdentifierCharacters)
            {
                continue;
            }

            facts.Add(new EvidenceFact(CategoryCountType, name, ToolParameterType.Integer, JsonValue.Create(records)));
        }

        if (facts.Count > 0)
        {
            facts.Add(new EvidenceFact(WindowDaysType, WindowDaysKey, ToolParameterType.Integer, JsonValue.Create(query.WindowDays)));
        }

        return EvidenceFactBounds.Fit(facts);
    }
}
