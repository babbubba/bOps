// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// HARDEN-2 / ADR-0039: a failed model call is contained, classified, retried only within bounds, audited attempt by
/// attempt, persisted terminally, and never leaves a task <see cref="AgentTaskStatus.Running"/>. Deterministic: the retry
/// wait and the jitter are replaced by seams, so no test sleeps for a real backoff.
/// </summary>
public sealed class ModelCallFailureContainmentTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public required AgentRunner Runner { get; init; }
        public required RecordingAuditSink Audit { get; init; }
        public required InMemoryTaskStore Store { get; init; }
        public required ScriptedChatModel Model { get; init; }
        public List<TimeSpan> Delays { get; } = [];

        public List<ModelCallAuditEvent> ModelEvents(int stepIndex) =>
            Audit.Events.OfType<ModelCallAuditEvent>().Where(e => e.StepIndex == stepIndex).ToList();

        public async Task<TaskState> StoredAsync(Guid id) => (await Store.LoadAsync(id))!;
    }

    private static Harness Create(ScriptedChatModel model, AgentRunnerOptions? options = null, FakeTimeProvider? clock = null, params ITool[] tools)
    {
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        var audit = new RecordingAuditSink();
        var store = new InMemoryTaskStore();
        var runner = new AgentRunner(
            model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), audit, store,
            (TimeProvider?)clock ?? TimeProvider.System, NullLogger<AgentRunner>.Instance, options ?? new AgentRunnerOptions());
        var harness = new Harness { Runner = runner, Audit = audit, Store = store, Model = model };
        runner.ModelRetryJitter = () => 0.0;
        runner.ModelRetryDelay = (delay, _) =>
        {
            harness.Delays.Add(delay);
            clock?.Advance(delay);
            return Task.CompletedTask;
        };
        return harness;
    }

    private static Func<ModelRequest, CancellationToken, Task<ModelResponse>> Reply(ModelResponse response) =>
        (_, _) => Task.FromResult(response);

    private static Func<ModelRequest, CancellationToken, Task<ModelResponse>> Plan() => Reply(PlanningTestSupport.PlanResponse());

    private static Func<ModelRequest, CancellationToken, Task<ModelResponse>> Done(string text = "Done.") =>
        Reply(new ModelResponse(text, [], true, null));

    private static Func<ModelRequest, CancellationToken, Task<ModelResponse>> Fail(
        ModelFailureKind kind, int? status = null, TimeSpan? retryAfter = null, string message = "provider said no") =>
        (_, _) => throw new ModelProtocolException(message) { FailureKind = kind, ProviderStatusCode = status, RetryAfter = retryAfter };

    private static Func<ModelRequest, CancellationToken, Task<ModelResponse>> Stall() =>
        async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        };

    private static AgentRunnerOptions Fast(int attempts = 3) => new()
    {
        ModelCallMaxAttempts = attempts,
        ModelCallAttemptTimeout = TimeSpan.FromMilliseconds(150),
    };

    private static PlanStep FailureStep(TaskState task)
    {
        var step = task.Steps[^1];
        Assert.Equal("Model protocol failure", step.Description);
        return step;
    }

    // H2-01
    [Fact]
    public async Task RateLimitedWithRetryAfter_IsRetriedAfterTheRequestedWait_ThenSucceeds()
    {
        var h = Create(new ScriptedChatModel(Plan(), Fail(ModelFailureKind.RateLimited, 429, TimeSpan.FromSeconds(2)), Done()));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Equal([TimeSpan.FromSeconds(2)], h.Delays);
        var calls = task.Steps[0].ModelCalls!;
        Assert.Equal(2, calls.Count);
        Assert.Equal((1, ModelCallOutcome.Failure, ModelFailureKind.RateLimited, ModelRetryDecision.Retry, 2000L, 429),
            (calls[0].ModelAttempt!.Value, calls[0].Outcome, calls[0].FailureKind!.Value, calls[0].RetryDecision!.Value, calls[0].RetryDelayMs!.Value,
                calls[0].ProviderStatusCode!.Value));
        Assert.Equal((2, ModelCallOutcome.Success), (calls[1].ModelAttempt!.Value, calls[1].Outcome));
        Assert.Null(calls[1].FailureKind);
    }

    // H2-02 and the audit sequence 429, 429, 429
    [Fact]
    public async Task RepeatedRateLimit_EndsAfterTheConfiguredAttempts_AsATerminalRateLimitedFailure()
    {
        var h = Create(new ScriptedChatModel(
            Plan(),
            Fail(ModelFailureKind.RateLimited, 429),
            Fail(ModelFailureKind.RateLimited, 429),
            Fail(ModelFailureKind.RateLimited, 429)));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal(AgentTaskStatus.Failed, (await h.StoredAsync(task.Id)).Status);
        Assert.Equal(4, h.Model.Calls);
        Assert.Equal([TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1)], h.Delays);
        var events = h.ModelEvents(0);
        Assert.Equal([1, 2, 3], events.Select(e => e.ModelAttempt!.Value));
        Assert.All(events, e => Assert.Equal(ModelFailureKind.RateLimited, e.FailureKind));
        Assert.Equal(
            [ModelRetryDecision.Retry, ModelRetryDecision.Retry, ModelRetryDecision.AttemptsExhausted],
            events.Select(e => e.RetryDecision!.Value));
        var reason = FailureStep(task).Observation!;
        Assert.StartsWith("Provider rate limit exceeded.", reason, StringComparison.Ordinal);
        Assert.Contains("Gave up after 3 attempts.", reason, StringComparison.Ordinal);
        Assert.Contains("provider said no", reason, StringComparison.Ordinal);
    }

    // Audit test: 429, 429, success keeps every attempt's evidence.
    [Fact]
    public async Task EveryAttempt_KeepsItsOwnRecordAndAuditEvent_WithoutOverwritingEarlierOnes()
    {
        var h = Create(new ScriptedChatModel(
            Plan(), Fail(ModelFailureKind.RateLimited, 429), Fail(ModelFailureKind.RateLimited, 429), Done()));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        var events = h.ModelEvents(0);
        Assert.Equal(
            [(1, ModelCallOutcome.Failure, (ModelFailureKind?)ModelFailureKind.RateLimited),
             (2, ModelCallOutcome.Failure, ModelFailureKind.RateLimited),
             (3, ModelCallOutcome.Success, null)],
            events.Select(e => (e.ModelAttempt!.Value, e.Outcome, e.FailureKind)));
        Assert.Equal(
            events.Select(e => (e.ModelAttempt, e.Outcome, e.FailureKind)),
            task.Steps[0].ModelCalls!.Select(c => (c.ModelAttempt, c.Outcome, c.FailureKind)));
    }

    // H2-03
    [Fact]
    public async Task ServiceUnavailable_IsRetriedWithBackoff_ThenSucceeds()
    {
        var h = Create(new ScriptedChatModel(Plan(), Fail(ModelFailureKind.Transient, 503), Done()));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Equal([TimeSpan.FromSeconds(0.5)], h.Delays);
    }

    [Fact]
    public async Task ServiceUnavailable_EveryAttempt_EndsAsATerminalTransientFailure()
    {
        var h = Create(new ScriptedChatModel(
            Plan(), Fail(ModelFailureKind.Transient, 503), Fail(ModelFailureKind.Transient, 503), Fail(ModelFailureKind.Transient, 503)));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, (await h.StoredAsync(task.Id)).Status);
        Assert.StartsWith("Provider is temporarily unavailable.", FailureStep(task).Observation, StringComparison.Ordinal);
    }

    // H2-13
    [Fact]
    public async Task Unreachable_IsRetriedWithinBounds()
    {
        var h = Create(new ScriptedChatModel(Plan(), Fail(ModelFailureKind.Unreachable), Done()));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Single(h.Delays);
        Assert.Equal(ModelFailureKind.Unreachable, task.Steps[0].ModelCalls![0].FailureKind);
    }

    // H2-06, H2-07, H2-08, H2-09, H2-10, H2-11, H2-12 at the runtime: a permanent kind is never retried.
    [Theory]
    [InlineData(ModelFailureKind.Authentication, 401, "Provider authentication failed. Check the configured provider credential.")]
    [InlineData(ModelFailureKind.Authentication, 403, "Provider authentication failed. Check the configured provider credential.")]
    [InlineData(ModelFailureKind.QuotaExceeded, 402, "Provider account quota or credit is unavailable.")]
    [InlineData(ModelFailureKind.InvalidRequest, 400, "Provider rejected the model request.")]
    [InlineData(ModelFailureKind.InvalidRequest, 413, "Provider rejected the model request.")]
    [InlineData(ModelFailureKind.ContextOverflow, 400, "Model context limit exceeded.")]
    [InlineData(ModelFailureKind.MalformedResponse, 200, "Provider/model returned a malformed response.")]
    [InlineData(ModelFailureKind.Unknown, 501, "Model call failed unexpectedly.")]
    public async Task PermanentFailure_IsNotRetried_AndEndsTheTaskFailedWithAnActionableReason(
        ModelFailureKind kind, int status, string operatorReason)
    {
        var h = Create(new ScriptedChatModel(Plan(), Fail(kind, status)));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(2, h.Model.Calls);
        Assert.Empty(h.Delays);
        Assert.Equal(AgentTaskStatus.Failed, (await h.StoredAsync(task.Id)).Status);
        var call = Assert.Single(FailureStep(task).ModelCalls!);
        Assert.Equal((kind, ModelRetryDecision.NotRetryable, status, 1), (call.FailureKind!.Value, call.RetryDecision!.Value, call.ProviderStatusCode!.Value, call.ModelAttempt!.Value));
        Assert.StartsWith(operatorReason, FailureStep(task).Observation, StringComparison.Ordinal);
        var audited = Assert.Single(h.ModelEvents(0));
        Assert.Equal((ModelCallOutcome.Failure, kind, ModelRetryDecision.NotRetryable), (audited.Outcome, audited.FailureKind!.Value, audited.RetryDecision!.Value));
    }

    [Fact]
    public async Task AnUnclassifiedAdapterException_IsUnknown_AndNeverRetried()
    {
        var h = Create(new ScriptedChatModel(Plan(), (_, _) => throw new InvalidOperationException("adapter bug")));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal(2, h.Model.Calls);
        Assert.Equal(ModelFailureKind.Unknown, FailureStep(task).ModelCalls![0].FailureKind);
    }

    // H2-04, H2-17: the attempt's own timeout is a Timeout failure, not a cancellation.
    [Fact]
    public async Task AStallingProvider_TimesOut_AsAnAuditedTimeoutFailure_NotACancellation()
    {
        var h = Create(new ScriptedChatModel(Plan(), Stall()), Fast(attempts: 1));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        var call = Assert.Single(FailureStep(task).ModelCalls!);
        Assert.Equal((ModelFailureKind.Timeout, ModelRetryDecision.AttemptsExhausted), (call.FailureKind!.Value, call.RetryDecision!.Value));
        Assert.StartsWith("Provider/model call timed out.", FailureStep(task).Observation, StringComparison.Ordinal);
        Assert.Equal(ModelFailureKind.Timeout, Assert.Single(h.ModelEvents(0)).FailureKind);
    }

    [Fact]
    public async Task AProviderThatIgnoresItsToken_StillCannotHoldTheLoopPastTheAttemptTimeout()
    {
        var never = new TaskCompletionSource<ModelResponse>();
        var h = Create(new ScriptedChatModel(Plan(), (_, _) => never.Task), Fast(attempts: 1));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal(ModelFailureKind.Timeout, FailureStep(task).ModelCalls![0].FailureKind);
    }

    // H2-05, H2-26: timeouts exhausted leave the persisted task Failed, never Running.
    [Fact]
    public async Task TimeoutExhaustion_PersistsATerminalFailedState_NeverRunning()
    {
        var h = Create(new ScriptedChatModel(Plan(), Stall(), Stall()), Fast(attempts: 2));

        var task = await h.Runner.RunAsync("check", Actor);

        var stored = await h.StoredAsync(task.Id);
        Assert.Equal(AgentTaskStatus.Failed, stored.Status);
        Assert.Empty(await h.Store.ListByStatusAsync(AgentTaskStatus.Running));
        Assert.Equal([ModelRetryDecision.Retry, ModelRetryDecision.AttemptsExhausted], h.ModelEvents(0).Select(e => e.RetryDecision!.Value));
    }

    // H2-16: the caller's cancellation keeps its meaning.
    [Fact]
    public async Task CallerCancellation_DuringAModelCall_PropagatesAsCancellation_NotTimeout()
    {
        using var cts = new CancellationTokenSource();
        var h = Create(new ScriptedChatModel(Plan(), async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }), new AgentRunnerOptions { ModelCallAttemptTimeout = TimeSpan.FromSeconds(30) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Runner.RunAsync("check", Actor, ct: cts.Token));

        Assert.DoesNotContain(h.Audit.Events.OfType<ModelCallAuditEvent>(), e => e.FailureKind == ModelFailureKind.Timeout);
        Assert.Empty(h.Delays);
    }

    [Fact]
    public async Task CallerCancellation_DuringARetryWait_PropagatesAsCancellation()
    {
        using var cts = new CancellationTokenSource();
        var h = Create(new ScriptedChatModel(Plan(), Fail(ModelFailureKind.Transient, 503), Done()));
        h.Runner.ModelRetryDelay = async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Runner.RunAsync("check", Actor, ct: cts.Token));
        Assert.Equal(2, h.Model.Calls);
    }

    // H2-14
    [Fact]
    public async Task RetryAfterAboveTheMaximumDelay_EndsTheCall_WithoutWaiting()
    {
        var h = Create(new ScriptedChatModel(Plan(), Fail(ModelFailureKind.RateLimited, 429, TimeSpan.FromSeconds(3600)), Done()));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Empty(h.Delays);
        Assert.Equal(ModelRetryDecision.RetryAfterExceedsLimit, FailureStep(task).ModelCalls![0].RetryDecision);
        Assert.Contains("asked to retry after 3600 s", FailureStep(task).Observation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANegativeRetryAfter_CountsAsZero_AndTheBackoffStillApplies()
    {
        var h = Create(new ScriptedChatModel(Plan(), Fail(ModelFailureKind.RateLimited, 429, TimeSpan.FromSeconds(-5)), Done()));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Equal([TimeSpan.FromSeconds(0.5)], h.Delays);
    }

    // H2-15: the overall budget always wins.
    [Fact]
    public async Task RetryAfterThatDoesNotFitTheRemainingBudget_EndsTheCall_WithoutSleepingPastIt()
    {
        var clock = new FakeTimeProvider(Start);
        var options = new AgentRunnerOptions { ModelCallBudget = TimeSpan.FromSeconds(110), ModelCallAttemptTimeout = TimeSpan.FromSeconds(109) };
        var h = Create(new ScriptedChatModel(Plan(), (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(100));
            throw new ModelProtocolException("slow down") { FailureKind = ModelFailureKind.RateLimited, RetryAfter = TimeSpan.FromSeconds(20) };
        }, Done()), options, clock);

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Empty(h.Delays);
        Assert.Equal(ModelRetryDecision.BudgetExhausted, FailureStep(task).ModelCalls![0].RetryDecision);
    }

    [Fact]
    public async Task TheBudgetBoundsTheAttempts_EvenWhenMoreAttemptsAreConfigured()
    {
        var clock = new FakeTimeProvider(Start);
        var options = new AgentRunnerOptions
        {
            ModelCallMaxAttempts = 10,
            ModelCallBudget = TimeSpan.FromSeconds(20),
            ModelCallAttemptTimeout = TimeSpan.FromSeconds(10),
            ModelRetryBaseDelay = TimeSpan.FromSeconds(4),
            ModelRetryMaxDelay = TimeSpan.FromSeconds(8),
        };
        var failures = Enumerable.Range(0, 10).Select(_ => Fail(ModelFailureKind.Transient, 503)).ToArray();
        var h = Create(new ScriptedChatModel([Plan(), .. failures]), options, clock);

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        // Jitter 0: waits of 2, 4, 4, 4 and 4 s. After 18 s of the 20 s budget a further 4 s wait would not leave the one
        // second an attempt needs, so the sixth attempt is the last although ten are configured.
        Assert.Equal(18, h.Delays.Sum(d => d.TotalSeconds));
        Assert.Equal(ModelRetryDecision.BudgetExhausted, h.ModelEvents(0)[^1].RetryDecision);
        Assert.Equal(6, h.Model.Calls - 1);
    }

    // H2-18, H2-19: every failed attempt is recorded and audited, and no secret or prompt reaches either.
    [Fact]
    public async Task AFailureReason_IsRedactedAndBounded_InTheRecordTheAuditAndTheTaskReason()
    {
        const string goal = "PROMPT-CONTENT-MARKER check the disk";
        var leaky = "denied: Authorization: Bearer sk-live-0123456789abcdefghij api_key=abcd1234secret user_id=user_98765 " +
                    "token=9f8e7d6c5b4a39281706f5e4d3c2b1a0 " + new string('x', 2000);
        var h = Create(new ScriptedChatModel(Plan(), Fail(ModelFailureKind.Authentication, 401, message: leaky)));

        var task = await h.Runner.RunAsync(goal, Actor);

        var call = FailureStep(task).ModelCalls![0];
        var audited = Assert.Single(h.ModelEvents(0));
        foreach (var text in new[] { call.ErrorMessage!, audited.ErrorMessage!, FailureStep(task).Observation! })
        {
            Assert.DoesNotContain("sk-live-0123456789abcdefghij", text, StringComparison.Ordinal);
            Assert.DoesNotContain("abcd1234secret", text, StringComparison.Ordinal);
            Assert.DoesNotContain("user_98765", text, StringComparison.Ordinal);
            Assert.DoesNotContain("9f8e7d6c5b4a39281706f5e4d3c2b1a0", text, StringComparison.Ordinal);
            Assert.DoesNotContain("PROMPT-CONTENT-MARKER", text, StringComparison.Ordinal);
            Assert.Contains("denied", text, StringComparison.Ordinal);
        }

        Assert.True(call.ErrorMessage!.Length <= 500);
        Assert.Equal(call.ErrorMessage, audited.ErrorMessage);
    }

    // Planning and replanning calls are contained the same way.
    [Fact]
    public async Task APlanningCallFailure_EndsTheTaskFailed_AndIsNeverLeftRunning()
    {
        var h = Create(new ScriptedChatModel(Fail(ModelFailureKind.Authentication, 401)));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, (await h.StoredAsync(task.Id)).Status);
        Assert.Equal(ModelFailureKind.Authentication, Assert.Single(h.ModelEvents(-1)).FailureKind);
    }

    [Fact]
    public async Task AReplanCallFailure_EndsTheTaskFailed_AndIsNeverLeftRunning()
    {
        var h = Create(new ScriptedChatModel(
            Reply(PlanningTestSupport.PlanResponse(stepCount: 1)),
            Reply(UnknownToolCall()),
            Fail(ModelFailureKind.InvalidRequest, 400)));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, (await h.StoredAsync(task.Id)).Status);
        Assert.Equal(ModelFailureKind.InvalidRequest, Assert.Single(h.ModelEvents(0), e => e.Outcome == ModelCallOutcome.Failure).FailureKind);
    }

    // H2-23, H2-25: malformed plan output, including duplicate keys at any depth, is contained and audited.
    [Theory]
    [InlineData("""{"rationale":"r","steps":[{"description":"a"}],"steps":[{"description":"b"}]}""")]
    [InlineData("""{"rationale":"r","steps":[{"description":"a","description":"b"}]}""")]
    [InlineData("""{"rationale":"r","rationale":"s","steps":[]}""")]
    public async Task APlanWithDuplicateKeys_IsAuditedAsMalformed_ReaskedOnce_ThenDegradesWithoutEscaping(string duplicate)
    {
        var bad = Reply(new ModelResponse(duplicate, [], false, null));
        var h = Create(new ScriptedChatModel(bad, bad, Done()));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Completed, (await h.StoredAsync(task.Id)).Status);
        Assert.Equal(3, h.Model.Calls);
        var planCalls = task.Plans[0].ModelCalls!;
        Assert.Equal(2, planCalls.Count);
        Assert.All(planCalls, c => Assert.Equal(
            (ModelCallOutcome.Failure, ModelFailureKind.MalformedResponse, ModelRetryDecision.NotRetryable, 1),
            (c.Outcome, c.FailureKind!.Value, c.RetryDecision!.Value, c.ModelAttempt!.Value)));
        Assert.All(h.ModelEvents(-1), e => Assert.Equal(ModelFailureKind.MalformedResponse, e.FailureKind));
        Assert.Empty(task.Plans[0].Steps);
        Assert.Empty(h.Delays);
    }

    [Fact]
    public async Task AMalformedPlanFollowedByAValidOne_UsesTheValidPlan_AndKeepsTheMalformedAttemptOnRecord()
    {
        var h = Create(new ScriptedChatModel(
            Reply(new ModelResponse("""{"steps":[],"steps":[]}""", [], false, null)), Plan(), Done()));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Equal(5, task.Plans[0].Steps.Count);
        Assert.Equal(
            [ModelCallOutcome.Failure, ModelCallOutcome.Success],
            task.Plans[0].ModelCalls!.Select(c => c.Outcome));
    }

    // H2-24
    [Fact]
    public async Task AReplanWithDuplicateKeys_IsContainedTheSameWay()
    {
        var bad = Reply(new ModelResponse("""{"rationale":"r","steps":[{"description":"a","expectedTool":"x","expectedTool":"y"}]}""", [], false, null));
        var h = Create(new ScriptedChatModel(
            Reply(PlanningTestSupport.PlanResponse(stepCount: 1, expectedTool: "no.such.tool")),
            Reply(UnknownToolCall()),
            bad,
            bad));

        var task = await h.Runner.RunAsync("check", Actor);

        Assert.Equal(AgentTaskStatus.Failed, (await h.StoredAsync(task.Id)).Status);
        Assert.Single(task.Plans);
        Assert.Equal(TaskTerminalKind.ModelFailure, task.TerminalReason!.Kind);
        Assert.Equal(ModelFailureKind.MalformedResponse, task.TerminalReason.FailureKind);
        Assert.True(h.Runner.EvaluateResume(task).Resumable);
        Assert.All(FailureStep(task).ModelCalls!, c => Assert.Equal(ModelFailureKind.MalformedResponse, c.FailureKind));
        Assert.Equal(2, h.ModelEvents(0).Count(e => e.FailureKind == ModelFailureKind.MalformedResponse));
    }

    [Fact]
    public void IncoherentModelCallOptions_AreRejectedWhenTheRunnerIsBuilt()
    {
        Assert.Throws<InvalidOperationException>(() => Create(new ScriptedChatModel(), new AgentRunnerOptions { ModelCallMaxAttempts = 0 }));
        Assert.Throws<InvalidOperationException>(() => Create(new ScriptedChatModel(), new AgentRunnerOptions { ModelCallBudget = TimeSpan.Zero }));
        Assert.Throws<InvalidOperationException>(() => Create(new ScriptedChatModel(),
            new AgentRunnerOptions { ModelRetryBaseDelay = TimeSpan.FromSeconds(10), ModelRetryMaxDelay = TimeSpan.FromSeconds(5) }));
        Assert.Throws<InvalidOperationException>(() => new AgentRunnerOptions().Validate(providerRequestTimeout: TimeSpan.FromSeconds(100)));
        new AgentRunnerOptions().Validate(ChatModelOptions.DefaultRequestTimeout);
    }

    // An unknown tool is a deviation (rule C8): the very next thing the loop does is replan.
    private static ModelResponse UnknownToolCall() =>
        new(null, [new ModelToolCall("call-1", "no.such.tool", ToolArguments.Empty)], false, null);
}

/// <summary>Runs one scripted behaviour per model call, in order — a reply, a classified failure, a stall.</summary>
internal sealed class ScriptedChatModel(params Func<ModelRequest, CancellationToken, Task<ModelResponse>>[] steps) : IChatModel
{
    private int _calls;

    public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

    public int Calls => _calls;

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        var index = Interlocked.Increment(ref _calls) - 1;
        if (index >= steps.Length)
        {
            throw new InvalidOperationException($"ScriptedChatModel received call {index + 1} but only {steps.Length} were scripted.");
        }

        return steps[index](request, ct);
    }
}
