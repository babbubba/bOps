// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace bOps.Runtime;

/// <summary>
/// The executable steps an execution attempt has left when a plan or a replan is asked for, and so how many steps that plan may
/// list (C03-R09). Every executed or refused tool call and the final answer each count one step, so one step is kept for the
/// answer. A plan that cannot finish inside the attempt is refused with this budget, which the bounded plan correction then names,
/// instead of running until <see cref="AgentTaskStatus.MaxStepsReached"/> without an answer (MAF A/B/C/D pilot B02-D).
/// </summary>
internal sealed record PlanStepBudget(int StepsLeft, bool NextAttempt = false)
{
    /// <summary>The most steps a plan may list: the steps left, less the one kept for the final answer.</summary>
    public int MaxPlanSteps => Math.Max(0, StepsLeft - 1);

    /// <summary>
    /// The attempt's own steps left, capped by what the task has left over its lifetime: the same bound as the step loop,
    /// <c>min(MaxSteps, MaxLifetimeSteps - lifetime steps at admission)</c> less the steps this attempt already took.
    /// </summary>
    public static PlanStepBudget For(AgentRunnerOptions options, int attemptSteps, int lifetimeSteps)
    {
        ArgumentNullException.ThrowIfNull(options);
        var left = Math.Min(options.MaxSteps - attemptSteps, options.MaxLifetimeSteps - lifetimeSteps);
        return new PlanStepBudget(Math.Max(0, left));
    }

    /// <summary>
    /// The budget of a replan. A replan asked for after the attempt's last step (the step that spent the plan was also the
    /// attempt's last) cannot run in this attempt: the attempt ends <see cref="AgentTaskStatus.MaxStepsReached"/> and the plan runs
    /// in the next one, a resume, so it is sized for a fresh attempt under the lifetime cap.
    /// </summary>
    public static PlanStepBudget ForReplan(AgentRunnerOptions options, int attemptSteps, int lifetimeSteps)
    {
        var current = For(options, attemptSteps, lifetimeSteps);
        return current.StepsLeft > 0 ? current : For(options, 0, lifetimeSteps) with { NextAttempt = true };
    }

    /// <summary>The planner-facing statement of the budget, appended to the planning and replanning instructions.</summary>
    public string Instructions => (NextAttempt
            ? $"This execution attempt has no step left: this plan runs in the next execution attempt, which has {Count(StepsLeft)}."
            : $"This execution attempt has {Count(StepsLeft)} left.")
        + (MaxPlanSteps == 0
            ? " The final answer needs a step, so no tool step fits: return an empty \"steps\" array."
            : " Every tool call the runtime executes or refuses, and the final answer, uses one step, so a plan may list at most " +
              $"{Count(MaxPlanSteps)} (conditional steps included); a longer plan is refused.");

    /// <summary>Why a plan of <paramref name="planSteps"/> steps does not fit, or <c>null</c> when it does.</summary>
    public string? Problem(int planSteps) => planSteps <= MaxPlanSteps
        ? null
        : $"The plan lists {Count(planSteps)}, but {(NextAttempt ? "the next execution attempt" : "this execution attempt")} has " +
          $"{Count(StepsLeft)} {(NextAttempt ? "" : "left ")}and the final answer needs one of them, so a plan may list at most " +
          $"{MaxPlanSteps.ToString(CultureInfo.InvariantCulture)}. Keep only the steps the goal needs.";

    private static string Count(int steps) =>
        steps.ToString(CultureInfo.InvariantCulture) + (steps == 1 ? " step" : " steps");
}
