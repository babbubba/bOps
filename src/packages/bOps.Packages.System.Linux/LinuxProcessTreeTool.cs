// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>
/// Collects <c>process.tree</c> data on Linux by reading every <c>/proc/&lt;pid&gt;/stat</c>
/// (ADR-0034). A PID that disappears between the listing and the read is an exit race, not a
/// failure, and is counted as skipped so the walk never claims to be complete when it is not.
/// </summary>
public sealed class LinuxProcessTreeTool() : ProcessTreeToolBase("linux")
{
    protected override async Task<ProcessTreeSnapshot> CollectAsync(CancellationToken ct)
    {
        var pids = LinuxProcReader.ListPids(ProcessDiagnosticsLimits.TreeScanCeiling, out var truncated);
        var entries = new List<ProcessTreeEntry>(pids.Count);
        var skipped = 0;

        foreach (var pid in pids)
        {
            ct.ThrowIfCancellationRequested();
            var stat = await LinuxProcReader.ReadStatAsync(pid, ct);
            if (stat is null)
            {
                skipped++;
                continue;
            }

            entries.Add(new ProcessTreeEntry(stat.Pid, stat.ParentPid, stat.Name));
        }

        return new ProcessTreeSnapshot(entries, skipped, truncated);
    }

    protected override async Task<IReadOnlyDictionary<int, string?>> ResolveUsersAsync(IReadOnlyList<int> pids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pids);

        var names = await LinuxUserNames.ReadAsync(ct);
        var users = new Dictionary<int, string?>(pids.Count);
        foreach (var pid in pids)
        {
            ct.ThrowIfCancellationRequested();
            var status = await LinuxProcReader.ReadStatusAsync(pid, ct);
            users[pid] = LinuxUserNames.Resolve(names, status?.Uid);
        }

        return users;
    }
}
