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
/// resume), shown to a replan as information. BENCH B03 re-proposed the five completed reads three times because the replan could
/// not tell which checks had run. This only informs: nothing is refused, suppressed or rewritten, because a read-only check
/// reflects live state and sampling it again, even with identical arguments, can be sound diagnosis. A replan cycle is bounded
/// by the existing replan budgets; whether stored evidence is still fresh is left to AF-EVID-01. Only checks of a read-only tool
/// whose result was successful and not declared incomplete are listed; mutations, failures and partial results never are.
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

    /// <summary>The bounded replan prompt section naming every completed check and its arguments (sensitive values redacted), or <c>null</c>.</summary>
    internal static string? Describe(IReadOnlyList<CompletedCheck> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (completed.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder(
            "Completed checks of this task (their evidence is persisted; plan one again only to sample live state that may have changed):");
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
