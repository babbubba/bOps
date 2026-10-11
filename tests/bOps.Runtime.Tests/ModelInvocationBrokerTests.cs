// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

public sealed class ModelInvocationBrokerTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("broker-test");

    [Fact]
    public async Task InvokeAsync_AccountsRecordsAndAuditsTheSuccessfulAttempt()
    {
        var response = new ModelResponse("done", [], true, new ModelUsage(7, 3, null));
        var model = new FakeChatModel(response);
        var audit = new RecordingAuditSink();
        var calls = new List<ModelCallRecord>();
        var accounted = 0;
        var broker = new ModelInvocationBroker(
            model, audit, TimeProvider.System, NullLogger.Instance, new AgentRunnerOptions());
        var context = new ModelInvocationContext(Guid.NewGuid(), 4, Actor, calls)
        {
            AccountTokens = tokens => accounted += tokens,
        };

        var actual = await broker.InvokeAsync(new ModelRequest("system", [ChatTurn.FromUser("hello")], []), context);

        Assert.Same(response, actual);
        Assert.Equal(10, accounted);
        Assert.Equal(ModelCallOutcome.Success, Assert.Single(calls).Outcome);
        var evt = Assert.Single(audit.Events.OfType<ModelCallAuditEvent>());
        Assert.Equal((context.TaskId, 4, ModelCallOutcome.Success), (evt.TaskId, evt.StepIndex, evt.Outcome));
    }

    [Fact]
    public async Task InvokeAsync_RetriesATransientFailureWithoutDelegatingRetryToTheAdapter()
    {
        var model = new ScriptedChatModel(
            (_, _) => throw new ModelProtocolException("temporary") { FailureKind = ModelFailureKind.Transient },
            (_, _) => Task.FromResult(new ModelResponse("done", [], true, null)));
        var audit = new RecordingAuditSink();
        var calls = new List<ModelCallRecord>();
        var broker = new ModelInvocationBroker(
            model, audit, TimeProvider.System, NullLogger.Instance, new AgentRunnerOptions());
        broker.RetryJitter = () => 0;
        broker.RetryDelay = (_, _) => Task.CompletedTask;

        var response = await broker.InvokeAsync(
            new ModelRequest("system", [ChatTurn.FromUser("hello")], []),
            new ModelInvocationContext(Guid.NewGuid(), 0, Actor, calls));

        Assert.Equal("done", response.TextResponse);
        Assert.Equal(2, model.Calls);
        Assert.Equal([ModelCallOutcome.Failure, ModelCallOutcome.Success], calls.Select(call => call.Outcome));
        Assert.Equal(2, audit.Events.OfType<ModelCallAuditEvent>().Count());
    }

    [Fact]
    public async Task InvokeAsync_CallerCancellationPropagatesAsCancellation_AndIsNotRecordedAsATimeout()
    {
        // AF-05 O8: caller cancellation != model timeout != execution budget. A conforming adapter observes the token it is handed.
        var model = new ScriptedChatModel(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new ModelResponse("unreachable", [], true, null);
        });
        var calls = new List<ModelCallRecord>();
        var broker = new ModelInvocationBroker(
            model, new RecordingAuditSink(), TimeProvider.System, NullLogger.Instance, new AgentRunnerOptions());
        using var caller = new CancellationTokenSource();

        var call = broker.InvokeAsync(
            new ModelRequest("system", [ChatTurn.FromUser("hello")], []),
            new ModelInvocationContext(Guid.NewGuid(), 0, Actor, calls), caller.Token);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(calls);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task InvokeAsync_ModelTimeoutStaysATimeoutFailureAndIsNotCallerCancellation()
    {
        var never = new TaskCompletionSource<ModelResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new ScriptedChatModel((_, _) => never.Task);
        var calls = new List<ModelCallRecord>();
        var broker = new ModelInvocationBroker(
            model, new RecordingAuditSink(), TimeProvider.System, NullLogger.Instance,
            new AgentRunnerOptions { ModelCallMaxAttempts = 1, ModelCallAttemptTimeout = TimeSpan.FromMilliseconds(100) });

        var failure = await Assert.ThrowsAsync<ModelProtocolException>(() => broker.InvokeAsync(
            new ModelRequest("system", [ChatTurn.FromUser("hello")], []),
            new ModelInvocationContext(Guid.NewGuid(), 0, Actor, calls)));

        Assert.Equal(ModelFailureKind.Timeout, failure.FailureKind);
        Assert.Equal(ModelFailureKind.Timeout, Assert.Single(calls).FailureKind);
    }
}
