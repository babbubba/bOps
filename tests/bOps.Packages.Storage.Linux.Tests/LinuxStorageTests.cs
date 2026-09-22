// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Storage.Conformance;
using System.Runtime.Versioning;

namespace bOps.Packages.Storage.Linux.Tests;

[SupportedOSPlatform("linux")]
public sealed class LinuxStorageTests
{
    [Fact]
    public void Provider_Conforms() => StorageConformance.AssertProvider(new LinuxStorageToolProvider(), "linux");

    [Fact]
    public void MountInfo_ParsesEscapesAndReadOnlyState()
    {
        var row = Assert.Single(LinuxMountInfoParser.Parse(["36 25 8:1 / /data\\040disk ro,nosuid - ext4 /dev/sda1 rw"]));
        Assert.Equal("/data disk", row.MountPoint);
        Assert.True(row.ReadOnly);
        Assert.Equal("/dev/sda1", row.Source);
    }

    [Fact]
    public void DiskStats_ParsesDocumentedKernelFields()
    {
        var row = Assert.Single(LinuxDiskStatsParser.Parse(["8 0 sda 10 0 20 30 40 0 50 60 2 70 80"], new HashSet<string> { "sda" }, null));
        Assert.Equal("/dev/sda", row.Device);
        Assert.Equal(10, row.Reads);
        Assert.Equal(20 * 512, row.ReadBytes);
        Assert.Equal(80, row.WeightedIoTimeMilliseconds);
    }

    [Fact]
    public void SmartctlFixture_MapsAtaAndNvmeEvidenceWithoutRawDump()
    {
        const string json = """
            {"smart_status":{"passed":true},"temperature":{"current":34},"power_on_time":{"hours":123},
             "nvme_smart_health_information_log":{"media_errors":2,"percentage_used":7},
             "ata_smart_attributes":{"table":[{"name":"Reallocated_Sector_Ct","raw":{"value":3}}]}}
            """;
        var row = LinuxSmartctl.Parse("/dev/sda", json)!;
        Assert.Equal("healthy", row.Health);
        Assert.Equal(34, row.TemperatureC);
        Assert.Equal(3, row.ReallocatedSectors);
        Assert.Equal(7, row.WearPercent);
    }

    [LinuxOnlyFact]
    public async Task RealLinuxSources_ProduceContractShapes() => await StorageConformance.AssertShapesAsync(new LinuxStorageToolProvider());

    [LinuxOnlyFact]
    public async Task RealSmartctlSmoke_WhenExplicitlyEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BOPS_RUN_SMARTCTL_SMOKE"), "1", StringComparison.Ordinal)) return;
        var executable = LinuxSmartctl.FindExecutable();
        Assert.NotNull(executable);
        var device = LinuxStorageCollector.Disks().Select(x => x.Id).First();
        Assert.NotNull(await LinuxSmartctl.ReadAsync(executable!, device, CancellationToken.None));
    }
}
