// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// AF-TOOLROUTE-01 (BENCH B03, archive 20261012T111826Z): tool routing and replanning after calls the step did not offer.
/// A not-offered call as a planned step's first deviation gets one in-step correction instead of an immediate replan; once every
/// planned step is resolved the step prompt says so without contradiction; a replan sees the completed read checks and may not
/// re-propose one with identical arguments, while new checks and an empty plan stay accepted. Nothing here widens the offered
/// tools, executes a rejected call, or changes policy, approval, audit or accounting. The provider-path reproduction of the
/// whole B03 sequence is <c>bOps.AgentFramework.Tests.Regression.ToolRoutingRegressionTests</c>.
/// </summary>
public sealed class ToolRoutingTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");
    private static readonly ActorIdentity Resumer = new("api-user", "resumer", "Resumer");
    private const string Crashes = "system.crashes";
    private const string Memory = "system.memory";
    private const string ToolNameCorrection = "named a tool that is not offered for this step";
    private const string PlanComplete = "Every planned step above is complete and no operational tool is offered.";
    private const string NextUnfinished = "Propose the concrete tool call for the next unfinished step above";

    private static RecordingReadTool CrashesTool() => new(Crashes,
    [
        new ToolParameter("mode", ToolParameterType.String, "Output mode.", Required: false) { AllowedValues = ["raw", "aggregate"] },
        new ToolParameter("sinceMinutes", ToolParameterType.Integer, "Window in minutes.", Required: false),
    ]);

    private static RecordingReadTool MemoryTool() => new(Memory, []);

    private static JsonObject Window(int minutes) => new() { ["mode"] = "raw", ["sinceMinutes"] = minutes };

    private static AgentRunner Runner(IChatModel model, ITaskStore store, RecordingAuditSink? audit = null, params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        return new AgentRunner(model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(),
            audit ?? new RecordingAuditSink(), store, TimeProvider.System, NullLogger<AgentRunner>.Instance,
            new AgentRunnerOptions { MaxSteps = 20, MaxLifetimeSteps = 40 });
    }

    private static ModelResponse Plan(params (string Tool, JsonObject? Expected)[] entries) => new(new JsonObject
    {
        ["rationale"] = "test",
        ["steps"] = new JsonArray([.. entries.Select((entry, i) =>
        {
            var step = new JsonObject { ["description"] = $"step {i}", ["expectedTool"] = entry.Tool };
            if (entry.Expected is { } expected)
            {
                step["expectedArguments"] = expected.DeepClone();
            }

            return (JsonNode)step;
        })]),
    }.ToJsonString(), [], false, null);

    private static ModelResponse Call(string tool, JsonObject arguments, string? nameError = null) =>
        new(null, [new ModelToolCall(Guid.NewGuid().ToString("N"), tool, ToolArguments.FromJson(arguments)) { ToolNameError = nameError }],
            false, null);

    private static ModelResponse Final() => new("done", [], true, null);

    private static IEnumerable<string> Offered(ModelRequest request) =>
        request.AvailableTools.Select(tool => tool.Name).Where(name => name != EvidenceRead.ControlFunctionName);

    private static string LastUser(ModelRequest request) =>
        request.History.Last(turn => turn.Role == ChatRole.User).Content ?? string.Empty;

    private static async Task<(InMemoryTaskStore Store, TaskState Task)> PersistedAsync(InMemoryTaskStore store, Guid taskId)
    {
        var stored = await store.LoadAsync(taskId);
        var reloaded = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(stored))!;
        var fresh = new InMemoryTaskStore();
        fresh.Seed(reloaded);
        return (fresh, reloaded);
    }

    // ---- A: a not-offered call keeps its planned step for one in-step correction ----

    [Fact]
    public async Task A1_ANotOfferedCall_IsCorrectedInTheSameStep_WithoutAReplan_AndNothingElseRuns()
    {
        var (crashes, memory) = (CrashesTool(), MemoryTool());
        var audit = new RecordingAuditSink();
        var model = new FakeChatModel(
            Plan((Crashes, Window(60))),
            Call("system_2Ecrashes", Window(60), "'system_2Ecrashes' is not a tool that was offered in this request."),
            Call(Crashes, Window(60)),
            Final());

        var result = await Runner(model, new InMemoryTaskStore(), audit, crashes, memory).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal((1, 0), (crashes.ExecutionCount, memory.ExecutionCount));
        Assert.Single(result.Plans);
        Assert.Equal(0, result.Accounting!.LifetimeReplans);
        Assert.Equal(PlannedStepExecutionClassification.UnofferedTool, result.Steps[0].ExecutionClassification);
        Assert.Equal(PlannedStepExecutionClassification.Matched, result.Steps[1].ExecutionClassification);
        Assert.Equal([0, 0], result.Steps.Take(2).Select(step => step.PlannedStepIndex));
        // The correction offers exactly the planned tool again and says why; it never widens the view.
        Assert.Equal([Crashes], Offered(model.Requests[2]));
        Assert.Contains(ToolNameCorrection, model.Requests[2].SystemPrompt, StringComparison.Ordinal);
        Assert.Contains(audit.Events, e => e is ToolCallAuditEvent { Authorization: AuthorizationKind.UnknownTool, Outcome: ToolOutcome.Denied });
    }

    [Fact]
    public async Task A2_ACanonicalNameOfAnotherRegisteredTool_IsCorrectedTheSameWay_AndNeverRuns()
    {
        var (crashes, memory) = (CrashesTool(), MemoryTool());
        var model = new FakeChatModel(Plan((Crashes, Window(60))), Call(Memory, []), Call(Crashes, Window(60)), Final());

        var result = await Runner(model, new InMemoryTaskStore(), null, crashes, memory).RunAsync("diagnose", Actor);

        Assert.Equal((1, 0), (crashes.ExecutionCount, memory.ExecutionCount));
        Assert.Equal(Memory, result.Steps[0].ToolCall!.ToolName);
        Assert.NotNull(result.Steps[0].ToolCall!.ToolNameError);
        Assert.Single(result.Plans);
    }

    [Fact]
    public async Task A3_ResumeAfterTheFirstNotOfferedCall_KeepsTheCorrection_AndTheSecondReplansBeforeAnyTool()
    {
        var store = new InMemoryTaskStore();
        var interrupted = await Runner(
                new FailingAfterModel(Plan((Crashes, Window(60))), Call(Memory, [])), store, null, CrashesTool(), MemoryTool())
            .RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, interrupted.Status);

        var (resumeStore, persisted) = await PersistedAsync(store, interrupted.Id);
        var (crashes, memory) = (CrashesTool(), MemoryTool());
        var model = new FakeChatModel(Call(Memory, []), Plan((Memory, null)), Call(Memory, []), Final());

        var result = await Runner(model, resumeStore, null, crashes, memory).ResumeAsync(persisted, Resumer);

        // The persisted fold remembers the spent correction: the resumed step offers the planned tool with the correction...
        Assert.Equal([Crashes], Offered(model.Requests[0]));
        Assert.Contains(ToolNameCorrection, model.Requests[0].SystemPrompt, StringComparison.Ordinal);
        // ...and the second not-offered call replans with no native tool before anything runs.
        Assert.Empty(Offered(model.Requests[1]));
        Assert.Equal((0, 1), (crashes.ExecutionCount, memory.ExecutionCount));
        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(1, result.Accounting!.LifetimeReplans);
    }

    [Fact]
    public void A4_TheFold_SpendsTheToolCorrectionOnce_OnlyAsTheFirstDeviation_AndAMatchResetsIt()
    {
        var plan = new AgentPlan(0, "p", [new PlannedStep(0, "crashes", Crashes), new PlannedStep(1, "memory", Memory)])
        {
            SemanticContractVersion = 1,
        };
        PlanStep Step(int index, PlannedStepExecutionClassification? classification, bool rejected = false) =>
            new(index, "s", new ModelToolCall($"c{index}", rejected ? "other" : Crashes, ToolArguments.Empty)
            {
                ToolNameError = rejected ? "not offered" : null,
            }, rejected ? null : ToolCallResult.Success("ok"), "ok", 0)
            {
                PlannedStepIndex = 0,
                ExecutionClassification = classification,
            };

        var once = PlannedStepPosition.Derive(plan, [Step(0, PlannedStepExecutionClassification.UnofferedTool, rejected: true)]);
        Assert.Equal((0, true, false), (once.Cursor, once.ToolCorrectionSpent, once.ReplanRequired));

        var twice = PlannedStepPosition.Derive(plan,
            [Step(0, PlannedStepExecutionClassification.UnofferedTool, true), Step(1, PlannedStepExecutionClassification.UnofferedTool, true)]);
        Assert.True(twice.ReplanRequired);

        var afterSemantic = PlannedStepPosition.Derive(plan,
            [Step(0, PlannedStepExecutionClassification.SemanticMismatch), Step(1, PlannedStepExecutionClassification.UnofferedTool, true)]);
        Assert.True(afterSemantic.ReplanRequired);

        var corrected = PlannedStepPosition.Derive(plan,
            [Step(0, PlannedStepExecutionClassification.UnofferedTool, true), Step(1, PlannedStepExecutionClassification.Matched)]);
        Assert.Equal((1, false, false), (corrected.Cursor, corrected.ToolCorrectionSpent, corrected.ReplanRequired));
    }

    // ---- B: a completed plan is described without contradiction ----

    [Fact]
    public async Task B1_OnceEveryPlannedStepIsResolved_ThePromptSaysSo_AndAFinalAnswerCompletesWithoutAReplan()
    {
        var model = new FakeChatModel(Plan((Memory, null)), Call(Memory, []), Final());

        var result = await Runner(model, new InMemoryTaskStore(), null, MemoryTool()).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Contains(NextUnfinished, model.Requests[1].SystemPrompt, StringComparison.Ordinal);
        Assert.Empty(Offered(model.Requests[2]));
        Assert.Contains(PlanComplete, model.Requests[2].SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(NextUnfinished, model.Requests[2].SystemPrompt, StringComparison.Ordinal);
        Assert.Single(result.Plans);
    }

    // ---- C: a replan sees the completed checks and may not repeat one with identical arguments ----

    [Fact]
    public async Task C1_AfterACompletedPlan_ResamplingACompletedCheckWithIdenticalArguments_IsAccepted_AndRuns()
    {
        var crashes = CrashesTool();
        var model = new FakeChatModel(
            Plan((Crashes, Window(60))),
            Call(Crashes, Window(60)),
            Call("system_crashes", Window(10_080), "'system_crashes' is not a tool that was offered in this request."),
            Plan((Crashes, Window(60))),
            Call(Crashes, Window(60)),
            Final());

        var result = await Runner(model, new InMemoryTaskStore(), null, crashes).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(2, crashes.ExecutionCount);
        Assert.Equal(2, result.Plans.Count);
        Assert.Equal(1, result.Accounting!.LifetimeReplans);

        // The replan shows the completed check with its exact arguments, and why the proposed call did not run.
        var replan = model.Requests[3];
        Assert.Empty(replan.AvailableTools);
        Assert.Contains("Completed checks of this task", replan.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("- task step 0 system.crashes {\"mode\":\"raw\",\"sinceMinutes\":60}", replan.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Every step of plan revision 0 was complete", LastUser(replan), StringComparison.Ordinal);

        // Live state may have changed, so the identical read is accepted at once: no malformed-reply correction.
        Assert.Equal(ModelCallOutcome.Success, Assert.Single(result.Plans[1].ModelCalls!).Outcome);
    }

    [Fact]
    public async Task C2_ANewCheckOfTheSameTool_IsAcceptedAtOnce_AndAnEmptyPlanLetsTheModelAnswer()
    {
        var crashes = CrashesTool();
        var model = new FakeChatModel(
            Plan((Crashes, Window(60))),
            Call(Crashes, Window(60)),
            Call(Crashes, Window(10_080)),
            Plan((Crashes, Window(10_080))),
            Call(Crashes, Window(10_080)),
            Call(Crashes, Window(43_200)),
            Plan(),
            Final());

        var result = await Runner(model, new InMemoryTaskStore(), null, crashes).RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(2, crashes.ExecutionCount);
        Assert.Equal(3, result.Plans.Count);
        Assert.All(result.Plans.Skip(1), plan => Assert.Equal(ModelCallOutcome.Success, Assert.Single(plan.ModelCalls!).Outcome));
        Assert.Empty(result.Plans[2].Steps);
        Assert.StartsWith("done", result.Steps[^1].Observation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task C3_ACycleWithoutProgress_IsBoundedByTheExistingReplanBudget_NotByAnExecutionCount()
    {
        var crashes = CrashesTool();
        var unoffered = "'system_crashes' is not a tool that was offered in this request.";
        var script = new List<ModelResponse> { Plan((Crashes, Window(60))) };
        for (var cycle = 0; cycle < 4; cycle++)
        {
            script.Add(Call(Crashes, Window(60)));
            script.Add(Call("system_crashes", Window(10_080), unoffered));
            script.Add(Plan((Crashes, Window(60))));
        }

        var model = new FakeChatModel([.. script]);

        var result = await Runner(model, new InMemoryTaskStore(), null, crashes).RunAsync("diagnose", Actor);

        // Identical resamples are never refused; the loop ends where every replan loop ends, at the configured budget.
        var budget = new AgentRunnerOptions().MaxReplans;
        Assert.Equal(AgentTaskStatus.ReplanLimitReached, result.Status);
        Assert.Equal(TaskTerminalKind.ReplanLimit, result.TerminalReason!.Kind);
        Assert.Equal(budget, result.Accounting!.LifetimeReplans);
        Assert.Equal(budget + 1, crashes.ExecutionCount);
        Assert.All(result.Plans.Skip(1), plan => Assert.Equal(ModelCallOutcome.Success, Assert.Single(plan.ModelCalls!).Outcome));
    }

    [Fact]
    public void C4_OnlySuccessfulCompleteReadChecks_Count()
    {
        var read = Manifest(Crashes, RiskLevel.Read, sensitive: false);
        var mutation = Manifest("service.restart", RiskLevel.Medium, sensitive: false);
        ToolManifest? ManifestOf(string name) => name == Crashes ? read : name == "service.restart" ? mutation : null;
        PlanStep Step(int index, string tool, ToolCallResult? result, string? nameError = null, VerificationStatus? verification = null) =>
            new(index, tool, new ModelToolCall($"c{index}", tool, ToolArguments.FromJson(Window(60))) { ToolNameError = nameError },
                result, "o", 0)
            {
                VerificationStatus = verification,
            };

        var completed = CompletedChecks.From(
        [
            Step(0, Crashes, ToolCallResult.Success("ok")),
            Step(1, Crashes, ToolCallResult.Success("ok") with { Completeness = ToolResultCompleteness.Complete }),
            Step(2, Crashes, ToolCallResult.Success("ok") with { Completeness = ToolResultCompleteness.Partial }),
            Step(3, Crashes, ToolCallResult.Success("ok") with { Completeness = ToolResultCompleteness.Unavailable }),
            Step(4, Crashes, ToolCallResult.Failure("boom")),
            Step(5, Crashes, null, nameError: "not offered"),
            Step(6, "service.restart", ToolCallResult.Success("ok")),
            Step(7, "unknown.tool", ToolCallResult.Success("ok")),
            new PlanStep(8, "final", null, null, "done", 0),
        ], ManifestOf);

        Assert.Equal([0, 1], completed.Select(check => check.StepIndex));
    }

    [Fact]
    public void C6_TheCompletedChecksSection_RedactsSensitiveValues_AndIsBounded()
    {
        var secret = Manifest("secret.read", RiskLevel.Read, sensitive: true);
        var section = CompletedChecks.Describe([new(3, secret, new JsonObject { ["token"] = "s3cr3t", ["mode"] = "raw" })]);

        Assert.Contains("- task step 3 secret.read {\"token\":\"[redacted]\",\"mode\":\"raw\"}", section, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", section, StringComparison.Ordinal);
        Assert.Null(CompletedChecks.Describe([]));

        var many = Enumerable.Range(0, 200).Select(i => new CompletedCheck(i, secret, new JsonObject { ["mode"] = new string('x', 40) })).ToList();
        Assert.InRange(CompletedChecks.Describe(many)!.Length, 1, CompletedChecks.MaxPromptCharacters);
    }

    private sealed class FailingAfterModel(params ModelResponse[] responses) : IChatModel
    {
        private int calls;

        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
            calls < responses.Length
                ? Task.FromResult(responses[calls++])
                : throw new ModelProtocolException("provider unavailable") { FailureKind = ModelFailureKind.Authentication };
    }

    private static ToolManifest Manifest(string name, RiskLevel risk, bool sensitive) => new()
    {
        Name = name,
        Description = "fixture",
        Risk = risk,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = sensitive
            ? [new ToolParameter("token", ToolParameterType.String, "Secret.", Required: false, Sensitive: true),
               new ToolParameter("mode", ToolParameterType.String, "Mode.", Required: false)]
            : [],
    };
}
