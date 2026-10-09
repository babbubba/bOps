// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>PRE-4: the execution-plan projection is a pure function of persisted plans and steps, reusing the PRE-2 fold.</summary>
public sealed class ExecutionPlanProjectorTests
{
    private const string Secret = "SECRET-MARKER";
    private const string FactType = "artifact.path";
    private const string FactKey = "primary";

    private static PlannedStep Planned(int index, string tool = "system.events", EvidenceFactExists? activation = null) =>
        new(index, $"objective {index}", tool)
        {
            ExpectedArguments = ToolArguments.FromJson(new JsonObject { ["arg"] = Secret }),
            Activation = activation,
        };

    private static AgentPlan Plan(int revision, params PlannedStep[] steps) =>
        new(revision, $"rationale {Secret}", steps) { SemanticContractVersion = 1 };

    private static PlanStep Exec(
        int revision, int index, PlannedStepExecutionClassification classification, ToolCallResult? result = null,
        int stepIndex = 0) =>
        new(stepIndex, "step", new ModelToolCall("c", "system.events", ToolArguments.FromJson(new JsonObject { ["arg"] = Secret })),
            result ?? ToolCallResult.Success(Secret), Secret, revision)
        {
            PlannedStepIndex = index,
            ExecutionClassification = classification,
        };

    private static PlanStep Matched(int revision, int index, int stepIndex = 0, params EvidenceFact[] facts) =>
        Exec(revision, index, PlannedStepExecutionClassification.Matched,
            ToolCallResult.Success(Secret) with { Facts = facts }, stepIndex);

    private static TaskState Task(AgentTaskStatus status, IReadOnlyList<AgentPlan> plans, params PlanStep[] steps) =>
        new(Guid.NewGuid(), NodeId.Local, "goal", status, steps, plans, DateTimeOffset.UnixEpoch);

    // A Running task in these tests is one this host executes, unless a test says otherwise.
    private static ProjectedPlan? Project(TaskState task, bool executing = true) => ExecutionPlanProjector.Project(task, executing);

    private static IReadOnlyList<ProjectedPlanStep> Steps(ProjectedPlan? plan, int revision) =>
        plan!.Revisions.Single(r => r.Revision == revision).Steps;

    [Fact]
    public void NoPlan_ProjectsNothing() =>
        Assert.Null(Project(Task(AgentTaskStatus.Running, [])));

    [Fact]
    public void EmptyPlan_ProjectsNoSteps()
    {
        var projected = Project(Task(AgentTaskStatus.Completed, [Plan(0)]));
        Assert.Equal(0, projected!.ActiveRevision);
        Assert.Empty(Steps(projected, 0));
    }

    [Fact]
    public void CompletedThenRunningThenPending_WithExactlyOneCurrentStep()
    {
        var task = Task(AgentTaskStatus.Running, [Plan(0, Planned(0), Planned(1), Planned(2))], Matched(0, 0));

        var steps = Steps(Project(task), 0);

        Assert.Equal([ProjectedStepStatus.Completed, ProjectedStepStatus.Running, ProjectedStepStatus.Pending], steps.Select(s => s.Status));
        Assert.Equal([1], steps.Where(s => s.Current).Select(s => s.Index));
        Assert.Equal("objective 1", steps[1].Objective);
        Assert.Equal("system.events", steps[1].ExpectedTool);
    }

    [Fact]
    public void FailingConsumedStep_IsFailed_AndTheNextOneIsCurrent()
    {
        var task = Task(AgentTaskStatus.Running, [Plan(0, Planned(0), Planned(1))],
            Exec(0, 0, PlannedStepExecutionClassification.Matched, ToolCallResult.Failure(Secret)));

        var steps = Steps(Project(task), 0);

        Assert.Equal([ProjectedStepStatus.Failed, ProjectedStepStatus.Running], steps.Select(s => s.Status));
    }

