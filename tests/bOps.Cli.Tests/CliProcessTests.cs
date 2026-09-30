// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace bOps.Cli.Tests;

/// <summary>
/// The real <c>bops</c> executable, started as a process in an empty folder: what only the composition root decides. That the
/// role profiles come from the same policy file the engine does (a missing or broken one has none, so a delegation is denied on the
/// Profile dimension before any model call), that a usage error ends before anything is composed, and that the commands that
/// existed before <c>bops delegate</c> still run.
/// </summary>
public sealed class CliProcessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bops-cli-proc-{Guid.NewGuid():N}");

    public CliProcessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that cannot be removed is not a test failure.
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "bops.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        // No provider is ever called: the model is created but a delegation is refused, or a command ends, before it is asked.
        start.Environment["ModelProvider__Provider"] = "Ollama";
        start.Environment["ModelProvider__BaseUrl"] = "http://127.0.0.1:9";
        start.Environment["ModelProvider__Model"] = "none";
        start.Environment["ModelProvider__SupportsNativeToolCalling"] = "false";
        start.Environment["ModelProvider__ApiKeySecret__Provider"] = "environment";
        start.Environment["ModelProvider__ApiKeySecret__Name"] = "BOPS_TEST_KEY";
        start.Environment["BOPS_TEST_KEY"] = "not-a-real-key";
        start.Environment["OTEL_SDK_DISABLED"] = "true";

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }

    [Fact]
    public async Task Delegate_WithNothingToDelegate_PrintsTheUsage_AndExitsWithOne()
    {
        var (code, _, error) = await RunAsync("delegate");

        Assert.Equal(1, code);
        Assert.Contains("Usage: bops delegate", error, StringComparison.Ordinal);
        Assert.Contains("Exit codes:", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delegate_WithABadRunId_ExitsBeforeAnythingIsComposed()
    {
        var (code, _, error) = await RunAsync("delegate", "resume", "not-a-run");

        Assert.Equal(1, code);
        Assert.Contains("takes one run id", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delegate_WithNoPolicyFile_IsDeniedOnTheProfileDimension_BecauseDelegationIsOffUntilGranted()
    {
        var (code, output, _) = await RunAsync("delegate", "Why did nginx stop?");

        Assert.Equal(2, code);
        Assert.Contains("Denied", output, StringComparison.Ordinal);
        Assert.Contains("Refused on Profile", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delegate_WithABrokenPolicyFile_IsDeniedOnTheProfileDimension_AndSaysWhyPolicyFailedToLoad()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "policy.yaml"), "this: is: [not valid");

        var (code, output, error) = await RunAsync("delegate", "Why did nginx stop?");

        Assert.Equal(2, code);
        Assert.Contains("Refused on Profile", output, StringComparison.Ordinal);
        Assert.Contains("every tool above Read is forbidden", output + error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delegate_WithADelegationSectionInThePolicy_GetsItsRoleProfilesFromThatFile()
    {
        // The profiles come from the same loaded policy the engine does: with them, the start is no longer refused for want of a
        // profile. It goes on to Discovery, whose model cannot be reached, so it fails: not denied.
        var yaml = "delegation:\n  roles:\n"
            + "    discovery:\n      tools: [host.info]\n      maxRisk: read\n      maxBlastRadius: single\n      targets: [local]\n      environments: [test]\n"
            + "      maxSteps: 4\n      maxTokens: 1000\n      maxDuration: 00:05:00\n"
            + "    diagnostic:\n      skills: [sample.skill]\n      capabilities: [sample.remediate]\n      tools: [host.info]\n      maxRisk: read\n      maxBlastRadius: single\n      targets: [local]\n      environments: [test]\n"
            + "      maxSteps: 4\n      maxTokens: 1000\n      maxDuration: 00:05:00\n"
            + "    remediation:\n      skills: [sample.skill]\n      capabilities: [sample.remediate]\n      tools: [service.restart]\n      maxRisk: high\n      maxBlastRadius: single\n      targets: [local]\n      environments: [test]\n"
            + "      maxSteps: 4\n      maxDuration: 00:05:00\n"
            + "    verification:\n      tools: [host.info]\n      maxRisk: read\n      maxBlastRadius: single\n      targets: [local]\n      environments: [test]\n"
            + "      maxSteps: 4\n      maxDuration: 00:05:00\n";
        await File.WriteAllTextAsync(Path.Combine(_dir, "policy.yaml"), yaml);

        var (code, output, error) = await RunAsync("delegate", "Why did nginx stop?");

        Assert.DoesNotContain("Refused on", output, StringComparison.Ordinal);
        Assert.NotEqual(2, code);
        Assert.Equal(1, code);
        Assert.Contains("Failed", output + error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delegate_Status_OfARunThatIsNotStored_ExitsWithOne()
    {
        var (code, _, error) = await RunAsync("delegate", "status", Guid.NewGuid().ToString());

        Assert.Equal(1, code);
        Assert.Contains("No delegation run", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_OfATask_StillWorksAsBefore()
    {
        var (code, _, error) = await RunAsync("resume", Guid.NewGuid().ToString());

        Assert.Equal(1, code);
        Assert.Contains("No stored task with id", error, StringComparison.Ordinal);
    }


    // HARDEN-3 / ADR-0040: `bops resume` applies the same resumability rule as the API; a refusal names its code, changes
    // nothing and exits 1 — including for a task still stored Running, which no host may resume.
    [Theory]
    [InlineData(bOps.Abstractions.AgentTaskStatus.Completed, "task_completed")]
    [InlineData(bOps.Abstractions.AgentTaskStatus.Running, "task_running")]
    public async Task Resume_OfANonResumableTask_IsRefusedWithItsCode_AndChangesNothing(bOps.Abstractions.AgentTaskStatus status, string code)
    {
        var store = new bOps.Memory.SqliteTaskStore(Path.Combine(_dir, "tasks.db"));
        var task = new bOps.Abstractions.TaskState(
            Guid.NewGuid(), bOps.Abstractions.NodeId.Local, "check", status, [], [], DateTimeOffset.UtcNow)
        {
            Origin = bOps.Abstractions.TaskOrigin.Ordinary,
        };
        await store.SaveAsync(task);

        var (exit, _, error) = await RunAsync("resume", task.Id.ToString());

        Assert.Equal(1, exit);
        Assert.Contains($"({code})", error, StringComparison.Ordinal);
        var after = (await store.LoadAsync(task.Id))!;
        Assert.Equal((status, 1), (after.Status, after.ExecutionAttempt));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
    [Fact]
    public async Task NoArguments_StillPrintsTheUsage_NamingDelegate()
    {
        var (code, _, error) = await RunAsync();

        Assert.Equal(1, code);
        Assert.Contains("bops delegate", error, StringComparison.Ordinal);
        Assert.Contains("bops resume", error, StringComparison.Ordinal);
    }
}
