// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Storage.Core;

public static class StorageCalculations
{
    public static IReadOnlyList<StorageIoResult> ComputeIo(
        IReadOnlyList<StorageIoSample> first, IReadOnlyList<StorageIoSample> second, double seconds)
    {
        if (seconds <= 0)
        {
            return [];
        }

        var before = first.ToDictionary(sample => sample.Device, StringComparer.OrdinalIgnoreCase);
        return second.Where(sample => before.ContainsKey(sample.Device)).Select(after =>
        {
            var earlier = before[after.Device];
            var reads = Delta(earlier.Reads, after.Reads);
            var writes = Delta(earlier.Writes, after.Writes);
            var readMs = Delta(earlier.ReadTimeMilliseconds, after.ReadTimeMilliseconds);
            var writeMs = Delta(earlier.WriteTimeMilliseconds, after.WriteTimeMilliseconds);
            var elapsedMs = seconds * 1_000;
            return new StorageIoResult(
                after.Device,
                reads / seconds,
                writes / seconds,
                Delta(earlier.ReadBytes, after.ReadBytes) / seconds,
                Delta(earlier.WriteBytes, after.WriteBytes) / seconds,
                reads > 0 ? readMs / (double)reads : null,
                writes > 0 ? writeMs / (double)writes : null,
                Math.Max(0, Delta(earlier.WeightedIoTimeMilliseconds, after.WeightedIoTimeMilliseconds) / elapsedMs),
                Math.Clamp(Delta(earlier.BusyTimeMilliseconds, after.BusyTimeMilliseconds) / elapsedMs * 100, 0, 100));
        }).ToArray();
    }

    private static long Delta(long before, long after) => Math.Max(0, after - before);
}
