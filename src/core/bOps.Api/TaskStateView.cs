// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Api;

/// <summary>
/// The shape of a task the API sends to a client. The task store keeps, for troubleshooting, the exact request and
/// reply bodies of every model call; a request repeats the whole conversation, and the events stream sends the full
/// task at every step, so those bodies stay in the store and never travel here. Which model answered, how long it
/// took and the tokens do.
/// </summary>
internal static class TaskStateView
{
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

    private static List<ModelCallRecord> Strip(IReadOnlyList<ModelCallRecord> calls) =>
        [.. calls.Select(call => call with { RequestJson = null, ResponseJson = null })];
}
