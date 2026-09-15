// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace bOps.Packages.Docker.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips visibly — never silently — when no Docker daemon
/// responds, or when it cannot run the Linux containers these tests need (agentic/04-testing-
/// rules.md: skipped tests must say why). Mirrors <c>LinuxOnlyFactAttribute</c> and
/// <c>SymlinkCapableFactAttribute</c> elsewhere in this solution: the capability is probed once, at
/// construction, by actually attempting it.
///
/// Probes via the <c>docker</c> CLI rather than <see cref="IDockerClientFactory"/> deliberately:
/// a <see cref="FactAttribute"/> constructor cannot be <c>async</c>, and blocking on an async call
/// here (<c>.GetAwaiter().GetResult()</c>) is exactly what agentic/02-coding-standards.md's async
/// rules forbid — shelling out to a diagnostic CLI command from test infrastructure is a different
/// thing entirely from rule S1 (no generic execution tool exposed to the model) and stays fully
/// synchronous.
///
/// Checks the daemon's own container OS, not just that it responds: GitHub-hosted
/// <c>windows-latest</c> runners ship Docker Desktop switched to Windows containers, where a
/// Linux image such as <c>alpine</c> (used by <see cref="TestContainer"/>) can never start —
/// that is a real environment limitation, not a flaky test, so it must skip visibly rather than
/// fail.
/// </summary>
internal sealed class DockerAvailableFactAttribute : FactAttribute
{
    public DockerAvailableFactAttribute()
    {
        try
        {
            var serverOs = RunDocker("version --format {{.Server.Os}}");
            if (serverOs is null)
            {
                Skip = "No Docker daemon responded to 'docker version' within 5s.";
            }
            else if (!string.Equals(serverOs, "linux", StringComparison.OrdinalIgnoreCase))
            {
                Skip = $"Docker daemon is running Linux containers as '{serverOs}', not 'linux' " +
                    "(e.g. GitHub's windows-latest runner defaults to Windows containers) — " +
                    "this suite's images (alpine) require a Linux daemon.";
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Skip = $"Docker CLI not available: {ex.Message}";
        }
    }

    private static string? RunDocker(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        if (process is null || !process.WaitForExit(5000) || process.ExitCode != 0)
        {
            return null;
        }

        return process.StandardOutput.ReadToEnd().Trim();
    }
}
