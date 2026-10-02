// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>
/// One call's time budget for an evidence collector that reads several sources one after another (HARDEN-7, review note R7). The
/// total stays below the runner's tool timeout, so a slow source turns into partial evidence instead of an escaped timeout that loses
/// everything; and each source gets a slice of what is left — the remaining time divided by the sources still to read, optionally
/// capped — so one busy or hung source cannot starve the ones after it. Time not used by a source rolls over to the next ones.
/// </summary>
public sealed class EvidenceTimeBudget
{
    private readonly TimeProvider clock;
    private readonly long started;

    /// <summary>Starts a budget of <paramref name="total"/> on <paramref name="clock"/>.</summary>
    public EvidenceTimeBudget(TimeSpan total, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(total, TimeSpan.Zero);
        Total = total;
        this.clock = clock;
        started = clock.GetTimestamp();
    }

    /// <summary>The whole budget of the call.</summary>
    public TimeSpan Total { get; }

    /// <summary>The time not yet used, never negative.</summary>
    public TimeSpan Remaining
    {
        get
        {
            var left = Total - clock.GetElapsedTime(started);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// The time the next source may use: what is left divided by <paramref name="sourcesLeft"/> (this one included), and at most
    /// <paramref name="cap"/> when one is given.
    /// </summary>
    public TimeSpan Slice(int sourcesLeft, TimeSpan? cap = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourcesLeft, 1);
        var share = Remaining / sourcesLeft;
        return cap is { } maximum && maximum < share ? maximum : share;
    }

    /// <summary>
    /// Starts the slice of the next source: a token cancelled when the slice runs out or when <paramref name="outer"/> is cancelled.
    /// </summary>
    public EvidenceBudgetSlice Start(int sourcesLeft, CancellationToken outer, TimeSpan? cap = null) =>
        new(Slice(sourcesLeft, cap), clock, outer);
}

/// <summary>The time slice of one source within an <see cref="EvidenceTimeBudget"/>.</summary>
public sealed class EvidenceBudgetSlice : IDisposable
{
    private readonly CancellationTokenSource timeout;
    private readonly CancellationTokenSource linked;
    private readonly CancellationToken outer;

    internal EvidenceBudgetSlice(TimeSpan length, TimeProvider clock, CancellationToken outer)
    {
        Length = length;
        this.outer = outer;
        timeout = new CancellationTokenSource(length, clock);
        linked = CancellationTokenSource.CreateLinkedTokenSource(outer, timeout.Token);
        if (length <= TimeSpan.Zero)
        {
            timeout.Cancel();
        }
    }

    /// <summary>How long this source may run.</summary>
    public TimeSpan Length { get; }

    /// <summary>Cancelled when the slice runs out or the caller cancels.</summary>
    public CancellationToken Token => linked.Token;

    /// <summary>True when the slice ran out while the caller had not cancelled: the source stopped on the time bound.</summary>
    public bool TimedOut => timeout.IsCancellationRequested && !outer.IsCancellationRequested;

    /// <inheritdoc />
    public void Dispose()
    {
        linked.Dispose();
        timeout.Dispose();
    }
}
