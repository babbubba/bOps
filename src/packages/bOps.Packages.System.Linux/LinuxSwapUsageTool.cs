// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Collects <c>system.swap</c> data on Linux via <c>/proc/meminfo</c>.</summary>
public sealed class LinuxSwapUsageTool() : SwapUsageToolBase("linux")
{
    protected override async Task<SwapUsageResult> CollectAsync(CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync("/proc/meminfo", ct);
        long totalKb = 0;
        long freeKb = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("SwapTotal:", StringComparison.Ordinal))
            {
                totalKb = ParseKb(line);
            }
            else if (line.StartsWith("SwapFree:", StringComparison.Ordinal))
            {
                freeKb = ParseKb(line);
            }
        }

        return new SwapUsageResult(totalKb / 1024, (totalKb - freeKb) / 1024);
    }

    private static long ParseKb(string line)
    {
        var value = line.Split(':', 2)[1].Trim().Replace(" kB", string.Empty, StringComparison.Ordinal);
        return long.Parse(value, CultureInfo.InvariantCulture);
    }
}
