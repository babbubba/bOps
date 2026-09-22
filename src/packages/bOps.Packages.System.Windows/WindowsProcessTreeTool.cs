// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// Collects <c>process.tree</c> data on Windows from <c>Win32_Process</c>, which is the only place
/// the parent PID of an arbitrary process is available without a toolhelp snapshot (ADR-0034).
/// </summary>
public sealed class WindowsProcessTreeTool() : ProcessTreeToolBase("windows")
{
    private const string ExecutableSuffix = ".exe";

    protected override Task<ProcessTreeSnapshot> CollectAsync(CancellationToken ct)
    {
        var snapshot = WindowsProcessInformation.ReadTree(ProcessDiagnosticsLimits.TreeScanCeiling, ct);
        var entries = snapshot.Entries
            .Select(entry => entry with { Name = WithoutExecutableSuffix(entry.Name) })
            .ToArray();

        return Task.FromResult(snapshot with { Entries = entries });
    }

    protected override Task<IReadOnlyDictionary<int, string?>> ResolveUsersAsync(IReadOnlyList<int> pids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pids);

        var users = new Dictionary<int, string?>(pids.Count);
        foreach (var pid in pids)
        {
            ct.ThrowIfCancellationRequested();
            users[pid] = WindowsProcessInformation.TryReadOwner(pid);
        }

        return Task.FromResult<IReadOnlyDictionary<int, string?>>(users);
    }

    // Win32_Process reports "explorer.exe" where Process.ProcessName — and therefore process.list
    // and process.inspect — reports "explorer". The agent correlates these by name, so the tree
    // uses the spelling the other process tools already use.
    private static string? WithoutExecutableSuffix(string? name) =>
        name is not null && name.EndsWith(ExecutableSuffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^ExecutableSuffix.Length]
            : name;
}
