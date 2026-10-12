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
/// the replan budget ended the task. A read-only check reflects live state (CPU, memory, a crash window), so one fresh sample of a
/// completed check is legitimate diagnosis and is accepted. A replan step whose tool and complete <c>expectedArguments</c> equal
/// a check that already ran <see cref="MaxExecutionsPerCheck"/> times, or a revision that is nothing but the whole previous plan
/// again, is a replay that adds nothing and is rejected through the normal corrective re-ask with this exact problem. Nothing is suppressed or rewritten: a new tool, any different argument or an
/// empty plan is accepted. Only checks that are provably identical, of a read-only tool, whose result was successful and not
/// declared incomplete, count; mutations, failures, partial results and conditional steps never do.
/// </summary>
internal static class CompletedChecks
{
    /// <summary>The bound of the replan prompt section; prompt text only, it decides nothing.</summary>
    internal const int MaxPromptCharacters = 2048;

    /// <summary>How often one identical check may have completed before a further identical step is a replay: the original and one resample.</summary>
    internal const int MaxExecutionsPerCheck = 2;

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
    internal static string? RepeatProblem(AgentPlan candidate, IReadOnlyList<CompletedCheck> completed, AgentPlan? previous = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(completed);
        var unconditional = candidate.Steps.Where(planned => planned.Activation is null && !string.IsNullOrEmpty(planned.ExpectedTool)).ToList();
        var previousChecks = previous?.Steps.Where(planned => planned.Activation is null && !string.IsNullOrEmpty(planned.ExpectedTool)).ToList() ?? [];
        var replaysWholePlan = previousChecks.Count > 1
            && unconditional.All(planned => RunsOf(planned, completed).Count > 0)
            && previousChecks.All(prior => unconditional.Any(planned => SameCheck(planned, prior.ExpectedTool!, prior.ExpectedArguments?.ToJson() ?? [])));
        foreach (var planned in unconditional)
        {
            var runs = RunsOf(planned, completed);
            if (runs.Count >= MaxExecutionsPerCheck || (replaysWholePlan && runs.Count > 0))
            {
                return $"Plan step {planned.Index} repeats completed task step {runs[^1].StepIndex} ({runs[^1].Manifest.Name} with " +
                    $"identical arguments), which already ran {runs.Count} time(s); its evidence is already persisted. Plan only " +
                    "checks with a different tool or different arguments, or return an empty \"steps\" array to answer from the " +
                    "evidence already collected.";
            }
        }

        return null;
    }

    private static List<CompletedCheck> RunsOf(PlannedStep planned, IReadOnlyList<CompletedCheck> completed)
    {
        var expected = planned.ExpectedArguments?.ToJson() ?? [];
        return [.. completed.Where(check => string.Equals(check.Manifest.Name, planned.ExpectedTool, StringComparison.Ordinal)
            && JsonNode.DeepEquals(check.Arguments, expected))];
    }

    private static bool SameCheck(PlannedStep planned, string tool, JsonObject arguments) =>
        string.Equals(planned.ExpectedTool, tool, StringComparison.Ordinal)
        && JsonNode.DeepEquals(planned.ExpectedArguments?.ToJson() ?? [], arguments);

    /// <summary>The bounded replan prompt section naming every completed check and its arguments (sensitive values redacted), or <c>null</c>.</summary>
    internal static string? Describe(IReadOnlyList<CompletedCheck> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (completed.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder(
            "Completed checks of this task (their evidence is persisted; one fresh sample of a check is allowed when live state may have " +
            "changed, but a revision that only replays the whole previous plan, or a step repeating a check that already ran twice with the " +
            "same arguments, is rejected):");
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
