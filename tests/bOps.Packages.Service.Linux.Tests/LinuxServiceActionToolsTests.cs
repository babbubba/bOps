// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Packages.Service.Linux;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace bOps.Packages.Service.Linux.Tests;

/// <summary>
/// Manifest and wiring checks for <c>service.start</c>/<c>stop</c>/<c>restart</c> on Linux —
/// deliberately not a real start/stop/restart lifecycle test against a throwaway systemd unit,
/// unlike <c>bOps.Packages.Service.Windows.Tests</c>'s <c>WindowsServiceActionToolsTests</c>.
/// Creating a system-scope unit and actually starting/stopping it requires root (or a polkit rule
/// this project does not control), and CI's <c>Test</c> step (<c>.github/workflows/ci.yml</c>)
/// runs as the unprivileged default runner user, not root — this dev environment has no Linux
/// host to verify a workaround against either. The shared <c>SystemctlInvoker</c> code path these
/// three tools use is already exercised for real by <c>service.list</c>/<c>service.status</c>'s
/// own CI-verified tests; what is not exercised here is the full elevated lifecycle. This is a
/// deliberate, documented scope boundary (HANDOFF.md), not a gap glossed over.
/// </summary>
[Trait("Platform", "Linux")]
public sealed class LinuxServiceActionToolsTests
{
    private static readonly string[] AllowedDisabledStates = ["disabled", "static", "indirect", "", "not-found"];
    [Theory]
    [InlineData("service.start", "running")]
    [InlineData("service.stop", "stopped")]
    [InlineData("service.restart", "running")]
    public void ActionTools_DeclareVerificationAgainstServiceStatus(string toolName, string expectedInDescription)
    {
        var tool = new LinuxServiceToolProvider().GetTools().Single(t => t.Manifest.Name == toolName);

        Assert.Equal(RiskLevel.Medium, tool.Manifest.Risk);
        Assert.NotNull(tool.Manifest.Verification);
        Assert.Equal("service.status", tool.Manifest.Verification!.VerifyToolName);
        Assert.Contains(expectedInDescription, tool.Manifest.Verification.Description, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IVerifiableTool>(tool);
    }

    [Theory]
    [InlineData("service.start")]
    [InlineData("service.stop")]
    [InlineData("service.restart")]
    public async Task ActionTools_RejectAnImplausibleUnitName_WithoutInvokingSystemctl(string toolName)
    {
        // No systemctl subprocess is spawned for a name that cannot possibly be a unit — this
        // needs no privilege and no real systemd interaction, so it runs everywhere, including
        // this dev environment despite it having no Linux host to run systemctl against for real.
        var tool = new LinuxServiceToolProvider().GetTools().Single(t => t.Manifest.Name == toolName);
        var arguments = ToolArguments.FromJson(
            new System.Text.Json.Nodes.JsonObject { ["name"] = "not a plausible unit name; rm -rf /" });

        var result = await tool.ExecuteAsync(arguments);

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
    }

    [RealSystemdFact]
    [Trait("Platform", "Linux")]
    public void RealSystemdServiceEnableDisableLifecycle()
    {
        var unit = $"bops-quality-{Guid.NewGuid():N}.service";
        var path = $"/etc/systemd/system/{unit}";
        File.WriteAllText(path, "[Unit]\nDescription=bOps quality test-owned service\n[Service]\nType=oneshot\nExecStart=/bin/true\n[Install]\nWantedBy=multi-user.target\n");
        try
        {
            Run("systemctl", "daemon-reload");
            Run("systemctl", "enable", unit);
            Assert.Equal("enabled", Run("systemctl", "is-enabled", unit).Trim());
            Run("systemctl", "disable", unit);
            Assert.Contains(RunCore("systemctl", ["is-enabled", unit], true).Trim(), AllowedDisabledStates, StringComparer.OrdinalIgnoreCase);
            Run("systemctl", "enable", unit);
            Assert.Equal("enabled", Run("systemctl", "is-enabled", unit).Trim());
        }
        finally
        {
            RunCore("systemctl", ["disable", unit], true);
            File.Delete(path);
            RunCore("systemctl", ["daemon-reload"], true);
        }
    }

    private sealed class RealSystemdFactAttribute : FactAttribute
    {
        public RealSystemdFactAttribute()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) Skip = "Requires Linux.";
            else if (Environment.GetEnvironmentVariable("BOPS_RUN_REAL_SYSTEMD_TESTS") != "1") Skip = "Requires explicit real-systemd opt-in.";
            else if (!string.Equals(Environment.UserName, "root", StringComparison.Ordinal)) Skip = "Requires root.";
            else if (!File.Exists("/run/systemd/system")) Skip = "Requires systemd.";
        }
    }

    private static string Run(string executable, params string[] arguments)
        => RunCore(executable, arguments, false);

    private static string RunCore(string executable, string[] arguments, bool allowFailure)
    {
        var startInfo = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo);
        if (process is null) throw new InvalidOperationException($"Could not start {executable}.");
        process.WaitForExit();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!allowFailure && process.ExitCode != 0) throw new InvalidOperationException($"{executable} failed ({process.ExitCode}): {error}");
        return output;
    }
}
