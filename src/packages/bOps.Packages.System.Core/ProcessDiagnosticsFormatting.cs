// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// Creates the bounded, deterministic JSON output of <c>process.metrics</c>, <c>process.tree</c>
/// and <c>process.modules</c> (ADR-0034). It lives here, not in either OS package, because the LLM
/// reads this output and a difference in shape between two operating systems is a difference in
/// behaviour (agentic/01-architecture-rules.md, rule A8).
/// </summary>
public static class ProcessDiagnosticsFormatting
{
    /// <summary>Formats a <see cref="ProcessMetricsResult"/> as single-line JSON.</summary>
    public static string Format(ProcessMetricsResult metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        var json = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["pid"] = metrics.Pid,
            ["exists"] = metrics.Exists,
            ["sampleMilliseconds"] = metrics.SampleMilliseconds,
            ["cpuPercent"] = Rounded(metrics.CpuPercent),
            ["workingSetMb"] = metrics.WorkingSetMb,
            ["privateMemoryMb"] = metrics.PrivateMemoryMb,
            ["virtualMemoryMb"] = metrics.VirtualMemoryMb,
            ["threadCount"] = metrics.ThreadCount,
            ["handleOrFdCount"] = metrics.HandleOrFdCount,
            ["readBytesPerSec"] = Rounded(metrics.ReadBytesPerSec),
            ["writeBytesPerSec"] = Rounded(metrics.WriteBytesPerSec),
            ["pageFaultsPerSec"] = Rounded(metrics.PageFaultsPerSec),
            ["partial"] = metrics.Partial,
        };
        return json.ToJsonString();
    }

    /// <summary>Formats a selected <c>process.tree</c> walk, with the user of each returned row.</summary>
    public static string FormatTree(
        ProcessTreeSelection selection,
        ProcessTreeSnapshot snapshot,
        int? rootPid,
        int maxDepth,
        IReadOnlyDictionary<int, string?> users)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(users);

        var rows = new JsonArray(selection.Rows.Select(row => (JsonNode)new JsonObject
        {
            ["pid"] = row.Pid,
            ["parentPid"] = row.ParentPid,
            ["name"] = Bounded(row.Name, ProcessDiagnosticsLimits.NameCharacters),
            ["user"] = Bounded(users.TryGetValue(row.Pid, out var user) ? user : null, ProcessDiagnosticsLimits.UserCharacters),
            ["depth"] = row.Depth,
        }).ToArray());

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["rootPid"] = rootPid,
            ["rootFound"] = selection.RootFound,
            ["maxDepth"] = maxDepth,
            ["observedProcesses"] = selection.ObservedProcesses,
            ["returnedProcesses"] = selection.Rows.Count,
            ["skipped"] = snapshot.Skipped,
            ["truncated"] = selection.Truncated,
            ["complete"] = selection.RootFound && !selection.Truncated && snapshot.Skipped == 0,
            ["processes"] = rows,
        };
        return root.ToJsonString();
    }

    /// <summary>
    /// Formats the modules of one process: unique by normalized path, ordered by name then path,
    /// cut to <paramref name="limit"/> rows and then to <paramref name="maxOutputBytes"/> UTF-8 bytes.
    /// </summary>
    public static string FormatModules(ProcessModuleSnapshot snapshot, int pid, int limit, int maxOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var observed = snapshot.Modules
            .Where(module => !string.IsNullOrWhiteSpace(module.Path) && !string.IsNullOrWhiteSpace(module.Name))
            .GroupBy(module => module.Path.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(module => module.Name, StringComparer.Ordinal)
            .ThenBy(module => module.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(module => module.Path, StringComparer.Ordinal)
            .ToArray();

        var selected = observed.Take(limit).Select(CreateModule).ToArray();
        var truncated = snapshot.CollectionTruncated || selected.Length < observed.Length;

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["pid"] = pid,
            ["exists"] = snapshot.Exists,
            ["status"] = SystemInventoryFormatting.ToWireValue(snapshot.Status),
            ["detail"] = Bounded(snapshot.Detail, 256),
            ["observedModules"] = observed.Length,
            ["returnedModules"] = selected.Length,
            ["truncated"] = truncated,
            ["complete"] = snapshot.Exists && snapshot.Status == InventorySourceStatus.Available && !truncated,
            ["modules"] = new JsonArray(selected.Select(module => (JsonNode)module).ToArray()),
        };

        var output = root.ToJsonString();
        var moduleArray = root["modules"]!.AsArray();
        while (Encoding.UTF8.GetByteCount(output) > maxOutputBytes && moduleArray.Count > 0)
        {
            moduleArray.RemoveAt(moduleArray.Count - 1);
            root["returnedModules"] = moduleArray.Count;
            root["truncated"] = true;
            root["complete"] = false;
            output = root.ToJsonString();
        }

        if (Encoding.UTF8.GetByteCount(output) > maxOutputBytes)
        {
            throw new InvalidOperationException(
                $"Module metadata exceeds the configured {maxOutputBytes}-byte output limit.");
        }

        return output;
    }

    private static JsonObject CreateModule(ProcessModuleEntry module) => new()
    {
        ["name"] = Bounded(module.Name, ProcessDiagnosticsLimits.NameCharacters),
        ["path"] = Bounded(module.Path, ProcessDiagnosticsLimits.PathCharacters),
        ["baseAddress"] = Bounded(module.BaseAddress, 32),
        ["sizeBytes"] = module.SizeBytes,
        ["version"] = Bounded(module.Version, ProcessDiagnosticsLimits.VersionCharacters),
    };

    private static double? Rounded(double? value) => value is { } number ? Math.Round(number, 2, MidpointRounding.AwayFromZero) : null;

    private static string? Bounded(string? value, int maximum) => SystemInventoryFormatting.Bounded(value, maximum);
}
