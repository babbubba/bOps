// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Reads the authoritative local package-manager history for the detected Linux family.</summary>
public sealed class LinuxUpdateHistoryTool : SystemUpdateHistoryToolBase
{
    private static readonly TimeSpan DnfTimeout = TimeSpan.FromSeconds(20);

    public LinuxUpdateHistoryTool() : base("linux") { }

    protected override async Task<MaintenanceSnapshot<UpdateHistoryRecord>> CollectAsync(MaintenanceArguments arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var distro = LinuxUpdateParser.Detect(File.Exists("/etc/os-release") ? await File.ReadAllTextAsync("/etc/os-release", ct).ConfigureAwait(false) : string.Empty);
        return distro switch
        {
            "debian" => await CollectDebianAsync(arguments, ct).ConfigureAwait(false),
            "dnf" => await CollectDnfAsync(arguments, ct).ConfigureAwait(false),
            "zypper" => await CollectZyppAsync(arguments, ct).ConfigureAwait(false),
            _ => Missing("linux.package-history", "Local package history source is unavailable or the distribution is unsupported."),
        };
    }

    private static async Task<MaintenanceSnapshot<UpdateHistoryRecord>> CollectDnfAsync(MaintenanceArguments arguments, CancellationToken ct)
    {
        const string source = "dnf-history";
        try
        {
            var run = await LinuxUpdatesProcessRunner.RunAsync("dnf", ["history", "list", "--reverse"], DnfTimeout, ct, configure: psi =>
            {
                psi.Environment["LC_ALL"] = "C";
                psi.Environment["LANG"] = "C";
            }).ConfigureAwait(false);
            return CreateDnfHistorySnapshot(run, arguments);
        }
        catch (Win32Exception) { return Missing(source, "DNF history command is unavailable."); }
        catch (IOException) { return Missing(source, "DNF history command could not be read."); }
    }

    internal static MaintenanceSnapshot<UpdateHistoryRecord> CreateDnfHistorySnapshot(LinuxUpdatesProcessRunner.Result run, MaintenanceArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(arguments);
        const string source = "dnf-history";
        if (!run.Started) return Missing(source, "DNF history command could not be started.");
        if (run.TimedOut) return Missing(source, "DNF history command timed out.");
        if (!run.Exited || run.ExitCode != 0) return Missing(source, "DNF history command did not complete successfully.");

        var parsed = LinuxUpdateHistoryParser.Parse("dnf", run.StandardOutput ?? string.Empty, source);
        return !parsed.Valid
            ? Missing(source, "DNF history output was malformed or ambiguous.")
            : Filter(parsed.Items, arguments, source, InventorySourceStatus.Available, parsed.Warnings);
    }

    private static async Task<MaintenanceSnapshot<UpdateHistoryRecord>> CollectZyppAsync(MaintenanceArguments arguments, CancellationToken ct)
    {
        const string path = "/var/log/zypp/history";
        const string source = "zypp-history";
        if (!File.Exists(path)) return Missing(source, "ZYpp history file is unavailable.");
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 4 * 1024 * 1024) return Missing(source, "ZYpp history file exceeds the bounded read size.");
            var parsed = LinuxUpdateHistoryParser.Parse("zypper", await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), source);
            if (!parsed.Valid) return Missing(source, "ZYpp history file contains a malformed package record.");

            var warnings = new List<string>(parsed.Warnings) { "Older rotated ZYpp history was not read; returned evidence may be incomplete." };
            return Filter(parsed.Items, arguments, source, InventorySourceStatus.Partial, warnings);
        }
        catch (UnauthorizedAccessException) { return Missing(source, "Permission was denied reading ZYpp history file."); }
        catch (IOException) { return Missing(source, "ZYpp history file could not be read."); }
    }

    private static async Task<MaintenanceSnapshot<UpdateHistoryRecord>> CollectDebianAsync(MaintenanceArguments arguments, CancellationToken ct)
    {
        const string path = "/var/log/apt/history.log";
        const string source = "linux.apt-history";
        if (!File.Exists(path)) return Missing(source, "Local package history source is unavailable or the distribution is unsupported.");
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 4 * 1024 * 1024) return Missing(source, "Local package history exceeds the bounded read size.");
            var parsed = LinuxUpdateHistoryParser.Parse("debian", await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), source);
            if (!parsed.Valid) return Missing(source, "Local package history is malformed or contains an incomplete transaction.");
            return Filter(parsed.Items, arguments, source, InventorySourceStatus.Partial, ["Older rotated package history was not read; returned evidence may be incomplete."]);
        }
        catch (UnauthorizedAccessException) { return Missing(source, "Permission was denied reading local package history."); }
        catch (IOException) { return Missing(source, "Local package history could not be read."); }
    }

    private static MaintenanceSnapshot<UpdateHistoryRecord> Filter(IReadOnlyList<UpdateHistoryRecord> items, MaintenanceArguments arguments, string source, InventorySourceStatus status, IReadOnlyList<string>? warnings)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-arguments.SinceDays!.Value);
        var rows = items.Where(x => x.TimestampUtc >= cutoff).ToArray();
        return new(rows, [new(source, status)], warnings, rows.Length > arguments.Limit);
    }

    private static MaintenanceSnapshot<UpdateHistoryRecord> Missing(string source, string warning) => new([], [new(source, InventorySourceStatus.Unavailable, "history-unavailable")], [warning]);
}

