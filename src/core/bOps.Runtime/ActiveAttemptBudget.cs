// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>A pausable active-work timer for one execution attempt.</summary>
internal sealed class ActiveAttemptBudget : IDisposable
{
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan? limit;
    private readonly CancellationTokenSource source = new();
    private ITimer? timer;
    private long resumedAt;
    private TimeSpan elapsed;
    private bool running;

    internal ActiveAttemptBudget(TimeProvider timeProvider, TimeSpan? limit)
    {
        this.timeProvider = timeProvider;
        this.limit = limit;
        Resume();
    }

    internal CancellationToken Token => source.Token;

    internal bool IsExpired => source.IsCancellationRequested;

    internal void Pause()
    {
        if (!running || limit is null || source.IsCancellationRequested)
        {
            return;
        }

        elapsed += timeProvider.GetElapsedTime(resumedAt);
        running = false;
        timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal void Resume()
    {
        if (running || limit is null || source.IsCancellationRequested)
        {
            return;
        }

        var remaining = limit.Value - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            source.Cancel();
            return;
        }

        resumedAt = timeProvider.GetTimestamp();
        running = true;
        timer ??= timeProvider.CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(), source,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        timer.Change(remaining, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        timer?.Dispose();
        source.Dispose();
    }
}

#pragma warning disable CA1032, CA1064 // Deliberately internal control-flow signals, never public exception contracts.
internal class AttemptDurationBudgetExceededException : OperationCanceledException
{
    internal AttemptDurationBudgetExceededException() : base("The execution attempt exhausted its active-duration budget.") { }
}

internal sealed class AttemptDurationStepInterruptedException(PlanStep step, string observation)
    : AttemptDurationBudgetExceededException
{
    internal PlanStep Step { get; } = step;
    internal string Observation { get; } = observation;
}

internal sealed class TokenBudgetCrossedException : Exception;
internal sealed class EvidenceReadLimitExceededException : Exception;
#pragma warning restore CA1032, CA1064
