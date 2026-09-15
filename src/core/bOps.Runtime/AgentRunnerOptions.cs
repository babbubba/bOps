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
}
