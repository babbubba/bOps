// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// Collects <c>system.io</c> data on Windows via the <c>PhysicalDisk</c> performance counter
/// category, one instance per physical disk (<c>_Total</c> excluded — the per-device breakdown is
/// the point). Mirrors <see cref="WindowsCpuUsageTool"/>'s priming-sample pattern: a "/sec"
/// counter's first <c>NextValue()</c> is always 0, so every counter is primed before the shared
/// sampling delay.
/// </summary>
public sealed class WindowsIoUsageTool() : IoUsageToolBase("windows")
{
    private const int BytesPerKb = 1024;

    protected override async Task<IReadOnlyList<IoUsageResult>> CollectAsync(CancellationToken ct)
    {
        var instanceNames = new PerformanceCounterCategory("PhysicalDisk")
            .GetInstanceNames()
            .Where(name => !string.Equals(name, "_Total", StringComparison.Ordinal))
            .ToList();

        var readCounters = instanceNames.ToDictionary(name => name, name => new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", name));
        var writeCounters = instanceNames.ToDictionary(name => name, name => new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", name));

        try
        {
            foreach (var counter in readCounters.Values.Concat(writeCounters.Values))
            {
                counter.NextValue();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);

            IReadOnlyList<IoUsageResult> results = instanceNames
                .Select(name => new IoUsageResult(
                    name,
                    readCounters[name].NextValue() / BytesPerKb,
                    writeCounters[name].NextValue() / BytesPerKb))
                .ToList();

            return results;
        }
        finally
        {
            foreach (var counter in readCounters.Values.Concat(writeCounters.Values))
            {
                counter.Dispose();
            }
        }
    }
}
