// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.System.Core.Tests;

/// <summary>
/// PRE-5 (ADR-0042 PRE-5 amendment §10): <c>system.events</c> owns the projection of its matched records into bounded typed
/// facts — one count per (source, event id) pair, never a zero, never more than the runtime accepts.
/// </summary>
public sealed class SystemEventFactsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static SystemEventRecord Event(int minutesAgo, string source, string? eventId, string message = "m") =>
        new(Now.AddMinutes(-minutesAgo), SystemEventSeverity.Warning, source, eventId, "System", message, null, null);

    private static SystemEventQuery Query(string? source = null) =>
        new(Now.AddMinutes(-60), Now, null, source, null, null, null, SystemEventsLimits.ScanCeiling);

    private static SystemEventSnapshot Snapshot(params SystemEventRecord[] events) =>
        new(events, [new InventorySourceResult("test.source", InventorySourceStatus.Available, null)]);

    [Fact]
    public void MatchedRecords_BecomeOneCountPerSourceAndEventId_OrderedByCountThenNewest()
    {
        var facts = SystemEventFacts.From(Snapshot(
            Event(5, "hw", "19"), Event(6, "hw", "19"), Event(7, "hw", "19"),
            Event(1, "disk", "7"),
            Event(2, "journal-source", null),
            Event(90, "hw", "18")), Query());

        Assert.Equal(
            [("source=hw;eventId=19", 3), ("source=disk;eventId=7", 1), ("source=journal-source", 1)],
            facts.Select(fact => (fact.Key, fact.Value.GetValue<int>())));
        Assert.All(facts, fact =>
        {
            Assert.Equal(SystemEventFacts.MatchedCountType, fact.Type);
            Assert.Equal(ToolParameterType.Integer, fact.ValueType);
        });
    }

    [Fact]
    public void OnlyRecordsMatchingTheQueryCount_AndNoMatchMeansNoFact_NeverAZero()
    {
        var snapshot = Snapshot(Event(5, "hw", "19"), Event(5, "other", "1"));

        Assert.Equal(["source=hw;eventId=19"], SystemEventFacts.From(snapshot, Query("hw")).Select(fact => fact.Key));
        Assert.Empty(SystemEventFacts.From(snapshot, Query("absent")));
    }

    [Fact]
    public void Facts_AreBoundedInCountKeyLengthAndSerializedSize()
    {
        var many = Enumerable.Range(0, 40).Select(index => Event(index % 50, $"source-{index:D2}", "1")).ToList();
        many.Add(Event(1, new string('s', 200), "1"));
        many.Add(Event(1, new string('s', 200), "1"));

        var facts = SystemEventFacts.From(Snapshot([.. many]), Query());

        Assert.Equal(SystemEventFacts.MaximumFacts, facts.Count);
        Assert.All(facts, fact => Assert.True(fact.Key.Length <= SystemEventFacts.MaximumKeyCharacters));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(facts).Length <= SystemEventFacts.MaximumSerializedBytes);
    }

    private static SystemEventRecord Event(int minutesAgo, SystemEventSeverity severity, string source, string eventId) =>
        new(Now.AddMinutes(-minutesAgo), severity, source, eventId, "System", "m", null, null);

    /// <summary>Twenty more frequent groups of the given severity: more than the bound holds.</summary>
    private static List<SystemEventRecord> FrequentNoise(SystemEventSeverity severity) =>
        [.. Enumerable.Range(0, 20).SelectMany(group => Enumerable.Range(0, 5).Select(_ => Event(group + 1, severity, $"noise-{group:D2}", "7036")))];

    [Fact]
    public void ARareSevereGroup_IsListedAheadOfMoreFrequentLessSevereGroups_AndTheRestIsCounted()
    {
        // I4: > 12 groups, the target matched once, every other group more frequent. The rule is generic: the record's own
        // normalized severity ranks first; no source or event id is known by name.
        var events = FrequentNoise(SystemEventSeverity.Information);
        events.Add(Event(30, SystemEventSeverity.Warning, "hw-logger", "19"));

        var facts = SystemEventFacts.From(Snapshot([.. events]), Query());

        Assert.Equal(SystemEventFacts.MaximumFacts, facts.Count);
        Assert.Equal(("source=hw-logger;eventId=19", 1), (facts[0].Key, facts[0].Value.GetValue<int>()));
        var unlisted = facts[^1];
        Assert.Equal((SystemEventFacts.UnlistedGroupsType, SystemEventFacts.UnlistedGroupsKey, 21 - 11),
            (unlisted.Type, unlisted.Key, unlisted.Value.GetValue<int>()));
    }

    [Fact]
    public void ARareGroupOutrankedByMoreThanTheBound_IsNotListed_ButCounted_AndATargetedQueryAlwaysListsIt()
    {
        // The documented residual: a broad call cannot list every group. The omission is explicit, never an absence, and
        // narrowing the query by source or event id (exact filters) makes the group representable.
        var events = FrequentNoise(SystemEventSeverity.Error);
        events.Add(Event(30, SystemEventSeverity.Warning, "hw-logger", "19"));
        var snapshot = Snapshot([.. events]);

        var broad = SystemEventFacts.From(snapshot, Query());
        Assert.DoesNotContain(broad, fact => fact.Key == "source=hw-logger;eventId=19");
        Assert.Equal(21 - 11, broad[^1].Value.GetValue<int>());

        var targeted = SystemEventFacts.From(snapshot, Query("hw-logger"));
        Assert.Equal([("source=hw-logger;eventId=19", 1)], targeted.Select(fact => (fact.Key, fact.Value.GetValue<int>())));
    }

    [Fact]
    public void NonAsciiKeys_AreTrimmedToTheSerializedByteBound()
    {
        var events = Enumerable.Range(0, 12).Select(index => Event(1, $"{index:D2}{new string('é', 100)}", "1")).ToArray();

        var facts = SystemEventFacts.From(Snapshot(events), Query());

        Assert.NotEmpty(facts);
        Assert.True(facts.Count < 12);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(facts).Length <= SystemEventFacts.MaximumSerializedBytes);
    }
}
