// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Storage.Core.Tests;

/// <summary>
/// ADR-0052 AF-04: the bounded <c>storage.health</c> facts. The shared contract carries each platform's own health vocabulary
/// (Windows: healthy/warning/unhealthy/unknown, Linux: healthy/critical/unknown) and never fabricates a category that was not observed.
/// </summary>
public sealed class StorageHealthFactsTests
{
    [Fact]
    public async Task WindowsVocabulary_CountsEachObservedValue_AndListsDevicesNeedingFollowUp()
    {
        var tool = new HealthTool(Row(@"\\.\PHYSICALDRIVE0", "healthy"), Row(@"\\.\PHYSICALDRIVE1", "warning"),
            Row(@"\\.\PHYSICALDRIVE2", "unhealthy"), Row(@"\\.\PHYSICALDRIVE3", "unknown"), Row(@"\\.\PHYSICALDRIVE4", "healthy"));

        var result = await tool.ExecuteAsync(ToolArguments.Empty);

        var facts = result.Facts.ToDictionary(fact => (fact.Type, fact.Key), fact => fact.Value);
        Assert.Equal(5, facts[(StorageHealthFacts.DevicesObservedType, "all")]!.GetValue<int>());
        Assert.Equal(2, facts[(StorageHealthFacts.DeviceCountType, "healthy")]!.GetValue<int>());
        Assert.Equal(1, facts[(StorageHealthFacts.DeviceCountType, "warning")]!.GetValue<int>());
        Assert.Equal(1, facts[(StorageHealthFacts.DeviceCountType, "unhealthy")]!.GetValue<int>());
        Assert.Equal(1, facts[(StorageHealthFacts.DeviceCountType, "unknown")]!.GetValue<int>());
        Assert.DoesNotContain((StorageHealthFacts.DeviceCountType, "critical"), facts.Keys);
        Assert.Equal(
            [(@"\\.\PHYSICALDRIVE2", "unhealthy"), (@"\\.\PHYSICALDRIVE1", "warning")],
            result.Facts.Where(fact => fact.Type == StorageHealthFacts.AttentionDeviceType)
                .Select(fact => (fact.Key, fact.Value.GetValue<string>())));
        AssertBoundedAndRoundTrips(result.Facts);
    }

    [Fact]
    public async Task LinuxVocabulary_UsesCritical_WithoutInventingWindowsCategories()
    {
        var tool = new HealthTool(Row("/dev/sda", "healthy"), Row("/dev/sdb", "critical"), Row("/dev/sdc", "unknown"));

        var result = await tool.ExecuteAsync(ToolArguments.Empty);

        Assert.Equal(["critical", "healthy", "unknown"],
            result.Facts.Where(fact => fact.Type == StorageHealthFacts.DeviceCountType).Select(fact => fact.Key));
        var attention = Assert.Single(result.Facts, fact => fact.Type == StorageHealthFacts.AttentionDeviceType);
        Assert.Equal(("/dev/sdb", "critical"), (attention.Key, attention.Value.GetValue<string>()));
    }

    [Fact]
    public async Task UnavailableProvider_UnknownRows_NeverBecomeAHealthyOrAbsenceFact()
    {
        var tool = new HealthTool(Row("disk0", "unknown"), Row("disk1", "unknown"));

        var result = await tool.ExecuteAsync(ToolArguments.Empty);

        Assert.Equal(
            [(StorageHealthFacts.DevicesObservedType, "all"), (StorageHealthFacts.DeviceCountType, "unknown")],
            result.Facts.Select(fact => (fact.Type, fact.Key)));
        Assert.DoesNotContain(result.Facts, fact => fact.Key is "healthy");
        Assert.DoesNotContain(result.Facts, fact => fact.Type == StorageHealthFacts.AttentionDeviceType);
    }

    [Fact]
    public async Task PartialDeviceEvidence_KeepsTheFacts_UnderAPartialResult()
    {
        var partial = Row("disk0", "healthy") with { Partial = true, PartialReason = "reliability counters unavailable" };

        var result = await new HealthTool(partial).ExecuteAsync(ToolArguments.Empty);

        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
        Assert.Equal(1, Assert.Single(result.Facts, fact => fact.Key == "healthy").Value.GetValue<int>());
    }

    [Fact]
    public async Task NoDevices_OrADeviceFilterThatMatchesNothing_EmitNoFacts()
    {
        Assert.Empty((await new HealthTool().ExecuteAsync(ToolArguments.Empty)).Facts);

        var filtered = await new HealthTool(Row("disk0", "critical"))
            .ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["device"] = "other" }));
        Assert.Empty(filtered.Facts);
    }

    [Fact]
    public void ManyDevices_StayWithinTheFactBounds_AndAreIndependentOfTheRowLimit()
    {
        var rows = Enumerable.Range(0, 400).Select(index => Row("disk" + index, index % 3 == 0 ? "critical" : "healthy")).ToArray();

        var facts = StorageHealthFacts.From(rows, null);

        Assert.Equal(400, facts.Single(fact => fact.Type == StorageHealthFacts.DevicesObservedType).Value.GetValue<int>());
        Assert.Equal(StorageHealthFacts.MaximumAttentionDevices, facts.Count(fact => fact.Type == StorageHealthFacts.AttentionDeviceType));
        AssertBoundedAndRoundTrips(facts);
        var raw = StorageFormatting.Health(rows, null, StorageLimits.MaximumRows, StorageLimits.MaximumOutputBytes);
        Assert.True(Encoding.UTF8.GetByteCount(raw) > 4 * JsonSerializer.SerializeToUtf8Bytes(facts).Length);
    }

    [Fact]
    public void OverlongIdentities_AreSkippedNeverCut()
    {
        var facts = StorageHealthFacts.From([Row(new string('d', 200), "critical"), Row("disk1", "critical")], null);

        Assert.Equal(["disk1"], facts.Where(fact => fact.Type == StorageHealthFacts.AttentionDeviceType).Select(fact => fact.Key));
    }

    private static void AssertBoundedAndRoundTrips(IReadOnlyList<EvidenceFact> facts)
    {
        Assert.InRange(facts.Count, 1, 16);
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(facts).Length, 1, 4096);
        Assert.Equal(facts.Count, facts.Select(fact => (fact.Type, fact.Key)).Distinct().Count());
        var again = JsonSerializer.Deserialize<EvidenceFact[]>(JsonSerializer.Serialize(facts))!;
        Assert.Equal(JsonSerializer.Serialize(facts), JsonSerializer.Serialize(again));
    }

    private static StorageHealth Row(string device, string health) =>
        new(device, health, null, null, null, null, null, null, false, "test", null);

    private sealed class HealthTool(params StorageHealth[] rows) : StorageHealthToolBase("test")
    {
        protected override Task<IReadOnlyList<StorageHealth>> CollectAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StorageHealth>>(rows);
    }
}
