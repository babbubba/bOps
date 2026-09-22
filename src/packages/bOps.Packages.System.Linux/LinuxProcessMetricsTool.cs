// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Collects one <c>process.metrics</c> sample on Linux from <c>/proc/&lt;pid&gt;</c> (ADR-0034).</summary>
public sealed class LinuxProcessMetricsTool() : ProcessMetricsToolBase("linux")
{
    protected override async Task<ProcessSample> SampleAsync(int pid, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var stat = await LinuxProcReader.ReadStatAsync(pid, ct);
        if (stat is null && !LinuxProcReader.Exists(pid))
        {
            return new ProcessSample { Exists = false };
        }

        var status = await LinuxProcReader.ReadStatusAsync(pid, ct);
        var io = await LinuxProcReader.ReadIoAsync(pid, ct);

        return new ProcessSample
        {
            Exists = true,
            CpuTotal = stat?.CpuTicks is { } ticks
                ? TimeSpan.FromSeconds((double)ticks / LinuxProcReader.ClockTicksPerSecond)
                : null,
            WorkingSetBytes = status?.ResidentBytes,
            PrivateMemoryBytes = status?.PrivateBytes,
            VirtualMemoryBytes = status?.VirtualBytes ?? stat?.VirtualBytes,
            ThreadCount = status?.ThreadCount ?? stat?.ThreadCount,
            HandleOrFdCount = LinuxProcReader.CountFileDescriptors(pid),
            ReadBytes = io.Read,
            WriteBytes = io.Write,
            PageFaults = stat?.PageFaults,
        };
    }
}
