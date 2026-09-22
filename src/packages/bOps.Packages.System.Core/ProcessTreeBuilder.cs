// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>
/// Turns the flat parent/child observations of a tree collector into the bounded, deterministic
/// walk <c>process.tree</c> reports (ADR-0034). The walk is depth-first from each root, children
/// ordered by PID, so the same machine state always produces the same rows in the same order —
/// which is what makes the output diffable and the tests meaningful. Shared here rather than
/// written twice because the output shape is part of the contract (rule A8).
/// </summary>
public static class ProcessTreeBuilder
{
    /// <summary>
    /// Selects the rows for one call. A <paramref name="rootPid"/> that is not among the observed
    /// processes yields no rows and <c>RootFound: false</c>, never an empty tree that looks like a
    /// quiet machine.
    /// </summary>
    public static ProcessTreeSelection Build(ProcessTreeSnapshot snapshot, int? rootPid, int maxDepth, int limit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var byPid = new Dictionary<int, ProcessTreeEntry>();
        foreach (var entry in snapshot.Entries)
        {
            byPid.TryAdd(entry.Pid, entry);
        }

        var children = new Dictionary<int, List<int>>();
        foreach (var entry in byPid.Values)
        {
            if (IsRoot(entry, byPid))
            {
                continue;
            }

            if (!children.TryGetValue(entry.ParentPid!.Value, out var siblings))
            {
                siblings = [];
                children[entry.ParentPid.Value] = siblings;
            }

            siblings.Add(entry.Pid);
        }

        foreach (var siblings in children.Values)
        {
            siblings.Sort();
        }

        int[] roots;
        if (rootPid is { } requested)
        {
            if (!byPid.ContainsKey(requested))
            {
                return new ProcessTreeSelection([], RootFound: false, ObservedProcesses: 0, Truncated: snapshot.CollectionTruncated);
            }

            roots = [requested];
        }
        else
        {
            roots = byPid.Values.Where(entry => IsRoot(entry, byPid)).Select(entry => entry.Pid).Order().ToArray();
        }

        var rows = new List<ProcessTreeRow>();
        var visited = new HashSet<int>();
        var observed = 0;
        var depthCut = false;

        foreach (var root in roots)
        {
            Walk(root, 0);
        }

        var truncated = snapshot.CollectionTruncated || depthCut || rows.Count > limit;
        if (rows.Count > limit)
        {
            rows.RemoveRange(limit, rows.Count - limit);
        }

        return new ProcessTreeSelection(rows, RootFound: true, observed, truncated);

        void Walk(int pid, int depth)
        {
            // A parent chain can, in principle, close on itself after PID reuse; the visited set
            // makes that a bounded walk rather than a stack overflow.
            if (!visited.Add(pid) || !byPid.TryGetValue(pid, out var entry))
            {
                return;
            }

            observed++;
            rows.Add(new ProcessTreeRow(entry.Pid, entry.ParentPid, entry.Name, depth));

            if (!children.TryGetValue(pid, out var siblings))
            {
                return;
            }

            if (depth >= maxDepth)
            {
                depthCut = true;
                return;
            }

            foreach (var child in siblings)
            {
                Walk(child, depth + 1);
            }
        }
    }

    private static bool IsRoot(ProcessTreeEntry entry, Dictionary<int, ProcessTreeEntry> byPid) =>
        entry.ParentPid is not { } parent || parent == entry.Pid || !byPid.ContainsKey(parent);
}
