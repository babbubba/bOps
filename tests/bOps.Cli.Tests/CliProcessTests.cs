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

    private Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args) => RunCoreAsync(withModel: true, args);

    /// <summary>ADR-0044 section 10.1: the configuration commands need no model provider, so none is configured at all.</summary>
    private Task<(int ExitCode, string Output, string Error)> RunWithoutModelAsync(params string[] args) => RunCoreAsync(withModel: false, args);

    private async Task<(int ExitCode, string Output, string Error)> RunCoreAsync(bool withModel, string[] args)
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

        foreach (var inherited in start.Environment.Keys.Where(key => key.StartsWith("ModelProvider__", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(inherited);
        }

        // No provider is ever called: the model is created but a delegation is refused, or a command ends, before it is asked.
        if (withModel)
        {
            start.Environment["ModelProvider__Provider"] = "Ollama";
            start.Environment["ModelProvider__BaseUrl"] = "http://127.0.0.1:9";
            start.Environment["ModelProvider__Model"] = "none";
            start.Environment["ModelProvider__SupportsNativeToolCalling"] = "false";
            start.Environment["ModelProvider__ApiKeySecret__Provider"] = "environment";
            start.Environment["ModelProvider__ApiKeySecret__Name"] = "BOPS_TEST_KEY";
            start.Environment["BOPS_TEST_KEY"] = "not-a-real-key";
        }

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

    [Fact]
    public async Task Recover_ThenAccept_ReusesTheRuntimeContracts_AndSucceeds()
    {
        var task = await SeedPendingMutationAsync();

        var recovered = await RunAsync("recover", task.Id.ToString(), "--attempt", "1");
        var accepted = await RunAsync("reconcile", task.Id.ToString(), "accept", "--note", "checked externally");

        Assert.True(recovered.ExitCode == 0, recovered.Output + recovered.Error);
        Assert.Contains("No tool was executed", recovered.Output, StringComparison.Ordinal);
        Assert.True(accepted.ExitCode == 0, accepted.Output + accepted.Error);
        Assert.Contains("ReconciledDone", accepted.Output, StringComparison.Ordinal);
        Assert.Contains("Unsettled mutations: 0", accepted.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recover_WithAStaleAttempt_IsAConflictWithTheStableCode()
    {
        var task = await SeedPendingMutationAsync();

        var result = await RunAsync("recover", task.Id.ToString(), "--attempt", "2");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("(recovery_conflict)", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("recover")]
    [InlineData("reconcile")]
    public async Task MutationCommands_RejectInvalidArgumentsBeforeExecution(string command)
    {
        var result = command == "recover"
            ? await RunAsync("recover", "not-a-task", "--attempt", "0")
            : await RunAsync("reconcile", Guid.NewGuid().ToString(), "retry");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"Usage: bops {command}", result.Error, StringComparison.Ordinal);
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

    // ---- HARDEN-11: the safe fresh-install path, through the real executable, with no model provider configured ----

    [Fact]
    public async Task FreshInstall_ReadinessInitCheckReadiness_TheSafePath_WithoutAModelProvider()
    {
        var policy = Path.Combine(_dir, "policy.yaml");

        var before = await RunWithoutModelAsync("delegate", "readiness");
        var printed = await RunWithoutModelAsync("delegate", "profiles", "init", "--read-only");
        var written = await RunWithoutModelAsync("delegate", "profiles", "init", "--read-only", "--write");
        var check = await RunWithoutModelAsync("delegate", "profiles", "check");
        var after = await RunWithoutModelAsync("delegate", "readiness");
        var remediation = await RunWithoutModelAsync("delegate", "readiness", "--remediation");
        var again = await RunWithoutModelAsync("delegate", "profiles", "init", "--read-only", "--write");

        Assert.True(before.ExitCode == 2, before.Output + before.Error);
        Assert.Contains("Discovery: missing [Profile]", before.Output, StringComparison.Ordinal);
        Assert.Contains("Remediation: not required", before.Output, StringComparison.Ordinal);
        Assert.Contains($"Policy: {policy} (noFile)", before.Output, StringComparison.Ordinal);

        Assert.Equal(0, printed.ExitCode);
        Assert.Contains("delegation:", printed.Output, StringComparison.Ordinal);
        Assert.Contains("Review it, then run again with --write.", printed.Error, StringComparison.Ordinal);
        Assert.StartsWith("# Generated by `bops delegate profiles init --read-only`", printed.Output, StringComparison.Ordinal);
        Assert.Equal(2, bOps.Policy.PolicyConfigLoader.Load(printed.Output).RoleProfiles.Count);

        Assert.True(written.ExitCode == 0, written.Output + written.Error);
        Assert.Contains($"Wrote {policy}", written.Output, StringComparison.Ordinal);
        var config = bOps.Policy.PolicyConfigLoader.Load(await File.ReadAllTextAsync(policy));
        Assert.Equal(2, config.RoleProfiles.Count);
        Assert.All(config.RoleProfiles, profile => Assert.Equal(bOps.Abstractions.RiskLevel.Read, profile.MaxRisk));

        Assert.True(check.ExitCode == 0, check.Output + check.Error);
        Assert.StartsWith($"Policy: {policy} (loaded)", check.Output, StringComparison.Ordinal);

        Assert.True(after.ExitCode == 0, after.Output + after.Error);
        Assert.Contains("Discovery: ready", after.Output, StringComparison.Ordinal);
        Assert.Contains("Diagnostic: ready", after.Output, StringComparison.Ordinal);
        Assert.Contains("Ready: yes", after.Output, StringComparison.Ordinal);

        Assert.Equal(2, remediation.ExitCode);
        Assert.Contains("Remediation: missing [Profile]", remediation.Output, StringComparison.Ordinal);

        Assert.Equal(1, again.ExitCode);
        Assert.Contains("never overwritten silently", again.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfilesInit_WithoutReadOnly_IsAUsageError()
    {
        var (code, _, error) = await RunWithoutModelAsync("delegate", "profiles", "init", "--write");

        Assert.Equal(1, code);
        Assert.Contains("--read-only", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_dir, "policy.yaml")));
    }

    [Fact]
    public async Task ADiagnosis_WithOnlyTheReadOnlyProfiles_IsNoLongerDenied_ItReachesDiscovery()
    {
        Assert.Equal(0, (await RunWithoutModelAsync("delegate", "profiles", "init", "--read-only", "--write")).ExitCode);

        var (code, output, _) = await RunAsync("delegate", "Why did nginx stop?");

        // The model cannot be reached, so Discovery fails; what matters is that no Remediation or Verification profile was needed.
        Assert.NotEqual(2, code);
        Assert.DoesNotContain("Refused on Profile", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Remediation", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryResume_OfADelegatedRoleTask_ExitsOne_WithTaskDelegated()
    {
        // ADR-0044 section 14 (HARDEN-3 guard): a role task resumes only through its delegation, never through `bops resume`.
        var store = new bOps.Memory.SqliteTaskStore(Path.Combine(_dir, "tasks.db"));
        var task = new bOps.Abstractions.TaskState(
            Guid.NewGuid(), bOps.Abstractions.NodeId.Local, "Discovery role goal", bOps.Abstractions.AgentTaskStatus.Failed, [], [], DateTimeOffset.UtcNow)
        {
            Origin = bOps.Abstractions.TaskOrigin.Delegated,
            DelegationId = Guid.NewGuid(),
            DelegationRole = bOps.Abstractions.AgentRoleKind.Discovery,
        };
        await store.SaveAsync(task);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var (code, _, error) = await RunAsync("resume", task.Id.ToString());

        Assert.Equal(1, code);
        Assert.Contains("task_delegated", error, StringComparison.Ordinal);
    }

    private async Task<bOps.Abstractions.TaskState> SeedPendingMutationAsync()
    {
        var store = new bOps.Memory.SqliteTaskStore(Path.Combine(_dir, "tasks.db"));
        var task = new bOps.Abstractions.TaskState(
            Guid.NewGuid(), bOps.Abstractions.NodeId.Local, "change", bOps.Abstractions.AgentTaskStatus.Running, [], [], DateTimeOffset.UtcNow)
        {
            Origin = bOps.Abstractions.TaskOrigin.Ordinary,
            MutationJournalMode = bOps.Abstractions.TaskMutationJournalMode.Journaled,
        };
        await store.SaveAsync(task);
        Assert.True(await store.TryRecordIntentAsync(new bOps.Abstractions.TaskMutationIntent
        {
            Key = new bOps.Abstractions.TaskMutationKey(task.Id, 1, 0),
            ToolName = "test.mutate",
            ArgumentsHash = new string('a', 64),
            Risk = bOps.Abstractions.RiskLevel.Medium,
            VerificationToolName = "test.observe",
            IntentAtUtc = DateTimeOffset.UtcNow,
        }));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return task;
    }
}
