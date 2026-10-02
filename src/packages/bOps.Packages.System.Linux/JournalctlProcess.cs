// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>The outcome of one <c>journalctl</c> run.</summary>
/// <param name="Started">False when the executable could not be started.</param>
/// <param name="ExitCode">The exit code, or <c>null</c> when the run was stopped by its consumer.</param>
/// <param name="StandardError">At most the first 4 KiB of standard error.</param>
/// <param name="StoppedByConsumer">True when the line consumer asked to stop and the child was killed.</param>
internal sealed record JournalRun(bool Started, int? ExitCode, string StandardError, bool StoppedByConsumer);

/// <summary>Runs <c>journalctl</c> with <paramref name="arguments"/>, feeding each standard-output line to <paramref name="onLine"/> until it returns <c>false</c>.</summary>
internal delegate Task<JournalRun> JournalRunner(IReadOnlyList<string> arguments, Func<string, bool> onLine, CancellationToken ct);

/// <summary>
/// Starts <c>journalctl</c> directly in the ADR-0032 model: <c>UseShellExecute = false</c>, every switch fixed by the caller in code,
/// every value its own <see cref="ProcessStartInfo.ArgumentList"/> item, a fixed environment (<c>LC_ALL=C</c>, <c>TZ=UTC</c>, no pager,
/// no colours), standard output read line by line, standard error read up to a small cap, and the child killed when the consumer stops
/// or the token is cancelled. No shell is ever involved.
/// </summary>
internal static class JournalctlProcess
{
    private const int MaximumErrorBytes = 4_096;

    /// <summary>A runner over the given executable.</summary>
    internal static JournalRunner For(string executable) =>
        (arguments, onLine, ct) => RunAsync(executable, arguments, onLine, ct);

    internal static async Task<JournalRun> RunAsync(string executable, IReadOnlyList<string> arguments, Func<string, bool> onLine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(onLine);
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
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

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
            return new JournalRun(false, null, string.Empty, false);
        }

        using (process)
        {
            process.StandardInput.Close();
#pragma warning disable CA2025 // Awaited in the finally block below, before the process is disposed.
            var errorTask = ReadErrorAsync(process, ct);
#pragma warning restore CA2025
            try
            {
                var stopped = false;
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    if (!onLine(line))
                    {
                        stopped = true;
                        break;
                    }
                }

                if (stopped)
                {
                    Kill(process);
                }

                await process.WaitForExitAsync(ct);
                var error = await errorTask;
                return new JournalRun(true, stopped ? null : process.ExitCode, error, stopped);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }
            finally
            {
                await errorTask.ConfigureAwait(false);
            }
        }
    }

    internal static void Kill(Process process)
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

    private static async Task<string> ReadErrorAsync(Process process, CancellationToken ct)
    {
        var buffer = new char[MaximumErrorBytes];
        var read = 0;
        try
        {
            int count;
            while (read < buffer.Length && (count = await process.StandardError.ReadAsync(buffer.AsMemory(read), ct)) > 0)
            {
                read += count;
            }

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
}

/// <summary>
/// The temporal-coverage probe of the journal (ADR-0032 HARDEN-7 amendment §5): one fixed forward <c>journalctl</c> read of the first
/// entry visible to the host identity, after which the child is stopped. The probe always runs in the scope of the source it backs
/// (review note R6) — the kernel transport for kernel evidence, the system journal for core dumps, the requested transport for
/// <c>system.events</c> — so a broader stream cannot make a narrower source's history look longer than it is.
/// </summary>
internal static class JournalCoverageProbe
{
    /// <summary>The store name of the journal.</summary>
    internal const string StoreName = "linux.journald";

    /// <summary>The fixed probe arguments followed by the scope of the source.</summary>
    internal static IReadOnlyList<string> Arguments(IReadOnlyList<string> scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return ["--no-pager", "--output=json", "--utc", "--output-fields=_TRANSPORT", .. scope];
    }

    /// <summary>The coverage store of a source backed by the journal, probed in <paramref name="scope"/> within <paramref name="slice"/>.</summary>
    internal static async Task<CoverageStore> ProbeAsync(JournalRunner runner, IReadOnlyList<string> scope, IReadOnlyList<string> backedSources, EvidenceBudgetSlice slice)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(slice);
        return new CoverageStore(StoreName, CoverageBasis.Journal, await OldestAsync(runner, scope, slice), null, backedSources);
    }

    /// <summary>The realtime timestamp of the first entry in <paramref name="scope"/>, or <c>null</c> when it cannot be read in time.</summary>
    internal static async Task<DateTimeOffset?> OldestAsync(JournalRunner runner, IReadOnlyList<string> scope, EvidenceBudgetSlice slice)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(slice);
        if (slice.Token.IsCancellationRequested)
        {
            return slice.TimedOut ? null : throw new OperationCanceledException(slice.Token);
        }

        DateTimeOffset? oldest = null;
        try
        {
            var run = await runner(Arguments(scope), line =>
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal))
                {
                    return true;
                }

                oldest = RealtimeTimestamp(trimmed);
                return false;
            }, slice.Token);
            return run.Started ? oldest : null;
        }
        catch (OperationCanceledException) when (slice.TimedOut)
        {
            return null;
        }
    }

    /// <summary>The <c>__REALTIME_TIMESTAMP</c> of a JSON journal line, or <c>null</c>.</summary>
    internal static DateTimeOffset? RealtimeTimestamp(string line)
    {
        try
        {
            return JsonNode.Parse(line) is JsonObject json
                && json["__REALTIME_TIMESTAMP"] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var microseconds)
                && microseconds is > 0 and <= 253_402_300_799_000_000L
                    ? DateTimeOffset.UnixEpoch.AddTicks(microseconds * 10)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
