// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.AgentFramework.Troubleshooting;
using bOps.Abstractions;
using bOps.Runtime;
using static bOps.AgentFramework.Tests.MafWorkflowWorld;
using static bOps.AgentFramework.Tests.RoleScriptedModel;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// C01-R23: the run stops at its limits (rounds, tool calls, model calls, tokens, time), survives and reports a failing specialist,
/// fails explicitly when the triage or review agent fails, ends cleanly on cancellation, and leaves a complete audit and accounting
/// trail with no duplicates. A run is terminal: nothing about it is written to the task store, and a retry is a new run.
/// </summary>
public sealed class MafWorkflowLimitsAndFailureTests
{
    private static void ScriptOneSpecialistRun(MafWorkflowWorld world, params Func<ModelRequest, object>[] systemSteps)
    {
        world.Model.Script("triage", Text(TriagePlan(("system", "Check the host."))));
        world.Model.Script("system", systemSteps);
    }

    [Fact]
    public async Task TheReviewersFollowUps_StopAtTheRoundLimit_AndTheReportSaysTheEvidenceWasStillInsufficient()
    {
        var world = Create(limits: new MafWorkflowLimits { MaxInvestigationRounds = 1 });
        ScriptOneSpecialistRun(world, Call("c1", "fake.sys.events"), Text(request => SpecialistReply("one", EvidenceIds(request))));
        world.Model.Script("reviewer", Text(_ => ReviewReply(false, "Not enough.", [], [("system", "Look again.")], ["a dump"])));

        var report = await world.Workflow.RunAsync("The host restarts", Actor);

        Assert.Equal(MafWorkflowOutcome.LimitReached, report.Outcome);
        Assert.Equal("max_investigation_rounds", report.LimitCode);
        Assert.Equal(1, report.Rounds);
        Assert.Equal(2, world.Model.CallsFor("system"));
        Assert.Equal(1, world.Model.CallsFor("reviewer"));
        Assert.Contains(report.Limitations, text => text.Contains("round limit", StringComparison.Ordinal));
        Assert.Contains(world.Audit.Events.OfType<WorkflowRunAuditEvent>(), evt => evt.Stage == WorkflowRunStage.LimitReached && evt.Code == "max_investigation_rounds");
    }

    [Fact]
    public async Task TheToolCallBudget_CutsTheInvestigationShort_TheRefusedCallNeverRuns_AndTheRunIsNotReportedAsComplete()
    {
        var world = Create(limits: new MafWorkflowLimits { MaxToolCalls = 1 });
        ScriptOneSpecialistRun(
            world, Call("c1", "fake.sys.events"), Call("c2", "fake.sys.services"), Text(request => SpecialistReply("partial", EvidenceIds(request))));
        world.Model.Script("reviewer", Text(request => ReviewReply(true, "Partial.", AllEvidenceIds(request))));

        var report = await world.Workflow.RunAsync("The host restarts", Actor);

        Assert.Equal(MafWorkflowOutcome.LimitReached, report.Outcome);
        Assert.Equal("tool_call_budget", report.LimitCode);
        Assert.Equal([WorkflowToolStatus.Executed, WorkflowToolStatus.BudgetExhausted], report.Evidence.Select(entry => entry.Status));
        Assert.Equal(1, world.Executions("fake.sys.events"));
        Assert.Equal(0, world.Executions("fake.sys.services"));
        Assert.Equal(2, report.ToolCalls);
    }

    [Fact]
    public async Task TheModelCallBudget_StopsTheNextCallBeforeItIsMade()
    {
        var world = Create(limits: new MafWorkflowLimits { MaxModelCalls = 2 });
        ScriptOneSpecialistRun(world, Call("c1", "fake.sys.events"), Text(request => SpecialistReply("never asked", EvidenceIds(request))));

        var report = await world.Workflow.RunAsync("The host restarts", Actor);

        Assert.Equal(MafWorkflowOutcome.LimitReached, report.Outcome);
        Assert.Equal("model_call_budget", report.LimitCode);
        Assert.Equal(2, world.Model.TotalCalls);
        Assert.Equal(0, world.Model.CallsFor("reviewer"));
        Assert.Equal(1, world.Executions("fake.sys.events"));
    }

