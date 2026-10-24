// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every world, store and host passes to the test, which disposes the world.
#pragma warning disable CA2000

using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Cli;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Memory;
using bOps.Policy;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.Cli.Tests;

/// <summary>
/// ADR-0058: everything the CLI's Knowledge composition needs, on disk and in memory, with throw-away test keys only. The packs are
/// written exactly as a publisher ships them, the trust store is a real file, the configuration is the real <c>Knowledge</c> section read
/// by the real <see cref="CliKnowledge"/>, and the runner is the one <see cref="CliRunnerComposition"/> builds for <c>Program.cs</c>.
/// Nothing here calls a model, a server or the network, and no tool reads the machine.
/// </summary>
internal sealed class CliKnowledgeWorld : IDisposable
{
    internal const string PackId = "bsoft.windows-event-runbooks";
    internal const string PackVersion = "1.0.0";
    internal const string Goal = "Analyze the Windows events of the last 24 hours, identify stability problems and separate evidence from possible causes.";

    /// <summary>A marker only the pack's resource contains, so a test can tell the model was served the procedure.</summary>
    internal const string ProcedureMarker = "PROCEDURE-MARKER-41";

    internal const string Description = "Procedure to interpret Windows event evidence and separate observations from possible causes";

    private const string Resource = "windows-event-triage/references/interpretation.md";

    private readonly TempDirectory directory = new();
    private readonly List<IDisposable> owned = [];

    internal CliKnowledgeWorld()
    {
        Directory.CreateDirectory(PacksDirectory);
        Install(EventTriagePack());
        WriteTrust(TestPublisher.Acme.Trusted());
    }

    internal static ActorIdentity Operator { get; } = ActorIdentity.FromOperatingSystemUser("alice");

    internal ConcurrentAuditSink Audit { get; } = new();

    /// <summary>BOPS-0122: compose the runners of this world with the MAF governed turn host (C01-R22). Off, as in the product default.</summary>
    internal bool MafGovernedTurn { get; set; }

    internal string Root => directory.Path;

    internal string PacksDirectory => directory.Sub("packs");

    internal string SnapshotsDirectory => directory.Sub("snapshots");

    internal string TrustStorePath => directory.Sub("publisher-trust.json");

    internal string TasksFile => directory.Sub("tasks.db");

    internal IReadOnlyList<AuditEvent> Events => Audit.Events;

    /// <summary>The pack the pilot is about: one skill, one resource, kind Knowledge, signed by the (throw-away) test publisher.</summary>
    internal static TestPack EventTriagePack(string version = PackVersion, TestPublisher? signer = null, string resource = DefaultResource) => new()
    {
        PackId = PackId,
        Version = version,
        Kind = PackKind.Knowledge,
        Signer = signer ?? TestPublisher.Acme,
        Files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["windows-event-triage/SKILL.md"] = TestPack.Skill(
                "windows-event-triage", Description, "Read references/interpretation.md for the procedure."),
            [Resource] = resource,
        },
    };

    internal const string DefaultResource =
        "1. Check status, coverage and truncation before reading rows. " + ProcedureMarker + "\n" +
        "2. Kernel-Power 41 records an unclean restart; its severity does not identify the cause.\n" +
        "3. Separate observed facts from hypotheses and name the missing verification.\n";

    internal void Install(TestPack pack, Func<PackManifest, PackManifest>? mutate = null) =>
        pack.WriteTo(Path.Combine(PacksDirectory, pack.PackId, pack.Version), mutate);

    /// <summary>Writes the operator-owned trust store: public keys only.</summary>
    internal void WriteTrust(params PublisherKey[] keys) => WriteTrust(keys, []);

    internal void WriteTrust(PublisherKey[] keys, string[] revokedPublishers)
    {
        var file = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["keys"] = new JsonArray([.. keys.Select(key => (JsonNode)new JsonObject
            {
                ["publisher"] = key.Publisher, ["keyId"] = key.KeyId, ["publicKeyPem"] = key.PublicKeyPem, ["revoked"] = key.Revoked,
            })]),
            ["revokedPublishers"] = new JsonArray([.. revokedPublishers.Select(name => (JsonNode)name)]),
        };
        File.WriteAllText(TrustStorePath, file.ToJsonString());
    }

    /// <summary>The <c>Knowledge</c> section an operator would write, enabled, granting and binding the event-triage pack.</summary>
    internal Dictionary<string, string?> Settings(bool enabled = true)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Knowledge:Enabled"] = enabled ? "true" : "false",
            ["Knowledge:PacksDirectory"] = PacksDirectory,
            ["Knowledge:SnapshotsDirectory"] = SnapshotsDirectory,
            ["Knowledge:TrustStorePath"] = TrustStorePath,
            ["Knowledge:Grants:0:PackId"] = PackId,
            ["Knowledge:Grants:0:Version"] = PackVersion,
            ["Knowledge:Grants:0:Kind"] = "Knowledge",
            ["Knowledge:Binding:PackId"] = PackId,
            ["Knowledge:Binding:Version"] = PackVersion,
        };
        return settings;
    }

    internal static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    internal IKnowledgeTaskHost? Host(Dictionary<string, string?> settings) =>
        CliKnowledge.CreateHost(Configuration(settings), Audit, TimeProvider.System, NullLogger.Instance);

    /// <summary>
    /// The runner of <c>bops "goal"</c> over a scripted model: the production composition with this world's store, audit, policy and
    /// tools. <paramref name="settings"/> <c>null</c> means no <c>Knowledge</c> section at all, as before the feature existed.
    /// </summary>
    internal Rig Runner(
        IEnumerable<ModelResponse> script,
        Dictionary<string, string?>? settings,
        IPolicyEngine? policy = null,
        IApprovalProvider? approval = null,
        params ITool[] extraTools)
    {
        var model = new ScriptedModel(script);
        var registry = new ToolRegistry(new AlwaysAvailable());
        var events = new EventsTool();
        registry.Register(new PackageId("test.package"), events);
        foreach (var tool in extraTools)
        {
            registry.Register(new PackageId("test.package"), tool);
        }

        var host = settings is null ? null : Host(settings);
        var store = new SqliteTaskStore(TasksFile);
        var (runner, broker) = CliRunnerComposition.Compose(new CliRunnerInputs(
            model,
            registry,
            policy ?? new PolicyEngine(PolicyConfig.SafeDefault),
            approval ?? new DenyingApproval(),
            Audit,
            store,
            TimeProvider.System,
            NullLogger<AgentRunner>.Instance,
            new AgentRunnerOptions(),
            new SkillRegistry(),
            null,
            null,
            host,
            MafGovernedTurn));
        return new Rig(runner, model, registry, events, store, host, broker);
    }

    public void Dispose()
    {
        foreach (var item in owned)
        {
            item.Dispose();
        }

        directory.Dispose();
    }

    internal sealed record Rig(
        AgentRunner Runner, ScriptedModel Model, ToolRegistry Registry, EventsTool Events, SqliteTaskStore Store, IKnowledgeTaskHost? Host,
        IModelInvocationBroker? Broker = null);
}

