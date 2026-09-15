// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Packages.Service.Linux;

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
}
