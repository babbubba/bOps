// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace bOps.Packages.Sys.Core;

/// <summary>Creates the bounded, deterministic JSON output of <c>system.events</c> (ADR-0032, schema 2 of the HARDEN-7 amendment).</summary>
public static class SystemEventFormatting
{
    /// <summary>The result schema version.</summary>
    public const int SchemaVersion = 2;

    /// <summary>
    /// The fixed reading of an aggregate group, carried as the top-level <c>sampleMessageSemantics</c> of an aggregate result: the sample
    /// describes one event, the count describes the whole group (LIVE-04A). Deterministic, provider-neutral, never derived from the data.
    /// </summary>
    public const string SampleMessageSemantics =
        "sampleMessage is the message of only the newest event of its group (the one at lastSeenUtc). "
        + "count is the number of events that share the group key (channel, source, unit, eventId, severity). "
        + "The other events of the group may have different messages: "
        + "an attribute seen in sampleMessage is not necessarily common to the whole group.";

    /// <summary>
    /// Longest <c>sources[].detail</c> of a result, in serialized UTF-8 bytes (JSON escapes included), on top of its 256-character bound.
    /// Host text such as a localized exception message is the one envelope value no argument grammar restricts to ASCII, and the default
    /// JSON encoder spends up to six bytes on one non-ASCII UTF-16 unit or on a quote, so a bound in characters alone cannot keep the
    /// envelope inside the smallest output budget (ADR-0032 HARDEN-9 amendment §4, ADR-0061 §2.8).
    /// </summary>
    internal const int SourceDetailBytes = 256;

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
    public static string Format(SystemEventSnapshot snapshot, SystemEventQuery query, EvidenceMode mode, int limit, int maxOutputBytes) =>
        Format(snapshot, query, mode, limit, maxOutputBytes, describeMessages: true);

