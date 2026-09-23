// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Conformance;

namespace bOps.Packages.System.Core.Tests;

public sealed class SystemMaintenanceContractTests
{
    private static ToolArguments Args(Action<JsonObject>? set = null) { var json = new JsonObject(); set?.Invoke(json); return ToolArguments.FromJson(json); }
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static MaintenanceSnapshot<T> Available<T>(params T[] items) => new(items, [new("local", InventorySourceStatus.Available)]);
    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();

    [Fact]
    public void Args_DefaultsAndMaximumBounds_AreSharedBoundaryContracts()
    {
        Assert.True(SystemMaintenanceArguments.TryReadUpdates(Args(), out var u, out _)); Assert.Equal("all", u.Kind); Assert.Equal(200, u.Limit);
        Assert.True(SystemMaintenanceArguments.TryReadHistory(Args(), out var h, out _)); Assert.Equal(30, h.SinceDays); Assert.Equal(200, h.Limit);
        Assert.True(SystemMaintenanceArguments.TryReadCrashes(Args(), out var c, out _)); Assert.Equal(1440, c.SinceMinutes); Assert.Equal(100, c.Limit);
        Assert.True(SystemMaintenanceArguments.TryReadDrivers(Args(), out var d, out _)); Assert.Equal(300, d.Limit);
        Assert.True(SystemMaintenanceArguments.TryReadUpdates(Args(j => j["limit"] = 2000), out u, out _)); Assert.Equal(2000, u.Limit);
        Assert.True(SystemMaintenanceArguments.TryReadHistory(Args(j => { j["sinceDays"] = 365; j["limit"] = 2000; }), out h, out _)); Assert.Equal(365, h.SinceDays); Assert.Equal(2000, h.Limit);
        Assert.True(SystemMaintenanceArguments.TryReadCrashes(Args(j => { j["sinceMinutes"] = 10080; j["limit"] = 1000; }), out c, out _)); Assert.Equal(10080, c.SinceMinutes); Assert.Equal(1000, c.Limit);
        Assert.True(SystemMaintenanceArguments.TryReadDrivers(Args(j => j["limit"] = 3000), out d, out _)); Assert.Equal(3000, d.Limit);
    }

    [Fact]
    public void InvalidKindAndAboveMaximumBounds_AreRejected()
    {
        Assert.False(SystemMaintenanceArguments.TryReadUpdates(Args(j => j["kind"] = "maybe"), out _, out _));
        Assert.False(SystemMaintenanceArguments.TryReadUpdates(Args(j => j["limit"] = 2001), out _, out _));
        Assert.False(SystemMaintenanceArguments.TryReadHistory(Args(j => j["sinceDays"] = 366), out _, out _));
        Assert.False(SystemMaintenanceArguments.TryReadCrashes(Args(j => j["sinceMinutes"] = 10081), out _, out _));
        Assert.False(SystemMaintenanceArguments.TryReadDrivers(Args(j => j["limit"] = 3001), out _, out _));
    }

    [Fact]
    public void Updates_SerializedContractOrdersRowsPreservesUnknownAndStaleCatalogEvidence()
    {
        var snapshot = Available(
            new UpdateRecord("z", "Zulu", null, null, null, null, "local", null),
            new UpdateRecord("a", "Alpha", "1", "2", "security", true, "local", Now)) with { CatalogAge = TimeSpan.FromHours(50), Warnings = ["catalog is stale"] };
        var json = Json(SystemMaintenanceFormatting.Updates(snapshot, 10));
        SystemToolConformance.AssertMaintenanceEnvelope(json.ToJsonString(), ["id", "name", "currentVersion", "availableVersion", "kind", "rebootMayBeRequired", "source", "publishedUtc"], 2000);
        Assert.Equal("complete", json["status"]!.GetValue<string>()); Assert.Equal(180000, json["catalogAge"]!.GetValue<double>());
        Assert.Equal("catalog is stale", json["warnings"]![0]!.GetValue<string>());
        var rows = json["items"]!.AsArray(); Assert.Equal("Alpha", rows[0]! ["name"]!.GetValue<string>());
        Assert.Null(rows[1]!["currentVersion"]); Assert.Null(rows[1]!["availableVersion"]); Assert.Null(rows[1]!["kind"]); Assert.Null(rows[1]!["rebootMayBeRequired"]); Assert.Null(rows[1]!["publishedUtc"]);
    }

