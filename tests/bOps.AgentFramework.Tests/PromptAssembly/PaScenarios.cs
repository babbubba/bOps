// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every world, process and HTTP client passes to the scenario, which disposes it before returning.
#pragma warning disable CA2000

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.AgentFramework;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Packages.Providers.OpenAiCompatible;
using bOps.Runtime;
using bOps.Runtime.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.AgentFramework.Tests.PromptAssembly;

/// <summary>What a PA-1 run needs besides its goal and its scripted upstream.</summary>
internal sealed record PaSetup(string Goal, IReadOnlyList<PaReply> Script)
{
    internal IReadOnlyList<PaTool>? Tools { get; init; }

    internal AgentRunnerOptions? Options { get; init; }

    internal IKnowledgeTaskHost? KnowledgeHost { get; init; }

    internal ITaskStore? Store { get; init; }

    internal ActorIdentity? Actor { get; init; }

    /// <summary>AE-1: the policy engine of the run; absent means <c>DefaultTestPolicyEngine</c> (Read is automatic, the rest is forbidden).</summary>
    internal IPolicyEngine? Policy { get; init; }

    /// <summary>Also run every call as one governed MAF turn (the opt-in broker; no production host composes it).</summary>
    internal bool ViaMafTurn { get; init; }
}

/// <summary>
/// PA-1: composes the real pipeline against an offline upstream. State of the agent → <c>AgentRunner</c> request building →
/// <c>ModelInvocationBroker</c> with the <c>MafContextProjector</c> (the composition of <c>bOps.Api</c> and <c>bOps.Cli</c>) →
/// the real <c>OpenAiCompatibleChatModel</c> → the HTTP body, captured by <see cref="PaUpstream"/>. Nothing is simulated except the
/// model's answer and the tools' output.
/// </summary>
internal static class PaCompose
{
    internal static readonly Guid TaskId = new("7a1c0de1-0000-4a11-8000-000000000001");

    internal static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("pa1");

    internal static async Task<PaRun> RunAsync(PaSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var upstream = new PaUpstream(setup.Script);
        using var http = new HttpClient(upstream, disposeHandler: false);
        var wire = new OpenAiCompatibleChatModel(new ChatModelOptions("LlamaCpp", "http://pa1.invalid/v1", null, "pa1-fixture-model"), http);
        var tap = new PaTapModel(wire);

        var tools = (setup.Tools ?? PaFixtures.StandardTools()).ToDictionary(tool => tool.Manifest.Name, StringComparer.Ordinal);
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools.Values)
        {
            registry.Register(new PackageId("pa1.package"), tool);
        }

        var options = setup.Options ?? new AgentRunnerOptions();
        var audit = new RecordingAuditSink();
        IModelInvocationBroker broker = new ModelInvocationBroker(
            tap, audit, TimeProvider.System, NullLogger.Instance, options,
            contextProjector: new MafContextProjector(options, NullLogger.Instance));
        if (setup.ViaMafTurn)
        {
            broker = new MafGovernedTurnBroker(broker, audit, null, MafContextAssembly.BopsProvider);
        }

        var runner = new AgentRunner(
            tap, registry, setup.Policy ?? new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), audit, setup.Store ?? new InMemoryTaskStore(),
            TimeProvider.System, NullLogger<AgentRunner>.Instance, options, invocationBroker: broker, knowledgeHost: setup.KnowledgeHost);
        var state = await runner.RunAsync(setup.Goal, setup.Actor ?? Actor, TaskId);
        return new PaRun
        {
            State = state,
            Bodies = [.. upstream.Bodies],
            Seen = [.. tap.Requests],
            Unconsumed = upstream.Unconsumed,
            Tools = tools,
            Audit = audit,
        };
    }
}

/// <summary>PA-1: the synthetic tools. Their names, descriptions and outputs are invented; nothing here names a real machine or user.</summary>
internal static class PaFixtures
{
    internal const string Goal = "Check whether the synthetic service alpha is healthy and how full the synthetic disk is.";

    internal const string Guidance = "Prefer summary for an overview; use detail only to inspect individual records.";

    private static ToolCallResult Complete(string output) =>
        ToolCallResult.Success(output) with { Completeness = ToolResultCompleteness.Complete };

    internal static PaTool Read(string output = "service alpha: running; restarts 0; uptime 4d") => new(
        "test.read", "Reads the synthetic service status. Read-only.", Complete(output),
        [
            new ToolParameter("mode", ToolParameterType.String, "Output mode.", Required: false)
            {
                AllowedValues = ["summary", "detail"],
                PlanningGuidance = Guidance,
            },
        ]);