    /// <summary>
    /// <see cref="Format(SystemEventSnapshot, SystemEventQuery, EvidenceMode, int, int)"/> with the message-distribution details switchable:
    /// with <paramref name="describeMessages"/> false an aggregate carries none (<c>messageDistribution.availability</c> is <c>none</c>),
    /// which is the reference the group count of the detailed result must never fall below.
    /// </summary>
    internal static string Format(SystemEventSnapshot snapshot, SystemEventQuery query, EvidenceMode mode, int limit, int maxOutputBytes, bool describeMessages)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);

        var observed = Matched(snapshot, query)
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
            // ADR-0061: envelope as well, never cut by the byte budget; observedMatch comes from the records observed (before grouping,
            // the limit and the budget), never from the entries that remain.
            ["sourceFilter"] = SourceFilterEvidence.ToJson(snapshot, query, observed.Length > 0),
            ["coverage"] = coverage,
            ["observedEvents"] = observed.Length,
            // The details are host text (a localized exception message) and the only envelope value not restricted to ASCII by an argument
            // grammar: they are bounded in serialized bytes so the envelope fits the smallest budget whatever the host writes.
            ["sources"] = Sources(snapshot.Sources.Select(source => source with { Detail = BoundedDetail(source.Detail) })),
        };

        JsonArray entries;
        string returnedName;
        bool cut;
        AggregateGroup[] aggregated = [];
        GroupSummary? summary = null;
        if (mode == EvidenceMode.Aggregate)
        {
            var groups = Aggregate(observed);
            aggregated = groups.Take(limit).ToArray();
            root["observedGroups"] = groups.Length;
            root["returnedGroups"] = aggregated.Length;
            root["sampleMessageSemantics"] = SampleMessageSemantics;
            root["messageDistribution"] = MessageDistribution(aggregated.Length, 0);
            // BOPS-0128 slice A: every observed group, compactly, ahead of the groups the limit and the budget may cut.
            if (groups.Length > 0)
            {
                root["groupSummary"] = (summary = Summarize(groups, maxOutputBytes)).Node;
            }

            root["groups"] = entries = new JsonArray(aggregated.Select(group => (JsonNode)group.Entry).ToArray());
            returnedName = "returnedGroups";
            cut = aggregated.Length < groups.Length;
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

        // The groups go first (they are the larger part); then the summary gives way entry by entry, declaring what it no longer lists,
        // and only as the last resort before refusing the budget does it leave the result.
        while (summary is not null && Encoding.UTF8.GetByteCount(output) > maxOutputBytes && summary.TrimLast())
        {
            output = root.ToJsonString();
        }

        if (summary is not null && Encoding.UTF8.GetByteCount(output) > maxOutputBytes)
        {
            root.Remove("groupSummary");
            output = root.ToJsonString();
        }

        if (Encoding.UTF8.GetByteCount(output) > maxOutputBytes)
        {
            throw new InvalidOperationException(
                $"Event metadata exceeds the configured {maxOutputBytes}-byte output limit.");
        }

        return mode == EvidenceMode.Aggregate && describeMessages
            ? DescribeMessages(root, entries, aggregated, maxOutputBytes)
            : output;
    }

    /// <summary>
    /// The compact summary of every observed group (BOPS-0128 slice A): source, event id, severity, count, first and last seen and
    /// <c>distinctMessages</c>: the number of distinct original messages among the records the call observed for the group, so at least 1
    /// and never 0 (a group has at least one record). Unlike the per-group message details it is not subject to the message-distribution
    /// allowance, so it is the same whether or not those details fit. A group with no records does not exist, so a missing value is
    /// never reported as a count; the summary is not created for a result without groups. The most severe groups are listed first, in
    /// the groups' own order within a severity (the <c>basis</c> says so): a rare critical event is the one a count ranking would push
    /// past the allowance.
    /// </summary>
    private static GroupSummary Summarize(AggregateGroup[] groups, int maxOutputBytes) =>
        GroupSummary.Create(
            "aggregate-groups-severity-first",
            [.. groups
                .OrderBy(group => group.Members[0].Severity)
                .Select(group => (
                new JsonObject
                {
                    ["source"] = group.Entry["source"]?.DeepClone(),
                    ["eventId"] = group.Entry["eventId"]?.DeepClone(),
                    ["severity"] = group.Entry["severity"]?.DeepClone(),
                    ["count"] = group.Members.Length,
                    ["firstSeenUtc"] = group.Entry["firstSeenUtc"]?.DeepClone(),
                    ["lastSeenUtc"] = group.Entry["lastSeenUtc"]?.DeepClone(),
                    ["distinctMessages"] = group.Members.Select(record => record.Message).Distinct(StringComparer.Ordinal).Count(),
                },
                group.Members.Length))],
            Math.Min(SummaryAllowanceBytes, maxOutputBytes / 6));

    /// <summary>The most serialized bytes the group summary of an event result may take before the output budget is divided.</summary>
    private const int SummaryAllowanceBytes = 6144;

    /// <summary>One aggregate group: the entry the result carries, and the records it counts (newest first).</summary>
    private sealed record AggregateGroup(JsonObject Entry, SystemEventRecord[] Members);

    /// <summary>
    /// Adds, to the groups the result already keeps, how the group's original messages (compared ordinally, in full) are distributed
    /// (LIVE-04C): <c>distinctMessages</c> for every described group and, when the messages differ, a bounded list of variants with their
    /// own counts. Groups are described in rank order and only while the details fit both <see cref="SystemEventsLimits.MessageDistributionBytes"/>
    /// and the output budget; a group is never dropped to make room for them, the groups not described say so in
    /// <c>messageDistribution</c>, and <c>complete</c>/<c>truncated</c> are not affected.
    /// </summary>
    private static string DescribeMessages(JsonObject root, JsonArray entries, AggregateGroup[] groups, int maxOutputBytes)
    {
        var details = new List<(int Distinct, JsonObject? Variants)>();
        var spent = 0;
        for (var index = 0; index < entries.Count; index++)
        {
            var detail = MessageDetail(groups[index].Members);
            var bytes = 32 + (detail.Variants is null ? 0 : 20 + Encoding.UTF8.GetByteCount(detail.Variants.ToJsonString()));
            if (spent + bytes > SystemEventsLimits.MessageDistributionBytes)
            {
                break;
            }

            spent += bytes;
            details.Add(detail);
        }

        string Apply(int described)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index]!.AsObject();
                entry.Remove("distinctMessages");
                entry.Remove("messageVariants");
                if (index < described)
                {
                    entry["distinctMessages"] = details[index].Distinct;
                    if (details[index].Variants is { } variants)
                    {
                        entry["messageVariants"] = variants.DeepClone();
                    }
                }
            }

            root["messageDistribution"] = MessageDistribution(entries.Count, described);
            return root.ToJsonString();
        }

        // Details only add bytes, so the largest prefix that fits is found by bisection; zero always fits (it is the result as it stands).
        var low = 0;
        var high = details.Count;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Encoding.UTF8.GetByteCount(Apply(middle)) <= maxOutputBytes)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return Apply(low);
    }

    /// <summary>The top-level statement of how many leading groups carry message-distribution details.</summary>
    private static JsonObject MessageDistribution(int groups, int described) => new()
    {
        ["availability"] = described == groups ? "all" : described == 0 ? "none" : "partial",
        ["describedGroups"] = described,
    };

    /// <summary>
    /// The number of distinct original messages of one group (ordinal, over the full text) and, when there are several, the most frequent
    /// ones with their own counts — most frequent first, then newest, then ordinal. A variant is a message, not a diagnosis.
    /// </summary>
    private static (int Distinct, JsonObject? Variants) MessageDetail(SystemEventRecord[] members)
    {
        var variants = members
            .GroupBy(record => record.Message, StringComparer.Ordinal)
            .Select(group => (Message: group.Key, Count: group.Count(), Newest: group.Max(record => record.TimestampUtc)))
            .OrderByDescending(variant => variant.Count)
            .ThenByDescending(variant => variant.Newest)
            .ThenBy(variant => variant.Message, StringComparer.Ordinal)
            .ToArray();
        if (variants.Length == 1)
        {
            return (1, null);
        }

        var listed = variants.Take(SystemEventsLimits.MaximumMessageVariants).ToArray();
        var unlisted = members.Length - listed.Sum(variant => variant.Count);

        // A list that accounts for less than half of the events (messages that are almost all unique, e.g. they embed a time or an id)
        // says little and would spend the allowance meant for groups it can describe: only the distinct count is given, and said so.
        if (unlisted * 2 > members.Length)
        {
            return (variants.Length, new JsonObject { ["distribution"] = "unlisted" });
        }

        var result = new JsonObject { ["distribution"] = unlisted == 0 ? "complete" : "partial" };
        if (unlisted > 0)
        {
            result["unlistedEvents"] = unlisted;
        }

        result["variants"] = new JsonArray(listed.Select(variant => (JsonNode)new JsonObject
        {
            ["count"] = variant.Count,
            ["message"] = SystemInventoryFormatting.Bounded(variant.Message, SystemEventsLimits.VariantMessageCharacters),
            ["messageTruncated"] = variant.Message.Length > SystemEventsLimits.VariantMessageCharacters,
        }).ToArray());
        return (variants.Length, result);
    }

    /// <summary>
    /// Groups records by (channel, source, unit, event id, severity), compared ordinally with <c>null</c> as a value of its own, and
    /// orders the groups by count, newest last time, then the key fields (case-insensitive ordinal, then ordinal, <c>null</c> first).
    /// <paramref name="newestFirst"/> must already be in the raw order, so the first member of a group is its sample.
    /// </summary>
    private static AggregateGroup[] Aggregate(IReadOnlyList<SystemEventRecord> newestFirst) =>
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
                return new AggregateGroup(new JsonObject
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
                }, group.Members);
            })
            .ToArray();

    /// <summary>
    /// <paramref name="detail"/> within 256 characters and <see cref="SourceDetailBytes"/> serialized UTF-8 bytes, counted with the encoder
    /// that writes the result. A detail within both bounds, which is every plain ASCII message of ordinary length, is returned unchanged;
    /// a longer one keeps its beginning (where an error message states its reason), is cut on a code-point boundary and ends with
    /// <c>...</c>, so the cut is visible.
    /// </summary>
    internal static string? BoundedDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return detail;
        }

        var whole = PrefixWithin(detail, 256, SourceDetailBytes);
        return whole == detail.Length ? detail : string.Concat(detail.AsSpan(0, PrefixWithin(detail, 256 - CutMarker.Length, SourceDetailBytes - CutMarker.Length)), CutMarker);
    }

    private const string CutMarker = "...";

    /// <summary>The length, in UTF-16 units and on a code-point boundary, of the longest prefix of <paramref name="text"/> within both bounds.</summary>
    private static int PrefixWithin(string text, int maximumCharacters, int maximumBytes)
    {
        var end = 0;
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var cost = JsonEncodedText.Encode(rune.ToString()).EncodedUtf8Bytes.Length;
            if (end + rune.Utf16SequenceLength > maximumCharacters || bytes + cost > maximumBytes)
            {
                break;
            }

            bytes += cost;
            end += rune.Utf16SequenceLength;
        }

        return end;
    }

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

    /// <summary>The records of <paramref name="snapshot"/> the output and the evidence facts both describe: valid and matching <paramref name="query"/>.</summary>
    internal static IEnumerable<SystemEventRecord> Matched(SystemEventSnapshot snapshot, SystemEventQuery query) =>
        snapshot.Events.Where(record => IsValid(record) && SystemEventFilter.Matches(record, query));

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