    [Fact]
    public void Updates_TruncationIsSerializedAsIncomplete_AndOrderIsIndependentOfInput()
    {
        var rows = new[] { new UpdateRecord("2", "Beta", null, null, "other", null, "src", null), new UpdateRecord("1", "Alpha", null, null, "all", null, "src", null) };
        var first = Json(SystemMaintenanceFormatting.Updates(Available(rows), 1));
        var second = Json(SystemMaintenanceFormatting.Updates(Available(rows.Reverse().ToArray()), 1));
        Assert.False(first["complete"]!.GetValue<bool>()); Assert.True(first["truncated"]!.GetValue<bool>());
        Assert.Equal(first["items"]!.ToJsonString(), second["items"]!.ToJsonString());
    }

    [Fact]
    public void UpdateHistory_UnavailableEmptyIsNotCompleteAndResultsAreNormalized()
    {
        var unavailable = new MaintenanceSnapshot<UpdateHistoryRecord>([], [new("history", InventorySourceStatus.Unavailable, "rotated")]);
        var empty = Json(SystemMaintenanceFormatting.History(unavailable, 200)); Assert.False(empty["complete"]!.GetValue<bool>()); Assert.Equal("unavailable", empty["status"]!.GetValue<string>());
        var history = Available(new UpdateHistoryRecord(Now, "id", "pkg", null, "SUCCESS", "logs"), new UpdateHistoryRecord(Now, "id2", "pkg2", "1", "maybe", "logs"));
        var rows = Json(SystemMaintenanceFormatting.History(history, 200))["items"]!.AsArray();
        Assert.Equal("success", rows[0]!["result"]!.GetValue<string>()); Assert.Equal("unknown", rows[1]!["result"]!.GetValue<string>());
    }

    [Fact]
    public void Crashes_DumpPathIsOnlySerializedMetadata_UnavailableAndTruncatedAreIncomplete()
    {
        var crash = new CrashRecord(Now, "app", 5, "segfault", "C:/dumps/a.dmp", "e1", "crashed", "wer");
        var json = Json(SystemMaintenanceFormatting.Crashes(Available(crash), 1));
        Assert.Equal("C:/dumps/a.dmp", json["items"]![0]!["dumpPath"]!.GetValue<string>());
        Assert.False(Json(SystemMaintenanceFormatting.Crashes(new([], [new("backend", InventorySourceStatus.Unavailable)]), 100))["complete"]!.GetValue<bool>());
        Assert.False(Json(SystemMaintenanceFormatting.Crashes(new([crash, crash with { Process = "other" }], [new("backend", InventorySourceStatus.Available)]), 1))["complete"]!.GetValue<bool>());
    }

    [Fact]
    public void Drivers_PreserveUnknownMetadataAndPartialAndTruncatedEvidence()
    {
        var row = new DriverRecord("mod", null, null, null, "unknown", null, null, "proc");
        var partial = new MaintenanceSnapshot<DriverRecord>([row], [new("proc", InventorySourceStatus.Partial, "one metadata lookup failed")], ["one metadata lookup failed"]);
        var json = Json(SystemMaintenanceFormatting.Drivers(partial, 300));
        Assert.Equal("partial", json["status"]!.GetValue<string>()); Assert.False(json["complete"]!.GetValue<bool>());
        Assert.Null(json["items"]![0]!["pathOrModule"]); Assert.Null(json["items"]![0]!["version"]); Assert.Null(json["items"]![0]!["loaded"]);
        Assert.False(Json(SystemMaintenanceFormatting.Drivers(Available(row, row with { Name = "other" }), 1))["complete"]!.GetValue<bool>());
    }

    [Fact]
    public void AllMaintenanceManifestsAreReadOnly()
    {
        foreach (var manifest in new[] { SystemToolManifests.Updates("test"), SystemToolManifests.UpdateHistory("test"), SystemToolManifests.Crashes("test"), SystemToolManifests.Drivers("test") })
            Assert.Equal(RiskLevel.Read, manifest.Risk);
    }
}
