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

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly string executable;
    private readonly TimeSpan timeout;

    /// <summary>Creates the tool over the system <c>journalctl</c>.</summary>
    public LinuxSystemEventsTool()
        : this("journalctl", TimeProvider.System, DefaultTimeout)
    {
    }

    internal LinuxSystemEventsTool(string executable, TimeProvider clock, TimeSpan timeout)
        : base("linux", clock)
    {
        this.executable = executable;
        this.timeout = timeout;
    }

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
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
            var events = new List<SystemEventRecord>();
            var scanned = 0;
            var malformed = 0;
#pragma warning disable CA2025 // Awaited in the finally block below, before the process is disposed.
            var errorTask = ReadErrorAsync(process, linked.Token);
#pragma warning restore CA2025

            try
            {
                while (await process.StandardOutput.ReadLineAsync(linked.Token) is { } line)
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

                await process.WaitForExitAsync(linked.Token);
                var error = await errorTask;
                return Describe(events, process.ExitCode, reachedCeiling, malformed, error);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                Kill(process);
                return new SystemEventSnapshot(
                    events,
                    [new(SourceName, InventorySourceStatus.Partial, "journalctl did not finish in time and was stopped.")],
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
        List<SystemEventRecord> events, int exitCode, bool reachedCeiling, int malformed, string error)
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
            [new(SourceName, status, detail)],
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
