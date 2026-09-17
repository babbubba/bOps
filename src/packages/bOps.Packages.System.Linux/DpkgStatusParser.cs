// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Parses installed records from Debian's paragraph-based dpkg status database.</summary>
internal static class DpkgStatusParser
{
    private const string SourceName = "linux.dpkg-status";

    internal static async Task<(IReadOnlyList<ApplicationInventoryItem> Items, bool Truncated)> ParseAsync(
        Stream stream,
        int collectionLimit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var items = new List<ApplicationInventoryItem>();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new StreamReader(stream, leaveOpen: true);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0)
            {
                if (TryAdd(fields, items) && items.Count >= collectionLimit)
                {
                    return (items, true);
                }

                fields.Clear();
                continue;
            }

            if (char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                fields[line[..separator]] = line[(separator + 1)..].Trim();
            }
        }

        _ = TryAdd(fields, items);
        return (items, false);
    }

    private static bool TryAdd(
        Dictionary<string, string> fields,
        List<ApplicationInventoryItem> items)
    {
        if (!fields.TryGetValue("Status", out var status)
            || !string.Equals(status, "install ok installed", StringComparison.Ordinal)
            || !fields.TryGetValue("Package", out var package)
            || string.IsNullOrWhiteSpace(package))
        {
            return false;
        }

        fields.TryGetValue("Architecture", out var architecture);
        fields.TryGetValue("Version", out var version);
        fields.TryGetValue("Maintainer", out var maintainer);
        var normalizedArchitecture = string.IsNullOrWhiteSpace(architecture) ? "unknown" : architecture;
        items.Add(new(
            $"deb:{package}:{normalizedArchitecture}",
            package,
            string.IsNullOrWhiteSpace(version) ? null : version,
            string.IsNullOrWhiteSpace(maintainer) ? null : maintainer,
            SourceName));
        return true;
    }
}
