// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0039 §1: the model-failure boundary surrounds only the adapter invocation. A failure of the runtime's own bookkeeping
/// after a successful call (the success audit write) is never a model failure: it is not classified, not recorded as a
/// second attempt and not retried, so it can never cause a second billable model call.
/// </summary>
public sealed class ModelCallBookkeepingBoundaryTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("test-user");

    private sealed class SuccessAuditFails(Exception? failure) : IAuditSink
    {
        public List<AuditEvent> Events { get; } = [];

        public Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
        {
            if (failure is not null && evt is ModelCallAuditEvent { StepIndex: 0, Outcome: ModelCallOutcome.Success })
            {
                throw failure;
            }

            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    private static (AgentRunner Runner, ScriptedChatModel Model, SuccessAuditFails Audit, InMemoryTaskStore Store, List<TimeSpan> Delays) Create(
        Exception? auditFailure, params Func<ModelRequest, CancellationToken, Task<ModelResponse>>[] script)
    {
        var model = new ScriptedChatModel(script);
        var audit = new SuccessAuditFails(auditFailure);
        var store = new InMemoryTaskStore();
        var delays = new List<TimeSpan>();
        var runner = new AgentRunner(
            model, new ToolRegistry(new AlwaysAvailableCapabilityProbe()), new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(),
            audit, store, TimeProvider.System, NullLogger<AgentRunner>.Instance, new AgentRunnerOptions());
        runner.ModelRetryJitter = () => 0.0;
        runner.ModelRetryDelay = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };
        return (runner, model, audit, store, delays);
    }

    private static Func<ModelRequest, CancellationToken, Task<ModelResponse>> Plan() =>
        (_, _) => Task.FromResult(PlanningTestSupport.PlanResponse());

    private static Func<ModelRequest, CancellationToken, Task<ModelResponse>> Done() =>
        (_, _) => Task.FromResult(new ModelResponse("Done.", [], true, null));

    // M1-01
    [Fact]
    public async Task ASuccessAuditIOException_IsNotAModelFailure_AndTheModelIsNotCalledAgain()
    {
        var (runner, model, audit, store, delays) = Create(new IOException("audit disk full"), Plan(), Done());

        var task = await runner.RunAsync("check", Actor);

        Assert.Equal(2, model.Calls); // the plan call and exactly one step call
        Assert.Empty(delays);
        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal(AgentTaskStatus.Failed, (await store.LoadAsync(task.Id))!.Status);
        Assert.Empty(await store.ListByStatusAsync(AgentTaskStatus.Running));
        Assert.DoesNotContain(audit.Events.OfType<ModelCallAuditEvent>(), e => e.Outcome == ModelCallOutcome.Failure);
        var calls = task.Steps.SelectMany(s => s.ModelCalls ?? []).Where(c => c.ModelAttempt == 1).ToList();
        Assert.DoesNotContain(calls, c => c.Outcome == ModelCallOutcome.Failure || c.FailureKind is not null);
        Assert.DoesNotContain(task.Steps, s => s.Observation?.Contains("Provider", StringComparison.Ordinal) == true);
    }

    // M1-02
    [Fact]
    public async Task ASuccessAuditOperationCanceled_WithoutTaskCancellation_IsNotATimeout_AndNotRetried()
    {
        var (runner, model, audit, store, delays) = Create(new OperationCanceledException("sink cancelled"), Plan(), Done());

        // It is not a model failure, so it propagates to the host's containment (the launcher backstop) instead of being retried.
        var escaped = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync("check", Actor));

        Assert.Equal(2, model.Calls);
        Assert.Empty(delays);
        Assert.DoesNotContain(audit.Events.OfType<ModelCallAuditEvent>(), e => e.FailureKind == ModelFailureKind.Timeout);
        var running = Assert.Single(await store.ListByStatusAsync(AgentTaskStatus.Running));
        Assert.True(await runner.ContainEscapedFailureAsync(running.Id, Actor, running, escaped));
        Assert.Empty(await store.ListByStatusAsync(AgentTaskStatus.Running));
        Assert.Equal(AgentTaskStatus.Failed, (await store.LoadAsync(running.Id))!.Status);
    }

    // M1-04: a genuine provider transport failure is still classified by the adapter and retried by the runtime.
    [Fact]
    public async Task ARealProviderTransportFailure_IsStillClassifiedAndRetried()
    {
        var (runner, model, audit, _, delays) = Create(
            null, Plan(),
            (_, _) => throw new ModelProtocolException("connection reset") { FailureKind = ModelFailureKind.Unreachable },
            Done());

        var task = await runner.RunAsync("check", Actor);

        Assert.Equal(3, model.Calls);
        Assert.Single(delays);
        var events = audit.Events.OfType<ModelCallAuditEvent>().Where(e => e.StepIndex == 0).ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal(ModelFailureKind.Unreachable, events[0].FailureKind);
        Assert.Equal(ModelRetryDecision.Retry, events[0].RetryDecision);
        Assert.NotEqual(AgentTaskStatus.Running, task.Status);
    }
}