/// <summary>A Read tool with the name and manifest of the real Windows events tool and a canned answer: the diagnosis is exercised, the machine is not.</summary>
internal sealed class EventsTool : ITool
{
    internal int Executions { get; private set; }

    public ToolManifest Manifest { get; } = new()
    {
        Name = "system.events",
        Description = "Reads Windows events (canned answer for the test).",
        Risk = RiskLevel.Read,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        Executions++;
        return Task.FromResult(ToolCallResult.Success("status=complete coverage=24h\nKernel-Power 41 at 02:14Z; WHEA-Logger 17 at 02:13Z (corrected)."));
    }
}

/// <summary>A High-risk tool that must never run unless the policy engine and the operator say so.</summary>
internal sealed class RestartTool : IVerifiableTool
{
    internal int Executions { get; private set; }

    public ToolManifest Manifest { get; } = new()
    {
        Name = "service.restart",
        Description = "Restarts a service.",
        Risk = RiskLevel.High,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [],
        Verification = new VerificationSpec("system.events", [], "Reads the events again."),
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        Executions++;
        return Task.FromResult(ToolCallResult.Success("restarted"));
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        Task.FromResult(new VerificationOutcome(VerificationStatus.Confirmed, "checked"));
}

internal sealed class AlwaysAvailable : ICapabilityProbe
{
    public Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default) => Task.FromResult(true);
}

/// <summary>Refuses every approval it is asked for and remembers what it was asked.</summary>
internal sealed class DenyingApproval : IApprovalProvider
{
    internal List<string> Asked { get; } = [];

    public Task<ApprovalDecision> RequestApprovalAsync(
        ToolManifest manifest, ToolArguments arguments, VerificationSpec? verification, string reason, CancellationToken ct = default)
    {
        Asked.Add(manifest.Name);
        return Task.FromResult(new ApprovalDecision(false, CliKnowledgeWorld.Operator, "denied by the test"));
    }
}

/// <summary>The scripted provider behind the real broker. An unscripted call throws, and every request is kept for assertion.</summary>
internal sealed class ScriptedModel(IEnumerable<ModelResponse> script) : IChatModel
{
    private readonly Queue<ModelResponse> queue = new(script);

    public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

    public List<ModelRequest> Requests { get; } = [];

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return queue.Count > 0 ? Task.FromResult(queue.Dequeue()) : throw new InvalidOperationException("Unscripted model call.");
    }
}

/// <summary>Builders for the scripted replies.</summary>
internal static class CliScript
{
    private static readonly ModelUsage Usage = new(100, 10, null);

    internal static ModelResponse Plan(params string[] tools) =>
        new(new JsonObject
        {
            ["rationale"] = "A plan.",
            ["steps"] = new JsonArray([.. tools.Select(tool => (JsonNode)new JsonObject { ["description"] = $"run {tool}", ["expectedTool"] = tool })]),
        }.ToJsonString(), [], false, null);

    internal static ModelResponse Tool(string id, string tool) =>
        new(null, [new ModelToolCall(id, tool, ToolArguments.Empty)], false, Usage);

    internal static ModelResponse Final(string text) => new(text, [], true, Usage);

    internal static ModelResponse Read(string id, string operation, string? reference = null)
    {
        var arguments = new JsonObject { ["operation"] = operation };
        if (reference is not null)
        {
            arguments["ref"] = reference;
        }

        return new ModelResponse(null, [new ModelToolCall(id, "runtime.knowledge_read", ToolArguments.FromJson(arguments))], false, Usage);
    }
}
