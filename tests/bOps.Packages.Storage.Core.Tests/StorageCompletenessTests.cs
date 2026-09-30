// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Storage.Core.Tests;

/// <summary>
/// HARDEN-6 / ADR-0022: the storage tools state the completeness of their evidence as a typed value derived from the fields their
/// formatter emitted (<c>truncated</c> and, for <c>storage.health</c>, the per-device <c>partial</c>), without replacing them.
/// </summary>
public sealed class StorageCompletenessTests
{
    [Fact]
    public async Task Health_ADeviceWithPartialEvidence_YieldsPartial_AndKeepsTheLegacyPartialFields()
    {
        var devices = new[]
        {
            Health("disk0"),
            Health("disk1") with { Partial = true, PartialReason = "SMART attributes not readable" },
        };

        var result = await new HealthTool(devices).ExecuteAsync(ToolArguments.Empty);

        Assert.True(result.Succeeded);
        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
        var rows = JsonNode.Parse(result.Output!)!["devices"]!.AsArray();
        Assert.True(rows[1]!["partial"]!.GetValue<bool>());
        Assert.Equal("SMART attributes not readable", rows[1]!["partialReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Health_EveryDeviceFullyRead_YieldsComplete()
    {
        var result = await new HealthTool([Health("disk0"), Health("disk1")]).ExecuteAsync(ToolArguments.Empty);

        Assert.Equal(ToolResultCompleteness.Complete, result.Completeness);
    }

    [Fact]
    public async Task Disks_CutByTheRowLimit_YieldsPartial_AndTheLegacyTruncatedFlagAgrees()
    {
        var result = await new DisksTool([Disk("d0"), Disk("d1"), Disk("d2")]).ExecuteAsync(
            ToolArguments.FromJson(new JsonObject { ["limit"] = 2 }));

        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
        Assert.True(JsonNode.Parse(result.Output!)!["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Disks_NothingCut_YieldsComplete_AndAnEmptyInventoryIsStillAnAnswer()
    {
        var full = await new DisksTool([Disk("d0")]).ExecuteAsync(ToolArguments.Empty);
        var empty = await new DisksTool([]).ExecuteAsync(ToolArguments.Empty);

        Assert.Equal(ToolResultCompleteness.Complete, full.Completeness);
        Assert.Equal(ToolResultCompleteness.Complete, empty.Completeness);
    }

    [Fact]
    public async Task Disks_CutByTheByteBound_YieldsPartial()
    {
        var disks = Enumerable.Range(0, 40).Select(index => Disk($"disk-{index}") with { Model = new string('m', 200) }).ToArray();

        var result = await new DisksTool(disks).ExecuteAsync(
            ToolArguments.FromJson(new JsonObject { ["limit"] = 40, ["maxOutputBytes"] = StorageLimits.MinimumOutputBytes }));

        Assert.Equal(ToolResultCompleteness.Partial, result.Completeness);
    }

    [Fact]
    public async Task OutOfRangeArguments_AreAValidationFailure_WithNoCompleteness()
    {
        var result = await new DisksTool([]).ExecuteAsync(ToolArguments.FromJson(new JsonObject { ["limit"] = 0 }));

        Assert.False(result.Succeeded);
        Assert.Equal(ToolFailureKind.Validation, result.FailureKind);
        Assert.Equal(ToolResultCompleteness.Unspecified, result.Completeness);
    }

    [Theory]
    [InlineData("""{"truncated":false,"devices":[{"partial":false}]}""", ToolResultCompleteness.Complete)]
    [InlineData("""{"truncated":false,"devices":[{"partial":true}]}""", ToolResultCompleteness.Partial)]
    [InlineData("""{"truncated":true,"devices":[]}""", ToolResultCompleteness.Partial)]
    [InlineData("""{"devices":[]}""", ToolResultCompleteness.Unspecified)]
    [InlineData("not json", ToolResultCompleteness.Unspecified)]
    [InlineData("", ToolResultCompleteness.Unspecified)]
    public void FromOutput_MapsTheCompletenessFieldsDeterministically(string output, ToolResultCompleteness expected) =>
        Assert.Equal(expected, EvidenceCompleteness.FromOutput(output));

    private static StorageHealth Health(string device) => new(device, "healthy", "OK", 35, 1000, 0, 0, 1, true, "smart", null);

    private static StorageDisk Disk(string id) => new(id, id, "model", "serial", "nvme", "ssd", 1_000_000, 512, 4096, false, false, false);

    private sealed class HealthTool(IReadOnlyList<StorageHealth> devices) : StorageHealthToolBase("test")
    {
        protected override Task<IReadOnlyList<StorageHealth>> CollectAsync(CancellationToken ct) => Task.FromResult(devices);
    }

    private sealed class DisksTool(IReadOnlyList<StorageDisk> disks) : StorageDisksToolBase("test")
    {
        protected override Task<IReadOnlyList<StorageDisk>> CollectAsync(CancellationToken ct) => Task.FromResult(disks);
    }
}
