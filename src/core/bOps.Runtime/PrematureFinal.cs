// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// ADR-0046 early-final amendment (AF-05H): a final candidate given while the accepted plan still has an unresolved executable step
/// is never accepted as completion. The paid model call is kept on a runtime-authored, non-tool bookkeeping step (never evidence,
/// never a final-response step) and the plan is re-evaluated by a governed replan, which may return an empty plan. Every value here
/// is a pure function of the persisted plan and steps, so live execution and resume reach the same state.
/// </summary>
internal static class PrematureFinal
{
    /// <summary>The fixed <see cref="PlanStep.Description"/> of the bookkeeping step; never one of the final-response markers.</summary>
    internal const string StepDescription = "Premature final response; plan re-evaluation required";

    /// <summary>The most unresolved planned steps the runtime-authored observation names.</summary>
    internal const int MaxListedSteps = 16;

    /// <summary>The runtime-authored instruction of the replan that follows a premature final.</summary>
    internal const string ReplanDirective =
        "The model indicated that the goal may already be achieved, but the accepted plan still contains unresolved executable " +
        "steps. Re-evaluate the plan. Return an empty \"steps\" array if no further operational work is needed. Otherwise return " +
        "the revised remaining plan.";

    /// <summary>Whether <paramref name="step"/> is the bookkeeping step of a rejected premature final.</summary>
    internal static bool IsStep(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.ToolCall is null && string.Equals(step.Description, StepDescription, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the plan still has an unresolved executable step at <paramref name="position"/>: the cursor, already settled over
    /// deterministically skipped conditional steps, is inside the plan. An empty or fully consumed plan has none.
    /// </summary>
    internal static bool HasUnresolvedStep(AgentPlan plan, PlannedStepPosition position) =>
        !position.ReplanRequired && position.Cursor >= 0 && position.Cursor < plan.Steps.Count;

    /// <summary>
    /// The runtime-authored observation of the bookkeeping step: which planned positions were unresolved, by number and the
    /// catalog-validated expected tool name only. No model text, plan description, argument or tool output enters it.
    /// </summary>
    internal static string Observation(AgentPlan plan, int cursor)
    {
        var text = new StringBuilder(string.Create(
            CultureInfo.InvariantCulture,
            $"Final answer not accepted: plan revision {plan.Revision} still had {plan.Steps.Count - cursor} unresolved planned step(s):"));
        foreach (var planned in plan.Steps.Skip(cursor).Take(MaxListedSteps))
        {
            text.Append(CultureInfo.InvariantCulture, $" {planned.Index + 1} [{ToolLabel(planned.ExpectedTool)}];");
        }

        if (plan.Steps.Count - cursor > MaxListedSteps)
        {
            text.Append(" …;");
        }

        return text.Append(" the plan is re-evaluated before any final answer is accepted.").ToString();
    }

    /// <summary>The protected user turn of the replan triggered by <paramref name="trigger"/>: its observation and the fixed directive.</summary>
    internal static string ReplanTurn(PlanStep trigger) => $"{trigger.Observation}\n{ReplanDirective}";

    /// <summary>The tool name when it is a plain catalog-style identifier, otherwise a fixed placeholder; never free text.</summary>
    internal static string ToolLabel(string? tool) =>
        tool is { Length: > 0 and <= 128 } name && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            ? name
            : "(tool name omitted)";
}
