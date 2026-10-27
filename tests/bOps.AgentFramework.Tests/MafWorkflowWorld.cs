// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bOps.AgentFramework.Troubleshooting;
using bOps.Abstractions;
using bOps.Runtime;
using bOps.Runtime.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// An offline, deterministic world for the MAF troubleshooting workflow (C01-R23): a scripted model that answers by agent role, the
/// real <see cref="AgentRunner"/> and <see cref="ModelInvocationBroker"/> over a real <see cref="ToolRegistry"/>, fake tools that count
/// their executions, and a thread-safe audit sink. Nothing here is live: no network, no provider, no machine tool.
/// </summary>
internal sealed class MafWorkflowWorld
{
    internal static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("maf-test-operator");

    internal required RoleScriptedModel Model { get; init; }

    internal required LockedAuditSink Audit { get; init; }

    internal required ToolRegistry Registry { get; init; }

    internal required AgentRunner Runner { get; init; }

    internal required ModelInvocationBroker Broker { get; init; }

    internal required MafTroubleshootingWorkflow Workflow { get; init; }

    internal required Dictionary<string, ITool> Tools { get; init; }

    internal required InMemoryTaskStore Store { get; init; }

    /// <summary>Builds a world with the System tools always registered and the PostgreSQL tools only when asked.</summary>
    internal static MafWorkflowWorld Create(
        bool postgres = false,
        MafWorkflowLimits? limits = null,
        IPolicyEngine? policy = null,
        IApprovalProvider? approval = null,
        IEnumerable<ITool>? extraTools = null,
        AgentRunnerOptions? options = null)
    {
        var tools = new Dictionary<string, ITool>(StringComparer.Ordinal);
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        void Add(ITool tool)
        {
            tools[tool.Manifest.Name] = tool;
            registry.Register(new PackageId("test.package"), tool);
        }

        Add(new ProbeTool("fake.sys.events", "Kernel-Power 41 at 03:12; LiveKernelEvent 141 (display driver)."));
        Add(new ProbeTool("fake.sys.services", "Service 'pgsvc' stopped unexpectedly twice today."));
        Add(new ProbeTool("fake.sys.dumps", "No kernel dump present; one LiveKernel report for the display driver."));
        if (postgres)
        {
            Add(new ProbeTool("fake.pg.health", "Instance up; 48 of 100 connections used."));
            Add(new ProbeTool("fake.pg.locks", "2 sessions blocked behind pid 4121 for 40 s."));
        }

        foreach (var extra in extraTools ?? [])
        {
            Add(extra);
        }

        var model = new RoleScriptedModel();
        var audit = new LockedAuditSink();
        var runnerOptions = options ?? new AgentRunnerOptions();
        var store = new InMemoryTaskStore();
        var broker = new ModelInvocationBroker(model, audit, TimeProvider.System, NullLogger.Instance, runnerOptions);
        var runner = new AgentRunner(
            model, registry, policy ?? new DefaultTestPolicyEngine(), approval ?? new NeverCalledApprovalProvider(), audit,
            store, TimeProvider.System, NullLogger<AgentRunner>.Instance, runnerOptions, invocationBroker: broker);

        var specialists = new List<DiagnosticSpecialist>
        {
            new("system", "Investigates the operating system and the services on it.", ["events and logs", "services", "stability and crashes"],
                "You investigate Windows and Linux hosts.", manifest => manifest.Name.StartsWith("fake.sys.", StringComparison.Ordinal)),
            new("postgresql", "Investigates a PostgreSQL instance.", ["health", "sessions", "locks", "slow queries"],
                "You investigate PostgreSQL.", manifest => manifest.Name.StartsWith("fake.pg.", StringComparison.Ordinal)),
        };
        var workflow = new MafTroubleshootingWorkflow(
            runner, broker, new DiagnosticSpecialistCatalog(specialists), audit, TimeProvider.System, NullLogger.Instance, limits);
        return new MafWorkflowWorld
        {
            Model = model, Audit = audit, Registry = registry, Runner = runner, Broker = broker, Workflow = workflow, Tools = tools, Store = store,
        };
    }

