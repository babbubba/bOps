// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>
/// Collects bounded systemd-coredump metadata for <c>system.crashes</c> (schema 2). Core contents are never opened. A core dump's
/// timestamp is when it happened (<c>occurred</c>); its <c>code</c> is the terminating signal number. The <c>coredumpctl</c> run and the
/// journal coverage probe share one 15-second bound.
/// </summary>
public sealed class LinuxCrashEvidenceTool : SystemCrashesToolBase
{
    internal const string SourceName = "linux-coredumpctl";
    private const string CorePatternSource = "linux-core-pattern";
    private const int MaximumRows = 1001;

    /// <summary>The coverage probe scope: systemd-coredump records live in the system journal (review note R6).</summary>
    internal static readonly IReadOnlyList<string> ProbeScope = ["--system"];

    private static readonly TimeSpan ProbeCap = TimeSpan.FromSeconds(2);

    private readonly Func<string?> corePattern;
    private readonly TimeSpan timeout;
    private readonly Func<IReadOnlyList<string>, TimeSpan, CancellationToken, Task<LinuxUpdatesProcessRunner.Result>> runQuery;
    private readonly JournalRunner probe;

    public LinuxCrashEvidenceTool() : this("coredumpctl", ReadCorePattern, TimeProvider.System, TimeSpan.FromSeconds(15)) { }

    internal LinuxCrashEvidenceTool(string executable, Func<string?> corePattern, TimeProvider clock, TimeSpan timeout,
        Func<IReadOnlyList<string>, TimeSpan, CancellationToken, Task<LinuxUpdatesProcessRunner.Result>>? runQuery = null,
        JournalRunner? probe = null) : base("linux", clock)
    {
        this.corePattern = corePattern;
        this.timeout = timeout;
        this.runQuery = runQuery ?? ((args, duration, token) => LinuxUpdatesProcessRunner.RunAsync(executable, args, duration, token, psi => { psi.Environment["LC_ALL"] = "C"; psi.Environment["LANG"] = "C"; psi.Environment["TZ"] = "UTC"; psi.Environment["SYSTEMD_PAGER"] = ""; }));
        this.probe = probe ?? JournalctlProcess.For("journalctl");
    }

    protected override async Task<CrashEvidenceSnapshot> CollectAsync(CrashesQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var budget = new EvidenceTimeBudget(timeout, Clock);
        CoverageStore store;
        using (var probeSlice = budget.Start(2, ct, ProbeCap))
        {
            store = await JournalCoverageProbe.ProbeAsync(probe, ProbeScope, [SourceName], probeSlice);
        }

        var snapshot = await QueryAsync(query, budget.Slice(1), ct);
        return snapshot with { Stores = [store] };
    }

    private async Task<CrashEvidenceSnapshot> QueryAsync(CrashesQuery query, TimeSpan duration, CancellationToken ct)
    {
        var sinceText = query.FromUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        LinuxUpdatesProcessRunner.Result run;
        try
        {
            run = await runQuery(["--no-pager", "--json=short", "--since", sinceText, "-n", MaximumRows.ToString(CultureInfo.InvariantCulture), "list"], duration, ct);
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
                return new([], [new(SourceName, InventorySourceStatus.Available) { ExaminedFromUtc = query.FromUtc }]);
            var permission = error.Contains("permission", StringComparison.OrdinalIgnoreCase) || error.Contains("access denied", StringComparison.OrdinalIgnoreCase);
            return Failure(permission ? "coredumpctl journal access denied" : "systemd-coredump journal unavailable or query failed", permission ? "permission denied" : "journal unavailable");
        }

        if (!TryParse(run.StandardOutput, query.FromUtc, out var parsed, out var rowCount)) return Failure("coredumpctl returned malformed metadata", "malformed output");
        var deduped = parsed.Where(x => string.IsNullOrWhiteSpace(x.EventIdOrCrashId))
            .Concat(parsed.Where(x => !string.IsNullOrWhiteSpace(x.EventIdOrCrashId)).GroupBy(x => x.EventIdOrCrashId, StringComparer.Ordinal).Select(g => g.First()))
            .ToArray();
        var truncated = rowCount >= MaximumRows;
        var warnings = truncated ? new[] { "crash evidence truncated by the coredumpctl row ceiling" } : Array.Empty<string>();
        var examined = truncated ? (deduped.Length == 0 ? (DateTimeOffset?)null : deduped.Min(x => x.TimestampUtc)) : query.FromUtc;
        return new(deduped, [new(SourceName, truncated ? InventorySourceStatus.Partial : InventorySourceStatus.Available, truncated ? "The coredumpctl row ceiling was reached." : null) { ExaminedFromUtc = examined }], warnings, truncated);
    }

