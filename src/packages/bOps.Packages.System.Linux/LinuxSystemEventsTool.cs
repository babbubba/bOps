// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>
/// Reads recent events from journald by running <c>journalctl</c> directly (ADR-0032): no shell, fixed switches, values as separate
/// arguments, a fixed environment, bounded output and a bounded run time. It asks for no privilege; what the host identity cannot
/// see is reported, not hidden.
/// </summary>
public sealed partial class LinuxSystemEventsTool : SystemEventsToolBase
{
    private const string SourceName = "linux.journald";
    private const int MaximumErrorCharacters = 200;
    private const int MaximumErrorBytes = 4_096;

    private static readonly TimeSpan ProbeCap = TimeSpan.FromSeconds(2);

    private readonly string executable;
    private readonly TimeSpan timeout;
    private readonly JournalRunner probe;

    /// <summary>Creates the tool over the system <c>journalctl</c>.</summary>
    public LinuxSystemEventsTool()
        : this("journalctl", TimeProvider.System, SystemEventsLimits.CallTimeout)
    {
    }

    internal LinuxSystemEventsTool(string executable, TimeProvider clock, TimeSpan timeout, JournalRunner? probe = null)
        : base("linux", clock)
    {
        this.executable = executable;
        this.timeout = timeout;
        this.probe = probe ?? JournalctlProcess.For(executable);
    }

    /// <summary>The journal scope of the coverage probe: the requested transport, else the whole journal the source reads (review note R6).</summary>
    internal static IReadOnlyList<string> ProbeScope(SystemEventQuery query) =>
        query.Channel is { } channel ? ["_TRANSPORT=" + channel.ToLowerInvariant()] : [];

    /// <inheritdoc />
    protected override string? ValidateEventId(string eventId) =>
        MessageIdPattern().IsMatch(eventId)
            ? null
            : "eventId must be a 32-digit hexadecimal MESSAGE_ID on Linux.";

    /// <inheritdoc />
    protected override string? ValidateChannel(string channel) =>
        JournalRecordParser.Transports.Contains(channel, StringComparer.OrdinalIgnoreCase)
            ? null
            : $"channel must be one of: {string.Join(", ", JournalRecordParser.Transports)}.";

    /// <inheritdoc />
    protected override async Task<SystemEventSnapshot> CollectAsync(SystemEventQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var budget = new EvidenceTimeBudget(timeout, Clock);
        CoverageStore store;
        using (var probeSlice = budget.Start(2, ct, ProbeCap))
        {
            store = await JournalCoverageProbe.ProbeAsync(probe, ProbeScope(query), [SourceName], probeSlice);
        }

        var snapshot = await ReadAsync(query, budget, ct);
        return snapshot with { Stores = [store] };
    }

    private async Task<SystemEventSnapshot> ReadAsync(SystemEventQuery query, EvidenceTimeBudget budget, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in JournalctlArguments.Build(query))
        {
            startInfo.ArgumentList.Add(argument);
        }

        // A fixed environment: journalctl's wording and time zone must not depend on the caller's, and it must never page.
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["TZ"] = "UTC";
        startInfo.Environment["SYSTEMD_PAGER"] = string.Empty;
        startInfo.Environment["SYSTEMD_COLORS"] = "0";

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("journalctl could not be started.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return Unavailable("journalctl could not be started: it is not installed or not executable.");
        }

        using (process)
        {
            process.StandardInput.Close();
            using var slice = budget.Start(1, ct);
            DateTimeOffset? oldestRead = null;
            var events = new List<SystemEventRecord>();
            var scanned = 0;
            var malformed = 0;
#pragma warning disable CA2025 // Awaited in the finally block below, before the process is disposed.
            var errorTask = ReadErrorAsync(process, slice.Token);
#pragma warning restore CA2025

            try
            {
                while (await process.StandardOutput.ReadLineAsync(slice.Token) is { } line)
                {
                    var kind = JournalRecordParser.TryParse(line, out var record);
                    if (kind == JournalRecordParser.LineKind.Malformed)
                    {
                        malformed++;
                        continue;
                    }

                    if (kind != JournalRecordParser.LineKind.Record)
                    {
                        continue;
                    }

                    scanned++;
                    oldestRead = oldestRead is { } known && known < record!.TimestampUtc ? known : record!.TimestampUtc;
                    if (SystemEventFilter.Matches(record!, query))
                    {
                        events.Add(record!);
                    }

                    if (scanned >= query.ScanCeiling)
                    {
                        break;
                    }
                }

                var reachedCeiling = scanned >= query.ScanCeiling;
                if (reachedCeiling)
                {
                    Kill(process);
                }

                await process.WaitForExitAsync(slice.Token);
                var error = await errorTask;
                return Describe(events, process.ExitCode, reachedCeiling, malformed, error, reachedCeiling ? oldestRead : query.FromUtc);
            }
            catch (OperationCanceledException) when (slice.TimedOut)
            {
                Kill(process);
                return new SystemEventSnapshot(
                    events,
                    [new(SourceName, InventorySourceStatus.Partial, "journalctl did not finish in time and was stopped.") { ExaminedFromUtc = oldestRead }],
                    CollectionTruncated: true);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }
            finally
            {
                // The error reader uses the process, which is disposed next: let it finish first (it ends when the process does).
                await errorTask.ConfigureAwait(false);
            }
        }
    }

    private static SystemEventSnapshot Describe(
        List<SystemEventRecord> events, int exitCode, bool reachedCeiling, int malformed, string error, DateTimeOffset? examinedFromUtc)
    {
        if (!reachedCeiling && exitCode != 0)
        {
            return Unavailable($"journalctl exited with code {exitCode}{FirstLine(error)}", events);
        }

        InventorySourceStatus status;
        string? detail = null;
        if (error.Contains("not seeing messages from other users", StringComparison.Ordinal))
        {
            status = InventorySourceStatus.Partial;
            detail = "The host identity sees only its own journal: it is not in the adm or systemd-journal group, so system messages are missing.";
        }
        else if (malformed > 0)
        {
            status = InventorySourceStatus.Partial;
            detail = $"{malformed} journal record(s) could not be read and were skipped.";
        }
        else
        {
            status = InventorySourceStatus.Available;
        }

        return new SystemEventSnapshot(
            events,
            [new(SourceName, status, detail) { ExaminedFromUtc = examinedFromUtc }],
            CollectionTruncated: reachedCeiling);
    }

    private static SystemEventSnapshot Unavailable(string detail, IReadOnlyList<SystemEventRecord>? events = null) =>
        new(events ?? [], [new(SourceName, InventorySourceStatus.Unavailable, detail)]);

    private static string FirstLine(string error)
    {
        var line = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line)
            ? "."
            : ": " + (line.Length > MaximumErrorCharacters ? line[..MaximumErrorCharacters] : line);
    }

    private static async Task<string> ReadErrorAsync(Process process, CancellationToken ct)
    {
        var buffer = new char[MaximumErrorBytes];
        var read = 0;
        try
        {
            int count;
            while (read < buffer.Length
                && (count = await process.StandardError.ReadAsync(buffer.AsMemory(read), ct)) > 0)
            {
                read += count;
            }

            // Drain what is left so journalctl never blocks on a full pipe, without keeping it.
            var discard = new char[512];
            while (await process.StandardError.ReadAsync(discard.AsMemory(), ct) > 0)
            {
            }
        }
        catch (OperationCanceledException)
        {
            // The caller is cancelling or timing out; what was read so far is all that is needed.
        }

        return new string(buffer, 0, read);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // It ended between the check and the kill.
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex MessageIdPattern();
}
