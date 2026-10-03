// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;

namespace bOps.Packages.Sys.Core;

/// <summary>Creates the bounded, deterministic JSON output of <c>system.events</c> (ADR-0032, schema 2 of the HARDEN-7 amendment).</summary>
public static class SystemEventFormatting
{
    /// <summary>The result schema version.</summary>
    public const int SchemaVersion = 2;

    /// <summary>The wire name of a severity.</summary>
    public static string ToWireValue(SystemEventSeverity severity) => severity switch
    {
        SystemEventSeverity.Critical => "critical",
        SystemEventSeverity.Error => "error",
        SystemEventSeverity.Warning => "warning",
        SystemEventSeverity.Information => "information",
        SystemEventSeverity.Verbose => "verbose",
        SystemEventSeverity.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown event severity."),
    };

    /// <summary>
    /// Selects the events <paramref name="query"/> asks for from <paramref name="snapshot"/>, orders them newest first, returns them as
    /// rows (<see cref="EvidenceMode.Raw"/>) or groups (<see cref="EvidenceMode.Aggregate"/>), applies the count and UTF-8 byte bounds
    /// and states, explicitly, whether the answer is complete and how far back the logs reach.
    /// </summary>
    public static string Format(SystemEventSnapshot snapshot, SystemEventQuery query, EvidenceMode mode, int limit, int maxOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);

