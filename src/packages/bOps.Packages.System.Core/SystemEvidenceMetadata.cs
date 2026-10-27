// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// BOPS-0128 slice B: the typed facts that say, beside a <c>system.events</c> or <c>system.crashes</c> result, which selection shaped
/// the evidence and how far back the stores behind it reach. They describe the call, not the machine: a selection fact states the
/// argument that was applied (or <c>none</c>), is never absent and never carries the text of a text or source filter; a coverage fact
/// is the same value the result's <c>coverage</c> object carries. Package data, never instruction.
/// </summary>
internal static class SystemEvidenceMetadata
{
    /// <summary>The fact type of the selection facts of <c>system.events</c>.</summary>
    internal const string EventsSelectionType = "system.events.selection";

    /// <summary>The fact type of the selection facts of <c>system.crashes</c>.</summary>
    internal const string CrashesSelectionType = "system.crashes.selection";

    /// <summary>The fact type of the coverage facts of <c>system.events</c>.</summary>
    internal const string EventsCoverageType = "system.events.coverage";

    /// <summary>The fact type of the coverage facts of <c>system.crashes</c>.</summary>
    internal const string CrashesCoverageType = "system.crashes.coverage";

    /// <summary>The value of a selection that was not applied.</summary>
    internal const string NoneValue = "none";

    /// <summary>The value of a text or source filter that was applied; the filter itself is never copied.</summary>
    internal const string PresentValue = "present";

    /// <summary>The value of a coverage fact that could not be determined.</summary>
    internal const string UnknownValue = "unknown";

    /// <summary>The key of the coverage state fact.</summary>
    internal const string CoverageStateKey = "state";

    /// <summary>The key prefix of a per-store oldest-retained fact; the store name follows.</summary>
    internal const string OldestRetainedKeyPrefix = "oldest-retained:";

    /// <summary>The key of the fact that counts the listed stores that have no oldest-retained fact.</summary>
    internal const string StoresOmittedKey = "stores-omitted";

    /// <summary>The most stores that get their own oldest-retained fact; the rest are counted in <see cref="StoresOmittedKey"/>.</summary>
    internal const int MaximumStoreFacts = 2;

    /// <summary>The longest channel value copied whole (and only without a separator); any other is reported as <see cref="PresentValue"/>, never cut.</summary>
    internal const int MaximumChannelCharacters = 64;

    /// <summary>The key of the one selection fact of <c>system.events</c>.</summary>
    internal const string AppliedKey = "applied";

    /// <summary>The selection and coverage facts of one <c>system.events</c> call.</summary>
    internal static List<EvidenceFact> ForEvents(SystemEventSnapshot snapshot, SystemEventQuery query)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);
        // One fact, not six: the runtime keeps 16 facts, and the counts of the (source, event id) pairs are what a claim is checked against.
        var applied = string.Join(
            ';',
            $"min-severity={(query.MinSeverity is { } severity ? SystemEventFormatting.ToWireValue(severity) : NoneValue)}",
            $"text-filter={Presence(query.Text)}",
            $"source-filter={Presence(query.Source)}",
            $"event-id-filter={Presence(query.EventId)}",
            $"channel={(query.Channel is { Length: > 0 and <= MaximumChannelCharacters } channel && !channel.Contains(';', StringComparison.Ordinal) ? channel : Presence(query.Channel))}",
            $"excluded-sources={query.ExcludeSources?.Count ?? 0}");
        var facts = new List<EvidenceFact> { Text(EventsSelectionType, AppliedKey, applied) };

        var statuses = SystemEventFormatting.StatusByName(snapshot.Sources);
        facts.AddRange(Coverage(EventsCoverageType, TemporalCoverage.Listed(snapshot.Stores, statuses), query.FromUtc));
        return facts;
    }

    /// <summary>The selection and coverage facts of one <c>system.crashes</c> call.</summary>
    internal static List<EvidenceFact> ForCrashes(CrashEvidenceSnapshot snapshot, CrashesQuery query)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);
        var facts = new List<EvidenceFact>
        {
            new EvidenceFact(CrashesSelectionType, "window-minutes", ToolParameterType.Integer,
                JsonValue.Create((int)Math.Round((query.ToUtc - query.FromUtc).TotalMinutes))),
        };

        var statuses = new Dictionary<string, InventorySourceStatus>(StringComparer.Ordinal);
        foreach (var source in snapshot.Sources)
        {
            statuses.TryAdd(source.Name, source.Status);
        }

        facts.AddRange(Coverage(CrashesCoverageType, TemporalCoverage.Listed(snapshot.Stores, statuses), query.FromUtc));
        return facts;
    }

    /// <summary>The serialized size of <paramref name="facts"/>, as the runtime measures it.</summary>
    internal static int SerializedBytes(IReadOnlyList<EvidenceFact> facts) => JsonSerializer.SerializeToUtf8Bytes(facts).Length;

    private static IEnumerable<EvidenceFact> Coverage(string type, IReadOnlyList<CoverageStore> listed, DateTimeOffset requestedFromUtc)
    {
        yield return Text(type, CoverageStateKey, TemporalCoverage.GlobalState(listed, requestedFromUtc));

        var withFact = 0;
        foreach (var store in listed)
        {
            var key = OldestRetainedKeyPrefix + store.Name;
            if (withFact == MaximumStoreFacts || key.Length > EvidenceFactBounds.MaximumIdentifierCharacters)
            {
                continue;
            }

            withFact++;
            yield return Text(type, key, EvidenceTime.Format(store.OldestAvailableUtc) ?? UnknownValue);
        }

        if (listed.Count > withFact)
        {
            yield return new EvidenceFact(type, StoresOmittedKey, ToolParameterType.Integer, JsonValue.Create(listed.Count - withFact));
        }
    }

    private static string Presence(string? value) => string.IsNullOrEmpty(value) ? NoneValue : PresentValue;

    private static EvidenceFact Text(string type, string key, string value) =>
        new(type, key, ToolParameterType.String, JsonValue.Create(value)!);
}

