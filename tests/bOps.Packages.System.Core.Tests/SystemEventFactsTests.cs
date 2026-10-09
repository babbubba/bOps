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
