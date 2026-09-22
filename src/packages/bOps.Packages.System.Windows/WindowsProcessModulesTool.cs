// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// Collects <c>process.modules</c> data on Windows from <see cref="Process.Modules"/> (ADR-0034).
/// A module list this identity may not read is reported as an explicit status, never as a process
/// that happens to have loaded nothing.
/// </summary>
public sealed class WindowsProcessModulesTool() : ProcessModulesToolBase("windows")
{
    protected override Task<ProcessModuleSnapshot> CollectAsync(int pid, int collectionLimit, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(new ProcessModuleSnapshot(Exists: false, [], InventorySourceStatus.NotApplicable, "No process with this PID is running."));
        }

        using (process)
        {
            ProcessModuleCollection modules;
            try
            {
                modules = process.Modules;
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Another user's process, a protected system process, or a bitness the caller
                // cannot enumerate across. The gap is the answer, not an empty list.
                return Task.FromResult(new ProcessModuleSnapshot(
                    Exists: true, [], InventorySourceStatus.Unavailable, "This identity cannot read the module list of this process."));
            }

            var entries = new List<ProcessModuleEntry>();
            var partial = false;
            var truncated = false;

            foreach (ProcessModule module in modules)
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count >= collectionLimit)
                {
                    truncated = true;
                    break;
                }

                var entry = ToEntry(module);
                if (entry is null)
                {
                    partial = true;
                    continue;
                }

                entries.Add(entry);
            }

            var status = partial ? InventorySourceStatus.Partial : InventorySourceStatus.Available;
            var detail = partial ? "One or more modules could not be read." : null;
            return Task.FromResult(new ProcessModuleSnapshot(Exists: true, entries, status, detail, truncated));
        }
    }

    private static ProcessModuleEntry? ToEntry(ProcessModule module)
    {
        var path = WindowsProcessCounters.Text(() => module.FileName);
        var name = WindowsProcessCounters.Text(() => module.ModuleName);
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var baseAddress = WindowsProcessCounters.Value(() => module.BaseAddress.ToInt64());
        var size = WindowsProcessCounters.Value(() => (long)module.ModuleMemorySize);
        var version = WindowsProcessCounters.Text(() => module.FileVersionInfo.FileVersion);

        return new ProcessModuleEntry(
            name,
            path,
            baseAddress is { } address ? "0x" + ((ulong)address).ToString("x", CultureInfo.InvariantCulture) : null,
            size,
            string.IsNullOrWhiteSpace(version) ? null : version);
    }
}