    internal static bool TryParse(string json, DateTimeOffset since, out IReadOnlyList<CrashEvidence> records) =>
        TryParse(json, since, out records, out _);

    internal static bool TryParse(string json, DateTimeOffset since, out IReadOnlyList<CrashEvidence> records, out int rowCount)
    {
        records = [];
        rowCount = 0;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() > MaximumRows) return false;
            rowCount = doc.RootElement.GetArrayLength();
            var rows = new List<CrashEvidence>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return false;
                if (!TryTimestamp(item, out var utc)) return false;
                if (utc < since) continue;
                var exe = String(item, "exe", "EXE", "COREDUMP_EXE");
                var comm = String(item, "comm", "COMM", "COREDUMP_COMM");
                var process = !string.IsNullOrWhiteSpace(exe) ? Path.GetFileName(exe) : comm;
                var id = String(item, "COREDUMP_ID", "id", "cursor");
                rows.Add(new CrashEvidence(utc, EvidenceTimestampKind.Occurred, SourceName, CrashKinds.Coredump)
                {
                    Process = Bound(process, 512),
                    Pid = Int(item, "pid", "PID", "COREDUMP_PID"),
                    DumpPath = Bound(String(item, "COREDUMP_FILENAME", "dumpPath", "COREFILE_PATH", "filename"), 2048),
                    EventIdOrCrashId = Bound(id, 256),
                    SignalCode = Int(item, "sig", "SIGNAL", "COREDUMP_SIGNAL") is { } signal ? signal.ToString(CultureInfo.InvariantCulture) : null,
                    EvidenceSources = [SourceName],
                });
            }

            records = rows;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private CrashEvidenceSnapshot Fallback(string message, string detail)
    {
        var pattern = corePattern();
        var suffix = string.IsNullOrWhiteSpace(pattern) ? "" : pattern.TrimStart().StartsWith('|') ? "; piped core handler configured" : "; core pattern metadata observed";
        return new([], [new(CorePatternSource, InventorySourceStatus.Unavailable, detail)], [$"{message}; only core pattern metadata observed{suffix}"]);
    }

    private static CrashEvidenceSnapshot Failure(string warning, string detail) => new([], [new(SourceName, InventorySourceStatus.Unavailable, detail)], [warning]);
    private static string? ReadCorePattern() { try { return File.ReadAllText("/proc/sys/kernel/core_pattern").Trim(); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } }
    private static string? String(JsonElement item, params string[] keys) { foreach (var key in keys) if (item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString(); return null; }
    private static int? Int(JsonElement item, params string[] keys) { foreach (var key in keys) if (item.TryGetProperty(key, out var value)) { if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n > 0) return n; if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0) return n; } return null; }

    /// <summary>The core dump's time: <c>time</c> (microseconds since the epoch, as <c>coredumpctl --json</c> writes it) or a textual timestamp.</summary>
    private static bool TryTimestamp(JsonElement item, out DateTimeOffset utc)
    {
        utc = default;
        foreach (var key in new[] { "time", "timestamp", "TIME", "COREDUMP_TIMESTAMP" })
        {
            if (!item.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var microseconds) && FromMicroseconds(microseconds, out utc))
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch) && FromMicroseconds(epoch, out utc))
                {
                    return true;
                }

                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc))
                {
                    return true;
                }
            }

            return false;
        }

        return false;
    }

    private static bool FromMicroseconds(long microseconds, out DateTimeOffset utc)
    {
        utc = default;
        if (microseconds is <= 0 or > 253_402_300_799_000_000L)
        {
            return false;
        }

        utc = DateTimeOffset.UnixEpoch.AddTicks(microseconds * 10);
        return true;
    }

    private static string? Bound(string? value, int size) => value is null || value.Length <= size ? value : value[..size];
}
