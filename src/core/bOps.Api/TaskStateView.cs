// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.Api;

/// <summary>
/// The shape of a task the API sends to a client. The task store keeps, for troubleshooting, the exact request and
/// reply bodies of every model call; a request repeats the whole conversation, and the events stream sends the full
/// task at every step, so those bodies stay in the store and never travel here. Which model answered, how long it
/// took and the tokens do.
/// </summary>
internal static class TaskStateView
{
    /// <summary>The same defaults ASP.NET Core's Minimal API JSON output uses, so the view reads like every other response.</summary>
    private static readonly JsonSerializerOptions ViewJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Returns <paramref name="task"/> with the request and reply bodies of its model calls removed.</summary>
    internal static TaskState WithoutModelPayloads(TaskState task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task with
        {
            Steps = [.. task.Steps.Select(step => step.ModelCalls is null ? step : step with { ModelCalls = Strip(step.ModelCalls) })],
            Plans = [.. task.Plans.Select(plan => plan.ModelCalls is null ? plan : plan with { ModelCalls = Strip(plan.ModelCalls) })],
        };
    }

    /// <summary>
    /// The task as a client sees it (ADR-0040 §9, ADR-0051 §14.2): every stored property without model bodies, the effective lifetime
    /// accounting (derived for a task stored before it was kept), the read-only execution-plan projection (PRE-4, with the mutation
    /// journal as input), whether this host is executing it, whether an ordinary resume would be accepted now, with the refusal when
    /// not, whether an administrator's recovery would be, and the task's mutation journal. <c>Running</c> and not <c>executing</c> is
    /// what a client may label "Interrupted"; it is not resumable.
    /// </summary>
    internal static JsonObject ToView(
        TaskState task, bool executing, TaskResumeDecision resume, ProjectedPlan? executionPlan, TaskRecoveryDecision recovery,
        IReadOnlyList<TaskMutationJournalEntry>? journal)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(resume);
        ArgumentNullException.ThrowIfNull(recovery);

        var view = JsonSerializer.SerializeToNode(WithoutModelPayloads(task), ViewJsonOptions)!.AsObject();
        view["accounting"] = JsonSerializer.SerializeToNode(TaskResumePolicy.EffectiveAccounting(task), ViewJsonOptions);
        view["executionPlan"] = JsonSerializer.SerializeToNode(executionPlan, ViewJsonOptions);
        view["executing"] = executing;
        view["resumable"] = resume.Resumable;
        view["resumeBlockedReason"] = resume.Refusal is { } refusal
            ? JsonSerializer.SerializeToNode(new TaskErrorResponse(refusal.Code, refusal.Message), ViewJsonOptions)
            : null;
        view["recoverable"] = recovery.Recoverable;
        view["recoveryBlockedReason"] = recovery.Refusal is { } recoveryRefusal
            ? JsonSerializer.SerializeToNode(new TaskErrorResponse(recoveryRefusal.Code, recoveryRefusal.Message), ViewJsonOptions)
            : null;
        view["mutationJournal"] = JournalView(task, journal);
        return view;
    }

    /// <summary>
    /// The mutation journal as a client sees it (ADR-0051 §14.2): the arguments only as a 12-character fingerprint of their redacted
    /// hash, never the arguments, output or error text; each entry's knowledge in words a client can show.
    /// </summary>
    internal static JsonObject JournalView(TaskState task, IReadOnlyList<TaskMutationJournalEntry>? journal)
    {
        ArgumentNullException.ThrowIfNull(task);

        var entries = new JsonArray();
        foreach (var entry in journal ?? [])
        {
            var classification = MutationJournalPolicy.Classify(entry);
            entries.Add(new JsonObject
            {
                ["executionAttempt"] = entry.Intent.Key.ExecutionAttempt,
                ["stepIndex"] = entry.Intent.Key.StepIndex,
                ["sequence"] = entry.Sequence,
                ["tool"] = entry.Intent.ToolName,
                ["risk"] = entry.Intent.Risk.ToString(),
                ["argumentsFingerprint"] = Fingerprint(entry.Intent.ArgumentsHash),
                ["intentAtUtc"] = entry.Intent.IntentAtUtc.ToString("O", CultureInfo.InvariantCulture),
                ["plannedStepIndex"] = entry.Intent.PlannedStepIndex,
                ["planRevision"] = entry.Intent.PlanRevision,
                ["outcome"] = entry.Outcome is { } outcome
                    ? new JsonObject
                    {
                        ["kind"] = outcome.Kind.ToString(),
                        ["toolOutcome"] = outcome.ToolOutcome?.ToString(),
                        ["failureKind"] = outcome.ToolFailureKind?.ToString(),
                        ["verification"] = outcome.Verification?.ToString(),
                        ["atUtc"] = outcome.AtUtc.ToString("O", CultureInfo.InvariantCulture),
                    }
                    : null,
                ["state"] = entry.State.ToString(),
                ["knowledge"] = classification.Knowledge.ToString(),
                ["reconciliation"] = entry.Reconciliation is { } reconciliation
                    ? new JsonObject
                    {
                        ["action"] = reconciliation.Action.ToString(),
                        ["verification"] = reconciliation.Verification?.ToString(),
                        ["resolvedBy"] = reconciliation.ResolvedBy.DisplayName ?? reconciliation.ResolvedBy.Id,
                        ["atUtc"] = reconciliation.AtUtc.ToString("O", CultureInfo.InvariantCulture),
                    }
                    : null,
            });
        }

        return new JsonObject
        {
            ["mode"] = task.MutationJournalMode.ToString(),
            ["available"] = journal is not null,
            ["unsettledCount"] = journal?.Count(entry => MutationJournalPolicy.IsUnsettled(entry.State)) ?? 0,
            ["entries"] = entries,
        };
    }

    /// <summary>A summary that changes whenever an entry's state does: the count per state, in a fixed order.</summary>
    internal static string JournalSummary(IReadOnlyList<TaskMutationJournalEntry>? journal) =>
        journal is null
            ? "unavailable"
            : string.Join(',', Enum.GetValues<TaskMutationState>().Select(state => journal.Count(entry => entry.State == state)));

    private static string Fingerprint(string hash) => hash.Length > 12 ? hash[..12] : hash;

    private static List<ModelCallRecord> Strip(IReadOnlyList<ModelCallRecord> calls) =>
        [.. calls.Select(call => call with { RequestJson = null, ResponseJson = null })];
}