    internal int Executions(string tool) => tool switch
    {
        _ when Tools[tool] is ProbeTool probe => probe.Executions,
        _ when Tools[tool] is ProbeMutationTool mutation => mutation.Executions,
        _ => throw new InvalidOperationException($"'{tool}' is not a counting fixture."),
    };

    /// <summary>The evidence ids that appear in the tool results the request carries, in order.</summary>
    internal static string[] EvidenceIds(ModelRequest request) =>
        [.. Regex.Matches(
                string.Join('\n', request.History.Where(turn => turn.Role == bOps.Abstractions.ChatRole.Tool).Select(turn => turn.Content)),
                @"ev1:[0-9a-f\-]+:\d+")
            .Select(match => match.Value).Distinct()];

    /// <summary>Every evidence id the request names anywhere in its history (a reviewer sees the run's whole evidence list).</summary>
    internal static string[] AllEvidenceIds(ModelRequest request) =>
        [.. Regex.Matches(string.Join('\n', request.History.Select(turn => turn.Content)), @"ev1:[0-9a-f\-]+:\d+")
            .Select(match => match.Value).Distinct()];

    internal static string TriagePlan(params (string Specialist, string Objective)[] assignments) =>
        new JsonObject
        {
            ["problem"] = "A host is slow and a service stops now and then.",
            ["assignments"] = new JsonArray([.. assignments.Select(item => (JsonNode)new JsonObject { ["specialist"] = item.Specialist, ["objective"] = item.Objective })]),
        }.ToJsonString();

    internal static string SpecialistReply(string summary, string[] observationIds, string? hypothesis = null, string? observation = null) =>
        new JsonObject
        {
            ["summary"] = summary,
            ["observations"] = new JsonArray(new JsonObject { ["text"] = observation ?? "What the tools returned.", ["evidence"] = new JsonArray([.. observationIds.Select(id => (JsonNode)id)]) }),
            ["hypotheses"] = new JsonArray(hypothesis is null ? [] : [(JsonNode)new JsonObject { ["text"] = hypothesis, ["evidence"] = new JsonArray() }]),
            ["limitations"] = new JsonArray("Only the events log was read."),
        }.ToJsonString();

    internal static string ReviewReply(
        bool sufficient, string diagnosis, string[] factIds, (string Specialist, string Objective)[]? followUps = null, string[]? missing = null) =>
        new JsonObject
        {
            ["sufficient"] = sufficient,
            ["diagnosis"] = diagnosis,
            ["facts"] = new JsonArray(factIds.Length == 0 ? [] : [(JsonNode)new JsonObject { ["text"] = "Kernel-Power 41 was logged.", ["evidence"] = new JsonArray([.. factIds.Select(id => (JsonNode)id)]) }]),
            ["hypotheses"] = new JsonArray(new JsonObject { ["text"] = "The display driver may be the cause.", ["evidence"] = new JsonArray() }),
            ["contradictions"] = new JsonArray(),
            ["unsupported"] = new JsonArray("Nothing shows a power supply fault."),
            ["missing"] = new JsonArray([.. (missing ?? []).Select(item => (JsonNode)item)]),
            ["followUps"] = new JsonArray([.. (followUps ?? []).Select(item => (JsonNode)new JsonObject { ["specialist"] = item.Specialist, ["objective"] = item.Objective })]),
            ["nextChecks"] = new JsonArray("Update the display driver and watch for a recurrence."),
            ["interventionPlan"] = new JsonArray("Advisory: update the display driver."),
        }.ToJsonString();
}

