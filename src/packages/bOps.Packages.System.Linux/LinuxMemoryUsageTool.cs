using System.Globalization;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Collects <c>system.memory</c> data on Linux via <c>/proc/meminfo</c>.</summary>
public sealed class LinuxMemoryUsageTool() : MemoryUsageToolBase("linux")
{
    protected override async Task<MemoryUsageResult> CollectAsync(CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync("/proc/meminfo", ct);
        long totalKb = 0;
        long availableKb = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                totalKb = ParseKb(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                availableKb = ParseKb(line);
            }
        }

        return new MemoryUsageResult(totalKb / 1024, availableKb / 1024);
    }

    private static long ParseKb(string line)
    {
        var value = line.Split(':', 2)[1].Trim().Replace(" kB", string.Empty, StringComparison.Ordinal);
        return long.Parse(value, CultureInfo.InvariantCulture);
    }
}
