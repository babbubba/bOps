// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.System.Core.Tests;

/// <summary>
/// BOPS-0128 slices A and B: every kernel code reaches the typed facts, every group reaches the compact summary, and the selection and
/// coverage that shaped a result are stated beside it, all within the existing output and fact bounds. Whatever does not fit is declared
/// (<c>kernel-codes-omitted</c>, <c>omittedEntries</c>), never dropped silently.
/// </summary>
public sealed class Bops0128EvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly SystemEventQuery DayQuery = new(Now.AddDays(-1), Now, null, null, null, null, null, 10_000);

    private static readonly string[] SummaryCodes = ["0x1e", "0x7f", "0xa", "0x101", "0x3b", "0x50", "0xd1"];

    // ---- slice A: system.crashes kernel codes ----

    [Fact]
    public async Task Crashes_SevenBugcheckAndTwoLiveDumpCodes_AreAllListedWithExactCounts()
    {
        var items = new List<CrashEvidence>();
        items.AddRange(Many(CrashKinds.KernelBugcheck, "0x1e", 9));
        items.AddRange(Many(CrashKinds.KernelBugcheck, "0xa", 2));
        items.AddRange(Many(CrashKinds.KernelBugcheck, "0x7f", 2));
        items.AddRange(Many(CrashKinds.KernelBugcheck, "0xd1", 1));
        items.AddRange(Many(CrashKinds.KernelBugcheck, "0x50", 1));
        items.AddRange(Many(CrashKinds.KernelBugcheck, "0x3b", 1));
        items.AddRange(Many(CrashKinds.KernelBugcheck, "0x101", 1));
        items.AddRange(Many(CrashKinds.KernelLiveDump, "0x141", 3));
        items.AddRange(Many(CrashKinds.KernelLiveDump, "0x117", 1));

        var result = await new CrashesTool(CrashSnapshot(items)).ExecuteAsync(ToolArguments.Empty);

        var bugchecks = Assert.Single(result.Facts, fact => fact.Type == SystemCrashFacts.KernelCodesType && fact.Key == CrashKinds.KernelBugcheck);
        Assert.Equal("0x1e:9,0x7f:2,0xa:2,0x101:1,0x3b:1,0x50:1,0xd1:1", bugchecks.Value.GetValue<string>());
        var liveDumps = Assert.Single(result.Facts, fact => fact.Type == SystemCrashFacts.KernelCodesType && fact.Key == CrashKinds.KernelLiveDump);
        Assert.Equal("0x141:3,0x117:1", liveDumps.Value.GetValue<string>());
        Assert.DoesNotContain(result.Facts, fact => fact.Type == SystemCrashFacts.KernelCodesOmittedType);
        Assert.Equal(ToolParameterType.String, bugchecks.ValueType);
        AssertWithinTheRuntimeBounds(result.Facts);
    }

    [Fact]
    public async Task Crashes_MoreCodesThanTheValueBound_DeclareHowManyAreNotListed()
    {
        var items = Enumerable.Range(0, 60)
            .SelectMany(index => Many(CrashKinds.KernelBugcheck, "0x" + (0x100 + index).ToString("x", CultureInfo.InvariantCulture), 1 + (index % 3)))
            .ToList();

        var result = await new CrashesTool(CrashSnapshot(items)).ExecuteAsync(ToolArguments.Empty);

        var list = Assert.Single(result.Facts, fact => fact.Type == SystemCrashFacts.KernelCodesType).Value.GetValue<string>();
        var omitted = Assert.Single(result.Facts, fact => fact.Type == SystemCrashFacts.KernelCodesOmittedType);
        var listed = list.Split(',');
        Assert.True(list.Length <= EvidenceFactBounds.MaximumValueCharacters, "The value is within the bound and was cut on an entry boundary.");
        Assert.All(listed, entry => Assert.Matches("^0x[0-9a-f]+:[1-3]$", entry));
        Assert.True(omitted.Value.GetValue<int>() > 0);
        Assert.Equal(60, listed.Length + omitted.Value.GetValue<int>());
        Assert.Equal(CrashKinds.KernelBugcheck, omitted.Key);
        // What is listed is what ranks first: the most frequent codes come first.
        Assert.Equal(3, int.Parse(listed[0].Split(':')[1], CultureInfo.InvariantCulture));
        AssertWithinTheRuntimeBounds(result.Facts);
    }

    [Fact]
    public async Task Crashes_NoKernelCrash_HasNoKernelCodesFact()
    {
        var result = await new CrashesTool(CrashSnapshot([.. Many(CrashKinds.ApplicationCrash, null, 3)])).ExecuteAsync(ToolArguments.Empty);

        Assert.DoesNotContain(result.Facts, fact => fact.Type is SystemCrashFacts.KernelCodesType or SystemCrashFacts.KernelCodesOmittedType);
    }

    // ---- slice A: compact group summary ----

    [Fact]
    public void CrashSummary_ListsEveryGroupBeforeTheGroups_EvenWhenTheLimitReturnsOne()
    {
        var items = new List<CrashEvidence>();
        foreach (var (code, count) in new[] { ("0x1e", 9), ("0xa", 2), ("0x7f", 2), ("0xd1", 1), ("0x50", 1), ("0x3b", 1), ("0x101", 1) })
        {
            items.AddRange(Many(CrashKinds.KernelBugcheck, code, count));
        }

        var output = SystemCrashFormatting.Format(CrashSnapshot(items), new CrashesQuery(EvidenceMode.Aggregate, Now.AddDays(-1), Now, 1));

        var json = JsonNode.Parse(output)!.AsObject();
        var keys = json.Select(pair => pair.Key).ToList();
        Assert.True(keys.IndexOf("groupSummary") < keys.IndexOf("groups"), "The summary precedes the groups.");
        Assert.Single(json["groups"]!.AsArray());
        var summary = json["groupSummary"]!.AsObject();
        Assert.Equal((7, 7, 0, 0), (summary["observedEntries"]!.GetValue<int>(), summary["listedEntries"]!.GetValue<int>(), summary["omittedEntries"]!.GetValue<int>(), summary["omittedCount"]!.GetValue<int>()));
        Assert.Equal(
            SummaryCodes.Order(StringComparer.Ordinal),
            summary["entries"]!.AsArray().Select(entry => entry!["code"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Equal(9, summary["entries"]!.AsArray()[0]!["count"]!.GetValue<int>());
        Assert.All(summary["entries"]!.AsArray(), entry => Assert.False(entry!.AsObject().ContainsKey("distinctMessages")));
    }

    [Fact]
    public void CrashSummary_IsAbsentWithoutGroups_AndRawModeHasNone()
    {
        var empty = JsonNode.Parse(SystemCrashFormatting.Format(CrashSnapshot([]), new CrashesQuery(EvidenceMode.Aggregate, Now.AddDays(-1), Now, 50)))!.AsObject();
        Assert.False(empty.ContainsKey("groupSummary"));

        var raw = JsonNode.Parse(SystemCrashFormatting.Format(CrashSnapshot([.. Many(CrashKinds.KernelBugcheck, "0x1e", 2)]), new CrashesQuery(EvidenceMode.Raw, Now.AddDays(-1), Now, 50)))!.AsObject();
        Assert.False(raw.ContainsKey("groupSummary"));
    }

    [Fact]
    public void EventSummary_ListsEveryGroupBeforeTheGroups_WithDistinctMessagesAtLeastOne()
    {
        var events = new List<SystemEventRecord>();
        for (var group = 0; group < 12; group++)
        {
            var source = "src" + group.ToString("D2", CultureInfo.InvariantCulture);
            events.AddRange(Repeated(source, "same", 2, 1 + group));
            events.AddRange(Repeated(source, "other " + group, 1, 20 + group));
        }

        var json = AggregateOf(events, limit: 3);

        var keys = json.Select(pair => pair.Key).ToList();
        Assert.True(keys.IndexOf("groupSummary") < keys.IndexOf("groups"));
        Assert.Equal(3, json["groups"]!.AsArray().Count);
        var summary = json["groupSummary"]!.AsObject();
        Assert.Equal(12, summary["observedEntries"]!.GetValue<int>());
        Assert.Equal(12, summary["listedEntries"]!.GetValue<int>());
        Assert.Equal(0, summary["omittedEntries"]!.GetValue<int>());
        Assert.All(summary["entries"]!.AsArray(), entry =>
        {
            Assert.Equal(2, entry!["distinctMessages"]!.GetValue<int>());
            Assert.Equal(3, entry["count"]!.GetValue<int>());
            Assert.NotNull(entry["firstSeenUtc"]);
            Assert.NotNull(entry["lastSeenUtc"]);
        });
    }

    [Fact]
    public void EventSummary_WhenTheBudgetCannotListEveryGroup_DeclaresWhatIsOmitted_AtEveryBudget()
    {
        var events = Enumerable.Range(0, 80)
            .SelectMany(group => Repeated("source-" + group.ToString("D3", CultureInfo.InvariantCulture), "message", 1 + (group % 5), 1 + (group % 50)))
            .ToArray();
        var sawOmission = false;

        for (var budget = SystemEventsLimits.MinimumOutputBytes; budget <= 24_000; budget += 331)
        {
            var output = SystemEventFormatting.Format(EventSnapshot(events), DayQuery, EvidenceMode.Aggregate, 500, budget);
            Assert.True(Encoding.UTF8.GetByteCount(output) <= budget, $"Budget {budget}: {Encoding.UTF8.GetByteCount(output)} bytes.");
            var json = JsonNode.Parse(output)!.AsObject();
            if (json["groupSummary"] is not JsonObject summary)
            {
                // The summary leaves the result only when the budget is gone, and then every group is gone too.
                Assert.Empty(json["groups"]!.AsArray());
                continue;
            }

            var entries = summary["entries"]!.AsArray();
            var observed = summary["observedEntries"]!.GetValue<int>();
            Assert.Equal(80, observed);
            Assert.Equal(entries.Count, summary["listedEntries"]!.GetValue<int>());
            Assert.Equal(observed - entries.Count, summary["omittedEntries"]!.GetValue<int>());
            Assert.Equal(events.Length - entries.Sum(entry => entry!["count"]!.GetValue<int>()), summary["omittedCount"]!.GetValue<int>());
            sawOmission |= entries.Count < observed;
            if (json["groups"]!.AsArray().Count < json["observedGroups"]!.GetValue<int>())
            {
                Assert.True(json["truncated"]!.GetValue<bool>());
            }
        }

        Assert.True(sawOmission, "The sweep must cross the budget at which the summary cannot list every group.");
    }

    // ---- slice B: selection facts ----

    [Fact]
    public void EventSelection_WithoutFilters_StatesNone_AndIsNeverAbsent()
    {
        var facts = SystemEventFacts.WithMetadata(EventSnapshot([.. Repeated("s", "m", 1, 1)]), DayQuery);

        var applied = Assert.Single(facts, fact => fact.Type == SystemEvidenceMetadata.EventsSelectionType);
        Assert.Equal(SystemEvidenceMetadata.AppliedKey, applied.Key);
        Assert.Equal("min-severity=none;text-filter=none;source-filter=none;event-id-filter=none;channel=none;excluded-sources=0", applied.Value.GetValue<string>());
    }

    [Fact]
    public void EventSelection_ReflectsEachFilterActuallyApplied_AndNeverCopiesFilterText()
    {
        var snapshot = EventSnapshot([.. Repeated("Contoso-Source", "m", 1, 1)]);

        string Applied(SystemEventQuery query) => Assert.Single(
            SystemEventFacts.WithMetadata(snapshot, query), fact => fact.Type == SystemEvidenceMetadata.EventsSelectionType).Value.GetValue<string>();

        Assert.Contains("min-severity=error;", Applied(DayQuery with { MinSeverity = SystemEventSeverity.Error }), StringComparison.Ordinal);
        Assert.Contains("text-filter=present;", Applied(DayQuery with { Text = "SECRET-TEXT" }), StringComparison.Ordinal);
        Assert.Contains("source-filter=present;", Applied(DayQuery with { Source = "Contoso-Source" }), StringComparison.Ordinal);
        Assert.Contains("event-id-filter=present;", Applied(DayQuery with { EventId = "4624" }), StringComparison.Ordinal);
        Assert.Contains("channel=System;", Applied(DayQuery with { Channel = "System" }), StringComparison.Ordinal);
        Assert.EndsWith("excluded-sources=2", Applied(DayQuery with { ExcludeSources = ["a", "b"] }), StringComparison.Ordinal);

        var everything = DayQuery with
        {
            MinSeverity = SystemEventSeverity.Warning,
            Text = "SECRET-TEXT",
            Source = "Contoso-Source",
            EventId = "4624",
            Channel = new string('c', 100),
            ExcludeSources = ["Exclude-Me"],
        };
        var serialized = JsonSerializer.Serialize(SystemEventFacts.WithMetadata(snapshot, everything));
        Assert.DoesNotContain("SECRET-TEXT", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Exclude-Me", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("4624", serialized, StringComparison.Ordinal);
        Assert.Contains("channel=present;", serialized, StringComparison.Ordinal); // too long to copy whole, and never cut
    }

    [Fact]
    public async Task CrashSelection_StatesTheWindowApplied()
    {
        var result = await new CrashesTool(CrashSnapshot([.. Many(CrashKinds.KernelBugcheck, "0x1e", 1)])).ExecuteAsync(ToolArguments.Empty);

        var window = Assert.Single(result.Facts, fact => fact.Type == SystemEvidenceMetadata.CrashesSelectionType);
        Assert.Equal(("window-minutes", ToolParameterType.Integer, 1440), (window.Key, window.ValueType, window.Value.GetValue<int>()));
    }

    // ---- slice B: coverage facts equal the coverage fields ----

    [Theory]
    [InlineData(-400, "complete")]
    [InlineData(-1, "partial")]
    public void EventCoverageFacts_EqualTheCoverageFieldsOfTheResult(int storeOldestDaysAgo, string expectedState)
    {
        var snapshot = EventSnapshotWithStore(Now.AddDays(storeOldestDaysAgo));
        var query = DayQuery with { FromUtc = Now.AddDays(-2) };

        var json = JsonNode.Parse(SystemEventFormatting.Format(snapshot, query, EvidenceMode.Aggregate, 50, 32_768))!.AsObject();
        var facts = SystemEventFacts.WithMetadata(snapshot, query)
            .Where(fact => fact.Type == SystemEvidenceMetadata.EventsCoverageType)
            .ToDictionary(fact => fact.Key, fact => fact.Value.GetValue<string>());

        var coverage = json["coverage"]!.AsObject();
        Assert.Equal(coverage["state"]!.GetValue<string>(), facts[SystemEvidenceMetadata.CoverageStateKey]);
        var store = coverage["stores"]!.AsArray().Single()!.AsObject();
        Assert.Equal(store["oldestAvailableUtc"]!.GetValue<string>(), facts[SystemEvidenceMetadata.OldestRetainedKeyPrefix + store["name"]!.GetValue<string>()]);
        Assert.Equal(expectedState, facts[SystemEvidenceMetadata.CoverageStateKey]);
    }

    [Fact]
    public async Task CrashCoverageFacts_EqualTheCoverageFieldsOfTheResult()
    {
        var result = await new CrashesTool(CrashSnapshot([.. Many(CrashKinds.KernelBugcheck, "0x1e", 1)])).ExecuteAsync(ToolArguments.Empty);

        var json = JsonNode.Parse(result.Output!)!.AsObject();
        var facts = result.Facts
            .Where(fact => fact.Type == SystemEvidenceMetadata.CrashesCoverageType)
            .ToDictionary(fact => fact.Key, fact => fact.Value.GetValue<string>());
        var coverage = json["coverage"]!.AsObject();
        Assert.Equal(coverage["state"]!.GetValue<string>(), facts[SystemEvidenceMetadata.CoverageStateKey]);
        var store = coverage["stores"]!.AsArray().Single()!.AsObject();
        Assert.Equal(store["oldestAvailableUtc"]!.GetValue<string>(), facts[SystemEvidenceMetadata.OldestRetainedKeyPrefix + store["name"]!.GetValue<string>()]);
    }

    [Fact]
    public void Coverage_WithMoreStoresThanTheFactsListed_CountsTheOnesWithoutAFact()
    {
        var stores = Enumerable.Range(0, 5)
            .Select(index => new CoverageStore("windows.channel.C" + index, CoverageBasis.EventLog, Now.AddDays(-30 - index), null, ["windows.channel.C" + index]))
            .ToArray();
        var snapshot = new SystemEventSnapshot([.. Repeated("s", "m", 1, 1)],
            [.. stores.Select(store => new InventorySourceResult(store.Name, InventorySourceStatus.Available, null))]) { Stores = stores };

        var facts = SystemEventFacts.WithMetadata(snapshot, DayQuery).Where(fact => fact.Type == SystemEvidenceMetadata.EventsCoverageType).ToArray();

        Assert.Equal(SystemEvidenceMetadata.MaximumStoreFacts, facts.Count(fact => fact.Key.StartsWith(SystemEvidenceMetadata.OldestRetainedKeyPrefix, StringComparison.Ordinal)));
        Assert.Equal(3, Assert.Single(facts, fact => fact.Key == SystemEvidenceMetadata.StoresOmittedKey).Value.GetValue<int>());
    }

    // ---- the whole is within the runtime's bounds ----

    [Fact]
    public void EventFacts_WithManyPairs_StayWithinTheRuntimeBounds_AndCountWhatIsNotListed()
    {
        var events = Enumerable.Range(0, 40)
            .SelectMany(index => Repeated("source-" + index.ToString("D2", CultureInfo.InvariantCulture), "m", 1 + index, 1 + index))
            .ToArray();

        var facts = SystemEventFacts.WithMetadata(EventSnapshot(events), DayQuery with { Text = "m" });

        AssertWithinTheRuntimeBounds(facts);
        var listed = facts.Count(fact => fact.Type == SystemEventFacts.MatchedCountType);
        Assert.Equal(40 - listed, Assert.Single(facts, fact => fact.Type == SystemEventFacts.UnlistedGroupsType).Value.GetValue<int>());
        Assert.Contains(facts, fact => fact.Type == SystemEvidenceMetadata.EventsSelectionType);
        Assert.Contains(facts, fact => fact.Type == SystemEvidenceMetadata.EventsCoverageType && fact.Key == SystemEvidenceMetadata.CoverageStateKey);
    }

    [Fact]
    public void ManifestOfEvents_SaysThatASeverityFloorExcludesWarnings()
    {
        var minSeverity = SystemToolManifests.Events("test").Parameters.Single(parameter => parameter.Name == "minSeverity");

        Assert.Contains("warning-level", minSeverity.Description, StringComparison.Ordinal);
    }

    private static void AssertWithinTheRuntimeBounds(IReadOnlyList<EvidenceFact> facts)
    {
        Assert.True(facts.Count <= EvidenceFactBounds.MaximumFacts, $"{facts.Count} facts.");
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(facts).Length <= EvidenceFactBounds.MaximumSerializedBytes);
        Assert.All(facts, fact => Assert.True(fact.Key.Length <= EvidenceFactBounds.MaximumIdentifierCharacters));
        Assert.Equal(facts.Count, facts.Select(fact => (fact.Type, fact.Key)).Distinct().Count());
    }

    private static JsonObject AggregateOf(IReadOnlyList<SystemEventRecord> events, int limit) =>
        JsonNode.Parse(SystemEventFormatting.Format(EventSnapshot(events), DayQuery, EvidenceMode.Aggregate, limit, 32_768))!.AsObject();

    private static SystemEventRecord[] Repeated(string source, string message, int count, int firstMinute) =>
        [.. Enumerable.Range(0, count).Select(index => new SystemEventRecord(Now.AddMinutes(-(firstMinute + index)), SystemEventSeverity.Warning, source, "19", "System", message, null, null))];

    private static SystemEventSnapshot EventSnapshot(IReadOnlyList<SystemEventRecord> events) =>
        new(events, [new InventorySourceResult("windows.channel.System", InventorySourceStatus.Available, null)])
        {
            Stores = [new CoverageStore("windows.channel.System", CoverageBasis.EventLog, Now.AddDays(-400), null, ["windows.channel.System"])],
        };

    private static SystemEventSnapshot EventSnapshotWithStore(DateTimeOffset oldest) =>
        new([.. Repeated("s", "m", 2, 1)], [new InventorySourceResult("windows.channel.System", InventorySourceStatus.Available, null)])
        {
            Stores = [new CoverageStore("windows.channel.System", CoverageBasis.EventLog, oldest, null, ["windows.channel.System"])],
        };

    private static IEnumerable<CrashEvidence> Many(string kind, string? code, int count) =>
        Enumerable.Range(0, count).Select(index => new CrashEvidence(Now.AddMinutes(-(index + 1)), EvidenceTimestampKind.Occurred, "wer", kind)
        {
            BugcheckCode = kind == CrashKinds.KernelBugcheck ? code : null,
            LiveDumpCode = kind == CrashKinds.KernelLiveDump ? code : null,
            Process = kind == CrashKinds.ApplicationCrash ? "app.exe" : null,
            EvidenceSources = ["wer"],
        });

    private static CrashEvidenceSnapshot CrashSnapshot(IEnumerable<CrashEvidence> items) =>
        new([.. items], [new MaintenanceSource("wer", InventorySourceStatus.Available)])
        {
            Stores = [new CoverageStore("store.wer", CoverageBasis.EventLog, Now.AddDays(-400), 20_971_520, ["wer"])],
        };

    private sealed class CrashesTool(CrashEvidenceSnapshot snapshot) : SystemCrashesToolBase("test", new FixedClock())
    {
        protected override Task<CrashEvidenceSnapshot> CollectAsync(CrashesQuery query, CancellationToken ct) => Task.FromResult(snapshot);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
