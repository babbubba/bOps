// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace bOps.Packages.Sys.Windows;

/// <summary>What one bounded debugger process run produced.</summary>
/// <param name="Started">False when the executable could not be started at all.</param>
/// <param name="TimedOut">True when the run hit its own bound and the process tree was killed.</param>
/// <param name="ExitCode">The exit code, or null when the process did not exit by itself.</param>
/// <param name="StandardOutput">Standard output up to the character cap.</param>
/// <param name="StandardOutputTruncated">True when standard output exceeded the cap; the rest was drained and discarded.</param>
/// <param name="StandardError">Standard error up to its cap.</param>
internal sealed record DebuggerProcessResult(
    bool Started, bool TimedOut, int? ExitCode, string StandardOutput, bool StandardOutputTruncated, string StandardError)
{
    internal static DebuggerProcessResult NotStarted { get; } = new(false, false, null, string.Empty, false, string.Empty);
}

/// <summary>
/// Runs one debugger executable directly (ADR-0048 §6): never a shell, never a string command line, no window, no console
/// input. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>; standard output and error are drained concurrently
/// into bounded buffers; the whole process tree is killed when the bound elapses or the caller cancels; the caller's
/// cancellation is rethrown, the run's own bound is reported as <see cref="DebuggerProcessResult.TimedOut"/>.
/// </summary>
internal static class DebuggerProcessRunner
{
    internal const int StandardErrorCharacters = 64 * 1024;

    /// <summary>
    /// Environment variables that would let machine configuration change what the debugger loads: symbol, image, source and
    /// extension search paths, the dbghelp options, and the tools.ini location.
    /// </summary>
    internal static bool IsStrippedEnvironmentVariable(string name) =>
        name.StartsWith("_NT_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("DBGHELP_", StringComparison.OrdinalIgnoreCase)
        || name.Equals("INIT", StringComparison.OrdinalIgnoreCase);

    /// <summary>The start information for <paramref name="executable"/>; the only way this package builds one.</summary>
    internal static ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var name in startInfo.Environment.Keys.Where(IsStrippedEnvironmentVariable).ToArray())
        {
            startInfo.Environment.Remove(name);
        }

        return startInfo;
    }

    /// <summary>Runs <paramref name="startInfo"/> within <paramref name="timeout"/>.</summary>
    internal static async Task<DebuggerProcessResult> RunAsync(
        ProcessStartInfo startInfo, TimeSpan timeout, int maximumOutputCharacters, CancellationToken ct)
    {
        if (startInfo.UseShellExecute)
        {
            throw new InvalidOperationException("Debugger processes are never started through a shell.");
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return DebuggerProcessResult.NotStarted;
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return DebuggerProcessResult.NotStarted;
        }

        // No interactive console: input is closed at once, so a debugger waiting for a command reads end-of-file and exits.
        process.StandardInput.Close();
        var output = DrainAsync(process.StandardOutput, maximumOutputCharacters);
        var error = DrainAsync(process.StandardError, StandardErrorCharacters);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested)
            {
                await Task.WhenAll(output, error).ConfigureAwait(false);
                throw;
            }

            timedOut = true;
        }

        var (stdout, truncated) = await output.ConfigureAwait(false);
        var (stderr, _) = await error.ConfigureAwait(false);
        return new DebuggerProcessResult(true, timedOut, timedOut ? null : process.ExitCode, stdout, truncated, stderr);
    }

    /// <summary>Reads to end of stream, keeping at most <paramref name="maximumCharacters"/>; never stops draining early.</summary>
    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, int maximumCharacters)
    {
        var buffer = new char[8192];
        var text = new StringBuilder();
        var truncated = false;
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                var keep = Math.Min(count, maximumCharacters - text.Length);
                if (keep > 0)
                {
                    text.Append(buffer, 0, keep);
                }

                truncated |= keep < count;
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            truncated = true;
        }

        return (text.ToString(), truncated);
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
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }

        try
        {
            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
        }
    }
}
