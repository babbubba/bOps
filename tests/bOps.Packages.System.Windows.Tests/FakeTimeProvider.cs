// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.System.Windows.Tests;

/// <summary>
/// A controllable <see cref="TimeProvider"/>: its timers fire when the clock is advanced past their due time, so a time-budget slice
/// (a <see cref="CancellationTokenSource"/> on this clock) can be exhausted by a test without sleeping.
/// </summary>
internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly List<FakeTimer> timers = [];
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public override long GetTimestamp() => now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        now += by;
        while (true)
        {
            FakeTimer? due;
            lock (timers)
            {
                due = timers.Find(timer => timer.Due is { } at && at <= now);
                due?.Disarm();
            }

            if (due is null)
            {
                return;
            }

            due.Fire();
        }
    }

    private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.timers)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
                if (Due is not null && !owner.timers.Contains(this))
                {
                    owner.timers.Add(this);
                }
            }

            return true;
        }

        public void Disarm() => Due = null;

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner.timers)
            {
                owner.timers.Remove(this);
                Due = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
