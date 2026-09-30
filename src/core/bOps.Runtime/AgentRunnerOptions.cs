// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// Budgets and timeouts for one run of the agent loop. All configurable — never hardcoded
/// constants (agentic/01-architecture-rules.md, rule C5).
/// </summary>
public sealed record AgentRunnerOptions
{
    /// <summary>
    /// The per-execution-attempt step budget (ADR-0040 §5): an anti-infinite-loop guard-rail. Exhausting it ends the attempt
    /// as <c>MaxStepsReached</c>; a resume starts a fresh per-attempt budget, capped by <see cref="MaxLifetimeSteps"/>.
    /// </summary>
    public int MaxSteps { get; init; } = 15;

    /// <summary>
    /// The task-lifetime step budget across every execution attempt (ADR-0040 §5). It always wins over <see cref="MaxSteps"/>:
    /// an attempt runs at most the lifetime remainder, and a task that has used it up cannot be resumed. Must not be below
    /// <see cref="MaxSteps"/>, so a single execution attempt behaves exactly as without it.
    /// </summary>
    public int MaxLifetimeSteps { get; init; } = 60;

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
    /// An optional total token budget across the whole task — cumulative over every execution attempt and never reset by a
    /// resume (ADR-0040 §5). <c>null</c> (the V0.1 default)
    /// means unbounded — the mechanism exists from V0.1 per rule C5, even though no default
    /// limit is imposed until an operator configures one.
    /// </summary>
    public int? MaxTotalTokens { get; init; }

    /// <summary>
    /// How many times one execution attempt may replan before ending as <c>ReplanLimitReached</c> instead of continuing to
    /// retry (agentic/01-architecture-rules.md, rule C8; ADR-0040 §5) — the same anti-runaway guard-rail rule C5 already
    /// requires for steps, applied to plan revisions.
    /// </summary>
    public int MaxReplans { get; init; } = 3;

    /// <summary>The task-lifetime replan budget across every execution attempt (ADR-0040 §5); it always wins over <see cref="MaxReplans"/>, and must not be below it.</summary>
    public int MaxLifetimeReplans { get; init; } = 12;

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

    /// <summary>
    /// How many attempts one logical model call may make (ADR-0039). Only a failure the provider classified as
    /// transient, rate-limited, timed out or unreachable is tried again; <c>1</c> disables retry.
    /// </summary>
    public int ModelCallMaxAttempts { get; init; } = 3;

    /// <summary>
    /// How long one model call attempt may run before the runtime abandons it as a <c>Timeout</c> failure — distinct from
    /// the task's own cancellation. Must be shorter than the provider's transport timeout, so this one normally fires first.
    /// </summary>
    public TimeSpan ModelCallAttemptTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The wall-clock cap for one logical model call, every attempt and every wait between them included. It always wins:
    /// no attempt runs past it and no wait is started that would not leave room for another attempt.
    /// </summary>
    public TimeSpan ModelCallBudget { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>The first backoff delay before retrying a model call; each later retry doubles it, up to <see cref="ModelRetryMaxDelay"/>, with jitter.</summary>
    public TimeSpan ModelRetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest backoff delay, and the longest provider <c>Retry-After</c> honoured; a longer one ends the call instead.</summary>
    public TimeSpan ModelRetryMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> naming the configuration keys when the budgets or the model-call settings are not
    /// coherent. A host that knows the provider's transport timeout passes it, so an attempt timeout that could never fire
    /// first is rejected at start-up rather than discovered in production.
    /// </summary>
    /// <param name="providerRequestTimeout">The provider's effective transport timeout, when the caller knows it.</param>
    public void Validate(TimeSpan? providerRequestTimeout = null)
    {
        if (MaxSteps < 1)
        {
            throw new InvalidOperationException("'Agent:MaxSteps' must be at least 1.");
        }

        if (MaxLifetimeSteps < MaxSteps)
        {
            throw new InvalidOperationException(
                $"'Agent:MaxLifetimeSteps' ({MaxLifetimeSteps}) must not be below 'Agent:MaxSteps' ({MaxSteps}).");
        }

        if (MaxReplans < 0)
        {
            throw new InvalidOperationException("'Agent:MaxReplans' must not be negative.");
        }

        if (MaxLifetimeReplans < MaxReplans)
        {
            throw new InvalidOperationException(
                $"'Agent:MaxLifetimeReplans' ({MaxLifetimeReplans}) must not be below 'Agent:MaxReplans' ({MaxReplans}).");
        }

        if (ModelCallMaxAttempts < 1)
        {
            throw new InvalidOperationException("'Agent:ModelCallMaxAttempts' must be at least 1.");
        }

        if (ModelCallAttemptTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("'Agent:ModelCallAttemptTimeout' must be positive.");
        }

        if (ModelCallBudget <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("'Agent:ModelCallBudget' must be positive.");
        }

        if (ModelRetryBaseDelay < TimeSpan.Zero)
        {
            throw new InvalidOperationException("'Agent:ModelRetryBaseDelay' must not be negative.");
        }

        if (ModelRetryMaxDelay < ModelRetryBaseDelay)
        {
            throw new InvalidOperationException("'Agent:ModelRetryMaxDelay' must not be shorter than 'Agent:ModelRetryBaseDelay'.");
        }

        if (providerRequestTimeout is { } transport && ModelCallAttemptTimeout >= transport)
        {
            throw new InvalidOperationException(
                $"'Agent:ModelCallAttemptTimeout' ({ModelCallAttemptTimeout}) must be shorter than the provider transport timeout " +
                $"'ModelProvider:RequestTimeout' ({transport}), so the runtime's attempt timeout fires first.");
        }
    }
}
