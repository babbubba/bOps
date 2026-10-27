// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0052 AF-04 / ADR-0050: facts emitted by a real package tool survive persistence and drive conditional activation through the
/// runtime's generic fact machinery, which knows neither the tool nor the fact type or key.
/// </summary>
public sealed class HighDensityFactsActivationTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");
    private const string KernelDump = @"C:\Windows\Minidump\101026-1.dmp";

    [Fact]
    public async Task PackageFact_PersistsAndActivatesAndBindsTheFollowUp_WithoutRuntimeKnowingToolOrKey()
    {
        var dump = new DumpTool();
        var model = new FakeChatModel(
            Plan(activation: Activation(SystemCrashFacts.KernelDumpPathType, SystemCrashFacts.NewestKey, "path")),
            Call("system.crashes", []),
            Call("system.dump_analyze", new JsonObject { ["path"] = KernelDump }),
            Final());
        var store = new InMemoryTaskStore();
        var taskId = Guid.NewGuid();

        var result = await (await RunnerAsync(model, store, new CrashesTool(KernelCrash(KernelDump)), dump)).RunAsync("diagnose", Actor, taskId);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(2, result.Plans[0].Steps.Count);
        Assert.Equal(1, dump.Executions);
        Assert.Equal(["system.dump_analyze"], model.Requests[2].AvailableTools.Select(tool => tool.Name).Where(name => name != EvidenceRead.ControlFunctionName));

        // The facts are persisted with the step and survive a JSON round trip unchanged.
        var persisted = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(await store.LoadAsync(taskId)))!;
        var path = Assert.Single(persisted.Steps[0].Result!.Facts, fact => fact.Type == SystemCrashFacts.KernelDumpPathType);
        Assert.Equal((ToolParameterType.Path, KernelDump), (path.ValueType, path.Value.GetValue<string>()));
        Assert.Equal(JsonSerializer.Serialize(result.Steps[0].Result!.Facts), JsonSerializer.Serialize(persisted.Steps[0].Result!.Facts));

        // The grounding block carries the authoritative fact for the follow-up reasoning.
        Assert.Contains(SystemCrashFacts.ObservedCountType, model.Requests[2].SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoKernelDumpObserved_SkipsTheConditionalStep_WithoutCallingItsTool()
    {
        var dump = new DumpTool();
        var appCrash = new CrashEvidence(DateTimeOffset.UtcNow.AddMinutes(-5), EvidenceTimestampKind.Occurred, "wer", CrashKinds.ApplicationCrash)
        {
            Process = "a.exe",
            EvidenceSources = ["wer"],
        };
        var model = new FakeChatModel(
            Plan(activation: Activation(SystemCrashFacts.KernelDumpPathType, SystemCrashFacts.NewestKey, "path")),
            Call("system.crashes", []),
            Final());

        var result = await (await RunnerAsync(model, new InMemoryTaskStore(), new CrashesTool(appCrash), dump)).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(2, result.Plans[0].Steps.Count);
        Assert.Equal(0, dump.Executions);
        Assert.DoesNotContain(result.Steps, step => step.ToolCall?.ToolName == "system.dump_analyze");
    }

    [Fact]
    public async Task UnavailableCollection_NeverActivatesTheFollowUp()
    {
        var dump = new DumpTool();
        var model = new FakeChatModel(
            Plan(activation: Activation(SystemCrashFacts.ObservedCountType, SystemCrashFacts.AllKey, null)),
            Call("system.crashes", []),
            Final());

        var result = await (await RunnerAsync(model, new InMemoryTaskStore(), new CrashesTool(), dump)).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(2, result.Plans[0].Steps.Count);
        // BOPS-0128 slice B: an unavailable collection still states the window applied and that its coverage is unknown; what it never
        // emits is a fact of observation, which is what an activation reads.
        Assert.All(result.Steps[0].Result!.Facts, fact => Assert.True(
            fact.Type is "system.crashes.selection" or "system.crashes.coverage", $"{fact.Type} is a fact of observation."));
        Assert.DoesNotContain(result.Steps[0].Result!.Facts, fact => fact.Type == SystemCrashFacts.ObservedCountType);
        Assert.DoesNotContain(result.Steps[0].Result!.Facts, fact => fact.Type == "system.crashes.coverage" && fact.Value.GetValue<string>() == "complete");
        Assert.Equal(0, dump.Executions);
    }

    private static CrashEvidence KernelCrash(string dumpPath) =>
        new(DateTimeOffset.UtcNow.AddMinutes(-5), EvidenceTimestampKind.Occurred, "wer", CrashKinds.KernelBugcheck)
        {
            BugcheckCode = "0x124",
            DumpPath = dumpPath,
            EvidenceSources = ["wer"],
        };

    private static JsonObject Activation(string type, string key, string? bind)
    {
        var activation = new JsonObject { ["sourceStep"] = 0, ["factType"] = type, ["factKey"] = key };
        if (bind is not null)
        {
            activation["bindArgument"] = bind;
        }

        return activation;
    }

    private static ModelResponse Plan(JsonObject activation) =>
        new(new JsonObject
        {
            ["rationale"] = "test",
            ["steps"] = new JsonArray(
                new JsonObject { ["description"] = "crashes", ["expectedTool"] = "system.crashes" },
                new JsonObject { ["description"] = "analyze", ["expectedTool"] = "system.dump_analyze", ["activation"] = activation }),
        }.ToJsonString(), [], false, null);

    private static ModelResponse Call(string tool, JsonObject arguments) =>
        new(null, [new ModelToolCall(Guid.NewGuid().ToString("N"), tool, ToolArguments.FromJson(arguments))], false, null);

    private static ModelResponse Final() => new("done", [], true, null);

    private static async Task<AgentRunner> RunnerAsync(IChatModel model, ITaskStore store, params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        await registry.RefreshCapabilitiesAsync();
        return new AgentRunner(model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(),
            new RecordingAuditSink(), store, TimeProvider.System, NullLogger<AgentRunner>.Instance,
            new AgentRunnerOptions { MaxSteps = 20, MaxLifetimeSteps = 40 });
    }

    private sealed class CrashesTool(params CrashEvidence[] crashes) : SystemCrashesToolBase(CurrentPlatform.Id)
    {
        protected override Task<CrashEvidenceSnapshot> CollectAsync(CrashesQuery query, CancellationToken ct) =>
            Task.FromResult(crashes.Length == 0
                ? new CrashEvidenceSnapshot([], [new MaintenanceSource("wer", InventorySourceStatus.Unavailable, "denied")])
                : new CrashEvidenceSnapshot(crashes, [new MaintenanceSource("wer", InventorySourceStatus.Available)])
                {
                    Stores = [new CoverageStore("store.wer", CoverageBasis.EventLog, DateTimeOffset.UtcNow.AddDays(-400), 20_971_520, ["wer"])],
                });
    }

    private sealed class DumpTool() : SystemDumpAnalyzeToolBase(CurrentPlatform.Id, "test.capability")
    {
        public int Executions { get; private set; }

        protected override Task<DumpAnalysisReport> AnalyzeAsync(string path, CancellationToken ct)
        {
            Executions++;
            return Task.FromResult(new DumpAnalysisReport
            {
                Status = DumpAnalysisStatus.Complete,
                Dump = new DumpFileDescription(path, "101026-1.dmp", 100, DateTimeOffset.UtcNow, "kernel-small"),
                Bugcheck = new DumpBugcheck("0x124", "WHEA_UNCORRECTABLE_ERROR", []),
            });
        }
    }
}
