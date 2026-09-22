// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Storage.Conformance;

namespace bOps.Packages.Storage.Windows.Tests;

public sealed class WindowsStorageTests
{
    [Fact]
    public void Provider_Conforms() => StorageConformance.AssertProvider(new WindowsStorageToolProvider(), "windows");

    [WindowsOnlyFact]
    public async Task RealWindowsSources_ProduceContractShapes() => await StorageConformance.AssertShapesAsync(new WindowsStorageToolProvider());

    [Fact]
    public void PerformanceInstanceMapping_RejectsAmbiguousNames()
    {
        IReadOnlyDictionary<int, string> disks = new Dictionary<int, string> { [0] = @"\\.\PHYSICALDRIVE0" };
        Assert.False(WindowsStorageCollector.TryMapPerformanceInstance("disk zero", disks, out _));
        Assert.False(WindowsStorageCollector.TryMapPerformanceInstance("_Total", disks, out _));
        Assert.True(WindowsStorageCollector.TryMapPerformanceInstance("0 C:", disks, out var device));
        Assert.Equal(@"\\.\PHYSICALDRIVE0", device);
    }
}
