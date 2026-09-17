// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.System.Core.Tests;

public sealed class SystemInventoryContractTests
{
    [Fact]
    public void Applications_AreDeduplicatedAndSortedDeterministically()
    {
        var snapshot = new InventorySnapshot<ApplicationInventoryItem>(
            [
                new("app-b", "Zulu", "2", null, "source-b"),
                new("app-a", "Alpha", "1", "Publisher", "source-a"),
                new("APP-A", "Alpha duplicate", "1", "Publisher", "source-b"),
            ],
            [new InventorySourceResult("source-a", InventorySourceStatus.Available, null)]);

        var output = SystemInventoryFormatting.FormatApplications(snapshot, limit: 100, maxOutputBytes: 32_768);
        var json = JsonNode.Parse(output)!.AsObject();
        var items = json["items"]!.AsArray();

        Assert.Equal(2, items.Count);
        Assert.Equal("app-a", items[0]!["identity"]!.GetValue<string>());
        Assert.Equal("app-b", items[1]!["identity"]!.GetValue<string>());
        Assert.Equal(2, json["observedItems"]!.GetValue<int>());
        Assert.False(json["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Applications_ApplyItemAndUtf8ByteBounds()
    {
        var items = Enumerable.Range(0, 100)
            .Select(index => new ApplicationInventoryItem(
                $"app-{index:D3}", new string('x', 256) + index, "1", null, "source"))
            .ToArray();
        var snapshot = new InventorySnapshot<ApplicationInventoryItem>(
            items, [new InventorySourceResult("source", InventorySourceStatus.Available, null)]);

        var output = SystemInventoryFormatting.FormatApplications(snapshot, limit: 50, maxOutputBytes: 4_096);
        var json = JsonNode.Parse(output)!.AsObject();

        Assert.True(Encoding.UTF8.GetByteCount(output) <= 4_096);
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.True(json["returnedItems"]!.GetValue<int>() < 50);
    }

    [Fact]
    public void UnavailableInventory_IsDistinctFromAnObservedEmptyInventory()
    {
        var unavailable = new InventorySnapshot<ApplicationInventoryItem>(
            [], [new InventorySourceResult("source", InventorySourceStatus.Unavailable, "permission denied")]);
        var empty = new InventorySnapshot<ApplicationInventoryItem>(
            [], [new InventorySourceResult("source", InventorySourceStatus.Available, null)]);

        var unavailableJson = JsonNode.Parse(
            SystemInventoryFormatting.FormatApplications(unavailable, 100, 32_768))!.AsObject();
        var emptyJson = JsonNode.Parse(
            SystemInventoryFormatting.FormatApplications(empty, 100, 32_768))!.AsObject();

        Assert.Equal("unavailable", unavailableJson["status"]!.GetValue<string>());
        Assert.Equal("complete", emptyJson["status"]!.GetValue<string>());
    }

    [Fact]
    public void DeviceOptionalFields_AreSerializedAsExplicitNulls()
    {
        var snapshot = new InventorySnapshot<DeviceInventoryItem>(
            [new DeviceInventoryItem("device-1", "other", "Unknown device", null, null, null, "source")],
            [new InventorySourceResult("source", InventorySourceStatus.Available, null)]);

        var output = SystemInventoryFormatting.FormatDevices(snapshot, 100, 32_768);
        var item = JsonNode.Parse(output)!["items"]![0]!.AsObject();

        Assert.True(item.ContainsKey("vendor"));
        Assert.True(item.ContainsKey("model"));
        Assert.True(item.ContainsKey("status"));
        Assert.Null(item["vendor"]);
        Assert.Null(item["model"]);
        Assert.Null(item["status"]);
    }

    [Fact]
    public void SystemInfo_HardwareModelIsAdditiveAndHasExplicitUnknownFallback()
    {
        var known = SystemToolFormatting.Format(
            new SystemInfoResult("Test OS", "test-host", TimeSpan.FromHours(1), "Model X"));
        var unknown = SystemToolFormatting.Format(
            new SystemInfoResult("Test OS", "test-host", TimeSpan.FromHours(1), null));

        Assert.StartsWith("OS: Test OS\nHost: test-host\nUptime:", known, StringComparison.Ordinal);
        Assert.Contains("Hardware model: Model X", known, StringComparison.Ordinal);
        Assert.Contains("Hardware model: unknown", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InventoryTool_PropagatesCallerCancellation()
    {
        var tool = new CancellingApplicationInventoryTool();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tool.ExecuteAsync(ToolArguments.Empty, cancellation.Token));
    }

    [Theory]
    [InlineData("limit", 0)]
    [InlineData("limit", 501)]
    [InlineData("maxOutputBytes", 4_095)]
    [InlineData("maxOutputBytes", 65_537)]
    public async Task InventoryTool_RejectsArgumentsOutsideHardBounds(string name, int value)
    {
        var tool = new CancellingApplicationInventoryTool();
        var arguments = ToolArguments.FromJson(new JsonObject { [name] = value });

        var result = await tool.ExecuteAsync(arguments);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void PartialSource_ProducesPartialEnvelopeStatus()
    {
        var snapshot = new InventorySnapshot<ApplicationInventoryItem>(
            [], [new InventorySourceResult("source", InventorySourceStatus.Partial, "one item unreadable")]);

        var json = JsonNode.Parse(SystemInventoryFormatting.FormatApplications(snapshot, 100, 32_768))!.AsObject();

        Assert.Equal("partial", json["status"]!.GetValue<string>());
        Assert.Equal("partial", json["sources"]![0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void NotApplicableSource_DoesNotDegradeAnAvailableSource()
    {
        var snapshot = new InventorySnapshot<ApplicationInventoryItem>(
            [],
            [
                new InventorySourceResult("supported", InventorySourceStatus.Available, null),
                new InventorySourceResult("absent", InventorySourceStatus.NotApplicable, null),
            ]);

        var json = JsonNode.Parse(SystemInventoryFormatting.FormatApplications(snapshot, 100, 32_768))!.AsObject();

        Assert.Equal("complete", json["status"]!.GetValue<string>());
    }

    private sealed class CancellingApplicationInventoryTool()
        : ApplicationInventoryToolBase("test")
    {
        protected override Task<InventorySnapshot<ApplicationInventoryItem>> CollectAsync(
            int collectionLimit, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new InventorySnapshot<ApplicationInventoryItem>([], []));
        }
    }
}
