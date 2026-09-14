using System.ComponentModel;
using System.Diagnostics;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Collects <c>process.list</c> data on Linux via <see cref="Process"/>.</summary>
public sealed class LinuxProcessListTool() : ProcessListToolBase("linux")
{
    private const int BytesPerMb = 1024 * 1024;

    protected override Task<IReadOnlyList<ProcessSummary>> CollectAsync(int limit, CancellationToken ct)
    {
        IReadOnlyList<ProcessSummary> processes = Process.GetProcesses()
            .OrderByDescending(SafeWorkingSetBytes)
            .Take(limit)
            .Select(process => new ProcessSummary(process.Id, SafeProcessName(process), SafeWorkingSetBytes(process) / BytesPerMb))
            .ToList();

        return Task.FromResult(processes);
    }

    // A process can exit, or be another user's, between GetProcesses() and reading its
    // properties — an expected race, not a bug, so it degrades to a safe default rather than
    // throwing and losing the rest of the listing.
    private static long SafeWorkingSetBytes(Process process)
    {
        try
        {
            return process.WorkingSet64;
        }
        catch (Win32Exception)
        {
            return 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static string SafeProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (Win32Exception)
        {
            return "<unknown>";
        }
        catch (InvalidOperationException)
        {
            return "<unknown>";
        }
    }
}
