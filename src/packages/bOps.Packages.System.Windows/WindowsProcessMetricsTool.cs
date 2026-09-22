// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>Collects one <c>process.metrics</c> sample on Windows (ADR-0034).</summary>
public sealed class WindowsProcessMetricsTool() : ProcessMetricsToolBase("windows")
{
    protected override Task<ProcessSample> SampleAsync(int pid, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(new ProcessSample { Exists = false });
        }

        using (process)
        {
            // A PID can still resolve to a Process object just after the process exited; that is
            // the exit race process.metrics has to report as exists:false, not as a sample full of
            // nulls. A HasExited that cannot be read at all leaves the process counted as present.
            if (WindowsProcessCounters.Value(() => process.HasExited) == true)
            {
                return Task.FromResult(new ProcessSample { Exists = false });
            }

            var sample = new ProcessSample
            {
                Exists = true,
                CpuTotal = WindowsProcessCounters.Value(() => process.TotalProcessorTime),
                WorkingSetBytes = WindowsProcessCounters.Value(() => process.WorkingSet64),
                PrivateMemoryBytes = WindowsProcessCounters.Value(() => process.PrivateMemorySize64),
                VirtualMemoryBytes = WindowsProcessCounters.Value(() => process.VirtualMemorySize64),
                ThreadCount = WindowsProcessCounters.Value(() => process.Threads.Count),
                HandleOrFdCount = WindowsProcessCounters.HandleCount(process),
                ReadBytes = WindowsProcessCounters.ReadTransferBytes(process),
                WriteBytes = WindowsProcessCounters.WriteTransferBytes(process),
                PageFaults = WindowsProcessCounters.PageFaults(process),
            };
            return Task.FromResult(sample);
        }
    }
}
