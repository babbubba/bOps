// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>The operator-facing status of one planned step (PRE-4). Derived, never stored.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProjectedStepStatus>))]
public enum ProjectedStepStatus
{
    /// <summary>Not reached yet.</summary>
    Pending,

    /// <summary>The current step of a running task this host is executing.</summary>
    Running,

    /// <summary>The current step after a spent bounded correction (see <see cref="ProjectedCorrectionKind"/>); still the same planned step.</summary>
    Correcting,

    /// <summary>Consumed by a successful execution.</summary>
    Completed,

    /// <summary>Consumed by a failing execution, or never matched before its revision ended.</summary>
    Failed,

    /// <summary>A conditional step whose evidence fact was not produced; no model or tool call was made.</summary>
    Skipped,

    /// <summary>Unresolved when an accepted replan replaced its revision.</summary>
    Superseded,
}

/// <summary>Which bounded same-step correction is in progress (ADR-0047, ADR-0050).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProjectedCorrectionKind>))]
public enum ProjectedCorrectionKind
{
    /// <summary>The call named the planned tool but not the expected argument constraints.</summary>
    Semantic,

    /// <summary>The call failed complete argument validation.</summary>
    ArgumentValidation,
}

/// <summary>The determined outcome of a conditional step's activation (ADR-0050); a qualifier, never an execution status.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProjectedConditionOutcome>))]
public enum ProjectedConditionOutcome
{
    /// <summary>The referenced fact exists; the step runs through the normal path.</summary>
    Activated,

    /// <summary>The referenced fact does not exist; the step was skipped.</summary>
    Skipped,
}

/// <summary>
/// One planned step as an operator sees it: the model's own short objective and expected tool, plus derived state. Carries no
/// arguments, activation fact, result, observation or rationale.
/// </summary>
public sealed record ProjectedPlanStep(
    int Revision,
    int Index,
    string Objective,
    string? ExpectedTool,
    ProjectedStepStatus Status,
    bool Current,
    bool Conditional,
    ProjectedConditionOutcome? ConditionOutcome,
    ProjectedCorrectionKind? CorrectionKind);

/// <summary>One accepted plan revision.</summary>
public sealed record ProjectedPlanRevision(int Revision, bool Active, IReadOnlyList<ProjectedPlanStep> Steps);

/// <summary>The execution plan of a task: every accepted revision, oldest first.</summary>
public sealed record ProjectedPlan(int ActiveRevision, IReadOnlyList<ProjectedPlanRevision> Revisions);

/// <summary>
/// PRE-4: the single deterministic, read-only projection of a task's persisted plan revisions and execution history into
/// operator-visible step state. It adds no state of its own: cursor, correction budgets, skipping and replan-required are the
/// <see cref="PlannedStepPosition"/> fold the runner itself uses live and on resume, applied step by step.
/// </summary>
public static class ExecutionPlanProjector
{
    internal const int MaxObjectiveCharacters = 200;

    /// <summary>Projects <paramref name="task"/>; <c>null</c> when no plan revision was ever persisted.</summary>
    /// <param name="task">The persisted task.</param>
    /// <param name="executing">
    /// Whether this host holds an execution attempt of the task. <see cref="AgentTaskStatus.Running"/> without it is an interrupted
    /// task (ADR-0040 §9): nothing is executing it, so no step is <see cref="ProjectedStepStatus.Running"/> or current.
    /// </param>
    /// <param name="manifestOf">Resolves a tool's manifest for conditional fact binding; <c>null</c> when unavailable.</param>
    public static ProjectedPlan? Project(TaskState task, bool executing, Func<string, ToolManifest?>? manifestOf = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Plans.Count == 0)
        {
            return null;
        }

