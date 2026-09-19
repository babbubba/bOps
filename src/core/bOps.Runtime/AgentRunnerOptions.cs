// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// Budgets and timeouts for one run of the agent loop. All configurable — never hardcoded
/// constants (agentic/01-architecture-rules.md, rule C5).
/// </summary>
public sealed record AgentRunnerOptions
{
    /// <summary>The step budget: an anti-infinite-loop guard-rail. Exceeding it ends the task as <c>MaxStepsReached</c>.</summary>
    public int MaxSteps { get; init; } = 15;

    /// <summary>How long a single tool execution may run before it is cancelled and reported as <c>Timeout</c> (rule S7).</summary>
    public TimeSpan DefaultToolTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The maximum characters of tool output kept in history before deterministic head+tail truncation (rule C3).</summary>
    public int MaxObservationCharacters { get; init; } = 4000;

    /// <summary>
    /// How many consecutive policy denials of the *same* tool end the task as <c>PolicyBlocked</c>
    /// (rule C4) instead of letting the model retry a forbidden tool until <see cref="MaxSteps"/>.
    /// </summary>
    public int MaxConsecutivePolicyDenials { get; init; } = 2;

    /// <summary>
    /// An optional total token budget across the whole task. <c>null</c> (the V0.1 default)
    /// means unbounded — the mechanism exists from V0.1 per rule C5, even though no default
    /// limit is imposed until an operator configures one.
    /// </summary>
    public int? MaxTotalTokens { get; init; }

    /// <summary>
    /// How many times the task may replan before ending as <c>ReplanLimitReached</c> instead of
    /// continuing to retry (agentic/01-architecture-rules.md, rule C8) — the same anti-runaway
    /// guard-rail rule C5 already requires for steps, applied to plan revisions.
    /// </summary>
    public int MaxReplans { get; init; } = 3;

    /// <summary>
    /// How many times a model that ends a step with no text and no tool call (an empty final response) is asked
    /// again before the task fails instead of completing with nothing to show. <c>0</c> fails at the first empty
    /// reply. Rule S3: an empty reply is never taken as an answer.
    /// </summary>
    public int EmptyFinalResponseRetries { get; init; } = 1;

    /// <summary>
    /// The most characters of each request body and each reply body kept with a recorded model call; a longer body is
    /// cut and the record marked. <c>0</c> keeps no bodies (the model, timing and tokens are still recorded). The
    /// request repeats the whole conversation on every call, so this bounds how fast a task's stored state grows.
    /// </summary>
    public int MaxModelPayloadCharacters { get; init; } = 200_000;
}