    [Fact]
    public async Task TheSharedTokenBudget_IsAccountedAcrossAgents_AndStopsTheRunWhenExceeded()
    {
        // Every scripted call reports 120 tokens: the triage agent and the first specialist call pass 200 between them.
        var world = Create(limits: new MafWorkflowLimits { MaxTokens = 200 });
        ScriptOneSpecialistRun(world, Call("c1", "fake.sys.events"), Text(request => SpecialistReply("never asked", EvidenceIds(request))));

        var report = await world.Workflow.RunAsync("The host restarts", Actor);

        Assert.Equal(MafWorkflowOutcome.LimitReached, report.Outcome);
        Assert.Equal("token_budget", report.LimitCode);
        Assert.Equal(240, report.Tokens);
        Assert.Equal(2, world.Model.TotalCalls);
        Assert.Contains(report.Limitations, text => text.Contains("did not complete round 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheTimeLimit_CancelsAnInFlightTool_AndTheRunEndsLimitReached_WithTheEvidenceSoFar()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var world = Create(
            limits: new MafWorkflowLimits { MaxDuration = TimeSpan.FromMilliseconds(600) },
            extraTools: [new ProbeTool("fake.sys.hang", "never", onExecute: async ct => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); })]);
        ScriptOneSpecialistRun(world, Call("c1", "fake.sys.hang"));

        var report = await world.Workflow.RunAsync("The host hangs", Actor);

