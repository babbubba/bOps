// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Windows;

namespace bOps.Packages.System.Windows.Tests;

/// <summary>
/// Integration tests against real, test-owned child processes this process spawns and always
/// cleans up (never a real unrelated process — agentic/04-testing-rules.md, "Never mock the
/// operating system", applied with the same "own throwaway resource" discipline
/// <c>FsToolsTests</c> uses for its temp directory).
/// </summary>
[Trait("Platform", "Windows")]
public sealed class WindowsProcessActionToolsTests
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

    // A console process with no window: CreateNoWindow means CloseMainWindow() has nothing to
    // close, exercising process.stop's honest "no graceful mechanism" failure path.
    private static Process StartWindowlessProcess()
    {
        var startInfo = new ProcessStartInfo("cmd.exe", "/c timeout /t 300 /nobreak >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the test process.");
    }

    // mshta.exe (built into every Windows edition, client and server) opens a genuine, classic
    // Win32 top-level window and then just sits on it — unlike notepad.exe, which on modern
    // Windows client editions is a thin launcher stub for a packaged app and exits within
    // milliseconds of spawning the real (differently-PID'd) window, making it an unreliable
    // target for this test.
    private static Process StartWindowedProcess()
    {
        var startInfo = new ProcessStartInfo("mshta.exe", "about:blank")
        {
            UseShellExecute = false,
        };
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the test process.");
    }

    [WindowsOnlyFact]
    public async Task ProcessKill_TerminatesARealSpawnedProcess()
    {
        using var process = StartWindowlessProcess();
        var tool = new WindowsProcessKillTool();

        var result = await tool.ExecuteAsync(PidArgs(process.Id));

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(await WaitForExitAsync(process, TimeSpan.FromSeconds(5)));
    }

    [WindowsOnlyFact]
    public async Task ProcessKill_Fails_ForAPidThatIsNotRunning()
    {
        using var process = StartWindowlessProcess();
        process.Kill();
        await WaitForExitAsync(process, TimeSpan.FromSeconds(5));
        var tool = new WindowsProcessKillTool();

        var result = await tool.ExecuteAsync(PidArgs(process.Id));

        Assert.Equal(ToolOutcome.Failure, result.Outcome);
    }

    [WindowsOnlyFact]
    public async Task ProcessStop_Fails_ForAProcessWithNoMainWindow()
    {
        using var process = StartWindowlessProcess();
        var tool = new WindowsProcessStopTool();

        try
        {
            var result = await tool.ExecuteAsync(PidArgs(process.Id));

            Assert.Equal(ToolOutcome.Failure, result.Outcome);
            Assert.Contains("no main window", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    [WindowsOnlyFact]
    public async Task ProcessStop_Succeeds_ForAProcessWithAMainWindow()
    {
        using var process = StartWindowedProcess();
        // Give the window time to actually appear before CloseMainWindow() looks for it.
        process.WaitForInputIdle(TimeSpan.FromSeconds(10));
        var tool = new WindowsProcessStopTool();

        try
        {
            var result = await tool.ExecuteAsync(PidArgs(process.Id));

            // CloseMainWindow() succeeding only means Windows accepted the close request for
            // delivery, not that the target complies (or complies quickly) — exactly like SIGTERM
            // on Linux. Whether it actually exited is what the separate, deferred verification
            // step (process.inspect) is for; asserting it here would conflate the action with its
            // verification, which is precisely the distinction IVerifiableTool exists to keep
            // separate.
            Assert.True(result.Succeeded, result.ErrorMessage);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    [Fact]
    public async Task ProcessStop_VerificationIsConfirmed_WhenInspectReportsAbsence()
    {
        var tool = new WindowsProcessStopTool();
        var inspectResult = ToolCallResult.Success("""{"pid":123,"exists":false}""");

        var outcome = await tool.EvaluateVerificationAsync(PidArgs(123), inspectResult);

        Assert.Equal(VerificationStatus.Confirmed, outcome.Status);
    }

    [Fact]
    public async Task ProcessKill_VerificationIsRefuted_WhenInspectReportsStillExisting()
    {
        var tool = new WindowsProcessKillTool();
        var inspectResult = ToolCallResult.Success("""{"pid":123,"exists":true,"name":"stubborn"}""");

        var outcome = await tool.EvaluateVerificationAsync(PidArgs(123), inspectResult);

        Assert.Equal(VerificationStatus.Refuted, outcome.Status);
    }

    [Fact]
    public async Task ProcessKill_VerificationIsInconclusive_WhenTheVerificationCallItselfFailed()
    {
        var tool = new WindowsProcessKillTool();
        var failedInspectResult = ToolCallResult.Failure("process.inspect is not registered.");

        var outcome = await tool.EvaluateVerificationAsync(PidArgs(123), failedInspectResult);

        Assert.Equal(VerificationStatus.Inconclusive, outcome.Status);
    }

    [Theory]
    [InlineData("process.stop", RiskLevel.Medium)]
    [InlineData("process.kill", RiskLevel.High)]
    public void ActionTools_DeclareVerificationAgainstProcessInspect(string toolName, RiskLevel expectedRisk)
    {
        var tool = new WindowsSystemToolProvider().GetTools().Single(t => t.Manifest.Name == toolName);

        Assert.Equal(expectedRisk, tool.Manifest.Risk);
        Assert.NotNull(tool.Manifest.Verification);
        Assert.Equal("process.inspect", tool.Manifest.Verification!.VerifyToolName);
        Assert.IsAssignableFrom<IVerifiableTool>(tool);
    }
}
