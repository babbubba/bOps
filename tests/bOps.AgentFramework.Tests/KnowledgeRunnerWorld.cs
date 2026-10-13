// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every disposable the worlds and processes build passes to the test, which disposes the world.
#pragma warning disable CA2000

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Audit;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Memory;
using bOps.Packages.Sys.Windows;
using bOps.Runtime;
using bOps.Runtime.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// What survives a process restart in the KNOW-01B tests: the disk (snapshots, the SQLite task store, the JSON-lines audit file), the
/// external pack source and the audit trail. Everything else lives in a <see cref="KnowledgeProcess"/>, which a test throws away and
/// rebuilds to prove that a resumed task needs nothing but what was persisted.
/// </summary>
internal sealed class KnowledgeRunnerWorld : IDisposable
{
    private readonly TempDirectory directory = new();
    private readonly JsonLinesAuditSink fileAudit;
    private readonly List<IDisposable> owned = [];

    internal KnowledgeRunnerWorld()
    {
        fileAudit = new JsonLinesAuditSink(AuditFile);
        Audit = new TeeAuditSink(new ConcurrentAuditSink(), fileAudit);
    }

    internal static TenantId TenantA { get; } = new("tenant-a");

    internal static TenantId TenantB { get; } = new("tenant-b");

    internal static ActorIdentity ActorA { get; } = new("os-user", "tenant-a", null);

    internal static ActorIdentity ActorB { get; } = new("os-user", "tenant-b", null);

    /// <summary>The external pack source: it outlives every process, like a distribution channel does.</summary>
    internal InMemoryPackSource Source { get; } = new();

    internal TeeAuditSink Audit { get; }

    internal string SnapshotRoot => directory.Sub("snapshots");

    internal string StorePath => directory.Sub("tasks.db");

    internal string AuditFile => directory.Sub("audit.jsonl");

    internal IReadOnlyList<AuditEvent> Events => Audit.Recorded.Events;

    /// <summary>A new set of runtime instances over the same disk, source and audit trail.</summary>
    internal KnowledgeProcess Boot(
        IEnumerable<PackGrant> grants,
        KnowledgeTrustStore? trust = null,
        IEnumerable<TenantId>? providerTenants = null,
        IReadOnlyDictionary<TenantId, PackReference>? bindings = null,
        KnowledgeReadBudget? budget = null)
    {
        var process = new KnowledgeProcess(this, grants, trust ?? new KnowledgeTrustStore([TestPublisher.Acme.Trusted()]),
            providerTenants ?? [TenantA, TenantB], bindings ?? new Dictionary<TenantId, PackReference>(), budget);
        owned.Add(process);
        return process;
    }

    public void Dispose()
    {
        foreach (var process in owned)
        {
            process.Dispose();
        }

        fileAudit.Dispose();
        directory.Dispose();
    }
}

/// <summary>Everything a bOps process builds at start-up: registry, admission service, host, task store, tools and runner.</summary>
internal sealed class KnowledgeProcess : IDisposable
{
    private readonly KnowledgeRunnerWorld world;
    private readonly List<IDisposable> owned = [];

    internal KnowledgeProcess(
        KnowledgeRunnerWorld world,
        IEnumerable<PackGrant> grants,
        KnowledgeTrustStore trust,
        IEnumerable<TenantId> providerTenants,
        IReadOnlyDictionary<TenantId, PackReference> bindings,
        KnowledgeReadBudget? budget)
    {
        this.world = world;
        Registry = new KnowledgeProviderRegistry();
        ProviderId = Registry.Register(
            world.Source, "test-source", KnowledgeProviderTrust.Trusted, [PackKind.Knowledge, PackKind.Experience, PackKind.Skill],
            [.. providerTenants]);
        Service = new KnowledgeAdmissionService(
            Registry, new KnowledgeGrants(grants), new KnowledgePackVerifier(trust, TestEngine.Version), new KnowledgeSnapshotStore(world.SnapshotRoot));
        Policy = new TestPolicy(world.Source, bindings);
        Host = new KnowledgeTaskHost(Service, new ActorIdTenantAuthority(), Policy, world.Audit, budget: budget);
        Store = new SqliteTaskStore(world.StorePath);
    }

