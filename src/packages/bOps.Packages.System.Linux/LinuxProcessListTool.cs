// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Collects <c>process.list</c> data on Linux via <see cref="Process"/>.</summary>
public sealed class LinuxProcessListTool() : ProcessListToolBase("linux")
{
    protected override Task<IReadOnlyList<ProcessListObservation>> ObserveAsync(CancellationToken ct)
    {
        var processes = Process.GetProcesses();
        try
        {
            IReadOnlyList<ProcessListObservation> observed = processes
                .Select(process => new ProcessListObservation(process.Id, SafeProcessName(process), SafeWorkingSetBytes(process)))
                .ToArray();
            return Task.FromResult(observed);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    // A process can exit, or be another user's, between GetProcesses() and reading its
    // properties — an expected race, not a bug, so that one property is reported unreadable
    // (null) rather than throwing and losing the rest of the listing; the shell then marks the
    // listing Partial.
    private static long? SafeWorkingSetBytes(Process process)
    {
        try
        {
            return process.WorkingSet64;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? SafeProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