internal static class LinuxUpdateHistoryParser
{
    internal sealed record Result(bool Valid, IReadOnlyList<UpdateHistoryRecord> Items, IReadOnlyList<string> Warnings);

    internal static Result Parse(string distro, string text, string source) => distro switch
    {
        "debian" => Apt(text, source),
        "dnf" => Dnf(text, source),
        "zypper" => Zypp(text, source),
        _ => new(false, [], []),
    };

    private static Result Apt(string text, string source)
    {
        var rows = new List<UpdateHistoryRecord>(); DateTimeOffset? timestamp = null; var open = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Start-Date:", StringComparison.Ordinal))
            {
                if (open || !TryLocal(line[11..].Trim(), out var date)) return new(false, [], []);
                timestamp = date; open = true; continue;
            }
            if (line.StartsWith("Upgrade:", StringComparison.Ordinal) && timestamp is not null)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(line[8..], "(?<name>[^,\\s]+)\\s+\\([^,]+,\\s*(?<version>[^)]+)\\)");
                if (matches.Count == 0) return new(false, [], []);
                foreach (System.Text.RegularExpressions.Match item in matches)
                    rows.Add(new(timestamp.GetValueOrDefault(), "deb:" + item.Groups["name"].Value, item.Groups["name"].Value, item.Groups["version"].Value.Trim(), "unknown", source));
            }
            if (line.StartsWith("End-Date:", StringComparison.Ordinal)) { if (!open) return new(false, [], []); open = false; }
        }
        return new(!open, rows, []);
    }

    private static Result Dnf(string text, string source)
    {
        var lines = text.Split('\n');
        var headerIndex = Array.FindIndex(lines, IsDnfHeader);
        if (headerIndex < 0) return new(false, [], []);

        var rows = new List<UpdateHistoryRecord>();
        for (var index = headerIndex + 1; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (string.IsNullOrWhiteSpace(line) || line.All(ch => ch is '-' or ' ')) continue;
            var fields = lines[index].Split('|', StringSplitOptions.TrimEntries);
            if (fields.Length != 5 || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1 || !TryLocal(fields[2], out var timestamp)) return new(false, [], []);
            var name = string.IsNullOrWhiteSpace(fields[1]) ? fields[3] : fields[1];
            if (string.IsNullOrWhiteSpace(name)) return new(false, [], []);
            rows.Add(new(timestamp, $"dnf:{id}", name, null, DnfResult(fields[4]), source));
        }
        return new(true, rows, []);
    }

    private static bool IsDnfHeader(string line)
    {
        var fields = line.Split('|', StringSplitOptions.TrimEntries);
        return fields.Length == 5
            && string.Equals(fields[0], "ID", StringComparison.OrdinalIgnoreCase)
            && string.Equals(fields[1], "Command line", StringComparison.OrdinalIgnoreCase)
            && string.Equals(fields[2], "Date and time", StringComparison.OrdinalIgnoreCase)
            && string.Equals(fields[3], "Action(s)", StringComparison.OrdinalIgnoreCase)
            && string.Equals(fields[4], "Altered", StringComparison.OrdinalIgnoreCase);
    }

    private static string DnfResult(string altered)
    {
        if (altered.Contains('*') || altered.Contains('#')) return "failure";
        if (altered.Any(ch => !char.IsDigit(ch) && !char.IsWhiteSpace(ch) && ch is not ',' and not 'E' and not '>' and not '<')) return "unknown";
        return "success";
    }

    private static Result Zypp(string text, string source)
    {
        var rows = new List<UpdateHistoryRecord>();
        var warnings = new List<string>();
        var index = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            index++;
            var fields = line.Split('|', StringSplitOptions.TrimEntries);
            if (fields.Length < 2 || !TryLocal(fields[0], out var timestamp) || string.IsNullOrWhiteSpace(fields[1])) return new(false, [], []);
            var type = fields[1];
            if (string.Equals(type, "install", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "remove", StringComparison.OrdinalIgnoreCase))
            {
                if (fields.Length < 4 || string.IsNullOrWhiteSpace(fields[2]) || string.IsNullOrWhiteSpace(fields[3])) return new(false, [], []);
                rows.Add(new(timestamp, ZyppId(index, line), fields[2], fields[3], "success", source));
                continue;
            }
            if (type is "command" or "repo" or "radd" or "rremove") continue;
            warnings.Add("An unrecognized ZYpp history record was not interpreted.");
        }
        return new(true, rows, warnings);
    }

    private static string ZyppId(int index, string line) => $"zypp:{index}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)))[..12].ToLowerInvariant()}";

    private static bool TryLocal(string value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local)) return false;
        timestamp = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUniversalTime(); return true;
    }
}
