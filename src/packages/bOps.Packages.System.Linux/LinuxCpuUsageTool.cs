using System.Globalization;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Collects <c>system.cpu</c> data on Linux by differencing two <c>/proc/stat</c> samples.</summary>
public sealed class LinuxCpuUsageTool() : CpuUsageToolBase("linux")
{
    protected override async Task<CpuUsageResult> CollectAsync(CancellationToken ct)
    {
        var first = await ReadCpuTimesAsync(ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        var second = await ReadCpuTimesAsync(ct);

        var idleDelta = second.Idle - first.Idle;
        var totalDelta = second.Total - first.Total;
        var percent = totalDelta == 0 ? 0 : 100.0 * (1 - (double)idleDelta / totalDelta);

        return new CpuUsageResult(percent);
    }

    private static async Task<(long Idle, long Total)> ReadCpuTimesAsync(CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync("/proc/stat", ct);
        var cpuLine = lines.First(line => line.StartsWith("cpu ", StringComparison.Ordinal));

        var fields = cpuLine
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(field => long.Parse(field, CultureInfo.InvariantCulture))
            .ToArray();

        // user, nice, system, idle, iowait, irq, softirq, steal, guest, guest_nice
        var idle = fields[3] + (fields.Length > 4 ? fields[4] : 0);
        var total = fields.Sum();
        return (idle, total);
    }
}
