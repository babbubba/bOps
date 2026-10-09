// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// The typed evidence facts <c>system.events</c> emits beside its output (ADR-0050; ADR-0042 PRE-5 amendment §10): for the
/// matched (source, event id) pairs, most severe first, how many matched records the call observed in its window and
/// filters, and — when not every pair fits the bound — how many pairs are not listed. The package owns this projection of
/// its own data; the runtime only sees opaque <see cref="EvidenceFact"/>s. A count is a minimum when the result is partial,
/// and no zero-count fact is ever emitted — absence is never a fact, and an unlisted pair is not an absent one.
/// </summary>
public static class SystemEventFacts
{
    /// <summary>The fact type: the number of matched records of one (source, event id) pair.</summary>
    public const string MatchedCountType = "system.events.matched-count";

    /// <summary>The fact type: the number of matched (source, event id) pairs that have no <see cref="MatchedCountType"/> fact.</summary>
    public const string UnlistedGroupsType = "system.events.unlisted-groups";

    /// <summary>The one key of the <see cref="UnlistedGroupsType"/> fact.</summary>
    public const string UnlistedGroupsKey = "source-and-event-id";

    /// <summary>The most facts one call emits, the unlisted-pairs fact included (below the runtime's 16).</summary>
    public const int MaximumFacts = 12;

    /// <summary>The longest key; a pair whose key is longer is skipped, never cut, so keys stay exact.</summary>
    public const int MaximumKeyCharacters = 128;

    /// <summary>The most serialized bytes of the whole list (the runtime's limit).</summary>
    public const int MaximumSerializedBytes = 4096;

    /// <summary>The key of a pair: <c>source=&lt;source&gt;;eventId=&lt;id&gt;</c>, or <c>source=&lt;source&gt;</c> without an event id.</summary>
    public static string KeyOf(string source, string? eventId) =>
        eventId is null ? $"source={source}" : $"source={source};eventId={eventId}";

    /// <summary>
    /// The facts of one call. Pairs are ordered by their most severe matched record (the record's own normalized
    /// <see cref="SystemEventSeverity"/>, most severe first), then count, newest matched record and key (ordinal); no event,
    /// source or id is ranked by name. The first representable pairs are listed within <see cref="MaximumFacts"/> and
    /// <see cref="MaximumSerializedBytes"/>; when any matched pair is left out, the last fact says how many.
    /// </summary>
    public static IReadOnlyList<EvidenceFact> From(SystemEventSnapshot snapshot, SystemEventQuery query)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);

        var groups = SystemEventFormatting.Matched(snapshot, query)
            .GroupBy(record => KeyOf(record.Source, record.EventId), StringComparer.Ordinal)
            .Select(group => (group.Key, Count: group.Count(), Newest: group.Max(record => record.TimestampUtc),
                Severity: group.Min(record => record.Severity)))
            .ToList();

        var listed = groups
            .Where(group => group.Key.Length <= MaximumKeyCharacters)
            .OrderBy(group => group.Severity)
            .ThenByDescending(group => group.Count)
            .ThenByDescending(group => group.Newest)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(MaximumFacts)
            .Select(group => new EvidenceFact(MatchedCountType, group.Key, ToolParameterType.Integer, JsonValue.Create(group.Count)))
            .ToList();

        while (true)
        {
            var unlisted = groups.Count - listed.Count;
            List<EvidenceFact> facts = unlisted > 0
                ? [.. listed, new EvidenceFact(UnlistedGroupsType, UnlistedGroupsKey, ToolParameterType.Integer, JsonValue.Create(unlisted))]
                : listed;
            if (listed.Count == 0
                || (facts.Count <= MaximumFacts && JsonSerializer.SerializeToUtf8Bytes(facts).Length <= MaximumSerializedBytes))
            {
                return facts;
            }

            listed.RemoveAt(listed.Count - 1);
        }
    }
}
