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
/// AF-TURN-01A equivalence: the same <see cref="AgentRunner"/> scenario is executed twice, once over the current
/// <see cref="ModelInvocationBroker"/> path and once with every model call run as a governed MAF turn
/// (<see cref="MafGovernedTurnBroker"/> in front of an identical broker), and what the provider, the audit sink and the task store
/// observed is compared. The comparison is over the canonical request the provider (the <see cref="IChatModel"/>) actually received
/// — after context projection — not over a fixture built for MAF. Offline: no live model, no network.
///
/// The only normalisations are the values that differ between two runs of the SAME path (verified by
/// <see cref="TheComparisonDetectsDivergence_AndNormalisationIsTheOnlyTolerance"/>): the task id (a new GUID per run), ISO-8601
/// timestamps, and wall-clock durations (the <c>*Duration*</c> fields). Tool-call ids, text, token usage and ordering are scripted and compared exactly.
/// </summary>
public sealed class MafTurnEquivalenceTests
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("equivalence");
    private static readonly ActorIdentity Resumer = new("api-user", "resumer", "Resumer");

    private static readonly ModelUsage Usage = new(120, 30, null);

    public static TheoryData<string> Scenarios =>
    [
        "planning-and-one-tool-step",
        "step-with-expected-tool-and-arguments",
        "several-tool-calls-in-one-reply",
        "compacted-context",
        "resume-from-persisted-state",
        "empty-final-output",
        "finish-reason-length",
        "context-overflow-recovery",
    ];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task AGovernedMafTurn_IsObservablyIdenticalToTheCurrentPath(string scenario)
    {
        var current = await RunAsync(scenario, maf: false);
        var governed = await RunAsync(scenario, maf: true);

        // A comparison of two empty runs would prove nothing.
        Assert.NotEmpty(current.ProviderRequests);
        Assert.Equal(current.ProviderRequests.Count, governed.ProviderRequests.Count);
        for (var index = 0; index < current.ProviderRequests.Count; index++)
        {
            Assert.Equal(current.ProviderRequests[index], governed.ProviderRequests[index]); // role, order, content, tools, provenance, params
        }

        Assert.Equal(current.Audit.Count, governed.Audit.Count);
        for (var index = 0; index < current.Audit.Count; index++)
        {
            Assert.Equal(current.Audit[index], governed.Audit[index]);
        }

        Assert.Equal(current.State, governed.State);
        Assert.Equal(current.Executions, governed.Executions);
        Assert.DoesNotContain(governed.Audit, line => line.Contains("ContextGateRejection", StringComparison.Ordinal));
        AssertScenarioWasNotVacuous(scenario, current);
    }

    [Fact]
    public async Task TheCompactionScenario_ReallyCompacts_AndTheDerivedSummaryKeepsADerivedAttributionOnBothPaths()
    {
        foreach (var maf in new[] { false, true })
        {
            var run = await RunAsync("compacted-context", maf);
            var last = JsonNode.Parse(run.ProviderRequests[^1])!;
            var history = last["History"]!.AsArray();
            var compacted = history.Where(turn => turn!["Content"]?.GetValue<string>().Contains("[Compacted tool interactions", StringComparison.Ordinal) == true).ToList();
            Assert.NotEmpty(compacted);
            Assert.All(compacted, turn =>
            {
                var provenance = turn!["Provenance"]!;
                Assert.True(provenance["Derived"]!.GetValue<bool>());
                Assert.NotEqual("Runtime", provenance["Origin"]!.GetValue<string>());
            });
        }
    }

    [Fact]
    public async Task TheMafTurn_PresentsTheModelAWireIdenticalToolSurface_AndNoToolIsExecutedByMaf()
    {
        var run = await RunAsync("several-tool-calls-in-one-reply", maf: true);

        // First call wins: the second call of the reply is fed back unexecuted and test.other runs once, at its own step. A framework
        // that executed what it was shown would have run it twice.
        Assert.Equal(1, run.Executions["test.read"]);
        Assert.Equal(1, run.Executions["test.other"]);
        // Each step is offered only its own allow-listed tool; MAF's declarations never widen it.
        Assert.Equal(["test.read"], OfferedTools(run.ProviderRequests[1]));
        Assert.Equal(["test.other", "runtime.evidence_read"], OfferedTools(run.ProviderRequests[2])); // plus the runtime's own control function
    }

    [Fact]
    public async Task TheComparisonDetectsDivergence_AndNormalisationIsTheOnlyTolerance()
    {
        var first = await RunAsync("planning-and-one-tool-step", maf: false);
        var second = await RunAsync("planning-and-one-tool-step", maf: false);
        Assert.Equal(first.State, second.State); // the normalisation absorbs exactly the per-run noise

        var different = await RunAsync("step-with-expected-tool-and-arguments", maf: false);
        Assert.NotEqual(first.State, different.State);
        Assert.NotEqual(first.ProviderRequests[1], different.ProviderRequests[1]);
    }

    private static IEnumerable<string> OfferedTools(string request) =>
        JsonNode.Parse(request)!["AvailableTools"]!.AsArray().Select(tool => tool!["Name"]!.GetValue<string>());

    private static void AssertScenarioWasNotVacuous(string scenario, Outcome run)
    {
        switch (scenario)
        {
            case "compacted-context":
                Assert.Contains(run.ProviderRequests, request => request.Contains("[Compacted tool interactions", StringComparison.Ordinal));
                break;
            case "empty-final-output":
            case "finish-reason-length":
                Assert.Contains(run.ProviderRequests, request => request.Contains("\"Role\":1", StringComparison.Ordinal)
                    && request.Contains("\"Origin\":\"Runtime\"", StringComparison.Ordinal));
                break;
            case "context-overflow-recovery":
                Assert.Contains(run.Audit, line => line.Contains($"\"FailureKind\":{(int)ModelFailureKind.ContextOverflow}", StringComparison.Ordinal));
                break;
            case "resume-from-persisted-state":
                Assert.Contains("\"ExecutionAttempt\":2", run.State, StringComparison.Ordinal);
                break;
        }
    }

    // ---- scenarios ----

    private static async Task<Outcome> RunAsync(string scenario, bool maf)
    {
        var tools = new List<CountingTool> { new("test.read", "ok") };
        var options = new AgentRunnerOptions();
        var store = new InMemoryTaskStore();
        TaskState? seeded = null;
        List<object> script;
        switch (scenario)
        {
            case "planning-and-one-tool-step":
                script = [Plan(Step("test.read")), Call("call-1", "test.read"), Final("Done.")];
                break;
            case "step-with-expected-tool-and-arguments":
                tools = [new("test.read", "ok", [new ToolParameter("path", ToolParameterType.String, "A path.")])];
                script =
                [
                    Plan(Step("test.read", new JsonObject { ["path"] = "C:/tmp" })),
                    Call("call-1", "test.read", new JsonObject { ["path"] = "C:/tmp" }),
                    Final("Done."),
                ];
                break;
            case "several-tool-calls-in-one-reply":
                tools = [new("test.read", "ok"), new("test.other", "other")];
                script =
                [
                    Plan(Step("test.read"), Step("test.other")),
                    new ModelResponse(null,
                    [
                        new ModelToolCall("call-1", "test.read", ToolArguments.Empty),
                        new ModelToolCall("call-2", "test.other", ToolArguments.Empty),
                    ], false, Usage),
                    Call("call-3", "test.other"),
                    Final("Done."),
                ];
                break;
            case "compacted-context":
                tools = [.. Enumerable.Range(1, 5).Select(n => new CountingTool($"test.t{n}", new string((char)('a' + n), 6000)))];
                options = new AgentRunnerOptions
                {
                    Context = new ModelContextOptions
                    {
                        SafetyReserveTokens = 0,
                        MinimumPreservedToolGroups = 1,
                        MinimumPreservedTurns = 1,
                        Profiles = [new ModelContextProfile { Provider = "fake", Model = "fake-model", MaxContextTokens = 6000, ReservedOutputTokens = 200 }],
                    },
                };
                script =
                [
                    Plan([.. tools.Select(tool => Step(tool.Manifest.Name))]),
                    .. tools.Select((tool, index) => Call($"call-{index + 1}", tool.Manifest.Name)),
                    Final("Done."),
                ];
                break;
            case "resume-from-persisted-state":
                seeded = Stored();
                store.Seed(seeded);
                script = [Call("call-r", "test.read"), Final("done"), Plan(), Final("done")];
                break;
            case "empty-final-output":
                script = [Plan(Step("test.read")), Call("call-1", "test.read"), new ModelResponse(null, [], true, Usage), Final("Done.")];
                break;
            case "finish-reason-length":
                script =
                [
                    Plan(Step("test.read")), Call("call-1", "test.read"),
                    new ModelResponse(null, [], true, Usage) { Details = new ModelCallDetails("fake-model", "length", null, null) },
                    Final("Done."),
                ];
                break;
            case "context-overflow-recovery":
                script =
                [
                    Plan(Step("test.read")), Call("call-1", "test.read"),
                    new ModelProtocolException("context too long") { FailureKind = ModelFailureKind.ContextOverflow },
                    Final("Done."),
                ];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        var model = new ScriptedModel(script);
        var audit = new RecordingAuditSink();
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        var inner = new ModelInvocationBroker(
            model, audit, TimeProvider.System, NullLogger.Instance, options,
            contextProjector: new MafContextProjector(options, NullLogger.Instance));
        IModelInvocationBroker broker = maf ? new MafGovernedTurnBroker(inner, audit) : inner;
        var runner = new AgentRunner(
            model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), audit, store,
            TimeProvider.System, NullLogger<AgentRunner>.Instance, options, invocationBroker: broker);

        TaskState state;
        if (scenario == "resume-from-persisted-state")
        {
            state = await runner.ResumeAsync(seeded!, Resumer);
        }
        else
        {
            state = await runner.RunAsync("check the machine", Actor);
        }

        return new Outcome(
            [.. model.Requests.Select(Normalise)],
            [.. audit.Events.Select(evt => Normalise(JsonSerializer.Serialize(evt)))],
            Normalise(JsonSerializer.Serialize(state)),
            tools.ToDictionary(tool => tool.Manifest.Name, tool => tool.Executions));
    }

    /// <summary>
    /// The complete list of tolerated differences, each one a value that is different between two runs of the SAME path: the task
    /// id in dashed or 32-hex form (also the base of the evidence ids <c>ev1:...</c>), ISO-8601 timestamps and measured wall-clock durations (fields named <c>*Duration*</c>).
    /// </summary>
    private static string Normalise(string json) => Regex.Replace(
        Regex.Replace(
            Regex.Replace(
                Regex.Replace(json, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "<guid>"),
                @"ev1:[0-9a-f]+|(?<![0-9a-f])[0-9a-f]{32}(?![0-9a-f])", "<guid>"),
            @"\d{4}-\d{2}-\d{2}T[\d:.]+(?:Z|[+-]\d{2}:\d{2})", "<ts>"),
        @"""(\w*Duration\w*)"":(?:\d+|""[\d:.]+"")", @"""$1"":0");

    private static ModelResponse Plan(params JsonObject[] steps) =>
        new(new JsonObject { ["rationale"] = "A plan.", ["steps"] = new JsonArray([.. steps]) }.ToJsonString(), [], false, null);

    private static JsonObject Step(string tool, JsonObject? arguments = null)
    {
        var step = new JsonObject { ["description"] = $"run {tool}", ["expectedTool"] = tool };
        if (arguments is not null)
        {
            step["expectedArguments"] = arguments;
        }

        return step;
    }

    private static ModelResponse Call(string id, string tool, JsonObject? arguments = null) =>
        new(null, [new ModelToolCall(id, tool, arguments is null ? ToolArguments.Empty : ToolArguments.FromJson(arguments))], false, Usage);

    private static ModelResponse Final(string text) => new(text, [], true, Usage);

    private static TaskState Stored()
    {
        ModelToolCall call = new("call-0", "test.read", ToolArguments.Empty);
        return new TaskState(
            Guid.NewGuid(), NodeId.Local, "check the machine", AgentTaskStatus.Failed,
            [
                new PlanStep(0, "test.read", call, ToolCallResult.Success("ok"), "ok", 0) { ExecutionAttempt = 1 },
                new PlanStep(1, "Model protocol failure", null, null, "The provider failed."),
            ],
            [new AgentPlan(0, "plan 0", [.. Enumerable.Range(0, 100).Select(index => new PlannedStep(index, $"step {index}", "test.read"))])],
            DateTimeOffset.UtcNow)
        {
            ExecutionAttempt = 1,
            Origin = TaskOrigin.Ordinary,
            Accounting = new TaskAccounting(100, 1, 0),
            MutationJournalMode = TaskMutationJournalMode.Journaled,
        };
    }

    private sealed record Outcome(
        IReadOnlyList<string> ProviderRequests, IReadOnlyList<string> Audit, string State, IReadOnlyDictionary<string, int> Executions);

    /// <summary>A scripted provider that records, as JSON, the exact request it received (after bOps projected the context).</summary>
    private sealed class ScriptedModel(IReadOnlyList<object> script) : IChatModel
    {
        private int next;

        public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

        public List<string> Requests { get; } = [];

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            Requests.Add(JsonSerializer.Serialize(request));
            if (next >= script.Count)
            {
                throw new InvalidOperationException($"Unscripted model call {next + 1}.");
            }

            return script[next++] switch
            {
                ModelResponse response => Task.FromResult(response),
                Exception exception => Task.FromException<ModelResponse>(exception),
                _ => throw new InvalidOperationException("Bad script entry."),
            };
        }
    }

    private sealed class CountingTool(string name, string output, IReadOnlyList<ToolParameter>? parameters = null) : ITool
    {
        public int Executions { get; private set; }

        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "A deterministic fixture tool.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = parameters ?? [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            Executions++;
            return Task.FromResult(ToolCallResult.Success(output));
        }
    }
}
