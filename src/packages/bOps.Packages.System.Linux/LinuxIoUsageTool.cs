// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>
/// Collects <c>system.io</c> data on Linux by differencing two <c>/proc/diskstats</c> samples,
/// restricted to whole-disk devices (a device counts as one when <c>/sys/block/&lt;name&gt;</c>
/// exists — the kernel's own distinction between a disk and one of its partitions), so a
/// single-partition disk's throughput is not double-counted between the disk and its partition
/// line.
/// </summary>
public sealed class LinuxIoUsageTool() : IoUsageToolBase("linux")
{
    private const int SectorBytes = 512;
    private const int BytesPerKb = 1024;
    private const double SampleIntervalSeconds = 0.5;

    protected override async Task<IReadOnlyList<IoUsageResult>> CollectAsync(CancellationToken ct)
    {
        var first = await ReadSectorCountsAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(SampleIntervalSeconds), ct);
        var second = await ReadSectorCountsAsync(ct);

        IReadOnlyList<IoUsageResult> results = first.Keys
            .Where(second.ContainsKey)
            .Select(name =>
            {
                var (readBefore, writeBefore) = first[name];
                var (readAfter, writeAfter) = second[name];
                var readKbPerSec = Math.Max(0, readAfter - readBefore) * SectorBytes / (double)BytesPerKb / SampleIntervalSeconds;
                var writeKbPerSec = Math.Max(0, writeAfter - writeBefore) * SectorBytes / (double)BytesPerKb / SampleIntervalSeconds;
                return new IoUsageResult(name, readKbPerSec, writeKbPerSec);
            })
            .OrderBy(result => result.DeviceName, StringComparer.Ordinal)
            .ToList();

        return results;
    }

    private static async Task<Dictionary<string, (long SectorsRead, long SectorsWritten)>> ReadSectorCountsAsync(CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync("/proc/diskstats", ct);
        var counts = new Dictionary<string, (long, long)>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10)
            {
                continue;
            }

            var name = fields[2];
            if (!Directory.Exists($"/sys/block/{name}"))
            {
                continue;
            }

            var sectorsRead = long.Parse(fields[5], CultureInfo.InvariantCulture);
            var sectorsWritten = long.Parse(fields[9], CultureInfo.InvariantCulture);
            counts[name] = (sectorsRead, sectorsWritten);
        }

        return counts;
    }
}
