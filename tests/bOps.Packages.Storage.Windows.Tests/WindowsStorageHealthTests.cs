// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using bOps.Packages.Storage.Core;

namespace bOps.Packages.Storage.Windows.Tests;

public sealed class WindowsStorageHealthTests
{
    private const string Drive0 = @"\\.\PHYSICALDRIVE0";

    private static readonly IReadOnlyList<StorageDisk> Identities =
        [new StorageDisk(Drive0, "Disk", null, null, null, null, 0, null, null, null, false, false)];

    private static readonly ReliabilityRow Counters = new(35, 70, 12, 3, 4, 9000);
    private static readonly string[] States = ["healthy", "warning", "unhealthy", "unknown", "unsupported"];
    private static readonly string[] Sources = ["MSFT_PhysicalDisk", "Win32_DiskDrive"];

    private static StorageHealthSnapshot Available(int? status, ReliabilityState state = ReliabilityState.Available, ReliabilityRow? reliability = null) =>
        new(StorageHealthProviderState.Available,
            [new PhysicalDiskEvidence(new PhysicalDiskRow("0", "Disk", status, "2"), state, reliability)], null);

    private static StorageHealth Single(StorageHealthSnapshot snapshot) =>
        Assert.Single(WindowsStorageHealthComposer.Compose(Identities, snapshot));

    [Theory]
    [InlineData(0, "healthy")]
    [InlineData(1, "warning")]
    [InlineData(2, "unhealthy")]
    [InlineData(5, "unknown")]
    public void HealthStatus_MapsDocumentedCodes(int code, string expected)
    {
        var row = Single(Available(code, ReliabilityState.Available, Counters));
        Assert.Equal(expected, row.Health);
        Assert.Equal(Drive0, row.Device);
        Assert.Equal("MSFT_PhysicalDisk", row.Source);
        Assert.Equal("2", row.OperationalStatus);
    }

    [Fact]
    public void UnexpectedHealthStatus_IsUnknownWithCode()
    {
        var row = Single(Available(99));
        Assert.Equal("unknown", row.Health);
        Assert.Contains("99", row.Detail);
    }

    [Fact]
    public void MissingHealthStatus_IsUnknownWithDetail()
    {
        var row = Single(Available(null));
        Assert.Equal("unknown", row.Health);
        Assert.Equal("HealthStatus unavailable.", row.Detail);
    }

    [Fact]
    public void UnmatchedPhysicalDisk_UsesPhysicalDriveName()
    {
        var snapshot = new StorageHealthSnapshot(StorageHealthProviderState.Available,
            [new PhysicalDiskEvidence(new PhysicalDiskRow("7", "Other", 0, null), ReliabilityState.Absent, null)], null);
        Assert.Equal(@"\\.\PHYSICALDRIVE7", Assert.Single(WindowsStorageHealthComposer.Compose(Identities, snapshot)).Device);
    }

    [Fact]
    public void ReliabilityCounters_ArePopulatedAndLabelled()
    {
        var row = Single(Available(0, ReliabilityState.Available, Counters));
        Assert.Equal(35, row.TemperatureC);
        Assert.Equal(70, row.TemperatureMaxC);
        Assert.Equal(12, row.WearPercent);
        Assert.Equal(3, row.ReadErrorsTotal);
        Assert.Equal(4, row.WriteErrorsTotal);
        Assert.Equal(9000, row.PowerOnHours);
        Assert.Equal("MSFT_StorageReliabilityCounter", row.ReliabilitySource);
        Assert.False(row.Partial);
        Assert.Null(row.PartialReason);
        Assert.False(row.SmartAvailable);
        Assert.Null(row.MediaErrors);
        Assert.Null(row.ReallocatedSectors);
    }

    [Fact]
    public void ZeroReliabilityValues_ArePreserved()
    {
        var row = Single(Available(0, ReliabilityState.Available, new ReliabilityRow(0, 0, 0, 0, 0, 0)));
        Assert.Equal(0, row.TemperatureC);
        Assert.Equal(0, row.TemperatureMaxC);
        Assert.Equal(0, row.WearPercent);
        Assert.Equal(0, row.ReadErrorsTotal);
        Assert.Equal(0, row.WriteErrorsTotal);
        Assert.Equal(0, row.PowerOnHours);
    }

    [Fact]
    public void ReliabilityFieldsTheDeviceOmits_StayNull()
    {
        var row = Single(Available(0, ReliabilityState.Available, new ReliabilityRow(null, null, null, null, null, 5)));
        Assert.Null(row.TemperatureC);
        Assert.Null(row.WearPercent);
        Assert.Null(row.ReadErrorsTotal);
        Assert.Equal(5, row.PowerOnHours);
        Assert.False(row.Partial);
    }

    [Fact]
    public void ReliabilityAccessDenied_KeepsBaseHealthAndIsPartial()
    {
        var row = Single(Available(0, ReliabilityState.AccessDenied));
        Assert.Equal("healthy", row.Health);
        Assert.Equal("MSFT_PhysicalDisk", row.Source);
        Assert.True(row.Partial);
        Assert.Equal("reliability counters require elevation", row.PartialReason);
        Assert.Null(row.ReliabilitySource);
        Assert.Null(row.TemperatureC);
        Assert.Null(row.WearPercent);
        Assert.Null(row.PowerOnHours);
        Assert.Null(row.ReadErrorsTotal);
        Assert.Null(row.WriteErrorsTotal);
        Assert.Null(row.TemperatureMaxC);
    }