    [Theory]
    [InlineData(PlannedStepExecutionClassification.ArgumentValidationFailure, ProjectedCorrectionKind.ArgumentValidation)]
    [InlineData(PlannedStepExecutionClassification.SemanticMismatch, ProjectedCorrectionKind.Semantic)]
    public void SpentCorrection_KeepsTheSameStepCurrent_AsCorrecting(
        PlannedStepExecutionClassification classification, ProjectedCorrectionKind kind)
    {
        var task = Task(AgentTaskStatus.Running, [Plan(0, Planned(0), Planned(1))],
            Exec(0, 0, classification, ToolCallResult.Failure(Secret)));

        var steps = Steps(Project(task), 0);

        Assert.Equal([ProjectedStepStatus.Correcting, ProjectedStepStatus.Pending], steps.Select(s => s.Status));
        Assert.True(steps[0].Current);
        Assert.Equal(kind, steps[0].CorrectionKind);
        Assert.Null(steps[1].CorrectionKind);
    }

    [Fact]
    public void LegacyPlan_WithAValidationFailure_StillCorrects_AndDoesNotThrow()
    {
        var legacy = new AgentPlan(0, "r", [new PlannedStep(0, "legacy objective", "system.events"), new PlannedStep(1, "next", "system.events")]);
        var failed = new PlanStep(0, "s", new ModelToolCall("c", "system.events", ToolArguments.Empty),
            new ToolCallResult(ToolOutcome.Failure, null, "bad") { FailureKind = ToolFailureKind.Validation }, "obs", 0);

        var steps = Steps(Project(Task(AgentTaskStatus.Running, [legacy], failed)), 0);

        Assert.Equal(ProjectedStepStatus.Correcting, steps[0].Status);
        Assert.Equal(ProjectedCorrectionKind.ArgumentValidation, steps[0].CorrectionKind);
        Assert.All(steps, s => Assert.False(s.Conditional));
    }

    [Fact]
    public void LegacyPlan_WithoutSteps_ProjectsFirstStepRunning()
    {
        var legacy = new AgentPlan(0, "r", [new PlannedStep(0, "only", null)]);
        var steps = Steps(Project(Task(AgentTaskStatus.Running, [legacy])), 0);
        Assert.Equal(ProjectedStepStatus.Running, steps[0].Status);
        Assert.Null(steps[0].ExpectedTool);
    }

    [Fact]
    public void ConditionalStep_WhoseFactExists_IsActivatedQualifier_NotAStatus()
    {
        var condition = new EvidenceFactExists(0, FactType, FactKey);
        var fact = new EvidenceFact(FactType, FactKey, ToolParameterType.String, JsonValue.Create(Secret)!);
        var task = Task(AgentTaskStatus.Running, [Plan(0, Planned(0), Planned(1, activation: condition))], Matched(0, 0, 0, fact));

        var step = Steps(Project(task), 0)[1];

        Assert.Equal(ProjectedStepStatus.Running, step.Status);
        Assert.True(step.Conditional);
        Assert.Equal(ProjectedConditionOutcome.Activated, step.ConditionOutcome);
    }

    [Fact]
    public void ConditionalStep_WhoseFactIsAbsent_IsSkipped_AndTheCursorMovesOn()
    {
        var condition = new EvidenceFactExists(0, FactType, FactKey);
        var task = Task(AgentTaskStatus.Running,
            [Plan(0, Planned(0), Planned(1, activation: condition), Planned(2))], Matched(0, 0));

        var steps = Steps(Project(task), 0);

        Assert.Equal([ProjectedStepStatus.Completed, ProjectedStepStatus.Skipped, ProjectedStepStatus.Running], steps.Select(s => s.Status));
        Assert.Equal(ProjectedConditionOutcome.Skipped, steps[1].ConditionOutcome);
        Assert.False(steps[1].Current);
        Assert.True(steps[2].Current);
    }

    [Fact]
    public void ConditionalStep_NotYetReached_HasNoOutcome()
    {
        var condition = new EvidenceFactExists(0, FactType, FactKey);
        var task = Task(AgentTaskStatus.Running, [Plan(0, Planned(0), Planned(1, activation: condition))]);

        var step = Steps(Project(task), 0)[1];

        Assert.Equal((ProjectedStepStatus.Pending, true, null), (step.Status, step.Conditional, step.ConditionOutcome));
    }

