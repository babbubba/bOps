// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>One read-only tool call of the task that completed successfully, with the exact arguments it ran with.</summary>
internal sealed record CompletedCheck(int StepIndex, ToolManifest Manifest, JsonObject Arguments);

/// <summary>
/// AF-TOOLROUTE-01: the read-only checks a task already completed, derived from persisted steps only (identical live and on
/// resume), and the replan rule built on them. BENCH B03 accepted three replans that re-proposed the five read checks it had just
/// completed, with the same arguments, after a call the step had not offered; each replan reset the cursor and re-ran them until
/// the replan budget ended the task. A replan step whose tool and complete <c>expectedArguments</c> equal a completed check is
/// therefore rejected through the normal corrective re-ask with this exact problem. Nothing is suppressed or rewritten: a new
/// tool, any different argument (a wider window, another source) or an empty plan is accepted. Only checks that are provably
/// identical, of a read-only tool, whose result was successful and not declared incomplete, count; mutations, failures, partial
/// results and conditional steps never do.
/// </summary>
internal static class CompletedChecks
{
    /// <summary>The bound of the replan prompt section; prompt text only, it decides nothing.</summary>
    internal const int MaxPromptCharacters = 2048;

    internal static IReadOnlyList<CompletedCheck> From(IReadOnlyList<PlanStep> steps, Func<string, ToolManifest?> manifestOf)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(manifestOf);
        var completed = new List<CompletedCheck>();
        foreach (var step in steps)
        {
            if (step.ToolCall is not { ToolNameError: null, ArgumentsError: null } call
                || step.Result is not { Outcome: ToolOutcome.Success } result
                || result.Completeness is not (ToolResultCompleteness.Unspecified or ToolResultCompleteness.Complete)
                || step.VerificationStatus == VerificationStatus.Refuted
                || manifestOf(call.ToolName) is not { Risk: RiskLevel.Read } manifest)
            {
                continue;
            }

            completed.Add(new CompletedCheck(step.Index, manifest, call.Arguments.ToJson()));
        }

        return completed;
    }

    /// <summary>Why <paramref name="candidate"/> may not be accepted as a replan, or <c>null</c> when no step repeats a completed check.</summary>
    internal static string? RepeatProblem(AgentPlan candidate, IReadOnlyList<CompletedCheck> completed)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(completed);
        foreach (var planned in candidate.Steps)
        {
            if (planned.Activation is not null || string.IsNullOrEmpty(planned.ExpectedTool))
            {
                continue;
            }

            var expected = planned.ExpectedArguments?.ToJson() ?? [];
            if (completed.FirstOrDefault(check => string.Equals(check.Manifest.Name, planned.ExpectedTool, StringComparison.Ordinal)
                    && JsonNode.DeepEquals(check.Arguments, expected)) is { } repeat)
            {
                return $"Plan step {planned.Index} repeats completed task step {repeat.StepIndex} ({repeat.Manifest.Name} with " +
                    "identical arguments); its evidence is already persisted. Plan only checks with a different tool or different " +
                    "arguments, or return an empty \"steps\" array to answer from the evidence already collected.";
            }
        }

        return null;
    }

    /// <summary>The bounded replan prompt section naming every completed check and its arguments (sensitive values redacted), or <c>null</c>.</summary>
    internal static string? Describe(IReadOnlyList<CompletedCheck> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (completed.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder(
            "Completed checks of this task (their evidence is persisted; a plan step repeating one with the same arguments is rejected):");
        foreach (var check in completed)
        {
            var shown = check.Arguments.DeepClone().AsObject();
            foreach (var parameter in check.Manifest.Parameters.Where(parameter => parameter.Sensitive && shown.ContainsKey(parameter.Name)))
            {
                shown[parameter.Name] = "[redacted]";
            }

            builder.Append("\n- task step ").Append(check.StepIndex).Append(' ').Append(check.Manifest.Name)
                .Append(' ').Append(shown.ToJsonString());
        }

        return AgentRunner.ProjectForPrompt(builder.ToString(), MaxPromptCharacters);
    }
}
