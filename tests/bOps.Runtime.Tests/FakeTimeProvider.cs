// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime.Tests;

/// <summary>
/// A controllable <see cref="TimeProvider"/> for tests that assert on timestamps. Its timers fire when the clock is advanced
/// past their due time, so a deadline expressed as a <see cref="CancellationTokenSource"/> can be reached by a test.
/// </summary>
internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    // Elapsed time follows the fake clock too, so a test can decide how long a measured call "took".
    public override long GetTimestamp() => _now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;

        // A callback may create or dispose timers, so each due timer is taken out of the list before it is fired.
        while (true)
        {
            FakeTimer? due;
            lock (_timers)
            {
                due = _timers.Find(timer => timer.Due is { } at && at <= _now);
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
            lock (owner._timers)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                if (Due is not null && !owner._timers.Contains(this))
                {
                    owner._timers.Add(this);
                }
            }

            return true;
        }

        public void Disarm() => Due = null;

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._timers)
            {
                owner._timers.Remove(this);
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