    [Fact]
    public void AcceptedReplan_KeepsTheResolvedPrefix_SupersedesTheUnresolvedSuffix_AndActivatesTheNewRevision()
    {
        var task = Task(AgentTaskStatus.Running,
            [Plan(0, Planned(0), Planned(1), Planned(2)), Plan(1, Planned(0), Planned(1))],
            Matched(0, 0),
            Exec(0, 1, PlannedStepExecutionClassification.ArgumentValidationFailure, ToolCallResult.Failure(Secret), 1),
            Exec(0, 1, PlannedStepExecutionClassification.ArgumentValidationFailure, ToolCallResult.Failure(Secret), 2));

        var projected = Project(task);

        Assert.Equal(1, projected!.ActiveRevision);
        Assert.Equal([false, true], projected.Revisions.Select(r => r.Active));
        Assert.Equal(
            [ProjectedStepStatus.Completed, ProjectedStepStatus.Superseded, ProjectedStepStatus.Superseded],
            Steps(projected, 0).Select(s => s.Status));
        Assert.Equal([ProjectedStepStatus.Running, ProjectedStepStatus.Pending], Steps(projected, 1).Select(s => s.Status));
        Assert.DoesNotContain(Steps(projected, 0), s => s.Current);
    }

    [Fact]
    public void ActiveRevision_WithASecondFailedCorrection_IsFailed_NotCorrecting()
    {
        var task = Task(AgentTaskStatus.Running, [Plan(0, Planned(0), Planned(1))],
            Exec(0, 0, PlannedStepExecutionClassification.ArgumentValidationFailure, ToolCallResult.Failure(Secret), 0),
            Exec(0, 0, PlannedStepExecutionClassification.ArgumentValidationFailure, ToolCallResult.Failure(Secret), 1));

        var steps = Steps(Project(task), 0);

        Assert.Equal([ProjectedStepStatus.Failed, ProjectedStepStatus.Pending], steps.Select(s => s.Status));
        Assert.DoesNotContain(steps, s => s.Current);
    }

    [Fact]
    public void TerminalTask_HasNoCurrentStep()
    {
        var task = Task(AgentTaskStatus.Cancelled, [Plan(0, Planned(0), Planned(1))], Matched(0, 0));

        var steps = Steps(Project(task), 0);

        Assert.Equal([ProjectedStepStatus.Completed, ProjectedStepStatus.Pending], steps.Select(s => s.Status));
        Assert.DoesNotContain(steps, s => s.Current);
    }