    internal static PaTool Other() => new(
        "test.other", "Reads the synthetic disk usage. Read-only.", Complete("volume data: 61% used of 500 GB"),
        [new ToolParameter("limit", ToolParameterType.Integer, "Maximum entries.", Required: false) { Minimum = 1, Maximum = 100 }]);

    internal static IReadOnlyList<PaTool> StandardTools() => [Read(), Other()];

    /// <summary>A deterministic result larger than the observation limit, so the persisted evidence needs a drill-down.</summary>
    internal static string LongListing()
    {
        var text = new StringBuilder();
        for (var line = 1; line <= 75; line++)
        {
            text.Append(CultureInfo.InvariantCulture, $"record {line:D3}: synthetic worker w{line % 7} state=idle queue={line * 3} note=ok\n");
        }

        return text.ToString();
    }

    /// <summary>A tool whose result persists typed facts, like a package's fact builders do.</summary>
    internal static PaTool Facts() => new(
        "test.events", "Reads the synthetic event counters. Read-only.",
        ToolCallResult.Success("{\"groups\":[{\"source\":\"alpha\",\"count\":3}]}") with
        {
            Completeness = ToolResultCompleteness.Complete,
            Facts =
            [
                new EvidenceFact("synthetic.events.matched-count", "source=alpha;id=19", ToolParameterType.Integer, JsonValue.Create(3)),
                new EvidenceFact("synthetic.events.any-critical", "source=alpha", ToolParameterType.Boolean, JsonValue.Create(false)),
            ],
        },
        []);
}

/// <summary>PA-1: the scenario scripts. Each is a fixed list of model answers; the golden of a scenario picks the requests that matter.</summary>
internal static class PaScenarios
{
    internal const string Final = "The synthetic service alpha is running and the synthetic disk is 61% used.";
    internal const string Premature = "The service looks healthy, so nothing else is needed.";
    internal const string GroundedAnswer = "The synthetic event source alpha reported 3 matching events and no critical event.";
    private const string TruncatedPlan = "{\"rationale\":\"the remaining work\",\"steps\":[{\"description\":\"";

    /// <summary>
    /// Two planned steps are run, a third call proposes a tool once the plan is complete, the runtime replans (an empty plan), the model
    /// answers. Provider requests: 0 planning, 1 step 0 pending, 2 step 1 pending, 3 plan complete, 4 replan, 5 final step.
    /// </summary>
    internal static PaSetup PlanExhaustedThenOrdinaryReplan() => new(PaFixtures.Goal,
    [
        PaReply.Plan("Read the service, then the disk.", "test.read", "test.other"),
        PaReply.Call("call-1", "test.read"),
        PaReply.Call("call-2", "test.other"),
        PaReply.Call("call-3", "test.read"),
        PaReply.Plan("Nothing further is needed."),
        PaReply.Text(Final),
    ]);

    /// <summary>
    /// The model answers after the first step with the second still unresolved; the runtime replans. Provider requests: 0 planning,
    /// 1 step 0, 2 step 1 (draws the premature final), 3 early-final replan, 4 step 1 again, 5 final.
    /// </summary>
    internal static PaSetup EarlyFinalReplan() => new(PaFixtures.Goal,
    [
        PaReply.Plan("Read the service, then the disk.", "test.read", "test.other"),
        PaReply.Call("call-1", "test.read"),
        PaReply.Text(Premature),
        PaReply.Plan("Only the disk remains.", "test.other"),
        PaReply.Call("call-2", "test.other"),
        PaReply.Text(Final),
    ]);

    /// <summary>
    /// A large result is persisted and the model reads it back through the control function. Provider requests: 0 planning,
    /// 1 step 0, 2 step 1 with the evidence index (draws the read), 3 the continuation after the read (draws the call), 4 final.
    /// </summary>
    internal static PaSetup EvidenceRead() => new(PaFixtures.Goal,
    [
        PaReply.Plan("Read the service, then the disk.", "test.read", "test.other"),
        PaReply.Call("call-1", "test.read"),
        PaReply.EvidenceRead("read-1", 0),
        PaReply.Call("call-2", "test.other"),
        PaReply.Text(Final),
    ])
    {
        Tools = [PaFixtures.Read(PaFixtures.LongListing()), PaFixtures.Other()],
    };

