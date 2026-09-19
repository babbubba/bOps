// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>Why a role was stopped by its budget (ADR-0030 section 6). Distinct from a denial of authority (ADR-0031 section 3).</summary>
internal enum BudgetStop
{
    /// <summary>The role has taken every step it was granted.</summary>
    Steps,

    /// <summary>The role has consumed more model tokens than it was granted.</summary>
    Tokens,

    /// <summary>The role's deadline has been reached.</summary>
    Deadline,
}

/// <summary>
/// What one role of a delegated run has spent, counted against what its envelope granted: steps, model tokens and the
/// deadline (ADR-0030 section 6). The runner charges it at the two places a role acts, the start of a step and the end of
/// a model call, so a delegated role cannot spend what it was not given; the orchestrator reads it when the role ends
/// to reconcile the run's budget.
/// </summary>
/// <remarks>
/// It starts from a prior <see cref="BudgetConsumption"/>, so a role restarted after an interruption carries on from what
/// it had already spent and never gets its budget back. It is used by one role at a time and is not thread-safe.
/// </remarks>
internal sealed class RoleMeter
{
    private readonly DelegationBudget _budget;
    private long _tokens;

    internal RoleMeter(DelegationBudget budget, BudgetConsumption? consumed = null)
    {
        ArgumentNullException.ThrowIfNull(budget);

        _budget = budget;
        Steps = consumed?.Steps ?? 0;
        _tokens = consumed?.Tokens ?? 0;
    }

    /// <summary>Steps taken so far.</summary>
    internal int Steps { get; private set; }

    /// <summary>What the role has spent.</summary>
    internal BudgetConsumption Consumed => new(Steps, (int)Math.Min(_tokens, int.MaxValue));

    /// <summary>The first reason the role was stopped by its budget, or <c>null</c> while it has not been.</summary>
    internal BudgetStop? Stopped { get; private set; }

    /// <summary>A side-effecting step that was running when the role was cancelled, so whether it took effect is unknown. <c>null</c> otherwise.</summary>
    internal string? UnknownOutcome { get; private set; }

    /// <summary>
    /// Charges the start of one step. Returns <c>null</c> and counts the step when the role may take it, otherwise why it may
    /// not, and counts nothing. The deadline is expired at the instant itself, not only after it.
    /// </summary>
    /// <param name="now">The current instant.</param>
    internal BudgetStop? BeginStep(DateTimeOffset now)
    {
        if (Check(now) is { } stop)
        {
            return stop;
        }

        if (Steps >= _budget.MaxSteps)
        {
            return Stop(BudgetStop.Steps);
        }

        Steps++;
        return null;
    }

    /// <summary>Adds the tokens of one model call to what the role has spent.</summary>
    /// <param name="tokens">Prompt and completion tokens of the call. Never negative.</param>
    internal void AddTokens(int tokens) => _tokens += Math.Max(0, tokens);

    /// <summary>
    /// Whether the role has used up its token budget or its deadline. A budget of exactly the tokens spent is not exceeded:
    /// a call cannot be stopped half way, so the overshoot of the last call is what this reports.
    /// </summary>
    /// <param name="now">The current instant.</param>
    internal BudgetStop? Check(DateTimeOffset now)
    {
        if (Stopped is { } already)
        {
            return already;
        }

        if (now >= _budget.DeadlineUtc)
        {
            return Stop(BudgetStop.Deadline);
        }

        return _tokens > _budget.MaxTokens ? Stop(BudgetStop.Tokens) : null;
    }

    /// <summary>Records that a side-effecting step was interrupted, so its outcome is unknown (ADR-0030 section 6).</summary>
    /// <param name="description">Which step, for the run's explanation.</param>
    internal void MarkUnknownOutcome(string description) => UnknownOutcome ??= description;

    private BudgetStop Stop(BudgetStop reason)
    {
        Stopped ??= reason;
        return Stopped.Value;
    }
}
