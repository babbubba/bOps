// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

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
    /// The task as a client sees it (ADR-0040 §9): every stored property without model bodies, the effective lifetime
    /// accounting (derived for a task stored before it was kept), whether this host is executing it, and whether an ordinary
    /// resume would be accepted now, with the refusal when not. <c>Running</c> and not <c>executing</c> is what a client may
    /// label "Interrupted"; it is not resumable.
    /// </summary>
    internal static JsonObject ToView(TaskState task, bool executing, TaskResumeDecision resume)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(resume);

        var view = JsonSerializer.SerializeToNode(WithoutModelPayloads(task), ViewJsonOptions)!.AsObject();
        view["accounting"] = JsonSerializer.SerializeToNode(TaskResumePolicy.EffectiveAccounting(task), ViewJsonOptions);
        view["executing"] = executing;
        view["resumable"] = resume.Resumable;
        view["resumeBlockedReason"] = resume.Refusal is { } refusal
            ? JsonSerializer.SerializeToNode(new TaskErrorResponse(refusal.Code, refusal.Message), ViewJsonOptions)
            : null;
        return view;
    }

    private static List<ModelCallRecord> Strip(IReadOnlyList<ModelCallRecord> calls) =>
        [.. calls.Select(call => call with { RequestJson = null, ResponseJson = null })];
}
