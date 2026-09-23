// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>Collects bounded coredump journal metadata. Core contents are never opened.</summary>
public sealed class LinuxCrashEvidenceTool : SystemCrashesToolBase
{
    private const string SourceName = "linux-coredumpctl";
    private const int MaximumRows = 1001;
    private readonly string executable;
    private readonly Func<string?> corePattern;
    private readonly TimeProvider clock;
    private readonly TimeSpan timeout;
    private readonly Func<IReadOnlyList<string>, TimeSpan, CancellationToken, Task<LinuxUpdatesProcessRunner.Result>> runQuery;

    public LinuxCrashEvidenceTool() : this("coredumpctl", ReadCorePattern, TimeProvider.System, TimeSpan.FromSeconds(15)) { }

    internal LinuxCrashEvidenceTool(string executable, Func<string?> corePattern, TimeProvider clock, TimeSpan timeout,
        Func<IReadOnlyList<string>, TimeSpan, CancellationToken, Task<LinuxUpdatesProcessRunner.Result>>? runQuery = null) : base("linux")
    { this.executable = executable; this.corePattern = corePattern; this.clock = clock; this.timeout = timeout; this.runQuery = runQuery ?? ((args, duration, token) => LinuxUpdatesProcessRunner.RunAsync(executable, args, duration, token, psi => { psi.Environment["LC_ALL"] = "C"; psi.Environment["LANG"] = "C"; psi.Environment["TZ"] = "UTC"; psi.Environment["SYSTEMD_PAGER"] = ""; })); }

    protected override async Task<MaintenanceSnapshot<CrashRecord>> CollectAsync(MaintenanceArguments arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var since = clock.GetUtcNow().AddMinutes(-arguments.SinceMinutes!.Value);
        var queryLimit = Math.Min(arguments.Limit + 1, MaximumRows);
        var sinceText = since.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        LinuxUpdatesProcessRunner.Result run;
        try
        {
            run = await runQuery(["--no-pager", "--json=short", "--since", sinceText, "-n", queryLimit.ToString(CultureInfo.InvariantCulture), "list"], timeout, ct);
        }
        catch (Win32Exception)
        {
            return Fallback("coredumpctl unavailable; crash history unavailable", "unavailable");
        }
        if (!run.Started) return Fallback("coredumpctl unavailable; crash history unavailable", "unavailable");
        if (run.TimedOut) return Failure("coredumpctl query timed out", "timeout");
        if (run.StandardOutput is null || run.StandardOutput.Length >= 1_048_576)
            return Failure("coredumpctl output exceeded the bounded limit", "oversized output");
        if (run.ExitCode != 0)
        {
            var error = run.StandardError ?? "";
            if (error.Contains("no coredumps found", StringComparison.OrdinalIgnoreCase) || error.Contains("no coredump found", StringComparison.OrdinalIgnoreCase))
                return new([], [new(SourceName, InventorySourceStatus.Available)]);
            var permission = error.Contains("permission", StringComparison.OrdinalIgnoreCase) || error.Contains("access denied", StringComparison.OrdinalIgnoreCase);
            return Failure(permission ? "coredumpctl journal access denied" : "systemd-coredump journal unavailable or query failed", permission ? "permission denied" : "journal unavailable");
        }
        if (!TryParse(run.StandardOutput, since, out var parsed)) return Failure("coredumpctl returned malformed metadata", "malformed output");
        var deduped = parsed.Where(x => string.IsNullOrWhiteSpace(x.EventIdOrCrashId))
            .Concat(parsed.Where(x => !string.IsNullOrWhiteSpace(x.EventIdOrCrashId)).GroupBy(x => x.EventIdOrCrashId, StringComparer.Ordinal).Select(g => g.First()))
            .OrderByDescending(x => x.TimestampUtc).ThenBy(x => x.EventIdOrCrashId, StringComparer.Ordinal).ToArray();
        var truncated = deduped.Length > arguments.Limit || parsed.Count >= queryLimit;
        var rows = deduped.Take(arguments.Limit).ToArray();
        var warnings = truncated ? new[] { "crash evidence truncated by requested limit" } : Array.Empty<string>();
        return new(rows, [new(SourceName, InventorySourceStatus.Available)], warnings, truncated);
    }

    internal static bool TryParse(string json, DateTimeOffset since, out IReadOnlyList<CrashRecord> records)
    {
        records = [];
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() > MaximumRows) return false;
            var rows = new List<CrashRecord>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return false;
                var timestamp = String(item, "timestamp", "TIME", "COREDUMP_TIMESTAMP");
                if (!TryTimestamp(timestamp, out var utc)) return false;
                if (utc < since) continue;
                var pid = Int(item, "pid", "PID", "COREDUMP_PID");
                var exe = String(item, "exe", "EXE", "COREDUMP_EXE");
                var comm = String(item, "comm", "COMM", "COREDUMP_COMM");
                var process = !string.IsNullOrWhiteSpace(exe) ? Path.GetFileName(exe) : comm;
                var id = String(item, "COREDUMP_ID", "id", "cursor");
                var path = String(item, "COREDUMP_FILENAME", "dumpPath", "COREFILE_PATH");
                var summary = String(item, "message", "MESSAGE", "summary", "signal", "SIGNAL");
                rows.Add(new(utc, Bound(process, 512), pid, "coredump", Bound(path, 2048), Bound(id, 256), Bound(summary, 1024), SourceName));
            }
            records = rows;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private MaintenanceSnapshot<CrashRecord> Fallback(string message, string detail)
    {
        var pattern = corePattern();
        var suffix = string.IsNullOrWhiteSpace(pattern) ? "" : pattern.TrimStart().StartsWith('|') ? "; piped core handler configured" : "; core pattern metadata observed";
        return new([], [new("linux-core-pattern", InventorySourceStatus.Unavailable, detail)], [$"{message}; only core pattern metadata observed{suffix}" ]);
    }
    private static MaintenanceSnapshot<CrashRecord> Failure(string warning, string detail) => new([], [new(SourceName, InventorySourceStatus.Unavailable, detail)], [warning]);
    private static string? ReadCorePattern() { try { return File.ReadAllText("/proc/sys/kernel/core_pattern").Trim(); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } }
    private static string? String(JsonElement item, params string[] keys) { foreach (var key in keys) if (item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString(); return null; }
    private static int? Int(JsonElement item, params string[] keys) { foreach (var key in keys) if (item.TryGetProperty(key, out var value)) { if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n > 0) return n; if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0) return n; } return null; }
    private static bool TryTimestamp(string? value, out DateTimeOffset utc)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        { try { utc = DateTimeOffset.UnixEpoch.AddTicks(checked(epoch * 10)); return true; } catch (ArgumentOutOfRangeException) { } catch (OverflowException) { } }
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
    }
    private static string? Bound(string? value, int size) => value is null || value.Length <= size ? value : value[..size];
}