        var observed = snapshot.Events
            .Where(record => IsValid(record) && SystemEventFilter.Matches(record, query))
            .OrderByDescending(record => record.TimestampUtc)
            .ThenBy(record => record.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Source, StringComparer.Ordinal)
            .ThenBy(record => record.Channel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.EventId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Message, StringComparer.Ordinal)
            .ThenBy(record => record.ProcessId)
            .ToArray();
        var status = SystemInventoryFormatting.GetStatus(snapshot.Sources);
        var stores = TemporalCoverage.Listed(snapshot.Stores, StatusByName(snapshot.Sources));
        var coverage = TemporalCoverage.ToJson(stores, query.FromUtc, query.ToUtc);
        var coverageState = coverage["state"]!.GetValue<string>();

        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["mode"] = EvidenceModes.ToWireValue(mode),
            ["status"] = status,
            ["complete"] = false,
            ["truncated"] = false,
            ["window"] = new JsonObject
            {
                ["fromUtc"] = EvidenceTime.Format(query.FromUtc),
                ["toUtc"] = EvidenceTime.Format(query.ToUtc),
            },
            // ADR-0032 HARDEN-9 amendment §4: always present, part of the envelope the byte budget never cuts, so a reader can see what
            // was left out. An excluded source is not looked at: never evidence that it logged nothing.
            ["excludeSources"] = new JsonArray((query.ExcludeSources ?? []).Select(entry => (JsonNode)JsonValue.Create(entry)!).ToArray()),
            ["coverage"] = coverage,
            ["observedEvents"] = observed.Length,
            ["sources"] = Sources(snapshot.Sources),
        };

        JsonArray entries;
        string returnedName;
        bool cut;
        if (mode == EvidenceMode.Aggregate)
        {
            var groups = Aggregate(observed);
            var selected = groups.Take(limit).ToArray();
            root["observedGroups"] = groups.Count;
            root["returnedGroups"] = selected.Length;
            root["groups"] = entries = new JsonArray(selected.Select(group => (JsonNode)group).ToArray());
            returnedName = "returnedGroups";
            cut = selected.Length < groups.Count;
        }
        else
        {
            var selected = observed.Take(limit).Select(CreateEvent).ToArray();
            root["returnedEvents"] = selected.Length;
            root["events"] = entries = new JsonArray(selected.Select(item => (JsonNode)item).ToArray());
            returnedName = "returnedEvents";
            cut = selected.Length < observed.Length;
        }

        var truncated = snapshot.CollectionTruncated || cut;
        SetCompleteness(root, status, truncated, coverageState);

        var output = root.ToJsonString();
        while (Encoding.UTF8.GetByteCount(output) > maxOutputBytes && entries.Count > 0)
        {
            entries.RemoveAt(entries.Count - 1);
            root[returnedName] = entries.Count;
            SetCompleteness(root, status, truncated: true, coverageState);
            output = root.ToJsonString();
        }

        if (Encoding.UTF8.GetByteCount(output) > maxOutputBytes)
        {
            throw new InvalidOperationException(
                $"Event metadata exceeds the configured {maxOutputBytes}-byte output limit.");
        }

        return output;
    }

    /// <summary>
    /// Groups records by (channel, source, unit, event id, severity), compared ordinally with <c>null</c> as a value of its own, and
    /// orders the groups by count, newest last time, then the key fields (case-insensitive ordinal, then ordinal, <c>null</c> first).
    /// <paramref name="newestFirst"/> must already be in the raw order, so the first member of a group is its sample.
    /// </summary>
    internal static IReadOnlyList<JsonObject> Aggregate(IReadOnlyList<SystemEventRecord> newestFirst) =>
        newestFirst
            .GroupBy(record => (record.Channel, record.Source, record.Unit, record.EventId, Severity: ToWireValue(record.Severity)))
            .Select(group => (group.Key, Members: group.ToArray()))
            .OrderByDescending(group => group.Members.Length)
            .ThenByDescending(group => group.Members[0].TimestampUtc)
            .ThenBy(group => group.Key.Channel, KeyComparer.Instance)
            .ThenBy(group => group.Key.Source, KeyComparer.Instance)
            .ThenBy(group => group.Key.Unit, KeyComparer.Instance)
            .ThenBy(group => group.Key.EventId, KeyComparer.Instance)
            .ThenBy(group => group.Key.Severity, KeyComparer.Instance)
            .Select(group =>
            {
                var sample = group.Members[0];
                return new JsonObject
                {
                    ["channel"] = SystemInventoryFormatting.Bounded(group.Key.Channel, 256),
                    ["source"] = SystemInventoryFormatting.Bounded(group.Key.Source, 256),
                    ["unit"] = SystemInventoryFormatting.Bounded(group.Key.Unit, 256),
                    ["eventId"] = SystemInventoryFormatting.Bounded(group.Key.EventId, 128),
                    ["severity"] = group.Key.Severity,
                    ["count"] = group.Members.Length,
                    ["firstSeenUtc"] = EvidenceTime.Format(group.Members[^1].TimestampUtc),
                    ["lastSeenUtc"] = EvidenceTime.Format(sample.TimestampUtc),
                    ["sampleMessage"] = SystemInventoryFormatting.Bounded(sample.Message, SystemEventsLimits.SampleMessageCharacters),
                    ["sampleMessageTruncated"] = sample.Message.Length > SystemEventsLimits.SampleMessageCharacters,
                };
            })
            .ToArray();

    internal static JsonArray Sources(IEnumerable<InventorySourceResult> sources) =>
        new(sources
            .OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Name, StringComparer.Ordinal)
            .Select(source => (JsonNode)new JsonObject
            {
                ["name"] = SystemInventoryFormatting.Bounded(source.Name, 128),
                ["status"] = SystemInventoryFormatting.ToWireValue(source.Status),
                ["detail"] = SystemInventoryFormatting.Bounded(source.Detail, 256),
                ["examinedFromUtc"] = EvidenceTime.Format(source.ExaminedFromUtc),
            })
            .ToArray());

    internal static Dictionary<string, InventorySourceStatus> StatusByName(IEnumerable<InventorySourceResult> sources)
    {
        var statuses = new Dictionary<string, InventorySourceStatus>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            statuses.TryAdd(source.Name, source.Status);
        }

        return statuses;
    }

    /// <summary><c>complete = status complete AND not truncated AND coverage complete</c> (ADR-0032 HARDEN-7 amendment §6).</summary>
    internal static void SetCompleteness(JsonObject root, string status, bool truncated, string coverageState)
    {
        root["truncated"] = truncated;
        root["complete"] = status == "complete" && !truncated && coverageState == TemporalCoverage.Complete;
    }

    private static JsonObject CreateEvent(SystemEventRecord record)
    {
        var messageTruncated = record.Message.Length > SystemEventsLimits.MessageCharacters;
        return new JsonObject
        {
            ["timestampUtc"] = EvidenceTime.Format(record.TimestampUtc),
            ["severity"] = ToWireValue(record.Severity),
            ["source"] = SystemInventoryFormatting.Bounded(record.Source, 256),
            ["unit"] = SystemInventoryFormatting.Bounded(record.Unit, 256),
            ["eventId"] = SystemInventoryFormatting.Bounded(record.EventId, 128),
            ["channel"] = SystemInventoryFormatting.Bounded(record.Channel, 256),
            ["message"] = SystemInventoryFormatting.Bounded(record.Message, SystemEventsLimits.MessageCharacters),
            ["messageTruncated"] = messageTruncated,
            ["processId"] = record.ProcessId,
            ["processName"] = SystemInventoryFormatting.Bounded(record.ProcessName, 256),
        };
    }

    private static bool IsValid(SystemEventRecord record) =>
        !string.IsNullOrWhiteSpace(record.Source) && record.Message is not null;
}

/// <summary>
/// The key ordering of the evidence aggregates: <c>null</c> first, then case-insensitive ordinal, then ordinal, so equal keys that
/// differ only by case still have one deterministic order.
/// </summary>
internal sealed class KeyComparer : IComparer<string?>
{
    internal static readonly KeyComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }

        var insensitive = StringComparer.OrdinalIgnoreCase.Compare(x, y);
        return insensitive != 0 ? insensitive : StringComparer.Ordinal.Compare(x, y);
    }
}