    [Fact]
    public void Projection_IsDeterministic_AndCarriesNoForbiddenData()
    {
        var condition = new EvidenceFactExists(0, "secret.type", "secret.key");
        var fact = new EvidenceFact("secret.type", "secret.key", ToolParameterType.String, JsonValue.Create(Secret)!);
        var task = Task(AgentTaskStatus.Running,
            [Plan(0, Planned(0), Planned(1, activation: condition)), Plan(1, Planned(0))],
            Matched(0, 0, 0, fact));

        var first = JsonSerializer.Serialize(Project(task));
        var second = JsonSerializer.Serialize(Project(task with { }));

        Assert.Equal(first, second);
        Assert.DoesNotContain(Secret, first, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.", first, StringComparison.Ordinal);
        Assert.DoesNotContain("rationale", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arguments", first, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PlannedStepExecutionClassification.ArgumentValidationFailure)]
    [InlineData(PlannedStepExecutionClassification.SemanticMismatch)]
    [InlineData(null)]
    public void InterruptedTask_RunningWithoutAnExecutor_HasNoRunningCorrectingOrCurrentStep(
        PlannedStepExecutionClassification? classification)
    {
        var steps = new List<PlanStep> { Matched(0, 0) };
        if (classification is { } kind)
        {
            steps.Add(Exec(0, 1, kind, ToolCallResult.Failure(Secret), 1));
        }

        var task = Task(AgentTaskStatus.Running, [Plan(0, Planned(0), Planned(1), Planned(2))], [.. steps]);

        var projected = Steps(Project(task, executing: false), 0);

        // Nothing executes an interrupted task, and it has not ended: its unconsumed step is neither Running nor Failed.
        Assert.Equal([ProjectedStepStatus.Completed, ProjectedStepStatus.Pending, ProjectedStepStatus.Pending], projected.Select(s => s.Status));
        Assert.DoesNotContain(projected, s => s.Current);
        Assert.All(projected, s => Assert.Null(s.CorrectionKind));
    }

    [Fact]
    public void ReplanTriggeringDeviation_IsSupersededOnceTheReplanIsAccepted_ButFailedWhileItsRevisionIsInForce()
    {
        var timedOut = Exec(0, 1, PlannedStepExecutionClassification.Matched,
            new ToolCallResult(ToolOutcome.Timeout, null, Secret), 1);
        var before = Task(AgentTaskStatus.ReplanLimitReached, [Plan(0, Planned(0), Planned(1), Planned(2))], Matched(0, 0), timedOut);
        var after = before with { Status = AgentTaskStatus.Running, Plans = [.. before.Plans, Plan(1, Planned(0))] };

        Assert.Equal(
            [ProjectedStepStatus.Completed, ProjectedStepStatus.Failed, ProjectedStepStatus.Pending],
            Steps(Project(before), 0).Select(s => s.Status));
        // The execution history keeps the timeout; the planned step's historical status is Superseded, never Failed.
        Assert.Equal(
            [ProjectedStepStatus.Completed, ProjectedStepStatus.Superseded, ProjectedStepStatus.Superseded],
            Steps(Project(after), 0).Select(s => s.Status));
    }

    [Theory]
    [InlineData(AgentTaskStatus.Completed)]
    [InlineData(AgentTaskStatus.Failed)]
    [InlineData(AgentTaskStatus.Cancelled)]
    [InlineData(AgentTaskStatus.MaxStepsReached)]
    [InlineData(AgentTaskStatus.BudgetExceeded)]
    [InlineData(AgentTaskStatus.ReplanLimitReached)]
    [InlineData(AgentTaskStatus.PolicyBlocked)]
    public void TerminalTask_KeepsItsHistory_NeverRunsOrPointsAtAStep(AgentTaskStatus terminal)
    {
        var condition = new EvidenceFactExists(0, FactType, FactKey);
        var task = Task(terminal,
            [Plan(0, Planned(0), Planned(1)), Plan(1, Planned(0), Planned(1, activation: condition), Planned(2), Planned(3))],
            Matched(0, 0),
            Matched(1, 0, 1),
            Exec(1, 2, PlannedStepExecutionClassification.ArgumentValidationFailure, ToolCallResult.Failure(Secret), 2));

        foreach (var executing in new[] { true, false })
        {
            var projected = Project(task, executing);

            Assert.DoesNotContain(projected!.Revisions.SelectMany(r => r.Steps), s => s.Current || s.Status is ProjectedStepStatus.Running or ProjectedStepStatus.Correcting);
            Assert.Equal([ProjectedStepStatus.Completed, ProjectedStepStatus.Superseded], Steps(projected, 0).Select(s => s.Status));
            // A correction spent on a step the task never consumed before ending is that step's failure.
            Assert.Equal(
                [ProjectedStepStatus.Completed, ProjectedStepStatus.Skipped, ProjectedStepStatus.Failed, ProjectedStepStatus.Pending],
                Steps(projected, 1).Select(s => s.Status));
        }
    }

    [Fact]
    public void LegacyFinalAnswer_DoesNotCompleteAPlannedStep()
    {
        var legacy = new AgentPlan(0, "r", [new PlannedStep(0, "inspect", "system.events"), new PlannedStep(1, "restart", "system.restart")]);
        var task = Task(AgentTaskStatus.Completed, [legacy],
            new PlanStep(0, "s", new ModelToolCall("c", "system.events", ToolArguments.Empty), ToolCallResult.Success("ok"), "ok", 0),
            new PlanStep(1, "final", null, null, "done", 0));

        Assert.Equal([ProjectedStepStatus.Completed, ProjectedStepStatus.Pending], Steps(Project(task), 0).Select(s => s.Status));
    }

    [Fact]
    public void ActiveRevision_IsTheLastAcceptedRevision_AsTheRunnerReadsIt()
    {
        var task = Task(AgentTaskStatus.Running, [Plan(1, Planned(0)), Plan(2, Planned(0))]);

        var projected = Project(task);

        Assert.Equal(2, projected!.ActiveRevision);
        Assert.Equal([(1, false), (2, true)], projected.Revisions.Select(r => (r.Revision, r.Active)));
    }

    [Fact]
    public void Objective_IsCollapsedAndBounded()
    {
        var plan = new AgentPlan(0, "r", [new PlannedStep(0, "a\n  b " + new string('x', 500), null)]);
        var objective = Steps(Project(Task(AgentTaskStatus.Running, [plan])), 0)[0].Objective;
        Assert.StartsWith("a b x", objective, StringComparison.Ordinal);
        Assert.Equal(ExecutionPlanProjector.MaxObjectiveCharacters, objective.Length);
    }
}
