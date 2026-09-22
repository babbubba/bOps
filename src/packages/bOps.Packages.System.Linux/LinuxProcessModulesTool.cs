// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>
/// Collects <c>process.modules</c> data on Linux from <c>/proc/&lt;pid&gt;/maps</c> (ADR-0034).
/// One shared object appears there as several mappings with different protections, so the
/// mappings of one path are coalesced into one module: the lowest mapped address and the sum of
/// the mapped sizes. Anonymous mappings and the kernel's pseudo-regions
/// (<c>[heap]</c>, <c>[stack]</c>, <c>[vdso]</c>) are not modules and are left out.
/// </summary>
public sealed class LinuxProcessModulesTool() : ProcessModulesToolBase("linux")
{
    protected override async Task<ProcessModuleSnapshot> CollectAsync(int pid, int collectionLimit, CancellationToken ct)
    {
        var maps = await LinuxProcReader.ReadMapsAsync(pid, ct);
        if (maps is null)
        {
            return LinuxProcReader.Exists(pid)
                ? new ProcessModuleSnapshot(Exists: true, [], InventorySourceStatus.Unavailable, "This identity cannot read the memory map of this process.")
                : new ProcessModuleSnapshot(Exists: false, [], InventorySourceStatus.NotApplicable, "No process with this PID is running.");
        }

        return Parse(maps, collectionLimit, ct);
    }

    /// <summary>Coalesces the mappings of one <c>/proc/&lt;pid&gt;/maps</c> into modules.</summary>
    internal static ProcessModuleSnapshot Parse(string maps, int collectionLimit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(maps);

        var modules = new Dictionary<string, (ulong Start, long Size)>(StringComparer.Ordinal);
        var examined = 0;
        var truncated = false;
        var partial = false;

        foreach (var line in maps.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            if (examined >= collectionLimit)
            {
                truncated = true;
                break;
            }

            examined++;
            var fields = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6)
            {
                continue; // an anonymous mapping has no sixth field at all
            }

            var path = fields[5].Trim();
            if (path.Length == 0 || path[0] != '/')
            {
                continue;
            }

            if (!TryReadRange(fields[0], out var start, out var end))
            {
                partial = true;
                continue;
            }

            var size = (long)(end - start);
            if (modules.TryGetValue(path, out var existing))
            {
                modules[path] = (Math.Min(existing.Start, start), existing.Size + size);
            }
            else
            {
                modules[path] = (start, size);
            }
        }

        var entries = modules
            .Select(module => new ProcessModuleEntry(
                System.IO.Path.GetFileName(module.Key) is { Length: > 0 } name ? name : module.Key,
                module.Key,
                "0x" + module.Value.Start.ToString("x", CultureInfo.InvariantCulture),
                module.Value.Size,
                // Linux records no version metadata for a mapped image; a version guessed from the
                // file name of a symlinked .so would be a fact the kernel never stated.
                null))
            .ToArray();

        return new ProcessModuleSnapshot(
            Exists: true,
            entries,
            partial ? InventorySourceStatus.Partial : InventorySourceStatus.Available,
            partial ? "One or more mappings could not be read." : null,
            truncated);
    }

    private static bool TryReadRange(string range, out ulong start, out ulong end)
    {
        start = 0;
        end = 0;
        var separator = range.IndexOf('-', StringComparison.Ordinal);
        return separator > 0
            && ulong.TryParse(range[..separator], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out start)
            && ulong.TryParse(range[(separator + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out end)
            && end >= start;
    }
}
