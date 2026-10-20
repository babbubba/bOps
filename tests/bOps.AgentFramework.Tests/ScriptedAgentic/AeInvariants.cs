// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Abstractions;
using bOps.AgentFramework.Tests.PromptAssembly;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests.ScriptedAgentic;

/// <summary>
/// AE-1: the safety and runtime properties of section 5 of the brief, as checks over a whole run. They are invariants, not expected
/// defects: none of them may be loosened because a prompt-level defect is reproduced, and each has a negative control in
/// <see cref="AeSafetyInvariantTests"/> that proves it can fail.
/// </summary>
internal static class AeInvariants
{
    private static readonly AgentTaskStatus[] BoundedTerminals = [AgentTaskStatus.Completed, AgentTaskStatus.ReplanLimitReached, AgentTaskStatus.Failed];

    internal static string CallLine(AeCall call)
    {
        var parts = new List<string> { call.Index.ToString(CultureInfo.InvariantCulture), call.Purpose.ToString(), call.Reply };
        if (call.Operational.Count > 0)
        {
            parts.Add("op=" + string.Join(',', call.Operational));
        }

        if (call.Control.Count > 0)
        {
            parts.Add("ctl=" + string.Join(',', call.Control));
        }

        if (call.Cursor is { } cursor)
        {
            parts.Add($"c={cursor}/{call.PlanLength}");
        }

        if (call.Recovery is not null)
        {
            parts.Add(call.Recovery);
        }

        if (call.Live05Continuation)
        {
            parts.Add("LIVE-05");
        }

        return string.Join(' ', parts);
    }

    internal static string StepLine(AeStep step) => string.Join(' ', new[]
    {
        step.Index.ToString(CultureInfo.InvariantCulture),
        step.Kind,
        step.Tool,
        step.Outcome,
        step.Revision is { } revision ? "r" + revision.ToString(CultureInfo.InvariantCulture) : null,
        step.PlannedIndex is { } planned ? "p" + planned.ToString(CultureInfo.InvariantCulture) : null,
        step.CursorAfter is { } after ? "after=" + after.ToString(CultureInfo.InvariantCulture) : null,
    }.OfType<string>());

    /// <summary>The scripted native calls to operational tools, paired in order with the steps that recorded them (executed or refused).</summary>
    internal static IReadOnlyList<(AeCall Call, string Tool, AeStep Step)> ToolPairs(AeRun run)
    {
        var calls = run.Calls
            .Where(call => call.Reply.StartsWith("call:", StringComparison.Ordinal) && !call.Reply.StartsWith("call:runtime.", StringComparison.Ordinal))
            .ToList();
        var steps = run.Steps.Where(step => step.Kind is "tool" or "refused-call").ToList();
        Assert.Equal(calls.Count, steps.Count); // every native operational call of the model left exactly one persisted step
        return [.. calls.Select((call, index) => (call, call.Reply["call:".Length..], steps[index]))];
    }

    /// <summary>The request that offered a planned step and the step it recorded agree on where the cursor was, by two independent routes.</summary>
    internal static void AssertCursorsAgree(AeRun run)
    {
        foreach (var (call, tool, step) in ToolPairs(run))
        {
            if (step.Kind == "refused-call")
            {
                Assert.DoesNotContain(tool, call.Operational); // a refused call named something the request did not offer
                continue;
            }

            Assert.Equal([tool], call.Operational); // the one operational tool offered is the one that ran
            if (step.Classification == "Matched" && step.PlannedIndex is { } planned)
            {
                Assert.Equal(planned, call.Cursor); // the cursor seen on the wire is the planned index the runtime recorded
                if (step.Outcome == nameof(ToolOutcome.Success))
                {
                    Assert.Equal(planned + 1, step.CursorAfter); // and a success moves it by exactly one
                }
            }
        }
    }

    /// <summary>The persisted steps are contiguous and the plan revisions run 0..n without a gap: nothing was lost.</summary>
    internal static void AssertNothingPersistedWasLost(AeRun run)
    {
        Assert.Equal(Enumerable.Range(0, run.State.Steps.Count), run.State.Steps.Select(step => step.Index));
        Assert.Equal(Enumerable.Range(0, run.State.Plans.Count), run.State.Plans.Select(plan => plan.Revision));
        Assert.All(run.State.Steps.Where(step => step.PlanRevision is not null), step => Assert.InRange(step.PlanRevision!.Value, 0, run.State.Plans.Count - 1));
        var revisions = run.State.Steps.Where(step => step.PlanRevision is not null).Select(step => step.PlanRevision!.Value).ToList();
        Assert.Equal(revisions.Order(), revisions); // a step never belongs to an older revision than the one before it
    }

    /// <summary>Completed means a usable final exists and the latest plan owes nothing; a failure never persists a final step.</summary>
    internal static void AssertCompletedMeansAnAnswerAndNoOwedStep(AeRun run)
    {
        if (run.State.Status == AgentTaskStatus.Completed)
        {
            Assert.True(AeObserver.HasAnswerContent(run.FinalText), "a Completed task has no usable final answer");
            Assert.False(AeRubric.LatestPlanStillOwesSteps(run), "a Completed task still owes a planned step in its latest plan revision");
        }
        else
        {
            Assert.DoesNotContain(run.State.Steps, FinalResponse.IsFinalStep); // an unusable or absent answer is never persisted as a final
        }
    }

