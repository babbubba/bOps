// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// AF-05H2-R2: BENCH-01 B02/B03 shapes. AF-05H2-R1 refused the persisted-plan continuation after an output-limited early-final
/// replan whenever any earlier tool call had not succeeded, including calls the runtime refused for invalid arguments that a
/// matched, successful call of the same planned step later corrected. Only those corrected validation failures are now
/// resolved; denials, timeouts, other failures, unknown outcomes and side effects still fail closed.
/// </summary>
public sealed class EarlyFinalValidationRecoveryTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");
    private static readonly ActorIdentity Resumer = new("api-user", "resumer", "Resumer");
    private const string Info = "test.info";
    private const string Stability = "test.stability";
    private const string Crashes = "test.crashes";
    private const string Events = "test.events";
    private const string Cpu = "test.cpu";
    private const string Memory = "test.memory";
    private const string Storage = "test.storage";
    private const string Action = "test.action";
    private const long LengthTokens = 17 + 4096;

    private static JsonObject Raw() => new() { ["mode"] = "raw" };

    private static ModelResponse Call(string tool, JsonObject arguments) =>
        new(null, [new ModelToolCall(Guid.NewGuid().ToString("N"), tool, ToolArguments.FromJson(arguments))], false, null);

    private static ModelResponse Valid(string tool) => Call(tool, Raw());

    /// <summary>Names the planned tool but contradicts the planned expected argument: SemanticMismatch, never run.</summary>
    private static ModelResponse Mismatch(string tool) => Call(tool, new JsonObject { ["mode"] = "summary" });

    /// <summary>Satisfies the planned argument but fails complete manifest validation: ArgumentValidationFailure, never run.</summary>
    private static ModelResponse Invalid(string tool) => Call(tool, new JsonObject { ["mode"] = "raw", ["sinceDays"] = 2 });

    private static ModelResponse Final(string text = "done") => new(text, [], true, null);

    private static ModelResponse Length() => new("{\"steps\":[{\"description\":\"", [], false, new ModelUsage(17, 4096, null))
    {
        Details = new ModelCallDetails("fake-model", "length", "request", "original truncated replan"),
    };

    private static ModelResponse Plan(params PlanTestStep[] steps) => PlanningTestSupport.PlanResponseFor(steps);

    private static ModelResponse B02Plan() => Plan(
        new(Info), new(Stability), new(Crashes), new(Events, Raw()), new(Cpu), new(Memory), new(Storage));

    private static ModelResponse B03Plan() => Plan(new(Stability), new(Events, Raw()), new(Info));

    private static List<ModelResponse> B02Script() =>
    [
        B02Plan(), Valid(Info), Valid(Stability), Valid(Crashes), Mismatch(Events), Invalid(Events), Valid(Events), Valid(Cpu),
        Valid(Memory), Final("premature"), Length(), Valid(Storage), Final(),
    ];

    private static List<ModelResponse> B03Script() =>
        [B03Plan(), Valid(Stability), Mismatch(Events), Valid(Events), Final("premature"), Length(), Valid(Info), Final()];

    private static RecordingReadTool ReadTool(string name) =>
        new(name, [new ToolParameter("mode", ToolParameterType.String, "Collection mode.")]);

    private static (AgentRunner Runner, RecordingAuditSink Audit, Dictionary<string, RecordingReadTool> Tools) Build(
        IChatModel model, ITaskStore? store = null, IPolicyEngine? policy = null, AgentRunnerOptions? options = null,
        params ITool[] replacements)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        var tools = new[] { Info, Stability, Crashes, Events, Cpu, Memory, Storage }.ToDictionary(name => name, ReadTool);
        foreach (var tool in tools.Values.Where(tool => replacements.All(r => r.Manifest.Name != tool.Manifest.Name)))
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        foreach (var replacement in replacements)
        {
            registry.Register(new PackageId("test.package"), replacement);
        }

        var audit = new RecordingAuditSink();
        var runner = new AgentRunner(model, registry, policy ?? new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), audit,
            store ?? new InMemoryTaskStore(), TimeProvider.System, NullLogger<AgentRunner>.Instance,
            options ?? new AgentRunnerOptions { MaxSteps = 30, MaxLifetimeSteps = 60 });
        return (runner, audit, tools);
    }

    private static IEnumerable<string> Operational(ModelRequest request) =>
        request.AvailableTools.Select(tool => tool.Name).Where(name => name != EvidenceRead.ControlFunctionName);

    private static void AssertRefused(TaskState result, EarlyFinalRecoveryReason reason, int? blocking)
    {
        Assert.Equal(AgentTaskStatus.Failed, result.Status);
        Assert.Equal((TaskTerminalKind.ModelFailure, ModelFailureKind.MalformedResponse),
            (result.TerminalReason!.Kind, result.TerminalReason.FailureKind));
        Assert.DoesNotContain(result.Steps, EarlyFinalRecovery.IsStep);
        var decision = result.Steps.Last(PrematureFinal.IsStep).EarlyFinalRecoveryDecision!;
        Assert.False(decision.Accepted);
        Assert.Equal((reason, blocking), (decision.Reason, decision.BlockingStepIndex));
        Assert.Contains($"({reason})", result.Steps[^1].Observation, StringComparison.Ordinal);
    }

    // ---- B02 / B03 reproduction ----

    [Fact]
    public async Task B02_CorrectedValidationFailures_DoNotBlockTheContinuationOfTheLastPlannedStep()
    {
        var script = B02Script();
        var model = new FakeChatModel([.. script]);
        var (runner, audit, tools) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(result.Plans);
        Assert.All(tools.Values, tool => Assert.Equal(1, tool.ExecutionCount));
        Assert.Equal(script.Count, model.Requests.Count);
        Assert.Equal([Storage], Operational(model.Requests[11]));

        // The live BENCH-01 B02 shape: two validation-class failures of the same planned step, then its matched success.
        Assert.Equal(
            [
                (Events, PlannedStepExecutionClassification.SemanticMismatch, ToolOutcome.Failure),
                (Events, PlannedStepExecutionClassification.ArgumentValidationFailure, ToolOutcome.Failure),
                (Events, PlannedStepExecutionClassification.Matched, ToolOutcome.Success),
            ],
            result.Steps.Skip(3).Take(3).Select(step =>
                (step.ToolCall!.ToolName, step.ExecutionClassification!.Value, step.Result!.Outcome)));
        Assert.All(result.Steps.Skip(3).Take(3), step => Assert.Equal((3, 0), (step.PlannedStepIndex, step.PlanRevision)));
        Assert.All(result.Steps.Skip(3).Take(2), step => Assert.Equal(ToolFailureKind.Validation, step.Result!.FailureKind));

        var premature = result.Steps[8];
        Assert.True(PrematureFinal.IsStep(premature));
        Assert.Equal(new EarlyFinalRecoveryDecision
        {
            Accepted = true, Reason = EarlyFinalRecoveryReason.Continued, PlanRevision = 0, Cursor = 6,
            ResolvedValidationStepIndexes = premature.EarlyFinalRecoveryDecision!.ResolvedValidationStepIndexes,
        }, premature.EarlyFinalRecoveryDecision);
        Assert.Equal([3, 4], premature.EarlyFinalRecoveryDecision.ResolvedValidationStepIndexes!);
        var recovery = result.Steps[9];
        Assert.True(EarlyFinalRecovery.IsStep(recovery));
        Assert.Equal(6, recovery.EarlyFinalRecoveryCursor);
        Assert.Equal((Storage, 6), (result.Steps[10].ToolCall!.ToolName, result.Steps[10].PlannedStepIndex));
        Assert.True(FinalResponse.IsFinalStep(result.Steps[^1]));

        // Request, response, finish reason, usage, audit and accounting of the truncated replan are all preserved.
        var replan = Assert.Single(premature.EarlyFinalReplanCalls!);
        Assert.Equal(("length", "request", "original truncated replan", ModelFailureKind.MalformedResponse),
            (replan.FinishReason, replan.RequestJson, replan.ResponseJson, replan.FailureKind));
        Assert.Equal(4096, replan.Usage!.CompletionTokens);
        Assert.Equal(script.Count, audit.Events.OfType<ModelCallAuditEvent>().Count());
        Assert.Contains(audit.Events.OfType<ModelCallAuditEvent>(), e => e.StepIndex == premature.Index && e.Outcome == ModelCallOutcome.Failure);
        Assert.Contains(audit.Events.OfType<TaskLifecycleAuditEvent>(), e =>
            e.Stage == TaskLifecycleStage.EarlyFinalPlanContinued && e.StepIndex == recovery.Index);
        Assert.DoesNotContain(audit.Events.OfType<TaskLifecycleAuditEvent>(), e => e.Stage == TaskLifecycleStage.EarlyFinalPlanContinuationRefused);
        Assert.Equal(LengthTokens, result.Accounting!.TokensUsed);
        Assert.Equal(result.Accounting.TokensUsed, TaskResumePolicy.DeriveAccounting(result).TokensUsed);
        Assert.Equal(new ReplanBreakdown(1, 0, 1, 1, 0), TaskResumePolicy.ReplanBreakdownOf(result));
        Assert.All(ExecutionPlanProjector.Project(result, false)!.Revisions[0].Steps,
            step => Assert.Equal(ProjectedStepStatus.Completed, step.Status));
    }

    [Fact]
    public async Task B03_ACorrectedSemanticMismatch_DoesNotBlockTheContinuationOfTheLastPlannedStep()
    {
        var script = B03Script();
        var model = new FakeChatModel([.. script]);
        var (runner, _, tools) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal((1, 1, 1), (tools[Stability].ExecutionCount, tools[Events].ExecutionCount, tools[Info].ExecutionCount));
        Assert.Equal(script.Count, model.Requests.Count);
        Assert.Equal([Info], Operational(model.Requests[6]));
        Assert.Equal(PlannedStepExecutionClassification.SemanticMismatch, result.Steps[1].ExecutionClassification);
        var decision = result.Steps[3].EarlyFinalRecoveryDecision!;
        Assert.True(decision.Accepted);
        Assert.Equal(2, decision.Cursor);
        Assert.Equal([1], decision.ResolvedValidationStepIndexes!);
        Assert.Equal(2, Assert.Single(result.Steps, EarlyFinalRecovery.IsStep).EarlyFinalRecoveryCursor);
        Assert.Equal(LengthTokens, result.Accounting!.TokensUsed);
        Assert.Equal(1, result.Accounting.LifetimeReplans);
    }

    // ---- bounded cost: an output-limited replan is not repeated ----

    [Fact]
    public async Task ASecondEarlyFinalAfterProgress_ContinuesWithoutASecondPaidReplan()
    {
        var script = new List<ModelResponse>
        {
            Plan(new(Stability), new(Events, Raw()), new(Info), new(Storage)), Valid(Stability), Mismatch(Events), Valid(Events),
            Final("premature"), Length(), Valid(Info), Final("premature again"), Valid(Storage), Final(),
        };
        var model = new FakeChatModel([.. script]);
        var (runner, _, tools) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(script.Count, model.Requests.Count);
        Assert.All(new[] { Stability, Events, Info, Storage }, name => Assert.Equal(1, tools[name].ExecutionCount));
        Assert.Equal([2, 3], result.Steps.Where(EarlyFinalRecovery.IsStep).Select(step => step.EarlyFinalRecoveryCursor!.Value));
        var second = result.Steps.Last(PrematureFinal.IsStep);
        Assert.Null(second.EarlyFinalReplanCalls);
        Assert.True(second.EarlyFinalRecoveryDecision!.PaidReplanSkipped);
        Assert.Equal(3, second.EarlyFinalRecoveryDecision.Cursor);
        Assert.Equal(LengthTokens, result.Accounting!.TokensUsed);
        Assert.Equal(new ReplanBreakdown(1, 0, 1, 2, 1), TaskResumePolicy.ReplanBreakdownOf(result));
    }

    // ---- what stays non-recoverable ----

    [Fact]
    public async Task AnUncorrectedValidationFailureOfThePendingStep_IsNotRecovered()
    {
        var model = new FakeChatModel(B03Plan(), Valid(Stability), Invalid(Events), Final("premature"), Length());
        var (runner, _, tools) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        AssertRefused(result, EarlyFinalRecoveryReason.PendingCorrection, null);
        Assert.Equal(1, result.Steps.Last(PrematureFinal.IsStep).EarlyFinalRecoveryDecision!.Cursor);
        Assert.Equal((0, 0), (tools[Events].ExecutionCount, tools[Info].ExecutionCount));
    }

    [Fact]
    public async Task AValidationFailureCorrectedOnlyInALaterRevision_IsNotResolved()
    {
        var model = new FakeChatModel(
            Plan(new(Events, Raw()), new(Info)), Invalid(Events), Invalid(Events),
            Plan(new(Events, Raw()), new(Info)), Valid(Events), Final("premature"), Length());
        var (runner, _, tools) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(2, result.Plans.Count);
        AssertRefused(result, EarlyFinalRecoveryReason.UnresolvedToolFailure, 0);
        Assert.Equal(0, tools[Info].ExecutionCount);
    }

    [Fact]
    public async Task AnEarlierToolFailureThatIsNotAValidationFailure_IsNotRecovered()
    {
        var failing = new ResultTool(Stability, ToolCallResult.Failure("collector unavailable") with { FailureKind = ToolFailureKind.Environment },
            [new ToolParameter("mode", ToolParameterType.String, "Collection mode.")]);
        var model = new FakeChatModel(B03Plan(), Valid(Stability), Valid(Events), Final("premature"), Length());
        var (runner, _, tools) = Build(model, replacements: failing);

        var result = await runner.RunAsync("diagnose", Actor);

        AssertRefused(result, EarlyFinalRecoveryReason.UnresolvedToolFailure, 0);
        Assert.Equal(0, tools[Info].ExecutionCount);
    }

    [Fact]
    public async Task AnEarlierPolicyDenial_IsNotRecovered()
    {
        var model = new FakeChatModel(
            B03Plan(), Valid(Stability), Plan(new(Events, Raw()), new(Info)), Valid(Events), Final("premature"), Length());
        var (runner, audit, tools) = Build(model, policy: new DenyingPolicyEngine(Stability));

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(ToolFailureKind.Authorization, result.Steps[0].Result!.FailureKind);
        AssertRefused(result, EarlyFinalRecoveryReason.AuthorizationDenied, 0);
        Assert.Equal((0, 0), (tools[Stability].ExecutionCount, tools[Info].ExecutionCount));
        Assert.Contains(audit.Events.OfType<TaskLifecycleAuditEvent>(), e =>
            e.Stage == TaskLifecycleStage.EarlyFinalPlanContinuationRefused && e.RefusalCode == "authorization_denied");
    }

    [Fact]
    public async Task AnEarlierTimeout_IsNotRecovered()
    {
        var model = new FakeChatModel(
            B03Plan(), Call(Stability, new JsonObject()), Plan(new(Events, Raw()), new(Info)), Valid(Events), Final("premature"), Length());
        var (runner, _, tools) = Build(model, options: new AgentRunnerOptions { DefaultToolTimeout = TimeSpan.FromMilliseconds(50) },
            replacements: new HangingTool(Stability));

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(ToolOutcome.Timeout, result.Steps[0].Result!.Outcome);
        AssertRefused(result, EarlyFinalRecoveryReason.ToolTimeout, 0);
        Assert.Equal(0, tools[Info].ExecutionCount);
    }

    [Fact]
    public async Task AnEarlierSideEffect_IsNotRecovered()
    {
        var model = new FakeChatModel(
            Plan(new(Action), new(Events, Raw()), new(Info)), Call(Action, new JsonObject()), Valid(Events), Final("premature"), Length());
        var (runner, _, tools) = Build(model, policy: new StubPolicyEngine(PolicyMode.Automatic),
            replacements: [new ResultHighRiskTool(Action, ToolCallResult.Success("changed")), new FakeReadTool("test.read")]);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal((Action, ToolOutcome.Success), (result.Steps[0].ToolCall!.ToolName, result.Steps[0].Result!.Outcome));
        AssertRefused(result, EarlyFinalRecoveryReason.NonReadOnlyHistory, 0);
        Assert.Equal(0, tools[Info].ExecutionCount);
    }

    // ---- the decision table, step by step ----

    [Fact]
    public void Evaluate_ResolvesOnlyValidationFailuresMatchedLaterAtTheSamePlannedIndexOfTheSameRevision()
    {
        var events = ReadTool(Events).Manifest;
        var info = ReadTool(Info).Manifest;
        var action = new ResultHighRiskTool(Action, ToolCallResult.Success("x")).Manifest;
        ToolManifest? ManifestOf(string name) => name switch { Events => events, Info => info, Action => action, _ => null };
        IReadOnlyList<ToolManifest> visible = [events, info];
        var plan = new AgentPlan(1, "r", [
            new PlannedStep(0, "events", Events) { ExpectedArguments = ToolArguments.FromJson(Raw()) },
            new PlannedStep(1, "info", Info),
        ]) { SemanticContractVersion = 1 };

        PlanStep Executed(int index, string tool, ToolCallResult? result, PlannedStepExecutionClassification? classification,
            int revision = 1, int planned = 0) =>
            new(index, tool, new ModelToolCall($"c{index}", tool, ToolArguments.FromJson(Raw())), result, "o", revision)
            {
                PlannedStepIndex = planned, ExecutionClassification = classification,
            };
        var invalid = ToolCallResult.Failure("invalid") with { FailureKind = ToolFailureKind.Validation };
        var ok = ToolCallResult.Success("ok");
        PlanStep Early(int index) => new(index, PrematureFinal.StepDescription, null, null, "x", 1)
        {
            EarlyFinalReplanFailed = true,
            EarlyFinalReplanCalls = [new ModelCallRecord("fake", "fake", null, DateTimeOffset.UnixEpoch, 1,
                ModelCallOutcome.Failure, null, "length", "malformed", null, null, false) { FailureKind = ModelFailureKind.MalformedResponse }],
        };
        Decision Decide(params PlanStep[] prefix)
        {
            var steps = prefix.Append(Early(prefix.Length)).ToList();
            var decision = EarlyFinalRecovery.Evaluate(plan, steps, prefix.Length, ManifestOf, visible).Decision;
            Assert.Equal(decision.Reason == EarlyFinalRecoveryReason.Continued, decision.Accepted);
            return new Decision(decision.Reason, decision.BlockingStepIndex, decision.ResolvedValidationStepIndexes);
        }

        var mismatch = Executed(0, Events, invalid, PlannedStepExecutionClassification.SemanticMismatch);
        var argument = Executed(1, Events, invalid, PlannedStepExecutionClassification.ArgumentValidationFailure);
        var matched = Executed(2, Events, ok, PlannedStepExecutionClassification.Matched);
        var continued = Decide(mismatch, argument, matched);
        Assert.Equal((EarlyFinalRecoveryReason.Continued, (int?)null), continued.Block);
        Assert.Equal([0, 1], continued.Resolved!);
        Assert.Null(Decide(Executed(0, Events, ok, PlannedStepExecutionClassification.Matched)).Resolved);

        // Not corrected: the correction is still pending at the cursor.
        Assert.Equal(EarlyFinalRecoveryReason.PendingCorrection, Decide(mismatch).Reason);
        // The matched call itself failed: the validation failure stays unresolved.
        var failedMatch = Executed(1, Events, ToolCallResult.Failure("env") with { FailureKind = ToolFailureKind.Environment },
            PlannedStepExecutionClassification.Matched);
        Assert.Equal((EarlyFinalRecoveryReason.UnresolvedToolFailure, (int?)0), Decide(mismatch, failedMatch).Block);

        // Older-revision history: a failure there is never resolved by the current revision, whatever its kind.
        var current = Executed(1, Events, ok, PlannedStepExecutionClassification.Matched);
        PlanStep Old(ToolCallResult? result, string tool = Events, VerificationStatus? verification = null) =>
            Executed(0, tool, result, PlannedStepExecutionClassification.Matched, revision: 0) with { VerificationStatus = verification };
        Assert.Equal((EarlyFinalRecoveryReason.UnresolvedToolFailure, (int?)0),
            Decide(Executed(0, Events, invalid, PlannedStepExecutionClassification.ArgumentValidationFailure, revision: 0), current).Block);
        Assert.Equal((EarlyFinalRecoveryReason.UnknownToolOutcome, (int?)0), Decide(Old(null), current).Block);
        Assert.Equal((EarlyFinalRecoveryReason.AuthorizationDenied, (int?)0),
            Decide(Old(new ToolCallResult(ToolOutcome.Denied, null, "no") { FailureKind = ToolFailureKind.Authorization }), current).Block);
        Assert.Equal((EarlyFinalRecoveryReason.ToolTimeout, (int?)0),
            Decide(Old(new ToolCallResult(ToolOutcome.Timeout, null, "slow") { FailureKind = ToolFailureKind.Timeout }), current).Block);
        Assert.Equal((EarlyFinalRecoveryReason.NonReadOnlyHistory, (int?)0), Decide(Old(ok, Action), current).Block);
        Assert.Equal((EarlyFinalRecoveryReason.NonReadOnlyHistory, (int?)0), Decide(Old(invalid, Action), current).Block);
        Assert.Equal((EarlyFinalRecoveryReason.NonReadOnlyHistory, (int?)0), Decide(Old(ok, "test.unknown"), current).Block);
        Assert.Equal((EarlyFinalRecoveryReason.VerificationRefuted, (int?)0),
            Decide(Old(ok, verification: VerificationStatus.Refuted), current).Block);

        // A validation failure of a non-read planned tool is a side-effect-capable history, even when later matched.
        var mutatingPlan = plan with { Steps = [plan.Steps[0] with { ExpectedTool = Action }, plan.Steps[1]] };
        var mutatingSteps = new List<PlanStep>
        {
            Executed(0, Action, invalid, PlannedStepExecutionClassification.ArgumentValidationFailure),
            Executed(1, Action, ok, PlannedStepExecutionClassification.Matched),
            Early(2),
        };
        Assert.Equal(EarlyFinalRecoveryReason.NonReadOnlyHistory,
            EarlyFinalRecovery.Evaluate(mutatingPlan, mutatingSteps, 2, ManifestOf, visible).Decision.Reason);

        // Legacy plans, a missing malformed checkpoint and a non-latest trigger are refused before any history is read.
        Assert.Equal(EarlyFinalRecoveryReason.LegacyPlanContract, EarlyFinalRecovery.Evaluate(
            plan with { SemanticContractVersion = 0 }, [matched with { Index = 0 }, Early(1)], 1, ManifestOf, visible).Decision.Reason);
        Assert.Equal(EarlyFinalRecoveryReason.NoMalformedReplan, EarlyFinalRecovery.Evaluate(
            plan, [matched with { Index = 0 }, Early(1) with { EarlyFinalReplanFailed = false }], 1, ManifestOf, visible).Decision.Reason);
        Assert.Equal(EarlyFinalRecoveryReason.TriggerNotLatest, EarlyFinalRecovery.Evaluate(
            plan, [Early(0), matched with { Index = 1 }], 0, ManifestOf, visible).Decision.Reason);
        Assert.Equal(EarlyFinalRecoveryReason.PendingToolUnavailable, EarlyFinalRecovery.Evaluate(
            plan, [matched with { Index = 0 }, Early(1)], 1, ManifestOf, [events]).Decision.Reason);
    }

    [Fact]
    public void RefusalCodes_AreStableSnakeCase()
    {
        Assert.Equal("unresolved_tool_failure", EarlyFinalRecovery.RefusalCode(EarlyFinalRecoveryReason.UnresolvedToolFailure));
        Assert.Equal("repeated_without_progress", EarlyFinalRecovery.RefusalCode(EarlyFinalRecoveryReason.RepeatedWithoutProgress));
        Assert.Equal("continued", EarlyFinalRecovery.RefusalCode(EarlyFinalRecoveryReason.Continued));
    }

    // ---- checkpoint and resume during and after the continuation ----

    internal enum B02Interruption
    {
        /// <summary>The truncated replan is durable; no decision or continuation record exists yet.</summary>
        AtMalformedCheckpoint,

        /// <summary>The continuation record is durable; the pending tool was not called.</summary>
        AfterContinuationRecord,

        /// <summary>The pending tool executed; the final answer was not given.</summary>
        AfterPendingTool,
    }

    [Theory]
    [InlineData((int)B02Interruption.AtMalformedCheckpoint)]
    [InlineData((int)B02Interruption.AfterContinuationRecord)]
    [InlineData((int)B02Interruption.AfterPendingTool)]
    public async Task B02_ResumeDuringOrAfterTheContinuation_NeverRepeatsAReplanOrATool(int interruptionPoint)
    {
        var interruption = (B02Interruption)interruptionPoint;
        var store = new InMemoryTaskStore();
        var (live, _, _) = Build(new FakeChatModel([.. B02Script()]), store);
        var completed = await live.RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Completed, completed.Status);
        var snapshot = store.Saves.First(task => task.Status == AgentTaskStatus.Running && interruption switch
        {
            B02Interruption.AtMalformedCheckpoint => task.Steps.Count == 9 && EarlyFinalRecovery.HasMalformedCheckpoint(task.Steps[^1]),
            B02Interruption.AfterContinuationRecord => task.Steps.Count == 10 && EarlyFinalRecovery.IsStep(task.Steps[^1]),
            _ => task.Steps.Count == 11 && task.Steps[^1].ToolCall?.ToolName == Storage,
        });
        Assert.Equal(interruption == B02Interruption.AtMalformedCheckpoint, snapshot.Steps[8].EarlyFinalRecoveryDecision is null);
        var reloaded = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(snapshot))!;
        var fresh = new InMemoryTaskStore();
        fresh.Seed(reloaded);
        var model = new FakeChatModel(interruption == B02Interruption.AfterPendingTool ? [Final()] : [Valid(Storage), Final()]);
        // An equal replan cap: the reserved, completed attempt is honoured without another paid attempt.
        var (resumer, _, tools) = Build(model, fresh,
            options: new AgentRunnerOptions { MaxSteps = 30, MaxLifetimeSteps = 60, MaxReplans = 1, MaxLifetimeReplans = 1 });

        var recovered = await resumer.TryRecoverAsync(reloaded.Id, reloaded.ExecutionAttempt, Resumer, executingHere: false);
        Assert.Equal(TaskRecoveryOutcome.Recovered, recovered.Outcome);
        var result = await resumer.ResumeAsync(recovered.Task!, Resumer);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.DoesNotContain(model.Requests, request => request.SystemPrompt.Contains("Revise it", StringComparison.Ordinal));
        Assert.Equal(interruption == B02Interruption.AfterPendingTool ? 0 : 1, tools[Storage].ExecutionCount);
        Assert.All(tools.Where(pair => pair.Key != Storage), pair => Assert.Equal(0, pair.Value.ExecutionCount));
        Assert.Single(result.Steps, EarlyFinalRecovery.IsStep);
        Assert.Single(result.Steps, step => step.ToolCall?.ToolName == Storage);
        Assert.Equal(6, Assert.Single(result.Steps, EarlyFinalRecovery.IsStep).EarlyFinalRecoveryCursor);
        Assert.True(result.Steps[8].EarlyFinalRecoveryDecision!.Accepted);
        Assert.Equal(reloaded.Steps[8].EarlyFinalReplanCalls, result.Steps[8].EarlyFinalReplanCalls);
        Assert.Equal(LengthTokens, result.Accounting!.TokensUsed);
        Assert.Equal(1, result.Accounting.LifetimeReplans);
        Assert.Equal(result.Accounting.TokensUsed, TaskResumePolicy.DeriveAccounting(result).TokensUsed);
    }

    [Fact]
    public async Task ResumeAfterASkippedPaidReplanContinuation_RevalidatesAndDoesNotRepeatIt()
    {
        var store = new InMemoryTaskStore();
        var plan = Plan(new(Stability), new(Events, Raw()), new(Info), new(Storage));
        var (live, _, _) = Build(new FakeChatModel(
            plan, Valid(Stability), Mismatch(Events), Valid(Events), Final("premature"), Length(), Valid(Info), Final("premature again")), store);
        var interrupted = await live.RunAsync("diagnose", Actor);
        // The call after the second continuation finds no scripted response: the attempt fails after the continuation is durable.
        Assert.Equal(AgentTaskStatus.Failed, interrupted.Status);
        Assert.Equal(2, interrupted.Steps.Count(EarlyFinalRecovery.IsStep));
        var reloaded = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(await store.LoadAsync(interrupted.Id)))!;
        var position = PlannedStepPosition.Derive(reloaded.Plans[0], reloaded.Steps);
        Assert.Equal((3, false), (position.Cursor, position.ReplanRequired));

        var fresh = new InMemoryTaskStore();
        fresh.Seed(reloaded);
        var model = new FakeChatModel(Valid(Storage), Final());
        var (resumer, _, tools) = Build(model, fresh);
        var result = await resumer.ResumeAsync(reloaded, Resumer);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal([Storage], Operational(model.Requests[0]));
        Assert.Equal(1, tools[Storage].ExecutionCount);
        Assert.Equal(0, tools[Info].ExecutionCount);
        Assert.Equal(LengthTokens, result.Accounting!.TokensUsed);
        Assert.Equal(1, result.Accounting.LifetimeReplans);
    }

    private sealed record Decision(EarlyFinalRecoveryReason Reason, int? Blocking, IReadOnlyList<int>? Resolved)
    {
        public (EarlyFinalRecoveryReason, int?) Block => (Reason, Blocking);
    }

    private sealed class DenyingPolicyEngine(string denied) : IPolicyEngine
    {
        public PolicyDecision Evaluate(PolicyContext context) =>
            context.Manifest.Name == denied
                ? new PolicyDecision(PolicyMode.Forbidden, "test: denied")
                : new PolicyDecision(PolicyMode.Automatic, "test: allowed");
    }
}