    /// <summary>
    /// The task has an authorized pinned knowledge pack and reads one resource before acting. Provider requests: 0 planning,
    /// 1 step 0 with the index (draws the read), 2 the continuation after the knowledge read (draws the call), 3 step 1, 4 final.
    /// </summary>
    internal static async Task<PaRun> Knowledge(bool viaMafTurn = false)
    {
        using var world = new KnowledgeRunnerWorld();
        var pack = new TestPack { Kind = PackKind.Knowledge };
        var raw = pack.Build();
        world.Source.Add(KnowledgeRunnerWorld.TenantA, pack.PackId, pack.Version, () => raw);
        var reference = new PackReference(pack.PackId, pack.Version);
        using var process = world.Boot(
            [new PackGrant(KnowledgeRunnerWorld.TenantA, reference, pack.Kind)],
            bindings: new Dictionary<TenantId, PackReference> { [KnowledgeRunnerWorld.TenantA] = reference });
        return await PaCompose.RunAsync(new PaSetup("diagnose nginx",
        [
            PaReply.Plan("Read the service, then the disk.", "test.read", "test.other"),
            PaReply.Knowledge("k1", "read_resource", "nginx-triage", "references/checklist.md"),
            PaReply.Call("call-1", "test.read"),
            PaReply.Call("call-2", "test.other"),
            PaReply.Text(Final),
        ])
        {
            KnowledgeHost = process.Host,
            Store = process.Store,
            Actor = KnowledgeRunnerWorld.ActorA,
            ViaMafTurn = viaMafTurn,
        });
    }

    /// <summary>
    /// The model answers once a tool with typed facts ran; the runtime checks the answer against the facts. Provider requests:
    /// 0 planning, 1 step 0, 2 the final synthesis, 3 the grounding check.
    /// </summary>
    internal static PaSetup Grounding() => new(PaFixtures.Goal,
    [
        PaReply.Plan("Read the counters.", "test.events"),
        PaReply.Call("call-1", "test.events"),
        PaReply.Text(GroundedAnswer),
        PaReply.Text("{\"contradictions\":[]}"),
    ])
    {
        Tools = [PaFixtures.Facts()],
    };

    /// <summary>
    /// The model answers with nothing while a step is pending (RC1). Provider requests: 0 planning, 1 step 0, 2 step 1 (empty reply),
    /// 3 the same step after the retry instruction, 4 final.
    /// </summary>
    internal static PaSetup EmptyReply() => new(PaFixtures.Goal,
    [
        PaReply.Plan("Read the service, then the disk.", "test.read", "test.other"),
        PaReply.Call("call-1", "test.read"),
        PaReply.Empty(),
        PaReply.Call("call-2", "test.other"),
        PaReply.Text(Final),
    ]);

    /// <summary>The same step answered with nothing twice: the single retry is spent and the task fails. Four provider requests, no fifth.</summary>
    internal static PaSetup EmptyReplyTwice() => new(PaFixtures.Goal,
    [
        PaReply.Plan("Read the service, then the disk.", "test.read", "test.other"),
        PaReply.Call("call-1", "test.read"),
        PaReply.Empty(),
        PaReply.Empty(),
    ]);

    /// <summary>
    /// The model stops at the output limit with no content while a step is pending (RC2, pending variant). Provider requests:
    /// 0 planning, 1 step 0, 2 step 1 (output-limited reply), 3 the same step after the retry instruction, 4 final.
    /// </summary>
    internal static PaSetup OutputLimitedWhilePending() => new(PaFixtures.Goal,
    [
        PaReply.Plan("Read the service, then the disk.", "test.read", "test.other"),
        PaReply.Call("call-1", "test.read"),
        PaReply.OutputLimited(),
        PaReply.Call("call-2", "test.other"),
        PaReply.Text(Final),
    ]);

    /// <summary>
    /// The model stops at the output limit with no content once the plan is complete (RC2, exhausted variant). Provider requests:
    /// 0 planning, 1 step 0, 2 plan complete (output-limited reply), 3 the same state after the retry instruction.
    /// </summary>
    internal static PaSetup OutputLimitedWhenComplete() => new(PaFixtures.Goal,
    [
        PaReply.Plan("Read the service.", "test.read"),
        PaReply.Call("call-1", "test.read"),
        PaReply.OutputLimited(),
        PaReply.Text(Final),
    ]);

    /// <summary>
    /// LIVE-05: an early final, then an early-final replan cut at the output limit, then the continuation of the persisted plan.
    /// Provider requests: 0 planning, 1 step 0, 2 step 1 (draws the premature final), 3 the truncated replan, 4 the continuation
    /// step, 5 final.
    /// </summary>
    internal static PaSetup EarlyFinalContinuation() => new(PaFixtures.Goal,
    [
        PaReply.Plan("Read the service, then the disk.", "test.read", "test.other"),
        PaReply.Call("call-1", "test.read"),
        PaReply.Text(Premature),
        PaReply.Truncated(TruncatedPlan),
        PaReply.Call("call-2", "test.other"),
        PaReply.Text(Final),
    ]);
}