    [Fact]
    public void ReliabilityAbsentForDevice_KeepsHealthWithoutFabricatingCounters()
    {
        var row = Single(Available(1, ReliabilityState.Absent));
        Assert.Equal("warning", row.Health);
        Assert.True(row.Partial);
        Assert.Equal("reliability counters are not reported for this device", row.PartialReason);
        Assert.Null(row.TemperatureC);
        Assert.Null(row.ReliabilitySource);
    }

    [Fact]
    public void ReliabilityFailure_IsPartialNotElevation()
    {
        var row = Single(Available(0, ReliabilityState.Failed));
        Assert.Equal("healthy", row.Health);
        Assert.True(row.Partial);
        Assert.DoesNotContain("elevation", row.PartialReason);
    }

    [Fact]
    public void NamespaceAbsent_IsUnsupportedWithIdentityOnly()
    {
        var row = Single(new StorageHealthSnapshot(StorageHealthProviderState.NamespaceUnsupported, [], null));
        Assert.Equal("unsupported", row.Health);
        Assert.Equal(Drive0, row.Device);
        Assert.Equal("Win32_DiskDrive", row.Source);
        Assert.Contains("namespace", row.Detail);
        Assert.False(row.Partial);
        Assert.Null(row.OperationalStatus);
        Assert.DoesNotContain("InvalidQuery", row.Detail);
    }

    [Fact]
    public void OtherProviderFailure_IsUnknownNotUnsupported()
    {
        var row = Single(new StorageHealthSnapshot(StorageHealthProviderState.Failed, [], "Storage health provider unavailable: ProviderFailure."));
        Assert.Equal("unknown", row.Health);
        Assert.Equal("Win32_DiskDrive", row.Source);
        Assert.Contains("ProviderFailure", row.Detail);
    }

    [Fact]
    public void ZeroPhysicalDiskRows_AreUnknownNeverHealthy()
    {
        var row = Single(new StorageHealthSnapshot(StorageHealthProviderState.Available, [], null));
        Assert.Equal("unknown", row.Health);
        Assert.Equal("MSFT_PhysicalDisk returned no rows.", row.Detail);
    }

    [Fact]
    public void Queries_AreLockedToDocumentedProperties()
    {
        Assert.DoesNotContain("Temperature", WindowsStorageCollector.PhysicalDiskHealthQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FROM MSFT_PhysicalDisk", WindowsStorageCollector.PhysicalDiskHealthQuery, StringComparison.Ordinal);
        Assert.Contains("HealthStatus", WindowsStorageCollector.PhysicalDiskHealthQuery, StringComparison.Ordinal);
        Assert.Contains("ObjectId", WindowsStorageCollector.PhysicalDiskHealthQuery, StringComparison.Ordinal);
        Assert.Equal("MSFT_StorageReliabilityCounter", WindowsStorageCollector.ReliabilityClass);
        Assert.Equal("MSFT_PhysicalDiskToStorageReliabilityCounter", WindowsStorageCollector.ReliabilityAssociationClass);
        Assert.Equal(
            ["DeviceId", "Temperature", "TemperatureMax", "Wear", "ReadErrorsTotal", "WriteErrorsTotal", "PowerOnHours"],
            WindowsStorageCollector.ReliabilityProperties);
    }

    [Fact]
    public void Formatting_ExposesAdditiveFields()
    {
        var json = StorageFormatting.Health(
            WindowsStorageHealthComposer.Compose(Identities, Available(0, ReliabilityState.Available, Counters)), null, 10, 65536);
        using var doc = JsonDocument.Parse(json);
        var device = doc.RootElement.GetProperty("devices")[0];
        Assert.False(device.GetProperty("partial").GetBoolean());
        Assert.Equal(JsonValueKind.Null, device.GetProperty("partialReason").ValueKind);
        Assert.Equal("MSFT_StorageReliabilityCounter", device.GetProperty("reliabilitySource").GetString());
        Assert.Equal(70, device.GetProperty("temperatureMaxC").GetDouble());
        Assert.Equal(3, device.GetProperty("readErrorsTotal").GetInt64());
        Assert.Equal(4, device.GetProperty("writeErrorsTotal").GetInt64());
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void Formatting_StaysBounded()
    {
        var rows = Enumerable.Range(0, 50)
            .Select(i => new StorageHealth($"d{i}", "healthy", null, null, null, null, null, null, false, "MSFT_PhysicalDisk", null)).ToArray();
        var json = StorageFormatting.Health(rows, null, 50, 1024);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= 1024);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    [WindowsOnlyFact]
    public async Task RealWindowsHealth_IsHonestAndCoherent()
    {
        var tool = new WindowsStorageToolProvider().GetTools().Single(t => t.Manifest.Name == "storage.health");
        var result = await tool.ExecuteAsync(bOps.Abstractions.ToolArguments.Empty);
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.DoesNotContain("InvalidQuery", result.Output, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(result.Output!);
        foreach (var device in doc.RootElement.GetProperty("devices").EnumerateArray())
        {
            var health = device.GetProperty("health").GetString();
            Assert.Contains(health, States);
            var source = device.GetProperty("source").GetString();
            Assert.Contains(source, Sources);
            if (health is "healthy" or "warning" or "unhealthy") Assert.Equal("MSFT_PhysicalDisk", source);
            var partial = device.GetProperty("partial").GetBoolean();
            Assert.Equal(partial, device.GetProperty("partialReason").ValueKind == JsonValueKind.String);
            var reliability = device.GetProperty("reliabilitySource");
            if (reliability.ValueKind == JsonValueKind.String)
            {
                Assert.Equal("MSFT_StorageReliabilityCounter", reliability.GetString());
                Assert.False(partial);
            }
        }
    }
}