/// <summary>
/// BOPS-0128 slice A: the compact summary of every group, written before the groups so that a reader who sees only the head of the
/// output still sees each kind and code with its count. It is bounded; whatever does not fit is declared (<c>omittedEntries</c>,
/// <c>omittedCount</c>), never dropped silently, and the declaration changes neither <c>complete</c> nor <c>truncated</c>.
/// </summary>
internal sealed class GroupSummary
{
    /// <summary>The most entries a summary lists, whatever the allowance.</summary>
    internal const int MaximumEntries = 64;

    private readonly JsonArray entries;
    private readonly List<(JsonObject Entry, int Count)> candidates;
    private readonly int observedEntries;
    private readonly int observedCount;
    private int listed;

    private GroupSummary(string basis, List<(JsonObject Entry, int Count)> candidates, int allowanceBytes)
    {
        this.candidates = candidates;
        observedEntries = candidates.Count;
        observedCount = candidates.Sum(candidate => candidate.Count);
        entries = [];
        var spent = 0;
        foreach (var candidate in candidates)
        {
            var bytes = Encoding.UTF8.GetByteCount(candidate.Entry.ToJsonString()) + 1;
            if (listed == MaximumEntries || spent + bytes > allowanceBytes)
            {
                break;
            }

            spent += bytes;
            entries.Add(candidate.Entry.DeepClone());
            listed++;
        }

        Node = new JsonObject { ["basis"] = basis };
        Refresh();
        Node["entries"] = entries;
    }

    /// <summary>The summary node to place in the result.</summary>
    internal JsonObject Node { get; }

    /// <summary>Entries currently listed.</summary>
    internal int Listed => listed;

    /// <summary>Builds the summary of <paramref name="candidates"/> (already in rank order) within <paramref name="allowanceBytes"/>.</summary>
    internal static GroupSummary Create(string basis, List<(JsonObject Entry, int Count)> candidates, int allowanceBytes) =>
        new(basis, candidates, allowanceBytes);

    /// <summary>Drops the last listed entry to make room; returns false when none is left.</summary>
    internal bool TrimLast()
    {
        if (listed == 0)
        {
            return false;
        }

        entries.RemoveAt(--listed);
        Refresh();
        return true;
    }

    private void Refresh()
    {
        var listedCount = candidates.Take(listed).Sum(candidate => candidate.Count);
        Node["observedEntries"] = observedEntries;
        Node["listedEntries"] = listed;
        Node["omittedEntries"] = observedEntries - listed;
        Node["omittedCount"] = observedCount - listedCount;
    }
}
