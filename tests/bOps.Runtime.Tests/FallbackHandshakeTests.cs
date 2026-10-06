// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// HARDEN-13 B2a (ADR-0045): the runtime owns the retry policy and hands off to the next configured candidate only when the
/// same-candidate policy is spent on a retryable kind. Candidate selection is a provider-neutral
/// <see cref="IFallbackChatModelControl"/>; no provider is named here.
/// </summary>
public sealed class FallbackHandshakeTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

    private static readonly Func<ModelRequest, CancellationToken, Task<ModelResponse>> Plan =
        (_, _) => Task.FromResult(PlanningTestSupport.PlanResponse());

    private static readonly Func<ModelRequest, CancellationToken, Task<ModelResponse>> Done =
        (_, _) => Task.FromResult(new ModelResponse("Done.", [], true, null));

    private static Func<ModelRequest, CancellationToken, Task<ModelResponse>> Fail(
        ModelFailureKind kind, TimeSpan? retryAfter = null, FakeTimeProvider? clock = null, TimeSpan? consumes = null) =>
        (_, _) =>
        {
            if (clock is not null && consumes is { } spent) clock.Advance(spent);
            throw new ModelProtocolException("provider said no") { FailureKind = kind, RetryAfter = retryAfter };
        };

    /// <summary>An ordered, sticky selector over scripted candidates, recording every invocation across all candidates.</summary>
    private sealed class Chain : IFallbackChatModelControl
    {
        private readonly List<ScriptedChatModel> candidates;
        private readonly List<ChatModelDescriptor> descriptors;
        private readonly PinnedProviderConfiguration pin;

        public Chain(PinnedProviderConfiguration pin, params (string Provider, string Model, Func<ModelRequest, CancellationToken, Task<ModelResponse>>[] Script)[] chain)
        {
            descriptors = [.. chain.Select(c => new ChatModelDescriptor(c.Provider, c.Model))];
            candidates = [.. chain.Select(c => new ScriptedChatModel(c.Script))];
            this.pin = pin;
        }

        public List<string> Journal { get; } = [];

        public ChatModelDescriptor Descriptor => descriptors[pin.FallbackOrdinal];

        public int FallbackOrdinal => pin.FallbackOrdinal;

        public bool HasNextCandidate => pin.FallbackOrdinal + 1 < candidates.Count;

        public int CallsOf(int candidate) => candidates[candidate].Calls;

        public bool TryAdvance()
        {
            if (!HasNextCandidate) return false;
            pin.FallbackOrdinal++;
            Journal.Add($"advance:{pin.FallbackOrdinal}");
            return true;
        }

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Journal.Add($"call:{pin.FallbackOrdinal}");
            return candidates[pin.FallbackOrdinal].CompleteAsync(request, ct);
        }
    }

    private sealed class Harness
    {
        public required AgentRunner Runner { get; init; }
        public required Chain Model { get; init; }
        public required PinnedProviderConfiguration Pin { get; init; }
        public required RecordingAuditSink Audit { get; init; }
        public required InMemoryTaskStore Store { get; init; }
        public List<TimeSpan> Delays { get; } = [];

        public List<ModelCallAuditEvent> ModelEvents() => Audit.Events.OfType<ModelCallAuditEvent>().ToList();
    }

    private static PinnedProviderConfiguration NewPin(int ordinal = 0) =>
        new(1, 1, "p0", "https://p0.test", "m0", true, null, "settings", "settings", "settings", "settings", "default", "hash")
        { FallbackOrdinal = ordinal };

    private static Harness Create(
        AgentRunnerOptions? options, FakeTimeProvider? clock, PinnedProviderConfiguration? pin,
        Func<Chain, Func<Guid, PinnedProviderConfiguration, CancellationToken, Task>>? persist,
        params (string Provider, string Model, Func<ModelRequest, CancellationToken, Task<ModelResponse>>[] Script)[] chain)
    {
        pin ??= NewPin();
        var model = new Chain(pin, chain);
        var audit = new RecordingAuditSink();
        var store = new InMemoryTaskStore();
        var runner = new AgentRunner(
            model, new ToolRegistry(new AlwaysAvailableCapabilityProbe()), new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(),
            audit, store, (TimeProvider?)clock ?? TimeProvider.System, NullLogger<AgentRunner>.Instance, options ?? new AgentRunnerOptions(),
            pinnedProviderConfiguration: pin, persistPinnedProviderConfiguration: persist?.Invoke(model));
        var harness = new Harness { Runner = runner, Model = model, Pin = pin, Audit = audit, Store = store };
        runner.ModelRetryJitter = () => 0.0;
        runner.ModelRetryDelay = (delay, _) =>
        {
            harness.Delays.Add(delay);
            clock?.Advance(delay);
            return Task.CompletedTask;
        };
        return harness;
    }

    private static Harness Create(
        params (string Provider, string Model, Func<ModelRequest, CancellationToken, Task<ModelResponse>>[] Script)[] chain) =>
        Create(null, null, null, null, chain);

    private static Func<Chain, Func<Guid, PinnedProviderConfiguration, CancellationToken, Task>> Persisting(List<int> persisted) =>
        chain => (_, pin, _) =>
        {
            // Crash safety: the ordinal is durable before the advanced candidate is invoked.
            Assert.DoesNotContain($"call:{pin.FallbackOrdinal}", chain.Journal);
            persisted.Add(pin.FallbackOrdinal);
            return Task.CompletedTask;
        };

    // Test A
    [Fact]
    public async Task APrimaryThatExhaustsItsRetries_HandsOffToTheFirstFallback_AndStaysThere()
    {
        var persisted = new List<int>();
        var h = Create(null, null, null, Persisting(persisted),
            ("p0", "m0", [Plan, Fail(ModelFailureKind.Transient), Fail(ModelFailureKind.Transient), Fail(ModelFailureKind.Transient)]),
            ("p1", "m1", [Done]));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Equal(
            ["call:0", "call:0", "call:0", "call:0", "advance:1", "call:1"], h.Model.Journal);
        Assert.Equal([TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1)], h.Delays);
        Assert.Equal(1, h.Model.FallbackOrdinal);
        Assert.Equal(1, h.Pin.FallbackOrdinal);
        Assert.Equal([1], persisted);

        // Test "attempt numbering": ProviderAttempt restarts per candidate, ModelAttempt and FallbackOrdinal never go back.
        var calls = task.Steps[0].ModelCalls!;
        Assert.Equal([1, 2, 3, 4], calls.Select(c => c.ModelAttempt!.Value));
        Assert.Equal([1, 2, 3, 1], calls.Select(c => c.ProviderAttempt!.Value));
        Assert.Equal([0, 0, 0, 1], calls.Select(c => c.FallbackOrdinal!.Value));
        Assert.Equal(
            [ModelRetryDecision.Retry, ModelRetryDecision.Retry, ModelRetryDecision.Fallback, null],
            calls.Select(c => c.RetryDecision));

        // The audit event and the record both name the candidate actually invoked.
        var events = h.ModelEvents().Where(e => e.StepIndex == 0).ToList();
        Assert.Equal(["p0", "p0", "p0", "p1"], events.Select(e => e.Provider));
        Assert.Equal(["m0", "m0", "m0", "m1"], events.Select(e => e.Model));
        Assert.Equal([1, 2, 3, 1], events.Select(e => e.ProviderAttempt!.Value));
        Assert.Equal([0, 0, 0, 1], events.Select(e => e.FallbackOrdinal!.Value));
        Assert.Equal(["p0", "p0", "p0", "p1"], calls.Select(c => c.Provider));
        Assert.Equal(["m0", "m0", "m0", "m1"], calls.Select(c => c.RequestedModel));
        Assert.Equal(ModelRetryDecision.Fallback, events[2].RetryDecision);
        Assert.Null(events[2].RetryDelayMs);
    }

    // Test B
    [Fact]
    public async Task ARetryAfterAboveTheMaximum_IsNotWaited_AndFallsBackImmediately()
    {
        var h = Create(
            ("p0", "m0", [Plan, Fail(ModelFailureKind.RateLimited, TimeSpan.FromMinutes(10))]),
            ("p1", "m1", [Done]));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Empty(h.Delays);
        var calls = task.Steps[0].ModelCalls!;
        Assert.Equal(ModelRetryDecision.Fallback, calls[0].RetryDecision);
        Assert.Null(calls[0].RetryDelayMs);
        Assert.Equal((1, 0), (calls[0].ProviderAttempt!.Value, calls[0].FallbackOrdinal!.Value));
        Assert.Equal((1, 1), (calls[1].ProviderAttempt!.Value, calls[1].FallbackOrdinal!.Value));
        Assert.Equal(1, h.Model.FallbackOrdinal);
    }

    // Test C
    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public async Task AnExhaustedCallBudget_EndsTheCall_WithoutInvokingTheFallback(int maxAttempts)
    {
        var clock = new FakeTimeProvider(Start);
        var options = new AgentRunnerOptions
        {
            ModelCallMaxAttempts = maxAttempts,
            ModelCallBudget = TimeSpan.FromSeconds(110),
            ModelCallAttemptTimeout = TimeSpan.FromSeconds(109.9),
        };
        var h = Create(options, clock, null, null,
            ("p0", "m0", [Plan, Fail(ModelFailureKind.Transient, clock: clock, consumes: TimeSpan.FromSeconds(109.5))]),
            ("p1", "m1", [Done]));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal(0, h.Model.CallsOf(1));
        Assert.Equal(0, h.Model.FallbackOrdinal);
        Assert.Equal(ModelRetryDecision.BudgetExhausted, Assert.Single(task.Steps[^1].ModelCalls!).RetryDecision);
    }

    [Fact]
    public async Task TheCallBudget_IsSharedAcrossCandidates_NeverReset()
    {
        var clock = new FakeTimeProvider(Start);
        var options = new AgentRunnerOptions
        {
            ModelCallMaxAttempts = 1,
            ModelCallBudget = TimeSpan.FromSeconds(110),
            ModelCallAttemptTimeout = TimeSpan.FromSeconds(109.9),
        };
        var h = Create(options, clock, null, null,
            ("p0", "m0", [Plan, Fail(ModelFailureKind.Transient, clock: clock, consumes: TimeSpan.FromSeconds(100))]),
            ("p1", "m1", [Fail(ModelFailureKind.Transient, clock: clock, consumes: TimeSpan.FromSeconds(9.5))]),
            ("p2", "m2", [Done]));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal((1, 1, 0), (h.Model.CallsOf(1), h.Model.FallbackOrdinal, h.Model.CallsOf(2)));
        Assert.Equal(
            [ModelRetryDecision.Fallback, ModelRetryDecision.BudgetExhausted],
            task.Steps[^1].ModelCalls!.Select(c => c.RetryDecision));
    }

    // Tests D and F: kinds the generic retry never retries stay terminal and never reach a fallback.
    [Theory]
    [InlineData(ModelFailureKind.Authentication)]
    [InlineData(ModelFailureKind.QuotaExceeded)]
    [InlineData(ModelFailureKind.InvalidRequest)]
    [InlineData(ModelFailureKind.ContextOverflow)]
    [InlineData(ModelFailureKind.Unknown)]
    public async Task ANonRetryableKind_IsTerminal_AndTheFallbackIsNeverInvoked(ModelFailureKind kind)
    {
        var h = Create(
            ("p0", "m0", [Plan, Fail(kind)]),
            ("p1", "m1", [Done]));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal(0, h.Model.CallsOf(1));
        Assert.Equal(0, h.Model.FallbackOrdinal);
        var call = Assert.Single(task.Steps[^1].ModelCalls!);
        Assert.Equal((kind, ModelRetryDecision.NotRetryable), (call.FailureKind!.Value, call.RetryDecision!.Value));
    }

    // Test F: a failure the adapter did not classify at all (an arbitrary exception) is Unknown and fails closed.
    [Fact]
    public async Task AnUnclassifiedAdapterException_FailsClosed_WithoutFallback()
    {
        var h = Create(
            ("p0", "m0", [Plan, (_, _) => throw new InvalidOperationException("surprise")]),
            ("p1", "m1", [Done]));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal(0, h.Model.CallsOf(1));
        Assert.Equal(ModelFailureKind.Unknown, Assert.Single(task.Steps[^1].ModelCalls!).FailureKind);
    }

    // Test E
    [Fact]
    public async Task AMalformedResponse_IsTerminal_NotAnAvailabilityFailure_AndNeverFallsBack()
    {
        var bad = (Func<ModelRequest, CancellationToken, Task<ModelResponse>>)((_, _) => Task.FromResult(
            new ModelResponse("""{"rationale":"r","rationale":"s","steps":[]}""", [], false, null)));
        var h = Create(
            ("p0", "m0", [bad, bad, Done]),
            ("p1", "m1", [Done]));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(0, h.Model.CallsOf(1));
        Assert.Equal(0, h.Model.FallbackOrdinal);
        Assert.All(h.ModelEvents().Where(e => e.StepIndex == -1), e =>
            Assert.Equal((ModelFailureKind.MalformedResponse, ModelRetryDecision.NotRetryable), (e.FailureKind!.Value, e.RetryDecision!.Value)));
        Assert.DoesNotContain(h.ModelEvents(), e => e.RetryDecision == ModelRetryDecision.Fallback);
        Assert.NotNull(task);
    }

    // Test L
    [Fact]
    public async Task AFullyExhaustedChain_VisitsEachCandidateOnce_InOrder_ThenFailsWithBoundedAttempts()
    {
        var h = Create(
            ("p0", "m0", [Plan, Fail(ModelFailureKind.Timeout), Fail(ModelFailureKind.Timeout), Fail(ModelFailureKind.Timeout)]),
            ("p1", "m1", [Fail(ModelFailureKind.Unreachable), Fail(ModelFailureKind.Unreachable), Fail(ModelFailureKind.Unreachable)]),
            ("p2", "m2", [Fail(ModelFailureKind.RateLimited), Fail(ModelFailureKind.RateLimited), Fail(ModelFailureKind.RateLimited)]),
            ("p3", "m3", [Fail(ModelFailureKind.Transient), Fail(ModelFailureKind.Transient), Fail(ModelFailureKind.Transient)]));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal(
            [
                "call:0", "call:0", "call:0", "call:0", "advance:1", "call:1", "call:1", "call:1", "advance:2",
                "call:2", "call:2", "call:2", "advance:3", "call:3", "call:3", "call:3",
            ],
            h.Model.Journal);
        Assert.Equal(3, h.Model.FallbackOrdinal);
        var calls = task.Steps[^1].ModelCalls!;
        Assert.Equal(12, calls.Count);
        Assert.Equal(calls.Select(c => c.FallbackOrdinal!.Value).Order(), calls.Select(c => c.FallbackOrdinal!.Value));
        Assert.Equal(ModelRetryDecision.AttemptsExhausted, calls[^1].RetryDecision);
        Assert.Equal(3, calls.Count(c => c.RetryDecision == ModelRetryDecision.Fallback));
    }

    // Test I (runtime half): the ordinal outlives a logical model call.
    [Fact]
    public async Task ALaterLogicalCall_StartsAtTheStickyCandidate_NotThePrimary()
    {
        var h = Create(
            ("p0", "m0", [Plan, Fail(ModelFailureKind.RateLimited, TimeSpan.FromMinutes(5))]),
            ("p1", "m1", [Done]));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        // Plan call -> primary. Step call: primary (fail) -> fallback. No later call returns to the primary.
        Assert.Equal(2, h.Model.CallsOf(0));
        Assert.Equal(1, h.Model.CallsOf(1));
        Assert.Equal("p1", h.ModelEvents().Last().Provider);
    }

    // Test I (resume half): an execution reconstructed from a persisted ordinal never retries the primary.
    [Fact]
    public async Task AnExecutionBuiltFromAPersistedOrdinal_StartsAtThatCandidate()
    {
        var h = Create(null, null, NewPin(ordinal: 1), null,
            ("p0", "m0", [Plan]),
            ("p1", "m1", [Plan, Done]));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Equal(0, h.Model.CallsOf(0));
        Assert.Equal(1, task.PinnedProviderConfiguration!.FallbackOrdinal);
        Assert.All(h.ModelEvents(), e => Assert.Equal(("p1", 1), (e.Provider, e.FallbackOrdinal!.Value)));
    }

    // Persistence failure: deterministic and fail safe — the advanced-but-unpersisted candidate is never invoked.
    [Fact]
    public async Task AFailedOrdinalPersist_NeverInvokesTheUnpersistedCandidate()
    {
        var h = Create(null, null, null,
            _ => (_, _, _) => throw new IOException("disk full"),
            ("p0", "m0", [Plan, Fail(ModelFailureKind.RateLimited, TimeSpan.FromMinutes(5))]),
            ("p1", "m1", [Done]));

        var thrown = await Record.ExceptionAsync(() => h.Runner.RunAsync("check", Actor));

        // Contained like any other runtime failure: the execution ends Failed, never continues on the unpersisted candidate.
        Assert.Null(thrown);
        Assert.Equal(AgentTaskStatus.Failed, h.Store.Saves[^1].Status);
        Assert.Equal(0, h.Model.CallsOf(1));
        Assert.DoesNotContain("call:1", h.Model.Journal);
    }
}
