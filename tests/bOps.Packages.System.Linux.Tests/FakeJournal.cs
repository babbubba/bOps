// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Packages.Sys.Linux;

namespace bOps.Packages.System.Linux.Tests;

/// <summary>
/// A scripted stand-in for <c>journalctl</c>: a coverage probe (recognised by its fixed <c>--output-fields=_TRANSPORT</c> switch) is
/// answered with the oldest entry of the scope it asks for, and any other run with the scripted lines. Every argument list is recorded,
/// so a test can prove which journal scope a tool probed (review note R6).
/// </summary>
internal sealed class FakeJournal(DateTimeOffset? oldest, IReadOnlyDictionary<string, DateTimeOffset?>? oldestByScope = null)
{
    private readonly List<IReadOnlyList<string>> calls = [];

    /// <summary>Lines the non-probe run writes to standard output.</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>The exit code of the non-probe run.</summary>
    public int ExitCode { get; init; }

    /// <summary>The standard error of the non-probe run.</summary>
    public string StandardError { get; init; } = string.Empty;

    /// <summary>False to behave as if journalctl were not installed.</summary>
    public bool Started { get; init; } = true;

    /// <summary>Every argument list the tool ran journalctl with, in order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Calls => calls;

    /// <summary>The probe argument lists only.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Probes => calls.Where(IsProbe).ToArray();

    public Task<JournalRun> Run(IReadOnlyList<string> arguments, Func<string, bool> onLine, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        calls.Add(arguments.ToArray());
        if (!Started)
        {
            return Task.FromResult(new JournalRun(false, null, string.Empty, false));
        }

        if (IsProbe(arguments))
        {
            var scope = string.Join(' ', arguments.Skip(4));
            var value = oldestByScope is not null && oldestByScope.TryGetValue(scope, out var scoped) ? scoped : oldest;
            if (value is { } time)
            {
                var micros = (time.ToUnixTimeMilliseconds() * 1000).ToString(CultureInfo.InvariantCulture);
                onLine("{\"__REALTIME_TIMESTAMP\":\"" + micros + "\",\"_TRANSPORT\":\"kernel\"}");
                return Task.FromResult(new JournalRun(true, null, string.Empty, true));
            }

            onLine("-- No entries --");
            return Task.FromResult(new JournalRun(true, 0, string.Empty, false));
        }

        foreach (var line in Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (!onLine(line))
            {
                return Task.FromResult(new JournalRun(true, null, StandardError, true));
            }
        }

        return Task.FromResult(new JournalRun(true, ExitCode, StandardError, false));
    }

    /// <summary>A journal JSON line with a realtime timestamp and a message.</summary>
    public static string Line(DateTimeOffset time, string message, string transport = "kernel") =>
        "{\"__REALTIME_TIMESTAMP\":\"" + (time.ToUnixTimeMilliseconds() * 1000).ToString(CultureInfo.InvariantCulture)
        + "\",\"_TRANSPORT\":\"" + transport + "\",\"MESSAGE\":" + global::System.Text.Json.JsonSerializer.Serialize(message) + "}";

    private static bool IsProbe(IReadOnlyList<string> arguments) => arguments.Contains("--output-fields=_TRANSPORT");
}
