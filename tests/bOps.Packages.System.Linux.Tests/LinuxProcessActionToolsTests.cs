// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Linux;

namespace bOps.Packages.System.Linux.Tests;

/// <summary>
/// Integration tests against real, test-owned child processes this process spawns and always
/// cleans up (never a real unrelated process — agentic/04-testing-rules.md, "Never mock the
/// operating system", applied with the same "own throwaway resource" discipline
/// <c>FsToolsTests</c> uses for its temp directory). There is no Linux host in this dev
/// environment; these run on CI's real ubuntu-latest VM (ADR-0021).
/// </summary>
[Trait("Platform", "Linux")]
public sealed class LinuxProcessActionToolsTests
{
    private static ToolArguments PidArgs(int pid) => ToolArguments.FromJson(new JsonObject { ["pid"] = pid });

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static Process StartSleeper() =>
        Process.Start(new ProcessStartInfo("sleep", "300") { UseShellExecute = false })
            ?? throw new InvalidOperationException("Could not start the test process.");

    [LinuxOnlyFact]
    public async Task ProcessKill_TerminatesARealSpawnedProcess()
    {
        using var process = StartSleeper();
        var tool = new LinuxProcessKillTool();

        var result = await tool.ExecuteAsync(PidArgs(process.Id));

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(await WaitForExitAsync(process, TimeSpan.FromSeconds(5)));
    }

    [LinuxOnlyFact]
    public async Task ProcessKill_Fails_ForAPidThatIsNotRunning()
    {
        using var process = StartSleeper();
        process.Kill();
        await WaitForExitAsync(process, TimeSpan.FromSeconds(5));
        var tool = new LinuxProcessKillTool();

        var result = await tool.ExecuteAsync(PidArgs(process.Id));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
    }

    [LinuxOnlyFact]
    public async Task ProcessStop_TerminatesARealSpawnedProcess()
    {
        // sleep installs no SIGTERM handler, so the default action (terminate) applies — unlike
        // Windows, there is no "process with no graceful mechanism" case to test here: every
        // process on Linux can be sent SIGTERM.
        using var process = StartSleeper();
        var tool = new LinuxProcessStopTool();

        var result = await tool.ExecuteAsync(PidArgs(process.Id));

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(await WaitForExitAsync(process, TimeSpan.FromSeconds(5)));
    }

    [LinuxOnlyFact]
    public async Task ProcessStop_Fails_ForAPidThatIsNotRunning()
    {
        using var process = StartSleeper();
        process.Kill();
        await WaitForExitAsync(process, TimeSpan.FromSeconds(5));
        var tool = new LinuxProcessStopTool();

        var result = await tool.ExecuteAsync(PidArgs(process.Id));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
    }

    [Theory]
    [InlineData("process.stop", RiskLevel.Medium)]
    [InlineData("process.kill", RiskLevel.High)]
    public void ActionTools_DeclareVerificationAgainstProcessInspect(string toolName, RiskLevel expectedRisk)
    {
        var tool = new LinuxSystemToolProvider().GetTools().Single(t => t.Manifest.Name == toolName);

        Assert.Equal(expectedRisk, tool.Manifest.Risk);
        Assert.NotNull(tool.Manifest.Verification);
        Assert.Equal("process.inspect", tool.Manifest.Verification!.VerifyToolName);
        Assert.IsAssignableFrom<IVerifiableTool>(tool);
    }
}