    internal KnowledgeProviderRegistry Registry { get; }

    internal string ProviderId { get; }

    internal KnowledgeAdmissionService Service { get; }

    internal TestPolicy Policy { get; }

    internal KnowledgeTaskHost Host { get; }

    internal SqliteTaskStore Store { get; }

    /// <summary>The real runner over the real broker and MAF turn path, a scripted model, the process's store and one read tool.</summary>
    internal RunnerRig Runner(
        IEnumerable<ModelResponse> script,
        IKnowledgeTaskHost? host = null,
        bool withHost = true,
        ITaskStore? store = null,
        Action<int>? beforeResponse = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        var model = new ScriptedRunnerModel(script) { BeforeResponse = beforeResponse };
        var composed = ComposeCanary(model, host, withHost, store);
        return new RunnerRig(composed.Runner, model, composed.Maf, composed.Read, composed.Other, composed.Store);
    }

    /// <summary>
    /// The explicitly selected test-host composition used by HOST-CANARY-01A. It accepts a caller-supplied model but reuses the
    /// normal runner, the one governed MAF broker and the normal knowledge host; it is not another executor or a production host.
    /// Windows diagnostics require a separate explicit opt-in and replace the dummy tool surface. This selection is independent
    /// of withHost, so a future benchmark can use the same operational catalog with or without an authorized knowledge host.
    /// </summary>
    internal CanaryRig ComposeCanary(
        IChatModel model,
        IKnowledgeTaskHost? host = null,
        bool withHost = true,
        ITaskStore? store = null,
        ILogger<AgentRunner>? diagnosticLogger = null,
        bool withWindowsSystemEvents = false,
        CanaryEventsActivation? eventsCorpus = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var read = new RecordingReadTool("test.read", []);
        var other = new RecordingReadTool("test.other", []);
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        if (eventsCorpus is not null && !withWindowsSystemEvents)
            throw new InvalidOperationException("Event corpus requires the explicit Windows diagnostic canary.");
        if (withWindowsSystemEvents)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The diagnostic canary requires Windows.");
            }

            // Use the existing registration factory, selecting only the native Read tool. Never register the provider's
            // remaining surface (including process mutation). Optional corpus decoration stays here, with the same manifest
            // and normal dispatch; it cannot change a product registry or bypass runtime governance.
            var events = new WindowsSystemToolProvider().GetTools().OfType<WindowsSystemEventsTool>().Single();
            registry.Register(new PackageId("bops.packages.system.windows"),
                eventsCorpus is null ? events : new CanaryEventsCorpusTool(events, eventsCorpus));
        }
        else
        {
            registry.Register(new PackageId("test.package"), read);
            registry.Register(new PackageId("test.package"), other);
        }
        var options = new AgentRunnerOptions();
        var inner = new ModelInvocationBroker(
            model, world.Audit, TimeProvider.System, diagnosticLogger ?? (ILogger)NullLogger.Instance, options, contextProjector: new MafContextProjector(options, diagnosticLogger ?? (ILogger)NullLogger.Instance));
        var maf = new MafGovernedTurnBroker(inner, world.Audit, null, MafContextAssembly.BopsProvider);
        var taskStore = store ?? Store;
        var runner = new AgentRunner(
            model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), world.Audit, taskStore,
            TimeProvider.System, diagnosticLogger ?? NullLogger<AgentRunner>.Instance, options, invocationBroker: maf,
            knowledgeHost: withHost ? host ?? Host : null);
        return new CanaryRig(runner, maf, read, other, taskStore, registry);
    }

    public void Dispose()
    {
        foreach (var item in owned)
        {
            item.Dispose();
        }
    }
}

/// <summary>The runner and the doubles around it, so a test can assert what the model saw and what ran.</summary>
internal sealed record RunnerRig(
    AgentRunner Runner, ScriptedRunnerModel Model, MafGovernedTurnBroker Maf, RecordingReadTool Read, RecordingReadTool Other, ITaskStore Store);

/// <summary>The reusable result of the isolated canary composition; no member exposes a second dispatch path.</summary>
internal sealed record CanaryRig(
    AgentRunner Runner, MafGovernedTurnBroker Maf, RecordingReadTool Read, RecordingReadTool Other, ITaskStore Store, IToolRegistry Registry);

