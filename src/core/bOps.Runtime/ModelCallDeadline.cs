// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// The single monotonic deadline of one logical model call (ADR-0039 <c>ModelCallBudget</c>). A broker creates it when the
/// logical call starts; every inference the call causes - context projection, summary inference and its retries, the primary
/// attempts, their retry delays and any fallback - reads its remaining window from it, so none of them can run past it.
/// A nested broker that serves part of a parent call links the parent deadline, and the more restrictive of the two wins.
/// </summary>
internal sealed class ModelCallDeadline
{
    private readonly TimeProvider clock;
    private readonly long startedAt;
    private readonly TimeSpan budget;
    private readonly ModelCallDeadline? inherited;

    /// <summary>Starts the deadline now, on <paramref name="clock"/>, optionally bounded by an enclosing logical call.</summary>
    internal ModelCallDeadline(TimeProvider clock, TimeSpan budget, ModelCallDeadline? inherited = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
        this.budget = budget;
        this.inherited = inherited;
        startedAt = clock.GetTimestamp();
    }

    /// <summary>What is left before this call, or any enclosing call, runs out of budget. May be negative.</summary>
    internal TimeSpan Remaining
    {
        get
        {
            var own = budget - clock.GetElapsedTime(startedAt);
            if (inherited is null)
            {
                return own;
            }

            var enclosing = inherited.Remaining;
            return enclosing < own ? enclosing : own;
        }
    }
}
