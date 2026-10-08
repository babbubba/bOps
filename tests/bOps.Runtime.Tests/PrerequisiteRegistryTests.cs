// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>ADR-0049 section 4: the host-owned prerequisite registry and its boolean <see cref="ICapabilityProbe"/> view.</summary>
public sealed class PrerequisiteRegistryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly PackageId PackageA = new("package.a");
    private static readonly PackageId PackageB = new("package.b");

    [Fact]
    public async Task UnregisteredId_IsUnknownAndNotAvailable()
    {
        var registry = NewRegistry(out _);

        Assert.False(await registry.IsAvailableAsync("sample.missing"));
        var result = await registry.CheckAsync("sample.missing");
        Assert.Equal(PrerequisiteState.Unknown, result.State);
        Assert.Equal(PrerequisiteCodes.NotRegistered, result.Code);
        Assert.Equal(PrerequisiteState.Unknown, registry.GetState("sample.missing"));
        Assert.Null(registry.GetLastResult("sample.missing"));
    }

    [Theory]
    [InlineData(PrerequisiteState.Available, true)]
    [InlineData(PrerequisiteState.Degraded, true)]
    [InlineData(PrerequisiteState.Unavailable, false)]
    [InlineData(PrerequisiteState.Error, false)]
    public async Task IsAvailable_IsTheCompatibilityBooleanView(PrerequisiteState state, bool expected)
    {
        var registry = NewRegistry(out _);
        registry.Register(PackageA, new FakeCheck("sample.x", state));

        Assert.Equal(expected, await registry.IsAvailableAsync("sample.x"));
        Assert.Equal(state, registry.GetState("sample.x"));
    }

    [Fact]
    public async Task Result_IsStampedByTheHostWithDescriptorIdAndTime()
    {
        var registry = NewRegistry(out var time);
        time.Advance(TimeSpan.FromMinutes(3));
        var metadata = OperationalMetadata.From(new JsonObject { ["requiredExecutable"] = "kd.exe" });
        registry.Register(PackageA, new FakeCheck("windows.debugger.kd", PrerequisiteState.Unavailable, "executable-not-found", metadata));

        var result = await registry.CheckAsync("windows.debugger.kd");

        Assert.Equal("windows.debugger.kd", result.Id);
        Assert.Equal(Start.AddMinutes(3), result.CheckedAtUtc);
        Assert.Equal("executable-not-found", result.Code);
        Assert.Equal(metadata, result.Metadata);
        Assert.Same(result, registry.GetLastResult("windows.debugger.kd"));
    }

    [Fact]
    public async Task IsAvailable_ReusesAResultWithinTheCacheDuration_AndChecksAgainAfter()
    {
        var registry = NewRegistry(out var time);
        var check = new FakeCheck("sample.x", PrerequisiteState.Available);
        registry.Register(PackageA, check);

        await registry.IsAvailableAsync("sample.x");
        check.State = PrerequisiteState.Unavailable;
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.True(await registry.IsAvailableAsync("sample.x"));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await registry.IsAvailableAsync("sample.x"));
        Assert.Equal(2, check.Calls);
    }

    [Fact]
    public async Task ThrowingCheck_IsAnError_WithoutTheExceptionText()
    {
        var registry = NewRegistry(out _);
        registry.Register(PackageA, new ThrowingCheck("sample.x"));

        var result = await registry.CheckAsync("sample.x");

        Assert.Equal(PrerequisiteState.Error, result.State);
        Assert.Equal(PrerequisiteCodes.CheckFailed, result.Code);
        Assert.DoesNotContain("hunter2", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", result.Metadata.ToString(), StringComparison.Ordinal);
        Assert.True(result.Metadata.TryGetString("exceptionType", out var type));
        Assert.Equal(typeof(InvalidOperationException).FullName, type);
        Assert.False(await registry.IsAvailableAsync("sample.x"));
    }

    [Fact]
    public async Task HangingCheck_ThatIgnoresItsToken_TimesOutAsAnError()
    {
        var registry = NewRegistry(out var time);
        registry.Register(PackageA, new HangingCheck(new PrerequisiteDescriptor("sample.x", "Sample", "Hangs.") { CheckTimeout = TimeSpan.FromSeconds(5) }));

        var pending = registry.CheckAsync("sample.x");
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(5));
        var result = await pending;

        Assert.Equal(PrerequisiteState.Error, result.State);
        Assert.Equal(PrerequisiteCodes.CheckTimeout, result.Code);
    }

    [Fact]
    public async Task CheckTimeout_IsClampedToTheMinimum()
    {
        var registry = NewRegistry(out var time);
        registry.Register(PackageA, new HangingCheck(new PrerequisiteDescriptor("sample.x", "Sample", "Hangs.") { CheckTimeout = TimeSpan.Zero }));

        var pending = registry.CheckAsync("sample.x");
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(PrerequisiteCodes.CheckTimeout, (await pending).Code);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var registry = NewRegistry(out _);
        registry.Register(PackageA, new HangingCheck(new PrerequisiteDescriptor("sample.x", "Sample", "Hangs.")));
        using var cts = new CancellationTokenSource();

        var pending = registry.CheckAsync("sample.x", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(registry.GetLastResult("sample.x"));
    }

    public static TheoryData<PrerequisiteCheckOutcome?> InvalidOutcomes => new()
    {
        null,
        new PrerequisiteCheckOutcome(PrerequisiteState.Unknown, "unknown", "Unknown."),
        new PrerequisiteCheckOutcome((PrerequisiteState)42, "odd", "Odd."),
        new PrerequisiteCheckOutcome(PrerequisiteState.Available, "Not A Code", "Bad code."),
        new PrerequisiteCheckOutcome(PrerequisiteState.Available, "available", " "),
        new PrerequisiteCheckOutcome(PrerequisiteState.Available, "available", new string('m', PrerequisiteCheckOutcome.MaxMessageLength + 1)),
    };

    [Theory]
    [MemberData(nameof(InvalidOutcomes))]
    public async Task InvalidOutcome_IsRecordedAsAnError(PrerequisiteCheckOutcome? outcome)
    {
        var registry = NewRegistry(out _);
        registry.Register(PackageA, new FixedCheck("sample.x", outcome));

        var result = await registry.CheckAsync("sample.x");

        Assert.Equal(PrerequisiteState.Error, result.State);
        Assert.Equal(PrerequisiteCodes.CheckInvalidResult, result.Code);
    }

    [Fact]
    public async Task DuplicateId_FromAnotherPackage_IsRefused_AndTheFirstCheckStays()
    {
        var registry = NewRegistry(out _);
        registry.Register(PackageA, new FakeCheck("sample.x", PrerequisiteState.Available));

        var error = Assert.Throws<PrerequisiteRegistrationException>(() =>
            registry.Register(PackageB, new FakeCheck("sample.x", PrerequisiteState.Unavailable)));

        Assert.Contains("package.a", error.Message, StringComparison.Ordinal);
        Assert.True(await registry.IsAvailableAsync("sample.x"));
        Assert.Equal(PackageA, Assert.Single(registry.GetRegistrations()).Package);
    }

    [Fact]
    public void ProviderRegistration_IsAtomic()
    {
        var registry = NewRegistry(out _);

        Assert.Throws<PrerequisiteRegistrationException>(() => registry.Register(PackageA,
            new FakeProvider(new FakeCheck("sample.a", PrerequisiteState.Available), new FakeCheck("sample.a", PrerequisiteState.Available))));
        Assert.Throws<PrerequisiteRegistrationException>(() => registry.Register(PackageA,
            new FakeProvider(new FakeCheck("sample.b", PrerequisiteState.Available), null!)));
        var thrown = Assert.Throws<PrerequisiteRegistrationException>(() => registry.Register(PackageA, new ThrowingProvider()));
        Assert.IsType<InvalidOperationException>(thrown.InnerException);
        Assert.Throws<PrerequisiteRegistrationException>(() => registry.Register(new PackageId(" "), new FakeCheck("sample.c", PrerequisiteState.Available)));

        Assert.Empty(registry.GetRegistrations());

        registry.Register(PackageA, new FakeProvider(new FakeCheck("sample.z", PrerequisiteState.Available), new FakeCheck("sample.m", PrerequisiteState.Available)));
        Assert.Equal(["sample.m", "sample.z"], registry.GetRegistrations().Select(registration => registration.Descriptor.Id));
    }

    [Fact]
    public void DescriptorAlteredAfterConstruction_IsRevalidated()
    {
        var registry = NewRegistry(out _);
        var valid = new PrerequisiteDescriptor("sample.x", "Sample", "Valid.");

        Assert.Throws<PrerequisiteRegistrationException>(() => registry.Register(PackageA, new DescriptorCheck(valid with { Id = "Bad Id" })));
        Assert.Throws<PrerequisiteRegistrationException>(() => registry.Register(PackageA, new DescriptorCheck(valid with { DisplayName = "" })));
        Assert.Throws<PrerequisiteRegistrationException>(() => registry.Register(PackageA,
            new DescriptorCheck(valid with { Remediation = new string('r', PrerequisiteDescriptor.MaxTextLength + 1) })));
    }

    [Fact]
    public async Task Unregister_RemovesOnlyThatPackagesChecksAndResults()
    {
        var registry = NewRegistry(out _);
        registry.Register(PackageA, new FakeCheck("sample.a", PrerequisiteState.Available));
        registry.Register(PackageB, new FakeCheck("sample.b", PrerequisiteState.Available));
        await registry.RefreshAsync();

        registry.Unregister(PackageA);

        Assert.Null(registry.GetLastResult("sample.a"));
        Assert.False(await registry.IsAvailableAsync("sample.a"));
        Assert.True(await registry.IsAvailableAsync("sample.b"));
        registry.Register(PackageB, new FakeCheck("sample.a", PrerequisiteState.Available));
    }

    [Fact]
    public async Task Refresh_ChecksEverything_OrderedById()
    {
        var registry = NewRegistry(out _);
        registry.Register(PackageA, new FakeCheck("sample.z", PrerequisiteState.Unavailable));
        registry.Register(PackageB, new FakeCheck("sample.a", PrerequisiteState.Available));

        var results = await registry.RefreshAsync();

        Assert.Equal(["sample.a", "sample.z"], results.Select(result => result.Id));
        Assert.Equal([PrerequisiteState.Available, PrerequisiteState.Unavailable], results.Select(result => result.State));
    }

    [Fact]
    public async Task BooleanCheck_AdaptsAnExistingCapabilityCheck()
    {
        var registry = NewRegistry(out _);
        var present = true;
        registry.Register(PackageA, new BooleanPrerequisiteCheck(
            new PrerequisiteDescriptor("sample.daemon", "Sample daemon", "A local daemon.", PrerequisiteKind.Service),
            _ => Task.FromResult(present)));

        var available = await registry.CheckAsync("sample.daemon");
        present = false;
        var unavailable = await registry.CheckAsync("sample.daemon");

        Assert.Equal((PrerequisiteState.Available, PrerequisiteCodes.Available), (available.State, available.Code));
        Assert.Equal((PrerequisiteState.Unavailable, PrerequisiteCodes.Unavailable), (unavailable.State, unavailable.Code));
        Assert.Contains("Sample daemon", unavailable.Message, StringComparison.Ordinal);
    }

    private static PrerequisiteRegistry NewRegistry(out FakeTimeProvider time)
    {
        time = new FakeTimeProvider(Start);
        return new PrerequisiteRegistry(time, TimeSpan.FromSeconds(30));
    }

    internal sealed class FakeCheck(string id, PrerequisiteState state, string? code = null, OperationalMetadata? metadata = null) : IPrerequisiteCheck
    {
        public PrerequisiteState State { get; set; } = state;

        public string? Code { get; set; } = code;

        public int Calls { get; private set; }

        public PrerequisiteDescriptor Descriptor { get; } = new(id, "Sample prerequisite", "A test prerequisite.");

        public Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new PrerequisiteCheckOutcome(State, Code ?? State.ToString().ToLowerInvariant(), $"The prerequisite is {State}.")
            {
                Metadata = metadata ?? OperationalMetadata.Empty,
            });
        }
    }

    private sealed class FixedCheck(string id, PrerequisiteCheckOutcome? outcome) : IPrerequisiteCheck
    {
        public PrerequisiteDescriptor Descriptor { get; } = new(id, "Sample", "Returns a fixed outcome.");

        public Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default) => Task.FromResult(outcome!);
    }

    private sealed class DescriptorCheck(PrerequisiteDescriptor descriptor) : IPrerequisiteCheck
    {
        public PrerequisiteDescriptor Descriptor { get; } = descriptor;

        public Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default) =>
            Task.FromResult(new PrerequisiteCheckOutcome(PrerequisiteState.Available, "available", "Available."));
    }

    private sealed class ThrowingCheck(string id) : IPrerequisiteCheck
    {
        public PrerequisiteDescriptor Descriptor { get; } = new(id, "Sample", "Throws.");

        public Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Server=db;Password=hunter2");
    }

    private sealed class HangingCheck(PrerequisiteDescriptor descriptor) : IPrerequisiteCheck
    {
        public PrerequisiteDescriptor Descriptor { get; } = descriptor;

        public Task<PrerequisiteCheckOutcome> CheckAsync(CancellationToken ct = default) =>
            new TaskCompletionSource<PrerequisiteCheckOutcome>().Task;
    }

    private sealed class FakeProvider(params IPrerequisiteCheck[] checks) : IPrerequisiteProvider
    {
        public IReadOnlyList<IPrerequisiteCheck> GetPrerequisiteChecks() => checks;
    }

    private sealed class ThrowingProvider : IPrerequisiteProvider
    {
        public IReadOnlyList<IPrerequisiteCheck> GetPrerequisiteChecks() => throw new InvalidOperationException("broken provider");
    }
}
