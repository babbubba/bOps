// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// AF-05H H2 (ADR-0046 early-final amendment): a final candidate is accepted only when the settled plan has no unresolved
/// executable step. Otherwise the paid call is kept on a bookkeeping step and the plan is re-evaluated by a governed replan, which
/// may be empty. Live tests LIVE-2 (one harmless step left) and LIVE-3 (five diagnostic steps left) both ended Completed before.
/// </summary>
public sealed class EarlyFinalReplanTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");
    private static readonly ActorIdentity Resumer = new("api-user", "resumer", "Resumer");
    private const string A = "diag.a";
    private const string B = "diag.b";
    private const string C = "diag.c";
    private const string D = "diag.d";
    private const string PrematureText = "PREMATURE-ANSWER-SENTINEL: the cause cannot be determined without further analysis.";

    private static ModelResponse Plan(params string[] tools) =>
        new(new JsonObject
        {
            ["rationale"] = "test",
            ["steps"] = new JsonArray([.. tools.Select(tool => (JsonNode)new JsonObject { ["description"] = $"use {tool}", ["expectedTool"] = tool })]),
        }.ToJsonString(), [], false, null);

    private static ModelResponse Call(string tool, ModelUsage? usage = null) =>
        new(null, [new ModelToolCall(Guid.NewGuid().ToString("N"), tool, ToolArguments.Empty)], false, usage);

    private static ModelResponse Final(string text = "done", ModelUsage? usage = null) => new(text, [], true, usage);

    private static ModelResponse Malformed(string finish = "stop") => new("{\"steps\":[", [], false, new ModelUsage(17, 4096, null))
    {
        Details = new ModelCallDetails("fake-model", finish, "request", "original malformed response"),
    };

    private static Func<ModelRequest, CancellationToken, Task<ModelResponse>> Reply(ModelResponse response) =>
        (_, _) => Task.FromResult(response);

    private static (AgentRunner Runner, RecordingAuditSink Audit, Dictionary<string, ResultTool> Tools) Build(
        IChatModel model, ITaskStore? store = null, AgentRunnerOptions? options = null,
        IPolicyEngine? policy = null, bool governed = false, IApprovalProvider? approval = null, TimeProvider? clock = null)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        var tools = new[] { A, B, C, D }.ToDictionary(name => name, name => new ResultTool(name, ToolCallResult.Success($"{name} ok")));
        foreach (var tool in tools.Values)
        {
            registry.Register(new PackageId("test.package"), PackageTrustLevel.Official,
                new EntitlementRequirement(governed ? EntitlementApplicability.Governed : EntitlementApplicability.NotGoverned), tool);
        }

        var audit = new RecordingAuditSink();
        var runner = new AgentRunner(model, registry, policy ?? new DefaultTestPolicyEngine(), approval ?? new NeverCalledApprovalProvider(), audit,
            store ?? new InMemoryTaskStore(), clock ?? TimeProvider.System, NullLogger<AgentRunner>.Instance,
            options ?? new AgentRunnerOptions { MaxSteps = 20, MaxLifetimeSteps = 40 });
        return (runner, audit, tools);
    }

    private static IEnumerable<string> Operational(ModelRequest request) =>
        request.AvailableTools.Select(tool => tool.Name).Where(name => name != EvidenceRead.ControlFunctionName);

    private static bool Mentions(ModelRequest request, string text) =>
        request.SystemPrompt.Contains(text, StringComparison.Ordinal)
        || request.History.Any(turn => turn.Content?.Contains(text, StringComparison.Ordinal) == true);

    // 1
    [Fact]
    public async Task AFinalWithAnEmptyPlan_CompletesDirectly()
    {
        var model = new FakeChatModel(Plan(), Final());
        var (runner, _, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(result.Plans);
        Assert.True(FinalResponse.IsFinalStep(Assert.Single(result.Steps)));
        Assert.Equal(2, model.Requests.Count);
    }

    // 2
    [Fact]
    public async Task AFinalAfterThePlanIsFullyConsumed_CompletesDirectly()
    {
        var model = new FakeChatModel(Plan(A, B), Call(A), Call(B), Final());
        var (runner, _, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(result.Plans);
        Assert.DoesNotContain(result.Steps, PrematureFinal.IsStep);
    }

    // 3
    [Fact]
    public async Task AFinalWithOneStepRemaining_IsNeverCompleted_ItReplansAndKeepsThePaidCall()
    {
        var model = new FakeChatModel(
            Plan(A, B), Call(A), Final(PrematureText, new ModelUsage(70, 30, null)), Plan(), Final("second answer"));
        var (runner, audit, tools) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(2, result.Plans.Count);
        Assert.Equal(0, tools[B].ExecutionCount);
        var premature = result.Steps[1];
        Assert.True(PrematureFinal.IsStep(premature));
        Assert.False(FinalResponse.IsFinalStep(premature));
        Assert.Null(premature.ToolCall);
        Assert.Null(premature.Result);
        Assert.Null(premature.PlannedStepIndex);
        Assert.Equal(0, premature.PlanRevision);
        // Runtime-authored: the planned position and tool only, never the model's text.
        Assert.Equal(
            "Final answer not accepted: plan revision 0 still had 1 unresolved planned step(s): 2 [diag.b]; the plan is re-evaluated before any final answer is accepted.",
            premature.Observation);
        Assert.DoesNotContain(PrematureText, premature.Observation, StringComparison.Ordinal);
        // The paid call is persisted, audited and counted.
        var record = Assert.Single(premature.ModelCalls!);
        Assert.Equal(100, record.Usage!.PromptTokens + record.Usage.CompletionTokens);
        Assert.Contains(audit.Events.OfType<ModelCallAuditEvent>(), e => e.StepIndex == premature.Index);
        Assert.Equal(TaskResumePolicy.RecordedTokens(result.Plans.SelectMany(p => p.ModelCalls ?? []).ToList())
            + result.Steps.Sum(step => TaskResumePolicy.RecordedTokens(step.ModelCalls)), result.Accounting!.TokensUsed);
        // It is not evidence and not a limitation.
        Assert.False(EvidenceIndex.IsAddressable(premature));
        Assert.DoesNotContain(EvidenceLimitationsDigest.Classify(result.Steps), entry => entry.Step.Index == premature.Index);
        Assert.Equal("second answer", result.Steps[^1].Observation);
    }

    [Fact]
    public async Task TheReplan_IsGoverned_ToolFree_RuntimeAuthored_AndNeverRepeatsThePrematureAnswer()
    {
        var model = new FakeChatModel(Plan(A, B, C), Call(A), Final(PrematureText), Plan(), Final());
        var (runner, audit, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        var replan = model.Requests[3];
        Assert.Empty(replan.AvailableTools);
        Assert.Contains("Revise it", replan.SystemPrompt, StringComparison.Ordinal);
        Assert.True(Mentions(replan, PrematureFinal.ReplanDirective));
        Assert.True(Mentions(replan, "2 [diag.b]; 3 [diag.c];"));
        Assert.False(Mentions(replan, PrematureText));
        // Audited as a model call of the replan (it is recorded on the accepted plan, not on a step).
        Assert.Equal(1, result.Accounting!.LifetimeReplans);
        Assert.Single(result.Plans[1].ModelCalls!);
        Assert.Contains(audit.Events.OfType<ModelCallAuditEvent>(), e => e.StepIndex == result.Steps[1].Index && e.Outcome == ModelCallOutcome.Success);
    }

    // 4
    [Fact]
    public async Task AnEmptyReplan_ThenANewToolFreeCall_ProducesTheAcceptedFinal()
    {
        var model = new FakeChatModel(Plan(A, B), Call(A), Final(PrematureText), Plan(), Final("accepted answer"));
        var (runner, _, _) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Empty(result.Plans[^1].Steps);
        // The final is a new call made in the context of the accepted empty revision, not the reused premature answer.
        Assert.Equal(5, model.Requests.Count);
        var finalCall = model.Requests[4];
        Assert.Empty(Operational(finalCall));
        Assert.False(Mentions(finalCall, PrematureText));
        Assert.Equal("accepted answer", result.Steps[^1].Observation);
        Assert.True(FinalResponse.IsFinalStep(result.Steps[^1]));
        Assert.Equal(1, result.Steps[^1].PlanRevision);
    }

    // 5
    [Fact]
    public async Task ANonEmptyReplan_ContinuesNormalExecution()
    {
        var model = new FakeChatModel(Plan(A, B), Call(A), Final(PrematureText), Plan(B), Call(B), Final());
        var (runner, _, tools) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(1, tools[B].ExecutionCount);
        Assert.Equal([B], Operational(model.Requests[4]));
    }

    // 6: LIVE-2 equivalent — the superfluous last step is removed by an empty replan
    [Fact]
    public async Task Live2Equivalent_ASuperfluousLastStep_IsDroppedByAnEmptyReplan_AndProjectsAsSuperseded()
    {
        var model = new FakeChatModel(Plan(A, B, C, D), Call(A), Call(B), Call(C), Final(PrematureText), Plan(), Final());
        var (runner, _, tools) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(0, tools[D].ExecutionCount);
        var projected = ExecutionPlanProjector.Project(result, executing: false)!;
        Assert.Equal(1, projected.ActiveRevision);
        Assert.Equal(
            [ProjectedStepStatus.Completed, ProjectedStepStatus.Completed, ProjectedStepStatus.Completed, ProjectedStepStatus.Superseded],
            projected.Revisions[0].Steps.Select(step => step.Status));
        Assert.True(projected.Revisions[1].Active);
        Assert.Empty(projected.Revisions[1].Steps);
        Assert.DoesNotContain(projected.Revisions.SelectMany(r => r.Steps), step => step.Status == ProjectedStepStatus.Failed);
    }

    // 7: LIVE-3 equivalent — the meaningful diagnostic suffix is not silently abandoned
    [Fact]
    public async Task Live3Equivalent_AFinalAfterTheFirstDiagnostic_DoesNotAbandonTheRemainingDiagnostics()
    {
        var model = new FakeChatModel(
            Plan(A, B, C, D), Call(A), Final(PrematureText), Plan(B, C, D), Call(B), Call(C), Call(D), Final("root cause found"));
        var (runner, _, tools) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.All(tools.Values, tool => Assert.Equal(1, tool.ExecutionCount));
        Assert.Equal("root cause found", result.Steps[^1].Observation);
        var projected = ExecutionPlanProjector.Project(result, executing: false)!;
        Assert.Equal([ProjectedStepStatus.Completed, ProjectedStepStatus.Superseded, ProjectedStepStatus.Superseded, ProjectedStepStatus.Superseded],
            projected.Revisions[0].Steps.Select(step => step.Status));
        Assert.All(projected.Revisions[1].Steps, step => Assert.Equal(ProjectedStepStatus.Completed, step.Status));
    }

    // 8
    [Fact]
    public async Task AMalformedEarlyFinalReplan_ContinuesOnlyThePersistedPendingStep()
    {
        var model = new FakeChatModel(
            Plan(A, B), Call(A), Final(PrematureText),
            new ModelResponse("not a plan", [], false, null), new ModelResponse("still not a plan", [], false, null), Call(B), Final());
        var (runner, _, tools) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(result.Plans);
        Assert.Equal((1, 1), (tools[A].ExecutionCount, tools[B].ExecutionCount));
        Assert.True(PrematureFinal.IsStep(result.Steps[1]));
        var recovery = Assert.Single(result.Steps, EarlyFinalRecovery.IsStep);
        Assert.Equal(1, recovery.EarlyFinalRecoveryCursor);
        Assert.Equal(2, result.Steps[1].EarlyFinalReplanCalls!.Count);
        Assert.Equal(1, result.Accounting!.LifetimeReplans);
        Assert.Equal([B], Operational(model.Requests[5]));
        Assert.Equal(2, PlannedStepPosition.Derive(result.Plans[0], result.Steps).Cursor);

    }

    // LIVE-05: the request after a persisted-plan continuation was byte-identical to the one that drew the premature final.
    [Fact]
    public async Task ARecoveredStepRequest_DiffersFromThePrematureOne_ByARuntimeAuthoredInstructionNamingThePendingTool()
    {
        var model = new FakeChatModel(
            Plan(A, B, C), Call(A), Final(PrematureText), Malformed("length"), Call(B), Call(C), Final());
        var (runner, _, tools) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(result.Plans);
        Assert.Equal(1, Assert.Single(result.Steps, EarlyFinalRecovery.IsStep).EarlyFinalRecoveryCursor);
        Assert.Equal((1, 1, 1), (tools[A].ExecutionCount, tools[B].ExecutionCount, tools[C].ExecutionCount));
        var prematureRequest = model.Requests[2];
        var recoveredRequest = model.Requests[4];
        static IEnumerable<(ChatRole, string?, string?, ContextProvenance?)> Shape(ModelRequest request) =>
            request.History.Select(turn => (turn.Role, turn.Content, turn.ToolCallId, turn.Provenance));

        // Not the same request: exactly one runtime-authored user turn is appended, nothing else in the context changes.
        Assert.Equal(prematureRequest.SystemPrompt, recoveredRequest.SystemPrompt);
        Assert.Equal(prematureRequest.History.Count + 1, recoveredRequest.History.Count);
        Assert.Equal(Shape(prematureRequest), Shape(recoveredRequest).Take(prematureRequest.History.Count));
        var control = recoveredRequest.History[^1];
        Assert.Equal((ChatRole.User, ContextProvenance.Runtime), (control.Role, control.Provenance));
        Assert.Contains("rejected", control.Content);
        Assert.Contains("incomplete", control.Content);
        Assert.Contains("step 2 of 3", control.Content);
        Assert.Contains($"Propose the call to {B}", control.Content);
        Assert.Contains("before giving any final answer", control.Content);

        // Built only from the persisted plan: no model text, plan description or tool output enters it, and the offered tools are unchanged.
        Assert.DoesNotContain(PrematureText, control.Content);
        Assert.DoesNotContain($"use {B}", control.Content);
        Assert.DoesNotContain($"{A} ok", control.Content);
        Assert.Equal([B], Operational(recoveredRequest));
        Assert.Equal(prematureRequest.AvailableTools.Select(tool => tool.Name), recoveredRequest.AvailableTools.Select(tool => tool.Name));

        // The ordinary request is untouched: no instruction before the recovery, nor after the recovered step has run.
        Assert.DoesNotContain(model.Requests.Take(4), request => Mentions(request, "Runtime control instruction"));
        Assert.DoesNotContain(model.Requests.Skip(5), request => Mentions(request, "Runtime control instruction"));
    }

    // 9
    [Fact]
    public async Task RepeatedPrematureFinals_AreBoundedByTheReplanBudget()
    {
        var model = new FakeChatModel(
            Plan(A, B), Call(A), Final(PrematureText), Plan(B), Final(PrematureText), Plan(B), Final(PrematureText));
        var (runner, _, tools) = Build(model, options: new AgentRunnerOptions { MaxReplans = 2, MaxLifetimeReplans = 12, MaxSteps = 20 });

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.ReplanLimitReached, result.Status);
        Assert.Equal(3, result.Plans.Count);
        Assert.Equal(3, result.Steps.Count(PrematureFinal.IsStep));
        Assert.Equal(0, tools[B].ExecutionCount);
        Assert.DoesNotContain(result.Steps, FinalResponse.IsFinalStep);
        // The unresolved step of the active revision was never attempted: it is not shown as failed.
        var active = ExecutionPlanProjector.Project(result, executing: false)!.Revisions[^1];
        Assert.Equal(ProjectedStepStatus.Pending, Assert.Single(active.Steps).Status);
    }

    // 10a: interrupted after the bookkeeping step, before the replan
    [Fact]
    public async Task Resume_AfterAPrematureFinalWhoseReplanWasNotMade_ReplansFirst_WithTheSameDirective()
    {
        var store = new InMemoryTaskStore();
        var (live, _, _) = Build(new FakeChatModel(Plan(A, B), Call(A), Final(PrematureText)), store,
            new AgentRunnerOptions { MaxSteps = 2, MaxLifetimeSteps = 10 });
        var interrupted = await live.RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.MaxStepsReached, interrupted.Status);
        Assert.True(PrematureFinal.IsStep(interrupted.Steps[^1]));

        var reloaded = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(await store.LoadAsync(interrupted.Id)))!;
        var position = PlannedStepPosition.Derive(reloaded.Plans[^1], reloaded.Steps);
        Assert.Equal((1, true), (position.Cursor, position.ReplanRequired));

        var fresh = new InMemoryTaskStore();
        fresh.Seed(reloaded);
        var model = new FakeChatModel(Plan(B), Call(B), Final());
        var (resumer, _, tools) = Build(model, fresh, new AgentRunnerOptions { MaxSteps = 2, MaxLifetimeSteps = 10 });
        var result = await resumer.ResumeAsync(reloaded, Resumer);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Empty(model.Requests[0].AvailableTools);
        Assert.True(Mentions(model.Requests[0], PrematureFinal.ReplanDirective));
        Assert.True(Mentions(model.Requests[0], "2 [diag.b];"));
        Assert.Equal(1, tools[B].ExecutionCount);
    }

    // 10b: interrupted after the accepted replan
    [Fact]
    public async Task Resume_AfterAnAcceptedEarlyFinalReplan_ContinuesAtTheStartOfTheNewRevision()
    {
        var store = new InMemoryTaskStore();
        // The call after the accepted replan finds no scripted response: the attempt fails there.
        var (live, _, _) = Build(new FakeChatModel(Plan(A, B, C), Call(A), Final(PrematureText), Plan(B, C)), store);
        var interrupted = await live.RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, interrupted.Status);
        Assert.Equal(2, interrupted.Plans.Count);

        var reloaded = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(await store.LoadAsync(interrupted.Id)))!;
        Assert.Equal(PlannedStepPosition.Start, PlannedStepPosition.Derive(reloaded.Plans[^1], reloaded.Steps));

        var fresh = new InMemoryTaskStore();
        fresh.Seed(reloaded);
        var model = new FakeChatModel(Call(B), Call(C), Final());
        var (resumer, _, tools) = Build(model, fresh);
        var result = await resumer.ResumeAsync(reloaded, Resumer);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal([B], Operational(model.Requests[0]));
        Assert.Equal([C], Operational(model.Requests[1]));
        Assert.Equal((1, 1, 0), (tools[B].ExecutionCount, tools[C].ExecutionCount, tools[A].ExecutionCount));
        Assert.Equal(2, result.Plans.Count);
    }

    // 11
    [Fact]
    public async Task ConditionalStepsAlreadySkippedDeterministically_DoNotBlockTheFinal()
    {
        var plan = new ModelResponse(new JsonObject
        {
            ["rationale"] = "test",
            ["steps"] = new JsonArray(
                new JsonObject { ["description"] = "discover", ["expectedTool"] = A },
                new JsonObject
                {
                    ["description"] = "follow up",
                    ["expectedTool"] = B,
                    ["activation"] = new JsonObject { ["sourceStep"] = 0, ["factType"] = "artifact.kind", ["factKey"] = "primary" },
                }),
        }.ToJsonString(), [], false, null);
        var model = new FakeChatModel(plan, Call(A), Final());
        var (runner, _, tools) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Single(result.Plans);
        Assert.Equal(0, tools[B].ExecutionCount);
        Assert.DoesNotContain(result.Steps, PrematureFinal.IsStep);
        Assert.Equal(3, model.Requests.Count);
    }

    [Fact]
    public void TheFold_TreatsABookkeepingStepAsReplanRequired_ForEveryPlanContract()
    {
        var bookkeeping = new PlanStep(1, PrematureFinal.StepDescription, null, null, "x", 0);
        var semantic = new AgentPlan(0, "r", [new PlannedStep(0, "a", A), new PlannedStep(1, "b", B)]) { SemanticContractVersion = 1 };
        var legacy = semantic with { SemanticContractVersion = 0 };

        Assert.True(PlannedStepPosition.Start.After(semantic, bookkeeping).ReplanRequired);
        Assert.True(PlannedStepPosition.Start.After(legacy, bookkeeping).ReplanRequired);
        Assert.Equal(0, PlannedStepPosition.Start.After(legacy, bookkeeping).Cursor);
        Assert.False(PrematureFinal.IsStep(bookkeeping with { ToolCall = new ModelToolCall("c", A, ToolArguments.Empty) }));
        Assert.False(FinalResponse.IsFinalStep(bookkeeping));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Length_IsNeverRetriedWithTheSameLimit_AndEveryPaidCallIsPreserved(bool lengthOnRetry)
    {
        var script = new List<ModelResponse> { Plan(A, B, C), Call(A), Final(PrematureText) };
        if (lengthOnRetry)
        {
            script.Add(Malformed());
        }
        script.AddRange([Malformed("length"), Call(B), Call(C), Final()]);
        var model = new FakeChatModel([.. script]);
        var (runner, audit, tools) = Build(model);
        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(script.Count, model.Requests.Count);
        Assert.Equal((1, 1, 1), (tools[A].ExecutionCount, tools[B].ExecutionCount, tools[C].ExecutionCount));
        var recovery = Assert.Single(result.Steps, EarlyFinalRecovery.IsStep);
        Assert.Equal(1, recovery.EarlyFinalRecoveryCursor);
        var failedCalls = result.Steps[1].EarlyFinalReplanCalls!;
        Assert.Equal(lengthOnRetry ? 2 : 1, failedCalls.Count);
        Assert.Equal("length", failedCalls[^1].FinishReason);
        Assert.All(failedCalls, call =>
        {
            Assert.Equal(ModelFailureKind.MalformedResponse, call.FailureKind);
            Assert.Equal("original malformed response", call.ResponseJson);
        });
        Assert.Equal(script.Count, audit.Events.OfType<ModelCallAuditEvent>().Count());
        Assert.Contains(audit.Events.OfType<TaskLifecycleAuditEvent>(), e =>
            e.Stage == TaskLifecycleStage.EarlyFinalPlanContinued && e.StepIndex == recovery.Index);
        Assert.Equal((lengthOnRetry ? 2 : 1) * 4113, result.Accounting!.TokensUsed);
        Assert.Equal(result.Accounting.TokensUsed, TaskResumePolicy.DeriveAccounting(result).TokensUsed);
        Assert.Equal([B], Operational(model.Requests[lengthOnRetry ? 5 : 4]));
        Assert.False(EvidenceIndex.IsAddressable(recovery));
        Assert.All(ExecutionPlanProjector.Project(result, false)!.Revisions[0].Steps,
            step => Assert.Equal(ProjectedStepStatus.Completed, step.Status));
    }

    // AF-05H2-R2: an output-limited replan is not paid for again in the same revision, and a continuation needs progress.
    [Fact]
    public async Task OutputLimitedRecoveryWithoutProgress_FailsWithoutASecondPaidReplan()
    {
        var model = new FakeChatModel(Plan(A), Final(), Malformed("length"), Final(), Malformed("length"), Final());
        var (runner, audit, tools) = Build(model, options: new AgentRunnerOptions { MaxReplans = 2, MaxLifetimeReplans = 2 });
        var result = await runner.RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, result.Status);
        Assert.Equal(ModelFailureKind.MalformedResponse, result.TerminalReason!.FailureKind);
        Assert.Equal(1, result.Accounting!.LifetimeReplans);
        Assert.Equal(4113, result.Accounting.TokensUsed);
        Assert.Equal(4, model.Requests.Count);
        Assert.Equal(0, tools[A].ExecutionCount);
        Assert.DoesNotContain(result.Steps, FinalResponse.IsFinalStep);
        var second = result.Steps.Last(PrematureFinal.IsStep);
        Assert.Null(second.EarlyFinalReplanCalls);
        Assert.Equal(new EarlyFinalRecoveryDecision
        {
            Accepted = false, Reason = EarlyFinalRecoveryReason.RepeatedWithoutProgress, PlanRevision = 0, Cursor = 0,
            BlockingStepIndex = result.Steps.Single(EarlyFinalRecovery.IsStep).Index,
        }, second.EarlyFinalRecoveryDecision);
        Assert.Contains(audit.Events.OfType<TaskLifecycleAuditEvent>(), e =>
            e.Stage == TaskLifecycleStage.EarlyFinalPlanContinuationRefused && e.RefusalCode == "repeated_without_progress");
        Assert.Equal(0, PlannedStepPosition.Derive(result.Plans[0], result.Steps).Cursor);
    }

    [Fact]
    public async Task MalformedRecoveryWithoutProgress_IsBoundedEvenWhenTheReplanWasNotOutputLimited()
    {
        var model = new FakeChatModel(Plan(A), Final(), Malformed(), Malformed(), Final(), Malformed(), Malformed(), Final());
        var (runner, _, tools) = Build(model, options: new AgentRunnerOptions { MaxReplans = 2, MaxLifetimeReplans = 2 });
        var result = await runner.RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, result.Status);
        Assert.Equal(2, result.Accounting!.LifetimeReplans);
        Assert.Equal(4 * 4113, result.Accounting.TokensUsed);
        Assert.Equal(7, model.Requests.Count);
        Assert.Equal(0, tools[A].ExecutionCount);
        Assert.Equal(EarlyFinalRecoveryReason.RepeatedWithoutProgress,
            result.Steps.Last(PrematureFinal.IsStep).EarlyFinalRecoveryDecision!.Reason);
    }

    [Theory]
    [InlineData(ModelFailureKind.Unreachable)]
    [InlineData(ModelFailureKind.Authentication)]
    [InlineData(ModelFailureKind.ContextOverflow)]
    [InlineData(ModelFailureKind.MalformedResponse)]
    public async Task ProviderExceptions_AreNeverAReceivedMalformedPlan_AndCannotRecover(ModelFailureKind kind)
    {
        var model = new ScriptedChatModel(Reply(Plan(A)), Reply(Final()), Reply(Malformed()),
            (_, _) => throw new ModelProtocolException("provider failure") { FailureKind = kind });
        var (runner, _, tools) = Build(model, options: new AgentRunnerOptions { ModelCallMaxAttempts = 1 });
        var result = await runner.RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, result.Status);
        Assert.Equal(kind, result.TerminalReason!.FailureKind);
        Assert.Single(result.Steps[0].EarlyFinalReplanCalls!);
        Assert.False(result.Steps[0].EarlyFinalReplanFailed);
        Assert.DoesNotContain(result.Steps, EarlyFinalRecovery.IsStep);
        Assert.Equal(0, tools[A].ExecutionCount);
    }

    [Fact]
    public async Task TokenBudgetCrossingOnTheMalformedReply_PreventsRecovery()
    {
        var model = new FakeChatModel(Plan(A), Final(), Malformed("length"));
        var (runner, _, tools) = Build(model, options: new AgentRunnerOptions { MaxTotalTokens = 4000 });
        var result = await runner.RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.BudgetExceeded, result.Status);
        Assert.Equal(4113, result.Accounting!.TokensUsed);
        Assert.Equal(0, tools[A].ExecutionCount);
        Assert.DoesNotContain(result.Steps, EarlyFinalRecovery.IsStep);
        Assert.Equal("length", result.Steps[^1].ModelCalls![0].FinishReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBetweenMalformedReplies_PreservesCallsAndBudget_ResumeCannotRepeatCompletedTools(bool returnedReply)
    {
        using var cancel = new CancellationTokenSource();
        var model = new ScriptedChatModel(Reply(Plan(A, B)), Reply(Call(A)), Reply(Final()), Reply(Malformed()),
            async (_, ct) =>
            {
                await cancel.CancelAsync();
                if (!returnedReply)
                {
                    ct.ThrowIfCancellationRequested();
                }
                return Malformed("length");
            });
        var store = new InMemoryTaskStore();
        var id = Guid.NewGuid();
        var (runner, _, tools) = Build(model, store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync("diagnose", Actor, id, cancel.Token));
        var interrupted = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(await store.LoadAsync(id)))!;
        Assert.DoesNotContain(interrupted.Steps, EarlyFinalRecovery.IsStep);
        Assert.Equal(returnedReply ? 8226 : 4113, interrupted.Accounting!.TokensUsed);
        Assert.Equal(1, interrupted.Accounting.LifetimeReplans);
        Assert.Equal(returnedReply ? 2 : 1, interrupted.Steps[1].EarlyFinalReplanCalls!.Count);
        Assert.Equal(1, tools[A].ExecutionCount);

        var fresh = new InMemoryTaskStore();
        fresh.Seed(interrupted);
        var resumedModel = new FakeChatModel(returnedReply ? [Call(B), Final()] : [Malformed("length"), Call(B), Final()]);
        var (resumer, _, resumedTools) = Build(resumedModel, fresh);
        var recovered = await resumer.TryRecoverAsync(id, interrupted.ExecutionAttempt, Resumer, executingHere: false);
        Assert.Equal(TaskRecoveryOutcome.Recovered, recovered.Outcome);
        var result = await resumer.ResumeAsync(recovered.Task!, Resumer);
        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(0, resumedTools[A].ExecutionCount);
        Assert.Equal(1, resumedTools[B].ExecutionCount);
        Assert.Equal(interrupted.Accounting.TokensUsed + (returnedReply ? 0 : 4113), result.Accounting!.TokensUsed);
        Assert.Equal(returnedReply ? 1 : 2, result.Accounting.LifetimeReplans);
        Assert.Equal(interrupted.Steps[1].ModelCalls, result.Steps[1].ModelCalls);
        Assert.Equal(interrupted.Steps[1].EarlyFinalReplanCalls,
            result.Steps[1].EarlyFinalReplanCalls!.Take(interrupted.Steps[1].EarlyFinalReplanCalls!.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeAfterRecoveryOrPendingTool_RetainsCursorAndNeverRepeatsTheTool(bool afterTool)
    {
        var responses = new List<ModelResponse> { Plan(A, B), Call(A), Final(), Malformed("length") };
        if (afterTool)
        {
            responses.Add(Call(B));
        }
        var store = new InMemoryTaskStore();
        var (live, _, _) = Build(new FakeChatModel([.. responses]), store);
        var interrupted = await live.RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, interrupted.Status);
        var reloaded = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(await store.LoadAsync(interrupted.Id)))!;
        var position = PlannedStepPosition.Derive(reloaded.Plans[0], reloaded.Steps);
        Assert.Equal(afterTool ? 2 : 1, position.Cursor);
        Assert.False(position.ReplanRequired);

        var fresh = new InMemoryTaskStore();
        fresh.Seed(reloaded);
        var model = new FakeChatModel(afterTool ? [Final()] : [Call(B), Final()]);
        var (resumer, _, tools) = Build(model, fresh);
        var result = await resumer.ResumeAsync(reloaded, Resumer);
        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(0, tools[A].ExecutionCount);
        Assert.Equal(afterTool ? 0 : 1, tools[B].ExecutionCount);
        Assert.Equal(reloaded.Steps, result.Steps.Take(reloaded.Steps.Count));
        Assert.Equal(4113, result.Accounting!.TokensUsed);
    }

    [Fact]
    public void RecoveryProof_RejectsUncertainConditionalUnavailableAndMutatingSteps()
    {
        var plan = new AgentPlan(0, "r", [new PlannedStep(0, "a", A)]) { SemanticContractVersion = 1 };
        var manifest = new ResultTool(A, ToolCallResult.Success("ok")).Manifest;
        var early = new PlanStep(0, PrematureFinal.StepDescription, null, null, "x", 0)
        {
            EarlyFinalReplanFailed = true,
            EarlyFinalReplanCalls = [new ModelCallRecord("fake", "fake", null, DateTimeOffset.UnixEpoch, 1,
                ModelCallOutcome.Failure, null, "length", "malformed", null, null, false)
                { FailureKind = ModelFailureKind.MalformedResponse }],
        };
        Assert.NotNull(EarlyFinalRecovery.SafePosition(plan, [early], 0, _ => manifest, [manifest]));
        Assert.Null(EarlyFinalRecovery.SafePosition(plan, [early], 0, _ => null, [manifest]));
        Assert.Null(EarlyFinalRecovery.SafePosition(plan, [early], 0, _ => manifest, []));
        Assert.Null(EarlyFinalRecovery.SafePosition(plan, [early], 0, _ => manifest with { Risk = RiskLevel.High }, [manifest]));
        Assert.Null(EarlyFinalRecovery.SafePosition(plan with { SemanticContractVersion = 0 }, [early], 0, _ => manifest, [manifest]));
        var uncertain = new PlanStep(0, "unknown outcome", new ModelToolCall("call", B, ToolArguments.Empty), null, "unknown");
        Assert.Null(EarlyFinalRecovery.SafePosition(plan, [uncertain, early with { Index = 1 }], 1, _ => manifest, [manifest]));
        var activated = plan with { Steps = [plan.Steps[0] with { Activation = new EvidenceFactExists(0, "x", "y", null) }] };
        Assert.Null(EarlyFinalRecovery.SafePosition(activated, [early], 0, _ => manifest, [manifest]));
        var recovery = new PlanStep(1, EarlyFinalRecovery.StepDescription, null, null, "x", 0) { EarlyFinalRecoveryCursor = 0 };
        Assert.True((PlannedStepPosition.Start with { ReplanRequired = true }).After(plan, recovery).ReplanRequired);
        Assert.True(PlannedStepPosition.Start.After(plan, early).After(plan, recovery with { EarlyFinalRecoveryCursor = 1 }).ReplanRequired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveredStep_StillRequiresPolicyAndEntitlement(bool governed)
    {
        var model = new FakeChatModel(Plan(A), Final(), Malformed("length"), Call(A));
        var (runner, audit, tools) = Build(model, options: new AgentRunnerOptions { MaxConsecutivePolicyDenials = 1 },
            policy: new StubPolicyEngine(governed ? PolicyMode.Automatic : PolicyMode.Forbidden), governed: governed);
        var result = await runner.RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.PolicyBlocked, result.Status);
        Assert.Equal(0, tools[A].ExecutionCount);
        Assert.Single(result.Steps, EarlyFinalRecovery.IsStep);
        Assert.Contains(audit.Events.OfType<ToolCallAuditEvent>(), e => e.Authorization ==
            (governed ? AuthorizationKind.EntitlementDenied : AuthorizationKind.PolicyDenied));
    }

    [Fact]
    public async Task RecoveredStep_DoesNotAutoApprove()
    {
        var model = new FakeChatModel(Plan(A), Final(), Malformed("length"), Call(A), Malformed(), Malformed());
        var (runner, audit, tools) = Build(model, policy: new StubPolicyEngine(PolicyMode.Approval), approval: new StubApprovalProvider(false));
        var result = await runner.RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, result.Status);
        Assert.Equal(0, tools[A].ExecutionCount);
        Assert.Single(result.Steps, EarlyFinalRecovery.IsStep);
        Assert.Contains(audit.Events.OfType<ApprovalAuditEvent>(), e => !e.Approved);
        Assert.Contains(audit.Events.OfType<ToolCallAuditEvent>(), e => e.Authorization == AuthorizationKind.UserRejected);
        // The second malformed replan is a denial trigger and cannot inherit the early-final fallback.
        Assert.Equal(2, result.Steps[^1].ModelCalls!.Count);
    }

    [Fact]
    public async Task Recovery_DoesNotExpandStepAuthority()
    {
        var model = new FakeChatModel(Plan(A, B), Final(), Malformed("length"), Call(B), Malformed(), Malformed());
        var (runner, audit, tools) = Build(model);
        var result = await runner.RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, result.Status);
        Assert.All(tools.Values, tool => Assert.Equal(0, tool.ExecutionCount));
        Assert.Equal([A], Operational(model.Requests[3]));
        Assert.Single(result.Steps, EarlyFinalRecovery.IsStep);
        Assert.Contains(audit.Events.OfType<ToolCallAuditEvent>(), e => e.Authorization == AuthorizationKind.UnknownTool);
    }

    [Fact]
    public async Task AttemptDurationExpiredDuringReplan_PreventsRecovery()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero));
        var model = new ScriptedChatModel(Reply(Plan(A)), Reply(Final()), (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(Malformed("length"));
        });
        var (runner, _, tools) = Build(model, options: new AgentRunnerOptions { MaxAttemptDuration = TimeSpan.FromSeconds(1) }, clock: clock);
        var result = await runner.RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.BudgetExceeded, result.Status);
        Assert.Equal(TaskTerminalKind.AttemptDurationBudget, result.TerminalReason!.Kind);
        Assert.Equal(0, tools[A].ExecutionCount);
        Assert.DoesNotContain(result.Steps, EarlyFinalRecovery.IsStep);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableOrMutatingPendingTool_FailsClosed(bool mutating)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        var package = new PackageId("pending.package");
        if (mutating)
        {
            registry.Register(package, new ResultHighRiskTool(A, ToolCallResult.Success("ok")));
        }
        else
        {
            registry.Register(package, new ResultTool(A, ToolCallResult.Success("ok")));
        }
        var model = new ScriptedChatModel(Reply(Plan(A)), Reply(Final()), (_, _) =>
        {
            if (!mutating)
            {
                registry.Unregister(package);
            }
            return Task.FromResult(Malformed("length"));
        });
        var audit = new RecordingAuditSink();
        var runner = new AgentRunner(model, registry, new StubPolicyEngine(PolicyMode.Automatic), new NeverCalledApprovalProvider(),
            audit, new InMemoryTaskStore(), TimeProvider.System, NullLogger<AgentRunner>.Instance, new AgentRunnerOptions());
        var result = await runner.RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Failed, result.Status);
        Assert.Equal(ModelFailureKind.MalformedResponse, result.TerminalReason!.FailureKind);
        Assert.DoesNotContain(result.Steps, EarlyFinalRecovery.IsStep);
        Assert.Empty(audit.Events.OfType<ToolCallAuditEvent>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_RevalidatesToolSafetyOfAPersistedRecovery(bool mutating)
    {
        var store = new InMemoryTaskStore();
        var (live, _, _) = Build(new FakeChatModel(Plan(A), Final(), Malformed("length")), store);
        var interrupted = await live.RunAsync("diagnose", Actor);
        Assert.Single(interrupted.Steps, EarlyFinalRecovery.IsStep);
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        if (mutating)
        {
            registry.Register(new PackageId("changed.package"), new ResultHighRiskTool(A, ToolCallResult.Success("ok")));
        }
        var model = new FakeChatModel();
        var runner = new AgentRunner(model, registry, new StubPolicyEngine(PolicyMode.Automatic), new NeverCalledApprovalProvider(),
            new RecordingAuditSink(), store, TimeProvider.System, NullLogger<AgentRunner>.Instance, new AgentRunnerOptions());
        var result = await runner.ResumeAsync(interrupted, Resumer);
        Assert.Equal(AgentTaskStatus.Failed, result.Status);
        Assert.Empty(model.Requests);
        Assert.Equal(interrupted.Steps, result.Steps.Take(interrupted.Steps.Count));
    }

    [Fact]
    public async Task Resume_WithAPersistedRecovery_CannotBypassAnUnknownMutationOutcome()
    {
        var store = new InMemoryTaskStore();
        var (live, _, _) = Build(new FakeChatModel(Plan(A), Final(), Malformed("length")), store);
        var interrupted = await live.RunAsync("diagnose", Actor);
        store.SeedEntry(new TaskMutationJournalEntry
        {
            Intent = new TaskMutationIntent
            {
                Key = new TaskMutationKey(interrupted.Id, interrupted.ExecutionAttempt, 99),
                ToolName = "previous.mutation", ArgumentsHash = "unknown", Risk = RiskLevel.High,
                IntentAtUtc = DateTimeOffset.UnixEpoch,
            },
            Sequence = 1, State = TaskMutationState.Pending,
        });
        var model = new FakeChatModel();
        var (resumer, _, tools) = Build(model, store);
        var admission = await resumer.TryAcquireResumeAsync(interrupted.Id, Resumer);
        Assert.Equal(TaskResumeOutcome.Refused, admission.Outcome);
        Assert.Equal(TaskResumeRefusal.MutationOutcomeUnknown, admission.Refusal!.Code);
        Assert.Empty(model.Requests);
        Assert.All(tools.Values, tool => Assert.Equal(0, tool.ExecutionCount));
        Assert.Equal(TaskMutationState.Pending, Assert.Single(store.JournalOf(interrupted.Id)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeAtTheMalformedCheckpoint_PreservesPaidCallsAndResumesThePendingCursor(bool complete)
    {
        var store = new InMemoryTaskStore();
        var (live, _, _) = Build(new FakeChatModel(Plan(A, B), Call(A), Final(), Malformed(), Malformed("length"), Call(B), Final()), store);
        await live.RunAsync("diagnose", Actor);
        var checkpoint = store.Saves.First(task => task.Steps.LastOrDefault(PrematureFinal.IsStep) is { } step
            && step.EarlyFinalReplanCalls?.Count == (complete ? 2 : 1) && step.EarlyFinalReplanFailed == complete
            && !task.Steps.Any(EarlyFinalRecovery.IsStep));
        var reloaded = JsonSerializer.Deserialize<TaskState>(JsonSerializer.Serialize(checkpoint))!;
        var fresh = new InMemoryTaskStore();
        fresh.Seed(reloaded);
        var model = new FakeChatModel(complete ? [Call(B), Final()] : [Malformed("length"), Call(B), Final()]);
        var (resumer, _, tools) = Build(model, fresh, complete
            ? new AgentRunnerOptions { MaxReplans = 1, MaxLifetimeReplans = 1 }
            : new AgentRunnerOptions());
        var recovery = await resumer.TryRecoverAsync(reloaded.Id, reloaded.ExecutionAttempt, Resumer, false);
        var result = await resumer.ResumeAsync(recovery.Task!, Resumer);
        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(0, tools[A].ExecutionCount);
        Assert.Equal(1, tools[B].ExecutionCount);
        Assert.Equal(complete ? 2 : 3, model.Requests.Count);
        Assert.Equal(complete ? 1 : 2, result.Accounting!.LifetimeReplans);
        Assert.Equal(8226, result.Accounting.TokensUsed);
        Assert.Equal(result.Accounting.TokensUsed, TaskResumePolicy.DeriveAccounting(result).TokensUsed);
    }

    [Fact]
    public async Task AValidReplanAtTheRetry_KeepsCheckpointCallsDisjointFromTheAcceptedPlan()
    {
        var model = new FakeChatModel(Plan(A, B), Call(A), Final(), Malformed(), Plan(B), Call(B), Final());
        var (runner, audit, tools) = Build(model);
        var result = await runner.RunAsync("diagnose", Actor);
        Assert.Equal(AgentTaskStatus.Completed, result.Status);
        Assert.Equal(2, result.Plans.Count);
        Assert.Equal(1, tools[B].ExecutionCount);
        Assert.Single(result.Steps[1].EarlyFinalReplanCalls!);
        Assert.False(result.Steps[1].EarlyFinalReplanFailed);
        Assert.Equal(ModelCallOutcome.Success, Assert.Single(result.Plans[1].ModelCalls!).Outcome);
        Assert.Equal(4113, result.Accounting!.TokensUsed);
        Assert.Equal(4113, TaskResumePolicy.DeriveAccounting(result).TokensUsed);
        Assert.Equal(7, audit.Events.OfType<ModelCallAuditEvent>().Count());
        Assert.DoesNotContain(result.Steps, EarlyFinalRecovery.IsStep);
    }
}
