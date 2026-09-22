// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Packages.Storage.Core;

namespace bOps.Packages.Storage.Linux;

internal static class LinuxDiskStatsParser
{
    public static IReadOnlyList<StorageIoSample> Parse(IEnumerable<string> lines, ISet<string> allowedDevices, string? requestedDevice)
    {
        var rows = new List<StorageIoSample>();
        foreach (var line in lines)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 14 || !allowedDevices.Contains(fields[2])) continue;
            var device = $"/dev/{fields[2]}";
            if (requestedDevice is not null && !string.Equals(requestedDevice, device, StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryLong(fields[3], out var reads) || !TryLong(fields[5], out var sectorsRead) || !TryLong(fields[6], out var readMs)
                || !TryLong(fields[7], out var writes) || !TryLong(fields[9], out var sectorsWritten) || !TryLong(fields[10], out var writeMs)
                || !TryLong(fields[11], out var inFlight) || !TryLong(fields[12], out var busyMs) || !TryLong(fields[13], out var weightedMs)) continue;
            rows.Add(new StorageIoSample(device, reads, writes, sectorsRead * 512, sectorsWritten * 512, readMs, writeMs, weightedMs, busyMs, inFlight));
        }

        return rows;
    }

    private static bool TryLong(string value, out long result) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
}
