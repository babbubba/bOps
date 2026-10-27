// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bOps.Abstractions;
using bOps.Runtime;
using bOps.Runtime.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// BOPS-0122: what <see cref="MafTurnEquivalenceTests"/> leaves open at the host level. A model call that is cancelled by the caller,
/// one that hits the runtime's per-attempt timeout and one that outlives the attempt's duration budget must end the same way whether
/// the turn is hosted by the Microsoft Agent Framework (<see cref="MafGovernedTurnBroker"/>) or by the current
/// <see cref="ModelInvocationBroker"/> path: same exception or terminal state, same audit trail, same persisted task, same provider
/// requests. The model hangs until its token is cancelled, so every outcome comes from the runtime's own cancellation and timeout
/// handling, never from a scripted failure. Offline: no live model, no network. The same normalisation as the equivalence tests
/// (ids, timestamps, measured durations) is the only tolerance.
/// </summary>
public sealed class MafTurnCancellationParityTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("cancellation-parity");
    private static readonly Guid TaskId = Guid.Parse("00000000-0000-0000-0000-00000000c0de");

    [Fact]
    public async Task ACallerCancellation_DuringAModelCall_EndsTheSameOnBothPaths()
    {
        var current = await RunAsync(maf: false, Scenario.CallerCancels);
        var governed = await RunAsync(maf: true, Scenario.CallerCancels);

        AssertIdentical(current, governed);
        Assert.Equal(nameof(OperationCanceledException), current.Ended);
        // The cancelled attempt leaves a resumable Running task; it is neither failed nor budget-exceeded.
        Assert.Contains("\"Status\":" + (int)AgentTaskStatus.Running, current.State, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModelCallAttemptTimeout_EndsTheSameOnBothPaths()
    {
        var current = await RunAsync(maf: false, Scenario.AttemptTimeout);
        var governed = await RunAsync(maf: true, Scenario.AttemptTimeout);

        AssertIdentical(current, governed);
        // The timeout is the runtime's, classified as a Timeout failure of the model call, with a single attempt (retry disabled).
        Assert.Contains($"\"FailureKind\":{(int)ModelFailureKind.Timeout}", string.Join('\n', current.Audit), StringComparison.Ordinal);
        Assert.Contains("\"Status\":" + (int)AgentTaskStatus.Failed, current.State, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAttemptDurationBudget_EndsTheSameOnBothPaths()
    {
        var current = await RunAsync(maf: false, Scenario.AttemptBudget);
        var governed = await RunAsync(maf: true, Scenario.AttemptBudget);

        AssertIdentical(current, governed);
        Assert.Contains("\"Status\":" + (int)AgentTaskStatus.BudgetExceeded, current.State, StringComparison.Ordinal);
    }

    private static void AssertIdentical(Outcome current, Outcome governed)
    {
        // A comparison of two empty runs would prove nothing: both paths reached the model and then hung in it.
        Assert.NotEmpty(current.ProviderRequests);
        Assert.Equal(current.ProviderRequests, governed.ProviderRequests);
        Assert.Equal(current.Audit, governed.Audit);
        Assert.Equal(current.State, governed.State);
        Assert.Equal(current.Ended, governed.Ended);
        Assert.Equal(current.Executions, governed.Executions);
        Assert.DoesNotContain(governed.Audit, line => line.Contains("ContextGateRejection", StringComparison.Ordinal));
    }

    private enum Scenario
    {
        CallerCancels,
        AttemptTimeout,
        AttemptBudget,
    }

    private static async Task<Outcome> RunAsync(bool maf, Scenario scenario)
    {
        var options = scenario switch
        {
            Scenario.AttemptTimeout => new AgentRunnerOptions
            {
                ModelCallMaxAttempts = 1,
                ModelCallAttemptTimeout = TimeSpan.FromMilliseconds(400),
                ModelCallBudget = TimeSpan.FromSeconds(5),
            },
            Scenario.AttemptBudget => new AgentRunnerOptions { MaxAttemptDuration = TimeSpan.FromMilliseconds(400) },
            _ => new AgentRunnerOptions(),
        };
        var tool = new CountingTool("test.read");
        var model = new HangingModel();
        var audit = new RecordingAuditSink();
        var store = new InMemoryTaskStore();
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        registry.Register(new PackageId("test.package"), tool);
        var inner = new ModelInvocationBroker(
            model, audit, TimeProvider.System, NullLogger.Instance, options,
            contextProjector: new MafContextProjector(options, NullLogger.Instance));
        IModelInvocationBroker broker = maf
            ? new MafGovernedTurnBroker(inner, audit, null, MafContextAssembly.InputOnly)
            : inner;
        var runner = new AgentRunner(
            model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), audit, store,
            TimeProvider.System, NullLogger<AgentRunner>.Instance, options, invocationBroker: broker);

        using var cancellation = new CancellationTokenSource();
        var run = runner.RunAsync("check the machine", Actor, TaskId, cancellation.Token);
        if (scenario == Scenario.CallerCancels)
        {
            // The hang is the second model call, after the plan: cancel once the model is really waiting in it.
            await model.WaitingInSecondCall.WaitAsync(TimeSpan.FromSeconds(30));
            await cancellation.CancelAsync();
        }

        string ended;
        try
        {
            var state = await run.WaitAsync(TimeSpan.FromSeconds(60));
            ended = "Returned:" + state.Status;
        }
        catch (OperationCanceledException)
        {
            ended = nameof(OperationCanceledException);
        }

        var persisted = await store.LoadAsync(TaskId);
        return new Outcome(
            [.. model.Requests.Select(Normalise)],
            [.. audit.Events.Select(evt => Normalise(JsonSerializer.Serialize(evt)))],
            persisted is null ? "<none>" : Normalise(JsonSerializer.Serialize(persisted)),
            ended,
            tool.Executions);
    }

    /// <summary>The same tolerated differences as <see cref="MafTurnEquivalenceTests"/>: ids, timestamps and measured durations.</summary>
    private static string Normalise(string json) => Regex.Replace(
        Regex.Replace(
            Regex.Replace(
                Regex.Replace(json, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "<guid>"),
                @"ev1:[0-9a-f]+|(?<![0-9a-f])[0-9a-f]{32}(?![0-9a-f])", "<guid>"),
            @"\d{4}-\d{2}-\d{2}T[\d:.]+(?:Z|[+-]\d{2}:\d{2})", "<ts>"),
        @"""(\w*(?:Duration|Elapsed|Latency)\w*)"":(?:\d+(?:\.\d+)?|""[\d:.]+"")", @"""$1"":0");

    private sealed record Outcome(IReadOnlyList<string> ProviderRequests, IReadOnlyList<string> Audit, string State, string Ended, int Executions);

    /// <summary>Answers the plan, then waits in the next call until its token is cancelled by the caller, the attempt timeout or the attempt budget.</summary>
    private sealed class HangingModel : IChatModel
    {
        private readonly TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public List<string> Requests { get; } = [];

        public Task WaitingInSecondCall => waiting.Task;

        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Requests.Add(JsonSerializer.Serialize(request));
            if (Interlocked.Increment(ref calls) == 1)
            {
                var step = new JsonObject { ["description"] = "run test.read", ["expectedTool"] = "test.read" };
                return new ModelResponse(
                    new JsonObject { ["rationale"] = "A plan.", ["steps"] = new JsonArray(step) }.ToJsonString(), [], false, null);
            }

            waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Unreachable: the wait only ends by cancellation.");
        }
    }

    private sealed class CountingTool(string name) : ITool
    {
        public int Executions { get; private set; }

        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "A deterministic fixture tool.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            Executions++;
            return Task.FromResult(ToolCallResult.Success("ok"));
        }
    }
}
