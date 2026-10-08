// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>ADR-0049 section 4: a refresh runs at most the configured number of checks at once, each exactly once, in a deterministic result order.</summary>
public sealed class PrerequisiteRefreshConcurrencyTests
{
    private static readonly PackageId Package = new("package.sample");

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task Refresh_NeverRunsMoreChecksThanTheConfiguredMaximum_AndRunsEachOnce(int maximum)
    {
        var registry = new PrerequisiteRegistry(TimeProvider.System, TimeSpan.Zero, maximum);
        var gate = new Gate(maximum);
        var checks = Enumerable.Range(0, 20).Select(i => new GatedCheck($"sample.c{i:00}", gate)).ToArray();
        foreach (var check in checks)
        {
            registry.Register(Package, check);
        }

        var refresh = registry.RefreshAsync();

        // The workers fill up to the maximum and no further: wait until that many are inside, then give a 4th a chance to (wrongly) enter.
        await gate.Saturated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(150);
        Assert.Equal(maximum, gate.Current);
        gate.Open.SetResult();
        var results = await refresh.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(maximum, gate.Max);
        Assert.All(checks, check => Assert.Equal(1, check.Calls));
        Assert.Equal(checks.Select(check => check.Descriptor.Id), results.Select(result => result.Id));
    }

    [Fact]
    public async Task AThrowingAndATimedOutCheck_DoNotStopTheOthers()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));
        var registry = new PrerequisiteRegistry(time, TimeSpan.Zero, 2);
        var ok = new PrerequisiteRegistryTests.FakeCheck("sample.a-ok", PrerequisiteState.Available);
        var later = new PrerequisiteRegistryTests.FakeCheck("sample.z-ok", PrerequisiteState.Available);
        registry.Register(Package, ok);
        registry.Register(Package, new ThrowingCheck("sample.b-throws"));
        registry.Register(Package, new HangingCheck("sample.c-hangs"));
        registry.Register(Package, later);

        var refresh = registry.RefreshAsync();
        while (!refresh.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(11));
            await Task.Delay(10);
        }

        var results = await refresh;

        Assert.Equal(["sample.a-ok", "sample.b-throws", "sample.c-hangs", "sample.z-ok"], results.Select(result => result.Id));
        Assert.Equal(
            [PrerequisiteState.Available, PrerequisiteState.Error, PrerequisiteState.Error, PrerequisiteState.Available],
            results.Select(result => result.State));
        Assert.Equal(PrerequisiteCodes.CheckFailed, results[1].Code);
        Assert.Equal(PrerequisiteCodes.CheckTimeout, results[2].Code);
        Assert.Equal(1, later.Calls);
    }

    [Fact]
    public async Task CallerCancellation_StopsTheRefresh()
    {
        var registry = new PrerequisiteRegistry(TimeProvider.System, TimeSpan.Zero, 2);
        var gate = new Gate(2);
        for (var i = 0; i < 6; i++)
        {
            registry.Register(Package, new GatedCheck($"sample.c{i}", gate));
        }

        using var cancellation = new CancellationTokenSource();
        var refresh = registry.RefreshAsync(cancellation.Token);
        await gate.Saturated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        gate.Open.TrySetResult();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(PrerequisiteRegistry.MaxConcurrencyLimit + 1)]
    public void TheConcurrencyBound_IsEnforced(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrerequisiteRegistry(TimeProvider.System, TimeSpan.Zero, value));

    [Fact]
    public void OptionsOutsideTheirRange_FailValidation()
    {
        Assert.Equal(4, new PrerequisiteOptions().Validate().MaxConcurrency);
        Assert.Throws<InvalidOperationException>(() => new PrerequisiteOptions { MaxConcurrency = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PrerequisiteOptions { MaxConcurrency = 33 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PrerequisiteOptions { RefreshIntervalSeconds = 1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PrerequisiteOptions { RefreshIntervalSeconds = 7200 }.Validate());
    }

    private sealed class Gate(int saturation)
    {
        private int _current;
        private int _max;

        public TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Saturated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Current => Volatile.Read(ref _current);

        public int Max => Volatile.Read(ref _max);

        public async Task EnterAsync(CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _current);
            int seen;
            while ((seen = Volatile.Read(ref _max)) < now && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }

            if (now >= saturation)
            {
                Saturated.TrySetResult();
            }

            try
            {
                await Open.Task.WaitAsync(ct);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }

    private sealed class GatedCheck(string id, Gate gate) : IPrerequisiteCheck
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public PrerequisiteDescriptor Descriptor { get; } = new(id, "Gated", "Waits at a gate.");

        public async Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            await gate.EnterAsync(ct);
            return new PrerequisiteCheckOutcome(PrerequisiteState.Available, "available", "Fine.");
        }
    }

    private sealed class ThrowingCheck(string id) : IPrerequisiteCheck
    {
        public PrerequisiteDescriptor Descriptor { get; } = new(id, "Throws", "Always throws.");

        public Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("secret connection string");
    }

    private sealed class HangingCheck(string id) : IPrerequisiteCheck
    {
        public PrerequisiteDescriptor Descriptor { get; } = new(id, "Hangs", "Never returns.") { CheckTimeout = TimeSpan.FromSeconds(5) };

        public Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default) => new TaskCompletionSource<PrerequisiteCheckOutcome>().Task;
    }
}