        Assert.True(started.Task.IsCompleted);
        Assert.Equal(MafWorkflowOutcome.LimitReached, report.Outcome);
        Assert.Equal("time_limit", report.LimitCode);
        Assert.Contains(world.Audit.Events.OfType<WorkflowRunAuditEvent>(), evt => evt.Stage == WorkflowRunStage.LimitReached && evt.Code == "time_limit");
    }

    [Fact]
    public async Task CancellationWhileAToolRuns_PropagatesToTheCaller_AndIsAuditedAsCancelled()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var world = Create(extraTools: [new ProbeTool("fake.sys.hang", "never", onExecute: async ct => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); })]);
        ScriptOneSpecialistRun(world, Call("c1", "fake.sys.hang"));
        using var cts = new CancellationTokenSource();

        var run = world.Workflow.RunAsync("The host hangs", Actor, ct: cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Contains(world.Audit.Events.OfType<WorkflowRunAuditEvent>(), evt => evt.Stage == WorkflowRunStage.Cancelled);
        Assert.DoesNotContain(world.Audit.Events.OfType<WorkflowRunAuditEvent>(), evt => evt.Stage == WorkflowRunStage.Completed);
        Assert.Equal(0, world.Model.CallsFor("reviewer"));
    }

    [Fact]
    public async Task AToolThatThrows_BecomesAFailedEvidenceEntry_TheAgentSeesTheError_AndTheRunGoesOn()
    {
        var world = Create(extraTools: [new ProbeTool("fake.sys.broken", "never", onExecute: _ => throw new InvalidOperationException("collector crashed"))]);
        ScriptOneSpecialistRun(world, Call("c1", "fake.sys.broken"), Text(request => SpecialistReply("The collector failed.", EvidenceIds(request))));
        world.Model.Script("reviewer", Text(request => ReviewReply(true, "Nothing could be read.", [])));

        var report = await world.Workflow.RunAsync("The host restarts", Actor);

        Assert.Equal(WorkflowToolStatus.Failed, Assert.Single(report.Evidence).Status);
        Assert.Equal(MafWorkflowOutcome.Completed, report.Outcome);
        Assert.Contains(report.Limitations, text => text.Contains("did not produce evidence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASpecialistWhoseModelCallFails_IsReportedAsFailed_TheOthersAndTheReviewContinue_AndTheModelFailureIsAudited()
    {
        var world = Create(postgres: true);
        world.Model.Script("triage", Text(TriagePlan(("system", "Check the host."), ("postgresql", "Check the database."))));
        world.Model.Script("system", Fail(ModelFailureKind.Authentication));
        world.Model.Script("postgresql", Call("p1", "fake.pg.health"), Text(request => SpecialistReply("Up.", EvidenceIds(request))));
        world.Model.Script("reviewer", Text(request => ReviewReply(true, "Only the database was examined.", AllEvidenceIds(request))));

        var report = await world.Workflow.RunAsync("PostgreSQL is slow", Actor);

        Assert.Equal(MafWorkflowOutcome.Completed, report.Outcome);
        Assert.Contains(report.Limitations, text => text.Contains("Specialist 'system' did not complete round 1 (Failed)", StringComparison.Ordinal));
        Assert.Contains(
            world.Audit.Events.OfType<ModelCallAuditEvent>(),
            evt => evt.Outcome == ModelCallOutcome.Failure && evt.FailureKind == ModelFailureKind.Authentication && evt.Workflow!.AgentName == "specialist-system");
        Assert.Equal(1, world.Executions("fake.pg.health"));
    }

    [Fact]
    public async Task AReviewerWhoseModelCallFails_FailsTheRunExplicitly_WithNoResume_AndNoFurtherExecution()
    {
        var world = Create();
        ScriptOneSpecialistRun(world, Call("c1", "fake.sys.events"), Text(request => SpecialistReply("one", EvidenceIds(request))));
        world.Model.Script("reviewer", Fail(ModelFailureKind.InvalidRequest));

        var failure = await Assert.ThrowsAsync<MafWorkflowFailedException>(() => world.Workflow.RunAsync("The host restarts", Actor));

        Assert.Contains("No resume exists", failure.Message, StringComparison.Ordinal);
        Assert.Contains(world.Audit.Events.OfType<WorkflowRunAuditEvent>(), evt => evt.Stage == WorkflowRunStage.Failed && evt.TaskId == failure.RunId);
        Assert.Equal(1, world.Executions("fake.sys.events"));
        Assert.Null(await world.Store.LoadAsync(failure.RunId));
    }

    [Fact]
    public async Task ATriageAgentWhoseModelCallFails_FailsTheRun_BeforeAnyToolOrSpecialistRuns()
    {
        var world = Create();
        world.Model.Script("triage", Fail(ModelFailureKind.Authentication));

        await Assert.ThrowsAsync<MafWorkflowFailedException>(() => world.Workflow.RunAsync("The host restarts", Actor));

        Assert.Equal(1, world.Model.TotalCalls);
        Assert.Equal(0, world.Executions("fake.sys.events"));
    }

    [Fact]
    public async Task AuditAndAccounting_AreCompleteAndNotDuplicated_ForTheWholeRun()
    {
        var world = Create();
        world.Model.Script("triage", Text(TriagePlan(("system", "Find why the host slows down."))));
        world.Model.Script(
            "system",
            Call("c1", "fake.sys.events"),
            Call("c2", "fake.sys.services"),
            Text(request => SpecialistReply("two reads", EvidenceIds(request))),
            Call("c3", "fake.sys.dumps"),
            Text(request => SpecialistReply("a third read", EvidenceIds(request))));
        world.Model.Script(
            "reviewer",
            Text(_ => ReviewReply(false, "More needed.", [], [("system", "Dumps.")], ["dumps"])),
            Text(request => ReviewReply(true, "Done.", AllEvidenceIds(request).Take(1).ToArray())));

        var report = await world.Workflow.RunAsync("The host slows down", Actor);

        var all = world.Audit.Events.Where(evt => evt.TaskId == report.RunId).ToArray();
        Assert.Equal(all.Length, world.Audit.Events.Count);

        // Every model call of the run has exactly one audit event, for the agent that made it, with the actor.
        var modelEvents = all.OfType<ModelCallAuditEvent>().ToArray();
        Assert.Equal(world.Model.TotalCalls, modelEvents.Length);
        Assert.Equal(report.ModelCalls, modelEvents.Length);
        Assert.All(modelEvents, evt =>
        {
            Assert.Equal(ModelCallOutcome.Success, evt.Outcome);
            Assert.Equal(Actor, evt.Actor);
            Assert.NotNull(evt.Workflow);
            Assert.Null(evt.Workflow!.InvocationId);
        });
        Assert.Equal(["review", "specialist-system", "triage"], modelEvents.Select(evt => evt.Workflow!.AgentName).Distinct().Order());
        Assert.Equal(report.ModelCalls, modelEvents.Select(evt => evt.StepIndex).Distinct().Count());

        // Tokens are the sum of what the provider reported, once per call.
        Assert.Equal(120L * world.Model.TotalCalls, report.Tokens);

        // Every tool call has exactly one event with its own invocation id; refusals included.
        var toolEvents = all.OfType<ToolCallAuditEvent>().ToArray();
        Assert.Equal(["c1", "c2", "c3"], toolEvents.Select(evt => evt.Workflow!.InvocationId).Order());
        Assert.Equal(report.ToolCalls, toolEvents.Length);
        Assert.All(toolEvents, evt => Assert.Equal("specialist-system", evt.Workflow!.AgentName));

        // The lifecycle reads in the order the run went, once each.
        Assert.Equal(
            [
                WorkflowRunStage.Started, WorkflowRunStage.Triaged, WorkflowRunStage.RoundStarted, WorkflowRunStage.Reviewed,
                WorkflowRunStage.RoundStarted, WorkflowRunStage.Reviewed, WorkflowRunStage.Completed,
            ],
            all.OfType<WorkflowRunAuditEvent>().Select(evt => evt.Stage));
        var started = all.OfType<WorkflowRunAuditEvent>().First();
        Assert.Equal(["review", "specialist-system", "triage"], started.Agents!.Order());
        Assert.NotEmpty(started.ToolAuthorityHashes!["specialist-system"]);
        Assert.Equal(report.ToolCalls, all.OfType<WorkflowRunAuditEvent>().Last().ToolCalls);
    }

    [Fact]
    public async Task TheRunIsTerminal_NothingIsWrittenToTheTaskStore_AndARetryIsANewRunWithItsOwnIdentityAndBudget()
    {
        var world = Create(postgres: true, limits: new MafWorkflowLimits { MaxToolCalls = 2 });
        world.Model.Script("triage", Text(TriagePlan(("system", "Check the host."))), Text(TriagePlan(("postgresql", "Check the database."))));
        world.Model.Script("system", Call("c1", "fake.sys.events"), Call("c2", "fake.sys.services"), Text(request => SpecialistReply("two", EvidenceIds(request))));
        world.Model.Script("postgresql", Call("p1", "fake.pg.health"), Text(request => SpecialistReply("up", EvidenceIds(request))));
        world.Model.Script("reviewer", Text(request => ReviewReply(true, "one", AllEvidenceIds(request))), Text(request => ReviewReply(true, "two", AllEvidenceIds(request))));

        var first = await world.Workflow.RunAsync("The host is slow", Actor);
        var second = await world.Workflow.RunAsync("The database is slow", Actor);

        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Null(await world.Store.LoadAsync(first.RunId));
        Assert.Null(await world.Store.LoadAsync(second.RunId));
        Assert.Equal(["system"], first.SelectedSpecialists);
        Assert.Equal(["postgresql"], second.SelectedSpecialists);
        Assert.Equal(1, second.ToolCalls);
        Assert.All(second.Evidence, entry => Assert.StartsWith($"wf1:{second.RunId}:", entry.EvidenceId, StringComparison.Ordinal));
        Assert.DoesNotContain(world.Audit.Events.Where(evt => evt.TaskId == second.RunId).OfType<ToolCallAuditEvent>(), evt => evt.Tool.StartsWith("fake.sys.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UncitedClaims_AreNeverReportedAsObservations_TheyAreMovedToTheUnsupportedList()
    {
        var world = Create();
        ScriptOneSpecialistRun(world, Call("c1", "fake.sys.events"), Text(request => SpecialistReply("one", EvidenceIds(request))));
        world.Model.Script("reviewer", Text(_ => ReviewReply(true, "The power supply failed.", ["wf1:00000000-0000-0000-0000-000000000000:9"])));

        var report = await world.Workflow.RunAsync("The host restarts", Actor);

        Assert.Empty(report.CitedObservations);
        Assert.Contains(report.Unsupported, text => text.Contains("citing no evidence from a tool call that ran", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedCall_IsNotEvidence_AClaimCitingOnlyIt_IsUnsupported()
    {
        var world = Create(limits: new MafWorkflowLimits { MaxToolCalls = 1 });
        ScriptOneSpecialistRun(
            world, Call("c1", "fake.sys.events"), Call("c2", "fake.sys.services"), Text(request => SpecialistReply("partial", [RefusedEvidenceId(request)])));
        // The specialist and the reviewer both cite only the call the budget refused.
        world.Model.Script("reviewer", Text(request => ReviewReply(true, "Partial.", [RefusedEvidenceId(request)])));

        var report = await world.Workflow.RunAsync("The host restarts", Actor);

        var refused = Assert.Single(report.Evidence, entry => entry.Status == WorkflowToolStatus.BudgetExhausted);
        Assert.DoesNotContain(refused.EvidenceId, report.CitedObservations.SelectMany(claim => claim.EvidenceIds));
        Assert.Empty(report.CitedObservations);
        Assert.Contains(report.Unsupported, text => text.Contains("citing no evidence from a tool call that ran", StringComparison.Ordinal));
    }

    /// <summary>The id of the second call of the run (sequence 1), the one a one-call budget refuses.</summary>
    private static string RefusedEvidenceId(ModelRequest request) =>
        System.Text.RegularExpressions.Regex.Match(string.Join('\n', request.History.Select(turn => turn.Content)), @"wf1:[0-9a-f\-]+:1\b").Value;

    [Fact]
    public async Task ACitedObservation_KeepsOnlyTheCitationsOfCallsThatRan()
    {
        var world = Create(limits: new MafWorkflowLimits { MaxToolCalls = 1 });
        ScriptOneSpecialistRun(
            world, Call("c1", "fake.sys.events"), Call("c2", "fake.sys.services"), Text(request => SpecialistReply("partial", EvidenceIds(request))));
        world.Model.Script("reviewer", Text(request => ReviewReply(true, "Partial.", AllEvidenceIds(request))));

        var report = await world.Workflow.RunAsync("The host restarts", Actor);

        var ran = Assert.Single(report.Evidence, entry => entry.Status == WorkflowToolStatus.Executed);
        var observation = Assert.Single(report.CitedObservations);
        Assert.Equal([ran.EvidenceId], observation.EvidenceIds);
    }

    [Fact]
    public void TheLimits_HaveConservativeDefaults_AndAHardMaximumThatConfigurationCannotExceed()
    {
        var defaults = new MafWorkflowLimits().Validate();
        Assert.Equal(2, defaults.MaxInvestigationRounds);
        Assert.True(defaults.MaxToolCalls <= 24);
        Assert.True(defaults.MaxInvestigationRounds <= MafWorkflowLimits.HardMaxInvestigationRounds);

        Assert.Throws<ArgumentOutOfRangeException>(() => new MafWorkflowLimits { MaxInvestigationRounds = MafWorkflowLimits.HardMaxInvestigationRounds + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MafWorkflowLimits { MaxInvestigationRounds = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MafWorkflowLimits { MaxToolCalls = MafWorkflowLimits.HardMaxToolCalls + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MafWorkflowLimits { MaxModelCalls = MafWorkflowLimits.HardMaxModelCalls + 1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MafWorkflowLimits { MaxTokens = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MafWorkflowLimits { MaxDuration = MafWorkflowLimits.HardMaxDuration + TimeSpan.FromSeconds(1) }.Validate());
    }
}
