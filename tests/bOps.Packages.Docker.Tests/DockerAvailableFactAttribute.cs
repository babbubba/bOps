using System.Diagnostics;

namespace bOps.Packages.Docker.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips visibly — never silently — when no Docker daemon
/// responds (agentic/04-testing-rules.md: skipped tests must say why). Mirrors
/// <c>LinuxOnlyFactAttribute</c> and <c>SymlinkCapableFactAttribute</c> elsewhere in this
/// solution: the capability is probed once, at construction, by actually attempting it.
///
/// Probes via the <c>docker</c> CLI rather than <see cref="IDockerClientFactory"/> deliberately:
/// a <see cref="FactAttribute"/> constructor cannot be <c>async</c>, and blocking on an async call
/// here (<c>.GetAwaiter().GetResult()</c>) is exactly what agentic/02-coding-standards.md's async
/// rules forbid — shelling out to a diagnostic CLI command from test infrastructure is a different
/// thing entirely from rule S1 (no generic execution tool exposed to the model) and stays fully
/// synchronous.
/// </summary>
internal sealed class DockerAvailableFactAttribute : FactAttribute
{
    public DockerAvailableFactAttribute()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "version --format {{.Server.Version}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null || !process.WaitForExit(5000) || process.ExitCode != 0)
            {
                Skip = "No Docker daemon responded to 'docker version' within 5s.";
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Skip = $"Docker CLI not available: {ex.Message}";
        }
    }
}