        // The runner's own invariant: accepted revisions are appended in order and the last one is the plan in force.
        var last = task.Plans.Count - 1;
        var revisions = task.Plans
            .Select((plan, i) => new ProjectedPlanRevision(
                plan.Revision, i == last, ProjectRevision(task, plan, i == last, executing, manifestOf)))
            .ToList();
        return new ProjectedPlan(task.Plans[last].Revision, revisions);
    }

    private static List<ProjectedPlanStep> ProjectRevision(
        TaskState task, AgentPlan plan, bool isActive, bool executing, Func<string, ToolManifest?>? manifestOf)
    {
        var all = task.Steps;
        var status = new ProjectedStepStatus?[plan.Steps.Count];
        var position = PlannedStepPosition.Start;
        foreach (var step in all.Where(step => step.PlanRevision == plan.Revision))
        {
            var before = position;
            var after = before.After(plan, step);
            position = after.Settle(plan, all, manifestOf);

            // Only a tool execution resolves a planned step; the legacy fold also advances over a final answer, which executed nothing.
            if (after.Cursor > before.Cursor && InRange(before.Cursor, status.Length) && step.ToolCall is not null)
            {
                status[before.Cursor] = step.Result is { Outcome: ToolOutcome.Success }
                    ? ProjectedStepStatus.Completed
                    : ProjectedStepStatus.Failed;
            }

            for (var skipped = after.Cursor; skipped < position.Cursor && InRange(skipped, status.Length); skipped++)
            {
                status[skipped] = ProjectedStepStatus.Skipped;
            }
        }

        var cursor = position.Cursor;
        var running = isActive && task.Status == AgentTaskStatus.Running && executing;
        var attempted = position.SemanticCorrectionSpent || position.ArgumentCorrectionSpent;
        ProjectedCorrectionKind? correction = null;
        if (plan.SemanticContractVersion == 1)
        {
            correction = all.LastOrDefault(step => step.PlanRevision == plan.Revision)?.ExecutionClassification switch
            {
                PlannedStepExecutionClassification.SemanticMismatch => ProjectedCorrectionKind.Semantic,
                PlannedStepExecutionClassification.ArgumentValidationFailure => ProjectedCorrectionKind.ArgumentValidation,
                _ => null,
            };
        }
        else if (position.ArgumentCorrectionSpent)
        {
            correction = ProjectedCorrectionKind.ArgumentValidation;
        }

        var currentIndex = -1;
        // A superseded revision resolves nothing further: an accepted replan replaced its whole unresolved suffix.
        if (isActive && InRange(cursor, status.Length))
        {
            if (position.ReplanRequired)
            {
                status[cursor] = ProjectedStepStatus.Failed;
            }
            else if (running)
            {
                currentIndex = cursor;
                status[cursor] = attempted && correction is not null ? ProjectedStepStatus.Correcting : ProjectedStepStatus.Running;
            }
            else if (attempted && task.Status != AgentTaskStatus.Running)
            {
                // The task ended with the step's correction spent and the step never consumed; an interrupted task has not ended.
                status[cursor] = ProjectedStepStatus.Failed;
            }
        }

        var projected = new List<ProjectedPlanStep>(plan.Steps.Count);
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var planned = plan.Steps[i];
            var resolved = status[i] ?? (isActive ? ProjectedStepStatus.Pending : ProjectedStepStatus.Superseded);
            var conditional = plan.SemanticContractVersion == 1 && planned.Activation is not null;
            projected.Add(new ProjectedPlanStep(
                plan.Revision,
                i,
                Objective(planned.Description),
                planned.ExpectedTool,
                resolved,
                i == currentIndex,
                conditional,
                conditional ? OutcomeOf(plan, all, cursor, i, resolved) : null,
                i == currentIndex && resolved == ProjectedStepStatus.Correcting ? correction : null));
        }

        return projected;
    }

    private static ProjectedConditionOutcome? OutcomeOf(
        AgentPlan plan, IReadOnlyList<PlanStep> steps, int cursor, int index, ProjectedStepStatus status)
    {
        if (status == ProjectedStepStatus.Skipped)
        {
            return ProjectedConditionOutcome.Skipped;
        }

        return ConditionalSteps.StateOf(plan, steps.Where(step => step.PlanRevision == plan.Revision), cursor, index) switch
        {
            ConditionalStepState.Activated => ProjectedConditionOutcome.Activated,
            ConditionalStepState.Skipped => ProjectedConditionOutcome.Skipped,
            _ => null,
        };
    }

    private static bool InRange(int index, int count) => index >= 0 && index < count;

    private static string Objective(string description)
    {
        var text = string.Join(' ', (description ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= MaxObjectiveCharacters ? text : string.Concat(text.AsSpan(0, MaxObjectiveCharacters - 1), "…");
    }
}
