// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Storage.Linux;

internal static class LinuxMountInfoParser
{
    public static IReadOnlyList<LinuxMountInfo> Parse(IEnumerable<string> lines)
    {
        var mounts = new List<LinuxMountInfo>();
        foreach (var line in lines)
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0) continue;
            var left = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var right = line[(separator + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (left.Length < 6 || right.Length < 2) continue;
            var mountOptions = Decode(left[5]);
            var superOptions = right.Length > 2 ? Decode(right[2]) : string.Empty;
            var options = string.IsNullOrEmpty(superOptions) ? mountOptions : $"{mountOptions};{superOptions}";
            mounts.Add(new LinuxMountInfo(
                Decode(right[1]), Decode(left[4]), right[0], options,
                mountOptions.Split(',').Contains("ro", StringComparer.Ordinal)
                || superOptions.Split(',').Contains("ro", StringComparer.Ordinal)));
        }

        return mounts;
    }

    private static string Decode(string value) => value
        .Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal)
        .Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);
}