/// <summary>A scripted <see cref="IChatModel"/> that answers by the role the standing instructions name, so concurrent agents cannot disturb one another's script.</summary>
internal sealed class RoleScriptedModel : IChatModel
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Queue<Func<ModelRequest, object>>> scripts = new(StringComparer.Ordinal);

    public ChatModelDescriptor Descriptor { get; } = new("fake", "fake-model");

    /// <summary>Every request the provider received, with the role it was for.</summary>
    internal List<(string Role, ModelRequest Request)> Requests { get; } = [];

    internal static string RoleOf(ModelRequest request) =>
        request.SystemPrompt.Contains("the triage agent", StringComparison.Ordinal) ? "triage"
        : request.SystemPrompt.Contains("the diagnostic reviewer", StringComparison.Ordinal) ? "reviewer"
        : Regex.Match(request.SystemPrompt, @"You are the '([a-z0-9\-]+)' specialist") is { Success: true } match ? match.Groups[1].Value
        : "unknown";

    /// <summary>Queues the answers for a role; an answer is a <see cref="ModelResponse"/> or an exception to throw.</summary>
    internal void Script(string role, params Func<ModelRequest, object>[] steps)
    {
        lock (gate)
        {
            if (!scripts.TryGetValue(role, out var queue))
            {
                scripts[role] = queue = new Queue<Func<ModelRequest, object>>();
            }

            foreach (var step in steps)
            {
                queue.Enqueue(step);
            }
        }
    }

    internal int CallsFor(string role)
    {
        lock (gate)
        {
            return Requests.Count(item => item.Role == role);
        }
    }

    internal int TotalCalls
    {
        get
        {
            lock (gate)
            {
                return Requests.Count;
            }
        }
    }

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        Func<ModelRequest, object> step;
        var role = RoleOf(request);
        lock (gate)
        {
            Requests.Add((role, request));
            if (!scripts.TryGetValue(role, out var queue) || queue.Count == 0)
            {
                throw new InvalidOperationException($"Unscripted model call for role '{role}'.");
            }

            step = queue.Dequeue();
        }

        return step(request) switch
        {
            ModelResponse response => Task.FromResult(response),
            Exception exception => Task.FromException<ModelResponse>(exception),
            _ => throw new InvalidOperationException("Bad script entry."),
        };
    }

    internal static Func<ModelRequest, object> Text(string text) => _ => new ModelResponse(text, [], true, new ModelUsage(100, 20, null));

    internal static Func<ModelRequest, object> Text(Func<ModelRequest, string> text) =>
        request => new ModelResponse(text(request), [], true, new ModelUsage(100, 20, null));

    internal static Func<ModelRequest, object> Call(string id, string tool, JsonObject? arguments = null) =>
        _ => new ModelResponse(null, [new ModelToolCall(id, tool, ToolArguments.FromJson(arguments ?? []))], false, new ModelUsage(100, 20, null));

    internal static Func<ModelRequest, object> Fail(ModelFailureKind kind) =>
        _ => new ModelProtocolException("scripted model failure") { FailureKind = kind };
}

/// <summary>An audit sink that is safe for the concurrent specialists of a round.</summary>
internal sealed class LockedAuditSink : IAuditSink
{
    private readonly Lock gate = new();
    private readonly List<AuditEvent> events = [];

    internal IReadOnlyList<AuditEvent> Events
    {
        get
        {
            lock (gate)
            {
                return [.. events];
            }
        }
    }

    public Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
    {
        lock (gate)
        {
            events.Add(evt);
        }

        return Task.CompletedTask;
    }
}

/// <summary>A read-only fixture tool that counts its executions and can wait, so a test can prove concurrency, a timeout or a cancellation.</summary>
internal sealed class ProbeTool(string name, string output, IReadOnlyList<ToolParameter>? parameters = null, Func<CancellationToken, Task>? onExecute = null) : ITool
{
    private int executions;

    public int Executions => Volatile.Read(ref executions);

    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = "A deterministic read-only fixture tool.",
        Risk = RiskLevel.Read,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = parameters ?? [],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        Interlocked.Increment(ref executions);
        if (onExecute is not null)
        {
            await onExecute(ct);
        }

        return ToolCallResult.Success(output);
    }
}

/// <summary>A fixture tool above Read that counts executions: nothing in a workflow run may ever execute it.</summary>
internal sealed class ProbeMutationTool(string name) : IVerifiableTool
{
    private int executions;

    public int Executions => Volatile.Read(ref executions);

    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = "A fixture tool with effects.",
        Risk = RiskLevel.High,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [],
        Verification = new VerificationSpec("fake.sys.events", [], "Reads the events."),
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        Interlocked.Increment(ref executions);
        return Task.FromResult(ToolCallResult.Success("changed"));
    }

    public Task<VerificationOutcome> EvaluateVerificationAsync(
        ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
        Task.FromResult(new VerificationOutcome(VerificationStatus.Confirmed, null));
}
