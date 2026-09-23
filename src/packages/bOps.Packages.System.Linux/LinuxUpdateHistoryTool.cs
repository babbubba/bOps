// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Reads only known local package history files; it never invokes a package manager.</summary>
public sealed class LinuxUpdateHistoryTool : SystemUpdateHistoryToolBase
{
    public LinuxUpdateHistoryTool() : base("linux") { }

    protected override async Task<MaintenanceSnapshot<UpdateHistoryRecord>> CollectAsync(MaintenanceArguments arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var distro = LinuxUpdateParser.Detect(File.Exists("/etc/os-release") ? await File.ReadAllTextAsync("/etc/os-release", ct).ConfigureAwait(false) : string.Empty);
        var (path, source) = distro switch
        {
            "debian" => ("/var/log/apt/history.log", "linux.apt-history"),
            "dnf" => ("/var/log/dnf.rpm.log", "linux.dnf-history"),
            "zypper" => ("/var/log/zypp/history", "linux.zypper-history"),
            _ => (string.Empty, "linux.package-history"),
        };
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Missing(source, "Local package history source is unavailable or the distribution is unsupported.");
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 4 * 1024 * 1024) return Missing(source, "Local package history exceeds the bounded read size.");
            var parsed = LinuxUpdateHistoryParser.Parse(distro!, await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), source);
            if (!parsed.Valid) return Missing(source, "Local package history is malformed or contains an incomplete transaction.");
            var cutoff = DateTimeOffset.UtcNow.AddDays(-arguments.SinceDays!.Value);
            var rows = parsed.Items.Where(x => x.TimestampUtc >= cutoff).ToArray();
            // Only the current canonical log is read: older rotated logs may exist and are not claimed complete.
            return new(rows, [new(source, InventorySourceStatus.Partial, "rotated-history-not-read")], ["Older rotated package history was not read; returned evidence may be incomplete."], rows.Length > arguments.Limit);
        }
        catch (UnauthorizedAccessException) { return Missing(source, "Permission was denied reading local package history."); }
        catch (IOException) { return Missing(source, "Local package history could not be read."); }
    }

    private static MaintenanceSnapshot<UpdateHistoryRecord> Missing(string source, string warning) => new([], [new(source, InventorySourceStatus.Unavailable, "history-unavailable")], [warning]);
}

internal static class LinuxUpdateHistoryParser
{
    internal sealed record Result(bool Valid, IReadOnlyList<UpdateHistoryRecord> Items);

    internal static Result Parse(string distro, string text, string source)
    {
        if (distro == "debian") return Apt(text, source);
        return LineHistory(text, source, distro == "zypper" ? '|' : ' ');
    }

    private static Result Apt(string text, string source)
    {
        var rows = new List<UpdateHistoryRecord>(); DateTimeOffset? timestamp = null; var open = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Start-Date:", StringComparison.Ordinal))
            {
                if (open || !TryLocal(line[11..].Trim(), out var date)) return new(false, []);
                timestamp = date; open = true; continue;
            }
            if (line.StartsWith("Upgrade:", StringComparison.Ordinal) && timestamp is not null)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(line[8..], "(?<name>[^,\\s]+)\\s+\\([^,]+,\\s*(?<version>[^)]+)\\)");
                if (matches.Count == 0) return new(false, []);
                foreach (System.Text.RegularExpressions.Match item in matches)
                    rows.Add(new(timestamp.GetValueOrDefault(), "deb:" + item.Groups["name"].Value, item.Groups["name"].Value, item.Groups["version"].Value.Trim(), "unknown", source));
            }
            if (line.StartsWith("End-Date:", StringComparison.Ordinal)) { if (!open) return new(false, []); open = false; }
        }
        return new(!open, rows);
    }

    private static Result LineHistory(string text, string source, char separator)
    {
        var rows = new List<UpdateHistoryRecord>();
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim(); if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            var fields = separator == '|' ? line.Split('|', StringSplitOptions.TrimEntries) : line.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !TryLocal(fields[0], out var timestamp)) return new(false, []);
            var name = fields[1]; if (string.IsNullOrWhiteSpace(name)) return new(false, []);
            rows.Add(new(timestamp, "pkg:" + name, name, null, "unknown", source));
        }
        return new(true, rows);
    }

    private static bool TryLocal(string value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local)) return false;
        timestamp = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUniversalTime(); return true;
    }
}