    /// <summary>Only registered tools ran, policy-forbidden calls never reached their tool, and every refusal is in the audit log.</summary>
    internal static void AssertOnlyGovernedRegisteredToolsExecuted(AeRun run)
    {
        var registered = run.Pa.Tools.Keys.ToHashSet(StringComparer.Ordinal);
        Assert.All(run.Metrics.ToolsExecuted, tool => Assert.Contains(tool, registered)); // nothing outside the registry ran
        Assert.Equal(run.Metrics.ToolsExecuted.Count, run.Metrics.ToolExecutions); // the audit and the tool objects count the same executions
        Assert.Equal(run.Metrics.ToolsExecuted.Count, run.Pa.Tools.Values.Sum(tool => tool.Invocations));

        // Policy audits a decision only when it is not Automatic; an executed Read call is recorded as Automatic (or approved) and no
        // Forbidden decision exists for the same step and tool.
        var forbidden = run.Audit<PolicyDecisionAuditEvent>().Where(decision => decision.Mode == PolicyMode.Forbidden).ToList();
        foreach (var executed in run.Audit<ToolCallAuditEvent>().Where(evt => evt.EffectiveTimeout is not null))
        {
            Assert.Contains(executed.Authorization, new[] { AuthorizationKind.Automatic, AuthorizationKind.UserApproved });
            Assert.DoesNotContain(forbidden, decision => decision.StepIndex == executed.StepIndex && decision.Tool == executed.Tool);
        }

        foreach (var denial in forbidden)
        {
            var call = Assert.Single(run.Audit<ToolCallAuditEvent>(), evt => evt.StepIndex == denial.StepIndex && evt.Tool == denial.Tool);
            Assert.Equal((AuthorizationKind.PolicyDenied, (TimeSpan?)null), (call.Authorization, call.EffectiveTimeout)); // a forbidden call never reached the tool
            Assert.Equal(0, run.Pa.Tools[denial.Tool].Invocations);
        }

        foreach (var refused in run.Audit<ToolCallAuditEvent>().Where(evt => evt.EffectiveTimeout is null))
        {
            Assert.NotEqual(AuthorizationKind.Automatic, refused.Authorization); // a call that did not run was never recorded as authorized
        }

        Assert.Equal(
            run.State.Steps.Count(step => AeObserver.KindOf(step) is "tool" or "refused-call"),
            run.Audit<ToolCallAuditEvent>().Count()); // every proposed operational call is audited, executed or not
    }

    /// <summary>Tool output and Knowledge text reach the model only as data turns: none of it ever appears in a system prompt.</summary>
    internal static void AssertNoToolOrKnowledgeTextBecameInstruction(AeRun run)
    {
        foreach (var request in run.Pa.Seen)
        {
            foreach (var turn in request.History.Where(turn => turn.Role == ChatRole.Tool))
            {
                Assert.Contains(turn.Provenance!.Origin, new[] { ContextOrigin.ToolResult, ContextOrigin.Knowledge, ContextOrigin.Runtime });
                foreach (var line in (turn.Content ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries).Where(line => line.Length >= 25 && !line.StartsWith("<<<", StringComparison.Ordinal)))
                {
                    Assert.DoesNotContain(line, request.SystemPrompt, StringComparison.Ordinal);
                }
            }
        }
    }

    /// <summary>The budgets that make every loop finite are respected by the run that was observed.</summary>
    internal static void AssertBounded(AeRun run, AgentRunnerOptions options)
    {
        Assert.Contains(run.State.Status, BoundedTerminals);
        Assert.True(run.Metrics.ReplansCharged <= options.MaxReplans, "the replan budget was exceeded");
        Assert.True(run.State.Accounting!.LifetimeSteps <= options.MaxSteps, "the step budget was exceeded");

        // A recovery instruction needs a step with extra model calls and is added at most EmptyFinalResponseRetries times per step.
        Assert.True(
            run.Metrics.RecoveryTurns <= run.Steps.Count(step => step.ModelCalls > 1) * options.EmptyFinalResponseRetries,
            "a recovery loop ran past its budget");
        Assert.Equal(run.Metrics.ModelCalls, run.Pa.Bodies.Count); // no hidden call: the audit of the adapter and the upstream agree
        Assert.Equal(0, run.Pa.Unconsumed);
    }

    /// <summary>Every check at once, for a run made with the default options.</summary>
    internal static void AssertAll(AeRun run, AgentRunnerOptions? options = null)
    {
        PaInvariants.AssertEveryRequestIsAValidGovernedBody(run.Pa, run.State.Status);
        AssertCursorsAgree(run);
        AssertNothingPersistedWasLost(run);
        AssertCompletedMeansAnAnswerAndNoOwedStep(run);
        AssertOnlyGovernedRegisteredToolsExecuted(run);
        AssertNoToolOrKnowledgeTextBecameInstruction(run);
        AssertBounded(run, options ?? new AgentRunnerOptions());
    }
}
