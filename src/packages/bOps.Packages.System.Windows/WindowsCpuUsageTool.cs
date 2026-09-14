using System.Diagnostics;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>Collects <c>system.cpu</c> data on Windows via the <c>% Processor Time</c> performance counter.</summary>
public sealed class WindowsCpuUsageTool() : CpuUsageToolBase("windows")
{
    protected override async Task<CpuUsageResult> CollectAsync(CancellationToken ct)
    {
        using var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");

        // The first NextValue() call always returns 0 — it establishes the baseline sample.
        counter.NextValue();
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);

        return new CpuUsageResult(counter.NextValue());
    }
}