/// <summary>The host's knowledge policy for tests: tenant to pack, and the one registered source.</summary>
internal sealed class TestPolicy(InMemoryPackSource source, IReadOnlyDictionary<TenantId, PackReference> bindings) : IKnowledgeTaskPolicy
{
    /// <summary>When <c>false</c> the source is no longer reachable for a pinned pack, as when a channel is withdrawn.</summary>
    internal bool SourceReachable { get; set; } = true;

    public KnowledgeBindingChoice? ChooseFor(ActorIdentity actor, KnowledgeTaskScope scope) =>
        bindings.TryGetValue(scope.Tenant, out var reference) ? new KnowledgeBindingChoice(source, reference) : null;

    public IKnowledgePackSource? SourceFor(KnowledgeTaskScope scope, PackReference reference) => SourceReachable ? source : null;
}

/// <summary>Writes to the recording sink and to the real JSON-lines sink, so a test can assert both what happened and what was persisted.</summary>
internal sealed class TeeAuditSink(ConcurrentAuditSink recorded, IAuditSink persisted) : IAuditSink
{
    internal ConcurrentAuditSink Recorded { get; } = recorded;

    public async Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
    {
        await Recorded.WriteAsync(evt, ct);
        await persisted.WriteAsync(evt, ct);
    }
}

/// <summary>
/// The scripted provider behind the real broker. An unscripted call throws, so a model call nobody planned cannot go unnoticed, and
/// every request is kept for assertion.
/// </summary>
internal sealed class ScriptedRunnerModel(IEnumerable<ModelResponse> script) : IChatModel
{
    private readonly Queue<ModelResponse> queue = new(script);

    public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

    public List<ModelRequest> Requests { get; } = [];

    /// <summary>Runs before the response with the given zero-based request index is returned: a hook for a side effect between two model calls.</summary>
    public Action<int>? BeforeResponse { get; init; }

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        BeforeResponse?.Invoke(Requests.Count - 1);
        return queue.Count > 0 ? Task.FromResult(queue.Dequeue()) : throw new InvalidOperationException("Unscripted model call.");
    }
}

/// <summary>Builders for the scripted replies.</summary>
internal static class Script
{
    internal static readonly ModelUsage Usage = new(100, 10, null);

    internal static ModelResponse Plan(params string[] tools) =>
        new(new JsonObject
        {
            ["rationale"] = "A plan.",
            ["steps"] = new JsonArray([.. tools.Select(tool => (JsonNode)new JsonObject { ["description"] = $"run {tool}", ["expectedTool"] = tool })]),
        }.ToJsonString(), [], false, null);

    internal static ModelResponse Tool(string id, string tool) =>
        new(null, [new ModelToolCall(id, tool, ToolArguments.Empty)], false, Usage);

    internal static ModelResponse Tools(params (string Id, string Tool)[] calls) =>
        new(null, [.. calls.Select(call => new ModelToolCall(call.Id, call.Tool, ToolArguments.Empty))], false, Usage);

    internal static ModelResponse Final(string text) => new(text, [], true, Usage);

    internal static ModelResponse Knowledge(string id, string operation, string? skill = null, string? resource = null)
    {
        var arguments = new JsonObject { ["operation"] = operation };
        if (skill is not null)
        {
            arguments["skill"] = skill;
        }

        if (resource is not null)
        {
            arguments["resource"] = resource;
        }

        return KnowledgeRaw(id, arguments);
    }

    internal static ModelResponse KnowledgeRaw(string id, JsonObject arguments) =>
        new(null, [new ModelToolCall(id, KnowledgeReadBridge.PublicToolName, ToolArguments.FromJson(arguments))], false, Usage);

    /// <summary>One reply that carries several calls in the order given.</summary>
    internal static ModelResponse Calls(params ModelToolCall[] calls) => new(null, calls, false, Usage);

    internal static ModelToolCall KnowledgeCall(string id, string operation, string? skill = null, string? resource = null) =>
        Knowledge(id, operation, skill, resource).ToolCalls[0];
}
