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

    private static (AgentRunner Runner, RecordingAuditSink Audit, Dictionary<string, ResultTool> Tools) Build(
        IChatModel model, ITaskStore? store = null, AgentRunnerOptions? options = null)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        var tools = new[] { A, B, C, D }.ToDictionary(name => name, name => new ResultTool(name, ToolCallResult.Success($"{name} ok")));
        foreach (var tool in tools.Values)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        var audit = new RecordingAuditSink();
        var runner = new AgentRunner(model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), audit,
            store ?? new InMemoryTaskStore(), TimeProvider.System, NullLogger<AgentRunner>.Instance,
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
    public async Task AMalformedEarlyFinalReplan_IsTransactional_TheAttemptFailsAndThePlanIsUnchanged()
    {
        var model = new FakeChatModel(
            Plan(A, B), Call(A), Final(PrematureText),
            new ModelResponse("not a plan", [], false, null), new ModelResponse("still not a plan", [], false, null));
        var (runner, _, tools) = Build(model);

        var result = await runner.RunAsync("diagnose", Actor);

        Assert.Equal(AgentTaskStatus.Failed, result.Status);
        Assert.Equal((TaskTerminalKind.ModelFailure, ModelFailureKind.MalformedResponse),
            (result.TerminalReason!.Kind, result.TerminalReason.FailureKind));
        Assert.Single(result.Plans);
        Assert.Equal(0, tools[B].ExecutionCount);
        Assert.True(PrematureFinal.IsStep(result.Steps[1]));
        Assert.Single(result.Steps[1].ModelCalls!);
        Assert.Equal(2, result.Steps[^1].ModelCalls!.Count); // both malformed replan attempts kept on the failure step
        Assert.DoesNotContain(result.Steps, FinalResponse.IsFinalStep);
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
}
