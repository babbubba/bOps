// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// The workflow run, agent and invocation the step being executed belongs to (C01-R23). The runner opens it around the one step
/// pipeline of a workflow tool call and reads it where it writes an audit event, so every event of that step carries the
/// correlation without any call site having to remember it. Internal: only the runtime opens one, so a model or a package cannot
/// attach a correlation of its own choosing.
/// </summary>
internal static class WorkflowStepScope
{
    private static readonly AsyncLocal<WorkflowCorrelation?> CurrentValue = new();

    /// <summary>The correlation of the step in progress, or <c>null</c> outside a workflow step.</summary>
    internal static WorkflowCorrelation? Current => CurrentValue.Value;

    /// <summary>Makes <paramref name="correlation"/> current until the returned scope is disposed.</summary>
    internal static IDisposable Begin(WorkflowCorrelation correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        var previous = CurrentValue.Value;
        CurrentValue.Value = correlation;
        return new Restore(previous);
    }

    private sealed class Restore(WorkflowCorrelation? previous) : IDisposable
    {
        public void Dispose() => CurrentValue.Value = previous;
    }
}
