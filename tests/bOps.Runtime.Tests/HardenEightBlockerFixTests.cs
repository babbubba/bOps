// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// HARDEN-8 independent-review blockers: B1 (attempt-duration interruption of side-effecting tools), B2 (ModelCall persistence and
/// token accounting on duration expiry), B3 (exact compact/archive grammar) and B4 (aggressive overflow goal/plan projections).
/// </summary>
public sealed class HardenEightBlockerFixTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("harden-eight-blockers");
    private static readonly ActorIdentity Approver = ActorIdentity.FromOperatingSystemUser("blocker-approver");
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly Guid TaskId = Guid.Parse("0a0b0c0d-1111-2222-3333-444455556666");

    // ---------------------------------------------------------------- B1: side-effecting tool interrupted by attempt duration

    [Fact]
    public async Task B1_NonReadTool_IsInterrupted_ThenVerifiedUnderItsOwnToken_AndTheAttemptEndsWithoutRetryOrReplan()
    {
        var clock = new FakeTimeProvider(Now);
        var verifier = new TokenProbeTool("test.verify-probe");
        var mutation = new MutatingTool("test.mutate", "test.verify-probe", clock, VerificationStatus.Inconclusive);
        var model = new SequenceModel(PlanningTestSupport.PlanResponse(expectedTool: "test.mutate"), Call("test.mutate"), PlanningTestSupport.PlanResponse(revision: 1), Final());
        var audit = new RecordingAuditSink();
        var store = new InMemoryTaskStore();

        var state = await Runner(model, audit, DurationOptions(), store, clock, tools: [mutation, verifier])
            .RunAsync("goal", Actor, TaskId);

        Assert.Equal((AgentTaskStatus.BudgetExceeded, TaskTerminalKind.AttemptDurationBudget), (state.Status, state.TerminalReason!.Kind));
        Assert.Equal(1, mutation.Executions);
        Assert.True(mutation.WasInterrupted);

        // The verification path ran, once, with a usable bounded token that the expired attempt token did not cancel.
        Assert.Equal(1, verifier.Executions);
        Assert.False(verifier.StartedCancelled);
        Assert.True(verifier.CanBeCancelled);

        // No mutation retry, no replan, no further model call: the plan and the one step call only.
        Assert.Equal(2, model.Requests.Count);
        Assert.Single(state.Plans);
        var step = Assert.Single(state.Steps);
        Assert.Equal(ToolOutcome.Timeout, step.Result!.Outcome);
        Assert.Equal(ToolFailureKind.Timeout, step.Result.FailureKind);
        Assert.Equal(VerificationStatus.Inconclusive, step.VerificationStatus);
        Assert.Contains("Verification: Inconclusive", step.Observation, StringComparison.Ordinal);
        Assert.Single(step.ModelCalls!);

        var call = Assert.Single(audit.Events.OfType<ToolCallAuditEvent>(), item => item.Tool == "test.mutate");
        Assert.Equal((ToolOutcome.Timeout, VerificationStatus.Inconclusive), (call.Outcome, call.Verification));

        var persisted = await store.LoadAsync(TaskId);
        Assert.Equal(TaskTerminalKind.AttemptDurationBudget, persisted!.TerminalReason!.Kind);
        Assert.Equal(VerificationStatus.Inconclusive, persisted.Steps[0].VerificationStatus);
    }

    [Fact]
    public async Task B1_VerificationOfAnInterruptedTool_IsStillBoundedByItsOwnTimeout()
    {
        var clock = new FakeTimeProvider(Now);
        var verifier = new TokenProbeTool("test.verify-probe", hang: true, clock: clock, hangTimeout: TimeSpan.FromMilliseconds(150));
        var mutation = new MutatingTool("test.mutate", "test.verify-probe", clock, VerificationStatus.Confirmed);
        var model = new SequenceModel(PlanningTestSupport.PlanResponse(expectedTool: "test.mutate"), Call("test.mutate"));
        var options = DurationOptions() with { DefaultToolTimeout = TimeSpan.FromMilliseconds(150) };

        var state = await Runner(model, new RecordingAuditSink(), options, clock: clock, tools: [mutation, verifier])
            .RunAsync("goal", Actor, TaskId);

        // The hung verification is stopped by its own timeout, so the attempt still ends, and the outcome stays unconfirmed.
        Assert.Equal(TaskTerminalKind.AttemptDurationBudget, state.TerminalReason!.Kind);
        Assert.True(verifier.WasCancelled);
        Assert.Equal(1, mutation.Executions);
        Assert.Equal(VerificationStatus.Inconclusive, state.Steps[0].VerificationStatus);
        Assert.Equal(ToolOutcome.Timeout, state.Steps[0].Result!.Outcome);
    }

    [Fact]
    public async Task B1_ReadTool_KeepsTheAcceptedDurationBehaviour_WithNoVerification()
    {
        var clock = new FakeTimeProvider(Now);
        var model = new SequenceModel(PlanningTestSupport.PlanResponse(expectedTool: "test.slow"), Call("test.slow"), PlanningTestSupport.PlanResponse(revision: 1));

        var state = await Runner(model, new RecordingAuditSink(), DurationOptions(), clock: clock, tools: [new SlowReadTool(clock)])
            .RunAsync("goal", Actor, TaskId);

        Assert.Equal(TaskTerminalKind.AttemptDurationBudget, state.TerminalReason!.Kind);
        var step = Assert.Single(state.Steps);
        Assert.Equal(ToolOutcome.Timeout, step.Result!.Outcome);
        Assert.Null(step.VerificationStatus);
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task B1_DelegatedNonReadStep_CompletesTheJournalAsUnknown_VerifiesAndNeverBecomesRepeatable()
    {
        var clock = new FakeTimeProvider(Now);
        var verifier = new TokenProbeTool("sample.read");
        var action = new MutatingTool("sample.action", "sample.read", clock, VerificationStatus.Inconclusive);
        var audit = new RecordingAuditSink();
        var skills = new SkillRegistry();
        var plan = new ExecutionPlan("sample.remediate", "1.0.0", "Apply the sample action.",
            [new ExecutionPlanStep(0, "sample.action", ToolArguments.Empty, "Step 0.")]);
        var package = new PackageId("sample.package");
        skills.Register(package, new TestSkillProvider([
            new DelegateCapability("sample.remediate", RiskLevel.High, (_, _, _) => Task.FromResult(new SkillReport([], [], plan)),
                verification: new VerificationSpec("sample.read", [], "Confirms the sample action.")),
        ]));
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        registry.Register(package, verifier);
        registry.Register(package, action);
        var runner = new AgentRunner(new SequenceModel(), registry, new StubPolicyEngine(PolicyMode.Automatic),
            new NeverCalledApprovalProvider(), audit, new InMemoryTaskStore(), clock, NullLogger<AgentRunner>.Instance,
            new AgentRunnerOptions { MaxObservationCharacters = 1024, DefaultToolTimeout = TimeSpan.FromMinutes(10) }, skills);

        var journal = new RecordingJournal();
        var diagnostic = DelegatedExecutionScope.For(Guid.NewGuid(), new AgentIdentity(AgentId.New(), AgentRoleKind.Diagnostic),
            Envelope(RiskLevel.Read, ["sample.skill"], ["sample.remediate"], "sample.read"));
        var remediation = DelegatedExecutionScope.For(Guid.NewGuid(), new AgentIdentity(AgentId.New(), AgentRoleKind.Remediation),
            Envelope(RiskLevel.High, ["sample.skill"], ["sample.remediate"], "sample.action", "sample.read")) with { Journal = journal };
        var prepared = await runner.PrepareDelegatedSkillAsync(
            TaskId, Actor, "sample.skill", "sample.remediate", new CapabilityRequest(ToolArguments.Empty, "local", "test", BlastRadius.Single), diagnostic);
        var approval = new ExecutionPlanApproval(prepared.PlanHash!, new ApprovalDecision(true, Approver, null));

        // No production entry point runs a plan step under an attempt budget, so the seam supplies the budget an attempt would.
        using var budget = new ActiveAttemptBudget(clock, OneMinute);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunUnderAttemptBudgetAsync(budget,
            () => runner.ExecuteDelegatedPlanAsync(TaskId, Actor, prepared, approval, remediation)));

        // The step ran once and was interrupted; its effect is unknown, never failed, never repeatable.
        Assert.Equal(1, action.Executions);
        Assert.True(action.WasInterrupted);
        Assert.Equal(1, verifier.Executions);
        Assert.False(verifier.StartedCancelled);
        Assert.Equal([0], journal.Begun);
        var completed = Assert.Single(journal.Completed);
        Assert.Equal(0, completed.StepIndex);
        Assert.Equal(StepOutcomeKind.Cancelled, completed.Outcome.Kind);
        Assert.Equal(VerificationStatus.Inconclusive, completed.Outcome.Verification);
        Assert.NotNull(remediation.Meter!.UnknownOutcome);
        Assert.Contains("sample.action", remediation.Meter.UnknownOutcome, StringComparison.Ordinal);
        var call = Assert.Single(audit.Events.OfType<ToolCallAuditEvent>(), item => item.Tool == "sample.action");
        Assert.Equal(ToolOutcome.Timeout, call.Outcome);
        Assert.NotEqual(ToolOutcome.Failure, call.Outcome);
        Assert.Equal(remediation.Correlation, call.Delegation);
    }

    // ---------------------------------------------------------------- B2: ModelCall persistence and token accounting

    [Fact]
    public async Task B2_StepModelCallExpiry_PersistsEveryCallOnce_CountsUsage_AndEndsAttemptDuration()
    {
        var clock = new FakeTimeProvider(Now);
        var model = new SequenceModel(
            PlanningTestSupport.PlanResponse() with { Usage = new ModelUsage(5, 5, null) },
            Call("test.read") with { Usage = new ModelUsage(7, 3, null) },
            new Expire(clock));
        var store = new InMemoryTaskStore();

        var state = await Runner(model, new RecordingAuditSink(), DurationOptions(), store, clock, tools: [new FakeReadTool()])
            .RunAsync("goal", Actor, TaskId);

        Assert.Equal((AgentTaskStatus.BudgetExceeded, TaskTerminalKind.AttemptDurationBudget), (state.Status, state.TerminalReason!.Kind));
        var bookkeeping = state.Steps[^1];
        Assert.Equal(TaskResumePolicy.AttemptDurationStepDescription, bookkeeping.Description);
        Assert.Null(bookkeeping.ToolCall);
        Assert.Null(bookkeeping.Result);
        var interrupted = Assert.Single(bookkeeping.ModelCalls!);
        Assert.Equal(ModelCallOutcome.Failure, interrupted.Outcome);
        Assert.Equal(model.Requests.Count, RecordedCalls(state));
        Assert.Equal(20, state.Accounting!.TokensUsed);
        Assert.Equal(20, TaskResumePolicy.RecordedTokens([.. state.Plans.SelectMany(plan => plan.ModelCalls ?? []),
            .. state.Steps.SelectMany(step => step.ModelCalls ?? [])]));
        Assert.Equal(1, state.Accounting.LifetimeSteps);
        Assert.Equal(20, (await store.LoadAsync(TaskId))!.Accounting!.TokensUsed);
    }

    [Fact]
    public async Task B2_ReplanExpiry_PersistsReplanCallsOnce_AndCountsTheCompletedContinuationUsage()
    {
        var clock = new FakeTimeProvider(Now);
        var directive = new ModelResponse(ReadDirective(0, "result", 0, 1), [], false, new ModelUsage(4, 1, null));
        var model = new SequenceModel(
            PlanningTestSupport.PlanResponse(stepCount: 1) with { Usage = new ModelUsage(5, 5, null) },
            Call("test.read", "c0") with { Usage = new ModelUsage(2, 2, null) },
            Call("test.read", "c1") with { Usage = new ModelUsage(3, 3, null) },
            directive,
            new Expire(clock));
        var audit = new RecordingAuditSink();

        var state = await Runner(model, audit, DurationOptions(), clock: clock, tools: [new FakeReadTool()])
            .RunAsync("goal", Actor, TaskId);

        Assert.Equal((AgentTaskStatus.BudgetExceeded, TaskTerminalKind.AttemptDurationBudget), (state.Status, state.TerminalReason!.Kind));
        Assert.Single(state.Plans);
        var bookkeeping = state.Steps[^1];
        Assert.Equal(TaskResumePolicy.AttemptDurationStepDescription, bookkeeping.Description);
        Assert.Equal(2, bookkeeping.ModelCalls!.Count);
        Assert.Equal(model.Requests.Count, RecordedCalls(state));
        Assert.Equal(10 + 4 + 6 + 5, state.Accounting!.TokensUsed);
        Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>(), item => item.ResultCode == EvidenceReadResultCode.Success);
    }

    [Fact]
    public async Task B2_InitialPlanningCorrectiveCallExpiry_KeepsTheFirstCall_CountsItsTokens_AndResumesWithTheCumulativeTotal()
    {
        var clock = new FakeTimeProvider(Now);
        var model = new SequenceModel(new ModelResponse("not a plan", [], false, new ModelUsage(11, 4, null)), new Expire(clock));
        var store = new InMemoryTaskStore();
        var runner = Runner(model, new RecordingAuditSink(), DurationOptions(), store, clock);

        var state = await runner.RunAsync("goal", Actor, TaskId);

        Assert.Equal((AgentTaskStatus.BudgetExceeded, TaskTerminalKind.AttemptDurationBudget), (state.Status, state.TerminalReason!.Kind));
        Assert.Empty(state.Plans);
        var bookkeeping = Assert.Single(state.Steps);
        Assert.Equal(TaskResumePolicy.AttemptDurationStepDescription, bookkeeping.Description);
        Assert.Equal(2, bookkeeping.ModelCalls!.Count);
        Assert.Equal(15, TaskResumePolicy.RecordedTokens(bookkeeping.ModelCalls));
        Assert.Equal(15, state.Accounting!.TokensUsed);
        Assert.Equal(model.Requests.Count, RecordedCalls(state));

        // A resume sees the cumulative total, and adds to it rather than starting again.
        var stored = (await store.LoadAsync(TaskId))!;
        Assert.Equal(15, TaskResumePolicy.EffectiveAccounting(stored).TokensUsed);
        var resumeModel = new SequenceModel(
            PlanningTestSupport.PlanResponse(stepCount: 0) with { Usage = new ModelUsage(1, 1, null) },
            new ModelResponse("done", [], true, new ModelUsage(1, 1, null)));
        var resumed = await Runner(resumeModel, new RecordingAuditSink(), DurationOptions(), store, clock).ResumeAsync(stored, Actor);

        Assert.Equal(AgentTaskStatus.Completed, resumed.Status);
        Assert.Equal(19, resumed.Accounting!.TokensUsed);
    }

    [Fact]
    public async Task B2_EvidenceReadContinuationExpiry_RetainsTheDirectiveCallOnce_AuditsIt_AndDoesNotContinue()
    {
        var clock = new FakeTimeProvider(Now);
        var model = new SequenceModel(
            PlanningTestSupport.PlanResponse() with { Usage = new ModelUsage(5, 5, null) },
            Call("test.read") with { Usage = new ModelUsage(7, 3, null) },
            new Advance(clock, new ModelResponse(ReadDirective(0, "result", 0, 1), [], false, new ModelUsage(6, 2, null))),
            Final());
        var audit = new RecordingAuditSink();

        var state = await Runner(model, audit, DurationOptions(), clock: clock, tools: [new FakeReadTool()])
            .RunAsync("goal", Actor, TaskId);

        Assert.Equal((AgentTaskStatus.BudgetExceeded, TaskTerminalKind.AttemptDurationBudget), (state.Status, state.TerminalReason!.Kind));
        Assert.Equal(3, model.Requests.Count);
        var bookkeeping = state.Steps[^1];
        Assert.Equal(TaskResumePolicy.AttemptDurationStepDescription, bookkeeping.Description);
        var directiveCall = Assert.Single(bookkeeping.ModelCalls!);
        Assert.Equal(ModelCallOutcome.Success, directiveCall.Outcome);
        Assert.Equal(model.Requests.Count, RecordedCalls(state));
        Assert.Equal(10 + 10 + 8, state.Accounting!.TokensUsed);
        var read = Assert.Single(audit.Events.OfType<EvidenceReadAuditEvent>());
        Assert.Equal(EvidenceReadResultCode.AttemptBudgetInterrupted, read.ResultCode);
    }

    [Fact]
    public async Task B2_TheBookkeepingStep_IsNotEvidenceAddressable_NorInTheDigest_NorAFinalMarker()
    {
        var clock = new FakeTimeProvider(Now);
        var model = new SequenceModel(PlanningTestSupport.PlanResponse(), Call("test.read"), new Expire(clock));

        var state = await Runner(model, new RecordingAuditSink(), DurationOptions(), clock: clock, tools: [new FakeReadTool()])
            .RunAsync("goal", Actor, TaskId);

        var bookkeeping = state.Steps[^1];
        Assert.True(TaskResumePolicy.IsSyntheticFailureStep(bookkeeping));
        Assert.False(FinalResponse.IsFinalStep(bookkeeping));
        Assert.Equal(EvidenceReadResultCode.MissingStep, EvidenceRead.Read(TaskId, state.Steps,
            new EvidenceReadDirective(EvidenceRead.EvidenceId(TaskId, bookkeeping.Index), "result", 0, 1)).Code);
        Assert.Null(EvidenceLimitationsDigest.Build([bookkeeping], diagnostic: false));
        Assert.InRange(bookkeeping.Observation!.Length, 1, 128);
    }

    // ---------------------------------------------------------------- B4: aggressive ContextOverflow projections

    [Fact]
    public async Task B4_AggressiveStepRecovery_ProjectsGoalTo2048_AndPlanTo1024_ThenTheNextCallIsNormalAgain()
    {
        var goal = HeadTail(6000);
        var model = new SequenceModel(
            PlanningTestSupport.PlanResponseIndexed("test.read", 5, HeadTail(3000)),
            PlanningTestSupport.IndexedCall("test.read", 0, "c0"),
            ContextOverflow(),
            PlanningTestSupport.IndexedCall("test.read", 1, "c1"),
            PlanningTestSupport.IndexedCall("test.read", 2, "c2"),
            PlanningTestSupport.IndexedCall("test.read", 3, "c3"),
            PlanningTestSupport.IndexedCall("test.read", 4, "c4"),
            Final());

        var state = await Runner(model, new RecordingAuditSink(), tools: [new FakeReadTool(parameters: [PlanningTestSupport.CallIndexParameter])]).RunAsync(goal, Actor, TaskId);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        var plan = state.Plans[0];
        var fullPlan = AgentRunner.DescribePlan(plan);
        Assert.True(fullPlan.Length > 2048);

        var normalBefore = model.Requests[2];
        Assert.Equal(goal, normalBefore.History[0].Content);
        Assert.Contains(fullPlan, normalBefore.SystemPrompt, StringComparison.Ordinal);

        var aggressive = model.Requests[3];
        var projectedGoal = aggressive.History[0].Content!;
        Assert.InRange(projectedGoal.Length, 1, 2048);
        Assert.Equal(AgentRunner.ProjectForPrompt(goal, 2048), projectedGoal);
        var projectedPlan = AgentRunner.ProjectForPrompt(fullPlan, 1024);
        Assert.InRange(projectedPlan.Length, 1, 1024);
        Assert.Contains(projectedPlan, aggressive.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(fullPlan, aggressive.SystemPrompt, StringComparison.Ordinal);
        AssertHeadTail(goal, projectedGoal);
        AssertHeadTail(fullPlan, projectedPlan);

        var next = model.Requests[4];
        Assert.Equal(goal, next.History[0].Content);
        Assert.Contains(fullPlan, next.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task B4_AggressiveReplanRecovery_ProjectsGoalTo2048_AndPlanTo1024_NormalReplanKeeps2048()
    {
        var goal = HeadTail(6000);
        var model = new SequenceModel(
            PlanningTestSupport.PlanResponse(stepCount: 1, rationale: HeadTail(3000)),
            Call("test.read", "c0"),
            Call("test.read", "c1"),
            ContextOverflow(),
            // AF-TOOLROUTE-01: the revision plans new checks; re-planning the completed test.read {} would be refused as a repeat.
            PlanningTestSupport.PlanResponseWithDistinctSteps(stepCount: 5, revision: 1, expectedTool: "test.read.next"),
            new ModelResponse("done\n\nEvidence limitations\n- the exhausted-plan proposal was not executed.", [], true, null),
            // AF-05H: revision 1 still has five unresolved steps, so that final is re-evaluated; the replan drops them.
            PlanningTestSupport.PlanResponse(stepCount: 0, revision: 2),
            new ModelResponse("done\n\nEvidence limitations\n- the exhausted-plan proposal was not executed.", [], true, null));

        var state = await Runner(model, new RecordingAuditSink(), tools: [new FakeReadTool()]).RunAsync(goal, Actor, TaskId);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        var fullPlan = AgentRunner.DescribePlan(state.Plans[0]);
        Assert.True(fullPlan.Length > 2048);

        // The normal replan attempt: full goal, current plan projected to the accepted 2,048.
        var normal = model.Requests[3];
        Assert.Equal(goal, normal.History[0].Content);
        Assert.Equal(AgentRunner.ProjectForPrompt(fullPlan, 2048), PlanText(normal));
        Assert.InRange(PlanText(normal)!.Length, 1, 2048);

        // The one aggressive recovery: goal <= 2048 and plan <= 1024, both deterministic head/tail projections.
        var aggressive = model.Requests[4];
        Assert.Equal(AgentRunner.ProjectForPrompt(goal, 2048), aggressive.History[0].Content);
        Assert.InRange(aggressive.History[0].Content!.Length, 1, 2048);
        Assert.Equal(AgentRunner.ProjectForPrompt(fullPlan, 1024), PlanText(aggressive));
        Assert.InRange(PlanText(aggressive)!.Length, 1, 1024);
        AssertHeadTail(goal, aggressive.History[0].Content!);
        AssertHeadTail(fullPlan, PlanText(aggressive)!);

        // The next logical call (a step) is back to the configured request.
        Assert.Equal(goal, model.Requests[5].History[0].Content);
        // AF-05H: then the re-evaluation of that premature final (an empty replan) and the accepted final.
        Assert.Equal(8, model.Requests.Count);
    }

    [Fact]
    public async Task B4_ASecondOverflow_StillTerminates_WithoutAnotherRecovery()
    {
        var model = new SequenceModel(
            PlanningTestSupport.PlanResponseWithDistinctSteps(stepCount: 5),
            Call("test.read"),
            ContextOverflow(),
            ContextOverflow());

        var state = await Runner(model, new RecordingAuditSink(), tools: [new FakeReadTool()]).RunAsync("goal", Actor, TaskId);

        Assert.Equal((AgentTaskStatus.Failed, ModelFailureKind.ContextOverflow), (state.Status, state.TerminalReason!.FailureKind));
        Assert.Equal(4, model.Requests.Count);
    }

    // ---------------------------------------------------------------- helpers

    private static AgentRunnerOptions DurationOptions() =>
        new() { MaxAttemptDuration = OneMinute, DefaultToolTimeout = TimeSpan.FromMinutes(10) };

    private static AgentRunner Runner(
        IChatModel model,
        IAuditSink audit,
        AgentRunnerOptions? options = null,
        ITaskStore? store = null,
        TimeProvider? clock = null,
        params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        return new AgentRunner(model, registry, new StubPolicyEngine(PolicyMode.Automatic), new NeverCalledApprovalProvider(),
            audit, store ?? new InMemoryTaskStore(), clock ?? TimeProvider.System,
            NullLogger<AgentRunner>.Instance, options ?? new AgentRunnerOptions());
    }

    private static AuthorityEnvelope Envelope(RiskLevel risk, IReadOnlyList<string> skills, IReadOnlyList<string> capabilities, params string[] tools) =>
        new(Actor, Depth: 1, skills, capabilities, tools, risk, BlastRadius.Single, ["local"], ["test"],
            new DelegationBudget(10, 100_000, Now.AddHours(1)), null);

    private static PlanStep Step(int index, string output, int? revision = 0, VerificationStatus? verification = null) =>
        new(index, "test.read", new ModelToolCall($"c-{index}", "test.read", ToolArguments.Empty),
            ToolCallResult.Success(output), output, revision)
        {
            VerificationStatus = verification,
        };

    private static ModelResponse Call(string tool, string id = "call") =>
        new(null, [new ModelToolCall(id, tool, ToolArguments.Empty)], false, null);

    private static ModelResponse Final() => new("done", [], true, null);

    private static ModelProtocolException ContextOverflow() =>
        new("context overflow") { FailureKind = ModelFailureKind.ContextOverflow };

    private static string ReadDirective(int index, string source, int offset, int length) =>
        $"{{\"runtime\":\"EvidenceRead/v1\",\"evidenceId\":\"{EvidenceRead.EvidenceId(TaskId, index)}\",\"source\":\"{source}\",\"offset\":{offset},\"length\":{length}}}";

    /// <summary>A deterministic string whose head and tail differ, so a head/tail projection is observable.</summary>
    /// <summary>The previous-plan turn of a replan request: unprojected, it follows the historical turns instead of the goal.</summary>
    private static string PlanText(ModelRequest request) =>
        request.History.Single(turn => turn.Role == ChatRole.Assistant
            && turn.Content?.StartsWith("Plan (revision ", StringComparison.Ordinal) == true).Content!;

    private static string HeadTail(int length) =>
        string.Concat(Enumerable.Range(0, length / 10 + 1).Select(i => i.ToString("D9", CultureInfo.InvariantCulture) + "|"))[..length];

    private static void AssertHeadTail(string original, string projected)
    {
        const string marker = "\n...[projected]...\n";
        var at = projected.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at > 0);
        var head = projected[..at];
        var tail = projected[(at + marker.Length)..];
        Assert.StartsWith(head, original, StringComparison.Ordinal);
        Assert.EndsWith(tail, original, StringComparison.Ordinal);
        Assert.True(head.Length > tail.Length);
    }

    private static int RecordedCalls(TaskState state) =>
        state.Plans.Sum(plan => plan.ModelCalls?.Count ?? 0) + state.Steps.Sum(step => step.ModelCalls?.Count ?? 0);

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>A scripted model item that advances the clock past the attempt-duration budget, then waits for cancellation.</summary>
    private sealed record Expire(FakeTimeProvider Clock);

    /// <summary>A scripted model item that advances the clock past the attempt-duration budget, then answers.</summary>
    private sealed record Advance(FakeTimeProvider Clock, ModelResponse Response);

    private sealed class SequenceModel(params object[] items) : IChatModel
    {
        private int index;
        internal List<ModelRequest> Requests { get; } = [];
        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var item = items[index++];
            switch (item)
            {
                case Expire expire:
                    expire.Clock.Advance(TimeSpan.FromMinutes(2));
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    throw new InvalidOperationException("unreachable");
                case Advance advance:
                    advance.Clock.Advance(TimeSpan.FromMinutes(2));
                    return advance.Response;
                case Exception exception:
                    throw exception;
                default:
                    return (ModelResponse)item;
            }
        }
    }

    private sealed class RecordingJournal : IStepJournal
    {
        internal List<int> Begun { get; } = [];
        internal List<(int StepIndex, StepOutcome Outcome)> Completed { get; } = [];

        public Task BeginAsync(int stepIndex, string toolName, ToolArguments arguments)
        {
            Begun.Add(stepIndex);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(int stepIndex, StepOutcome outcome)
        {
            Completed.Add((stepIndex, outcome));
            return Task.CompletedTask;
        }
    }

    /// <summary>A Read tool that reports the token it was given, to prove verification runs under a usable, bounded one.</summary>
    private sealed class TokenProbeTool(string name, bool hang = false, FakeTimeProvider? clock = null, TimeSpan hangTimeout = default) : ITool
    {
        internal int Executions { get; private set; }
        internal bool StartedCancelled { get; private set; }
        internal bool CanBeCancelled { get; private set; }
        internal bool WasCancelled { get; private set; }

        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "Reports its cancellation token.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };

        public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            Executions++;
            StartedCancelled = ct.IsCancellationRequested;
            CanBeCancelled = ct.CanBeCanceled;
            if (hang)
            {
                try
                {
                    // The runner's tool timeout is driven by the fake clock, so the hang must move it forward.
                    clock?.Advance(hangTimeout);
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    WasCancelled = true;
                    throw;
                }
            }

            return ToolCallResult.Success("observed state");
        }
    }

    /// <summary>A side-effecting tool that runs until the attempt-duration budget cancels it, and verifies through its probe.</summary>
    private sealed class MutatingTool(string name, string verifyTool, FakeTimeProvider clock, VerificationStatus verdict) : IVerifiableTool
    {
        internal int Executions { get; private set; }
        internal bool WasInterrupted { get; private set; }

        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "Mutates until cancelled.",
            Risk = RiskLevel.High,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
            Verification = new VerificationSpec(verifyTool, [], "Reads the state the mutation targets."),
        };

        public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            Executions++;
            clock.Advance(TimeSpan.FromMinutes(2));
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                WasInterrupted = true;
                throw;
            }

            return ToolCallResult.Success("unreachable");
        }

        public Task<VerificationOutcome> EvaluateVerificationAsync(
            ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
            Task.FromResult(verificationToolResult.Succeeded
                ? new VerificationOutcome(verdict, "the interrupted effect could not be established")
                : new VerificationOutcome(VerificationStatus.Inconclusive, "verification did not complete"));
    }

    private sealed class SlowReadTool(FakeTimeProvider clock) : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = "test.slow",
            Description = "Runs until cancelled.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };

        public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            clock.Advance(TimeSpan.FromMinutes(2));
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }
}
