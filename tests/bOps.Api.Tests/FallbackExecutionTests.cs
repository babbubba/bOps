// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace bOps.Api.Tests;

/// <summary>
/// HARDEN-13 B2a (ADR-0045) on the real <c>Program</c> composition: the host's own coordinator pins the fallback chain, the real
/// <see cref="bOps.Hosting.FallbackChatModel"/> and credential resolver build each candidate, and only the provider adapter
/// registry is replaced by a scripted one that records which key each candidate invocation actually received.
/// </summary>
public sealed class FallbackExecutionTests
{
    private const string Primary = "OpenRouter/openrouter/free";
    private const string Secondary = "Anthropic/sonnet-x";
    private const string PrimaryBlockKey = "test-model-provider-key";
    private const string VaultKey = "ANTHROPIC_KEY";
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(20);
    private static readonly PackageId Package = new("bops.tests.fallback");

    private const string PolicyYaml = """
        delegation:
          roles:
            discovery:
              tools: [fbtest.info]
              maxRisk: read
              maxBlastRadius: single
              targets: [local]
              environments: [test]
              maxSteps: 6
              maxTokens: 100000
              maxDuration: 00:05:00
            diagnostic:
              skills: [fbtest.skill]
              capabilities: [fbtest.remediate]
              tools: [fbtest.info]
              maxRisk: read
              maxBlastRadius: single
              targets: [local]
              environments: [test]
              maxSteps: 6
              maxTokens: 100000
              maxDuration: 00:05:00
            remediation:
              skills: [fbtest.skill]
              capabilities: [fbtest.remediate]
              tools: [fbtest.restart]
              maxRisk: high
              maxBlastRadius: single
              targets: [local]
              environments: [test]
              maxSteps: 6
              maxDuration: 00:05:00
            verification:
              tools: [fbtest.read]
              maxRisk: read
              maxBlastRadius: single
              targets: [local]
              environments: [test]
              maxSteps: 6
              maxDuration: 00:05:00
        """;

    // ---- scripted provider adapters ----

    private sealed record Call(string Candidate, string? Key);

    private sealed class Book
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<ModelResponse>>> _scripts = new();

        public ConcurrentQueue<Call> Calls { get; } = new();

        /// <summary>Observes the host at the moment a candidate is invoked (for ordering assertions).</summary>
        public Action<string>? OnCall { get; set; }

        public Book Script(string candidate, params Func<ModelResponse>[] responses)
        {
            var queue = _scripts.GetOrAdd(candidate, _ => new());
            foreach (var response in responses) queue.Enqueue(response);
            return this;
        }

        public int CallsTo(string candidate) => Calls.Count(call => call.Candidate == candidate);

        internal ModelResponse Invoke(string candidate, string? key)
        {
            Calls.Enqueue(new Call(candidate, key));
            OnCall?.Invoke(candidate);
            if (!_scripts.TryGetValue(candidate, out var queue) || !queue.TryDequeue(out var next))
                throw new InvalidOperationException($"Unexpected call to {candidate}.");
            return next();
        }
    }

    private sealed class ScriptedRegistry(Book book) : IChatModelRegistry
    {
        public IReadOnlyList<string> RegisteredProviderIds { get; } = ["OpenRouter", "Anthropic"];

        public void Register(PackageId package, IModelProviderPackage provider)
        {
        }

        public IChatModel Create(ChatModelOptions options) => new ScriptedModel(options, book);
    }

    private sealed class ScriptedModel(ChatModelOptions options, Book book) : IChatModel
    {
        public ChatModelDescriptor Descriptor { get; } = new(options.Provider, options.Model);

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
            Task.FromResult(book.Invoke($"{options.Provider}/{options.Model}", options.ResolvedApiKey));
    }

    private static Func<ModelResponse> Plan() => () =>
        new ModelResponse(new JsonObject
        {
            ["rationale"] = "A plan.",
            ["steps"] = new JsonArray { new JsonObject { ["description"] = "look", ["expectedTool"] = null } },
        }.ToJsonString(), [], false, null);

    private static Func<ModelResponse> Final(string text = "Done.") => () => new ModelResponse(text, [], true, null);

    private static Func<ModelResponse> CallInfo() =>
        () => new ModelResponse(null, [new ModelToolCall("c1", "fbtest.info", ToolArguments.Empty)], false, null);

    private static Func<ModelResponse> Findings() =>
        () => new ModelResponse(
            "{\"findings\":[{\"summary\":\"The service has stopped.\",\"evidenceIds\":[\"discovery-0\"],\"severity\":\"high\"}]}",
            [], true, null);

    private static Func<ModelResponse> Fail(ModelFailureKind kind = ModelFailureKind.Transient) =>
        () => throw new ModelProtocolException("provider said no") { FailureKind = kind };

    // ---- delegation fixtures ----

    private sealed class ReadTool(string name, string output) : ITool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = name,
            Description = "Reads something.",
            Risk = RiskLevel.Read,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success(output));
    }

    private sealed class RestartTool : IVerifiableTool
    {
        public ToolManifest Manifest { get; } = new()
        {
            Name = "fbtest.restart",
            Description = "Restarts the service.",
            Risk = RiskLevel.High,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
            Verification = new VerificationSpec("fbtest.read", [], "Reads the service state."),
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(ToolCallResult.Success("restarted"));

        public Task<VerificationOutcome> EvaluateVerificationAsync(
            ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
            Task.FromResult(new VerificationOutcome(VerificationStatus.Confirmed, "read the service"));
    }

    private sealed class RestoreCapability : ICapability
    {
        public CapabilityManifest Manifest { get; } = new(
            "fbtest.remediate", "1.0.0", "Restores the sample service.", RiskLevel.High, [], [], [], TimeSpan.FromSeconds(5),
            SupportsDryRun: true, new VerificationSpec("fbtest.read", [], "Reads the service state."));

        public Task<SkillReport> PrepareAsync(CapabilityRequest request, IToolInvoker toolInvoker, CancellationToken ct = default) =>
            Task.FromResult(new SkillReport(
                [new Evidence("cap-e1", EvidenceKind.Fact, "The service is down.", "down", "fbtest.info", DateTimeOffset.UtcNow)],
                [new Finding("cap-f1", "The service is down.", ["cap-e1"], RiskLevel.High)],
                new ExecutionPlan("fbtest.remediate", "1.0.0", "Restart the service.",
                    [new ExecutionPlanStep(0, "fbtest.restart", ToolArguments.Empty, "Restart the service.")])));
    }

    private sealed class SampleSkill : ISkillProvider
    {
        public string SkillId => "fbtest.skill";

        public IReadOnlyList<ICapability> GetCapabilities() => [new RestoreCapability()];

        public IEnumerable<ITool> GetTools() => [];
    }

    // ---- host ----

    private sealed class Host(TestAppFactory factory, HttpClient client, Book book) : IDisposable
    {
        public TestAppFactory Factory => factory;
        public HttpClient Client => client;
        public Book Book => book;
        public IServiceProvider Services => factory.Services;
        internal ProviderConfigurationCoordinator Coordinator => Services.GetRequiredService<ProviderConfigurationCoordinator>();
        public ITaskStore Tasks => Services.GetRequiredService<ITaskStore>();
        public IDelegationStore Delegations => Services.GetRequiredService<IDelegationStore>();
        public string AuditText => File.ReadAllText(Path.Combine(factory.TempDirectory, "audit.jsonl"));

        public void Dispose()
        {
            client.Dispose();
            factory.Dispose();
        }
    }

    /// <summary>Boots the host with the chain: configured primary, then the given fallbacks (provider, model).</summary>
    private static Host NewHost(Book book, params (string Provider, string Model)[] fallbacks)
    {
#pragma warning disable CA2000 // Ownership passes to the returned Host.
        var factory = new TestAppFactory
#pragma warning restore CA2000
        {
            PolicyEngine = new FixedPolicyEngine(PolicyMode.Automatic),
            Roles = ["viewer", "operator", "approver", "administrator"],
            ExtraConfiguration = new Dictionary<string, string?> { ["Agent:ModelCallMaxAttempts"] = "1" },
            ConfigureExtraServices = services =>
            {
                services.RemoveAll<IChatModelRegistry>();
                services.AddSingleton<IChatModelRegistry>(new ScriptedRegistry(book));
            },
        };
        try
        {
            File.WriteAllText(Path.Combine(factory.TempDirectory, "policy.yaml"), PolicyYaml);
            var tools = factory.Services.GetRequiredService<IToolRegistry>();
            tools.Register(Package, new ReadTool("fbtest.info", "down"));
            tools.Register(Package, new ReadTool("fbtest.read", "service is running"));
            tools.Register(Package, new RestartTool());
            factory.Services.GetRequiredService<ISkillRegistry>().Register(Package, new SampleSkill());
            tools.RefreshCapabilitiesAsync().GetAwaiter().GetResult();
            var host = new Host(factory, factory.CreateClient(), book);
            ConfigureChain(host, fallbacks);
            return host;
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    /// <summary>Stores an Anthropic profile and vault key, then publishes the configured fallback chain.</summary>
    private static void ConfigureChain(Host host, (string Provider, string Model)[] fallbacks)
    {
        host.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.test", "sonnet-x", true, null));
        host.Coordinator.SetKey("Anthropic", VaultKey, 0);
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        for (var i = 0; i < fallbacks.Length; i++)
        {
            configuration[$"ModelProvider:Fallbacks:{i}:Provider"] = fallbacks[i].Provider;
            configuration[$"ModelProvider:Fallbacks:{i}:Model"] = fallbacks[i].Model;
        }

        host.Coordinator.SetProfile("Anthropic", new("https://api.anthropic.test", "sonnet-x", false, null));
        Assert.Equal(fallbacks.Length, host.Coordinator.Current.Pin.Fallbacks.Count);
    }

    private static async Task<TaskState> StartAndWait(Host host)
    {
        var response = await host.Client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest("test"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TaskAcceptedResponse>();
        return await WaitTerminal(host.Tasks, body!.TaskId);
    }

    private static async Task<TaskState> WaitTerminal(ITaskStore store, Guid id)
    {
        using var timeout = new CancellationTokenSource(PollTimeout);
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var task = await store.LoadAsync(id, timeout.Token);
            if (task is { Status: not AgentTaskStatus.Running }) return task;
            await Task.Delay(20, timeout.Token);
        }
    }

    private static IEnumerable<ModelCallRecord> ModelCalls(TaskState task) =>
        task.Plans.SelectMany(plan => plan.ModelCalls ?? []).Concat(task.Steps.SelectMany(step => step.ModelCalls ?? []));

    // ---- ordinary task ----

    [Fact]
    public async Task OrdinaryTask_FallsBackAcrossProviders_PersistsTheOrdinalFirst_UsesTheFallbackVaultKey_AndStaysSticky()
    {
        var book = new Book()
            .Script(Primary, Plan(), Fail())
            .Script(Secondary, CallInfo(), Final());
        using var host = NewHost(book, ("Anthropic", "sonnet-x"));
        var persistedAtFallbackCall = new List<int>();
        book.OnCall = candidate =>
        {
            if (candidate != Secondary) return;
            var running = host.Tasks.ListByStatusAsync(AgentTaskStatus.Running).GetAwaiter().GetResult();
            persistedAtFallbackCall.Add(Assert.Single(running).PinnedProviderConfiguration!.FallbackOrdinal);
        };

        var task = await StartAndWait(host);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        // Cross-provider secret isolation, proven on the adapter invocation itself.
        Assert.Equal(PrimaryBlockKey, Assert.Single(book.Calls.Where(c => c.Candidate == Primary).Skip(1)).Key);
        Assert.All(book.Calls.Where(c => c.Candidate == Secondary), call => Assert.Equal(VaultKey, call.Key));
        Assert.DoesNotContain(book.Calls, call => call.Candidate == Secondary && call.Key == PrimaryBlockKey);
        Assert.DoesNotContain(book.Calls, call => call.Candidate == Primary && call.Key == VaultKey);
        // The advanced ordinal was durable before the fallback adapter ran, on both the first and the later logical call.
        Assert.Equal([1, 1], persistedAtFallbackCall);
        // Sticky: the second logical call (after the tool result) went straight to the fallback.
        Assert.Equal(2, book.CallsTo(Primary));
        Assert.Equal(2, book.CallsTo(Secondary));
        var stored = (await host.Tasks.LoadAsync(task.Id))!;
        Assert.Equal(1, stored.PinnedProviderConfiguration!.FallbackOrdinal);
        var step = stored.Steps[0].ModelCalls!;
        Assert.Equal(["OpenRouter", "Anthropic"], step.Select(c => c.Provider));
        Assert.Equal(["openrouter/free", "sonnet-x"], step.Select(c => c.RequestedModel));
        Assert.Equal([0, 1], step.Select(c => c.FallbackOrdinal!.Value));
        // ModelAttempt counts one logical call's attempts; ProviderAttempt restarts for the fallback candidate.
        Assert.Equal([1, 2], step.Select(c => c.ModelAttempt!.Value));
        Assert.Equal([1, 1], step.Select(c => c.ProviderAttempt!.Value));
        // The next logical call starts at the fallback with fresh attempt counters.
        var later = ModelCalls(stored).Last();
        Assert.Equal(("Anthropic", 1, 1, 1), (later.Provider, later.FallbackOrdinal!.Value, later.ModelAttempt!.Value, later.ProviderAttempt!.Value));
        Assert.Equal(ModelRetryDecision.Fallback, step[0].RetryDecision);
        // Secrets: no key reaches the task record, the pin, or the audit log.
        var serialised = JsonSerializer.Serialize(stored) + host.AuditText;
        Assert.DoesNotContain(PrimaryBlockKey, serialised, StringComparison.Ordinal);
        Assert.DoesNotContain(VaultKey, serialised, StringComparison.Ordinal);
        // The audit log tells a fallback transition from a retry, and names the candidate actually invoked.
        var modelEvents = host.AuditText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(JsonNode.Parse(line)!["EventJson"]!.GetValue<string>())!.AsObject())
            .Where(node => node["Provider"] is not null && node["FallbackOrdinal"] is not null).ToList();
        Assert.Equal(
            ["OpenRouter:0", "OpenRouter:0", "Anthropic:1", "Anthropic:1"],
            modelEvents.Select(e => $"{e["Provider"]}:{e["FallbackOrdinal"]}"));
        Assert.Equal(
            (int)ModelRetryDecision.Fallback,
            Assert.Single(modelEvents, e => e["RetryDecision"] is not null)["RetryDecision"]!.GetValue<int>());
        Assert.Contains(modelEvents, e => e["Model"]!.GetValue<string>() == "sonnet-x");
        // The pinned primary is explicit on every attempt, so a fallback is never inferred from a Provider change alone.
        Assert.All(modelEvents, e => Assert.Equal(
            ("OpenRouter", "openrouter/free", stored.PinnedProviderConfiguration.SnapshotHash),
            (e["PrimaryProvider"]!.GetValue<string>(), e["PrimaryModel"]!.GetValue<string>(),
                e["ConfigurationSnapshotHash"]!.GetValue<string>())));
        Assert.All(ModelCalls(stored), call => Assert.Equal(("OpenRouter", "openrouter/free"), (call.PrimaryProvider, call.PrimaryModel)));
    }

    // Test G
    [Fact]
    public async Task AFallbackWithoutACurrentCredential_EndsTheCallAsAuthentication_AndNeverReachesTheNextCandidate()
    {
        var book = new Book()
            .Script(Primary, Plan(), Fail())
            .Script(Secondary, Final())
            .Script("OpenRouter/openrouter/second", Final());
        using var host = NewHost(book, ("Anthropic", "sonnet-x"), ("OpenRouter", "openrouter/second"));
        host.Coordinator.RemoveKey("Anthropic", 1);

        var task = await StartAndWait(host);

        Assert.Equal(AgentTaskStatus.Failed, task.Status);
        Assert.Equal(2, book.CallsTo(Primary));
        Assert.Equal(0, book.CallsTo(Secondary));
        Assert.Equal(0, book.CallsTo("OpenRouter/openrouter/second"));
        var calls = task.Steps[^1].ModelCalls!;
        Assert.Equal(["OpenRouter", "Anthropic"], calls.Select(c => c.Provider));
        Assert.Equal((ModelFailureKind.Authentication, ModelRetryDecision.NotRetryable, 1),
            (calls[1].FailureKind!.Value, calls[1].RetryDecision!.Value, calls[1].FallbackOrdinal!.Value));
        Assert.Equal(1, (await host.Tasks.LoadAsync(task.Id))!.PinnedProviderConfiguration!.FallbackOrdinal);
    }

    // Test J
    [Fact]
    public async Task AnotherExecution_StartsAtThePrimary_NoFallbackStateIsShared()
    {
        var book = new Book()
            .Script(Primary, Plan(), Fail(), Plan(), Final("second"))
            .Script(Secondary, Final("first"));
        using var host = NewHost(book, ("Anthropic", "sonnet-x"));

        var first = await StartAndWait(host);
        var second = await StartAndWait(host);

        Assert.Equal(1, first.PinnedProviderConfiguration!.FallbackOrdinal);
        Assert.Equal(AgentTaskStatus.Completed, second.Status);
        Assert.Equal(0, second.PinnedProviderConfiguration!.FallbackOrdinal);
        Assert.Equal(4, book.CallsTo(Primary));
        Assert.Equal(1, book.CallsTo(Secondary));
        Assert.Equal(0, host.Coordinator.Current.Pin.FallbackOrdinal);
        Assert.Equal(1, (await host.Tasks.LoadAsync(first.Id))!.PinnedProviderConfiguration!.FallbackOrdinal);
    }

    // Durability: an ordinary execution reconstructed from its persisted pin starts at the persisted ordinal.
    [Fact]
    public async Task ResumedTask_ReconstructsFromThePersistedOrdinal_AndNeverRetriesThePrimary()
    {
        var book = new Book()
            .Script(Primary, Plan())
            .Script(Secondary, Final("resumed"));
        using var host = NewHost(book, ("Anthropic", "sonnet-x"));
        var pin = host.Coordinator.Current.Pin with { FallbackOrdinal = 1 };
        var seeded = new TaskState(Guid.NewGuid(), NodeId.Local, "resume", AgentTaskStatus.Cancelled,
            [], [new AgentPlan(0, "test", [])], DateTimeOffset.UtcNow)
        { Origin = TaskOrigin.Ordinary, PinnedProviderConfiguration = pin };
        await host.Tasks.SaveAsync(seeded);

        var response = await host.Client.PostAsync(new Uri($"/api/agents/tasks/{seeded.Id}/resume", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var completed = await WaitTerminal(host.Tasks, seeded.Id);
        Assert.Equal(AgentTaskStatus.Completed, completed.Status);
        Assert.Equal(0, book.CallsTo(Primary));
        Assert.Equal(1, book.CallsTo(Secondary));
        Assert.Equal(VaultKey, Assert.Single(book.Calls).Key);
        Assert.Equal(1, completed.PinnedProviderConfiguration!.FallbackOrdinal);
    }

    // ---- delegation ----

    private static async Task<DelegationView> WaitForAsync(HttpClient client, Guid id, Func<DelegationView, bool> until)
    {
        using var timeout = new CancellationTokenSource(PollTimeout);
        DelegationView? last = null;
        try
        {
            while (true)
            {
                var response = await client.GetAsync(new Uri($"/api/delegations/{id}", UriKind.Relative), timeout.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    last = await response.Content.ReadFromJsonAsync<DelegationView>(timeout.Token);
                    if (until(last!)) return last!;
                }

                await Task.Delay(25, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"The run never got there; it was {last?.Status ?? "not visible"}: {last?.ErrorMessage}");
        }
    }

    private static async Task<Guid> StartAsync(HttpClient client, StartDelegationRequest body)
    {
        var response = await client.PostAsJsonAsync("/api/delegations", body);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DelegationAcceptedResponse>())!.DelegationId;
    }

    private static StartDelegationRequest Diagnose() => new("Why did the service stop?", null, null, null);

    private static StartDelegationRequest Fix() =>
        new("Find out why the service stopped and fix it", null, null,
            new DelegationRemediationBody("fbtest.skill", "fbtest.remediate", "local", "test", null, false, null));

    // Test K + approval/resume stickiness.
    [Fact]
    public async Task Delegation_StaysOnTheFallbackAcrossRoles_AndAcrossThePlanApprovalWait()
    {
        // Discovery: primary plan, primary fails -> fallback. Diagnostic (a later role of the SAME run) must start at the fallback.
        var book = new Book()
            .Script(Primary, Plan(), Fail())
            .Script(Secondary, CallInfo(), Final(), Plan(), Findings());
        using var host = NewHost(book, ("Anthropic", "sonnet-x"));

        var id = await StartAsync(host.Client, Fix());
        var waiting = await WaitForAsync(host.Client, id, view => view.AwaitingPlanApproval);

        Assert.True(waiting.RunningInThisHost);
        Assert.Equal(2, book.CallsTo(Primary));
        Assert.Equal(4, book.CallsTo(Secondary));
        var atWait = (await host.Delegations.LoadAsync(id))!;
        Assert.Equal(1, atWait.PinnedProviderConfiguration!.FallbackOrdinal);
        var pending = (await host.Client.GetFromJsonAsync<IReadOnlyList<PendingPlanApproval>>("/api/delegations/approvals"))!
            .Single(p => p.DelegationId == id);

        var decided = await host.Client.PostAsJsonAsync(
            $"/api/delegations/{id}/approval", new RespondToPlanApprovalRequest(pending.PlanHash, true, null));
        Assert.Equal(HttpStatusCode.NoContent, decided.StatusCode);
        await WaitForAsync(host.Client, id, view => view.Status == nameof(DelegationStatus.Completed));

        Assert.Equal(2, book.CallsTo(Primary));
        var done = (await host.Delegations.LoadAsync(id))!;
        Assert.Equal(1, done.PinnedProviderConfiguration!.FallbackOrdinal);
        var children = (await host.Tasks.ListByStatusAsync(AgentTaskStatus.Completed)).Where(t => t.DelegationId == id).ToList();
        Assert.NotEmpty(children);
        Assert.All(children, child => Assert.Equal(1, child.PinnedProviderConfiguration!.FallbackOrdinal));
        Assert.All(book.Calls.Where(c => c.Candidate == Secondary), call => Assert.Equal(VaultKey, call.Key));
        var serialised = JsonSerializer.Serialize(done) + host.AuditText;
        Assert.DoesNotContain(VaultKey, serialised, StringComparison.Ordinal);
        Assert.DoesNotContain(PrimaryBlockKey, serialised, StringComparison.Ordinal);
    }

    // Durability: a DelegationRun reconstructed from its persisted pin runs every role at the persisted ordinal.
    [Fact]
    public async Task ResumedDelegation_ReconstructsFromThePersistedOrdinal_ForEveryRole()
    {
        var book = new Book()
            .Script(Primary, Plan())
            .Script(Secondary, Plan(), CallInfo(), Final(), Plan(), Findings());
        using var host = NewHost(book, ("Anthropic", "sonnet-x"));
        var actor = new ActorIdentity("api-user", "test-user", "Test User");
        var run = new DelegationRun
        {
            Id = Guid.NewGuid(),
            Node = NodeId.Local,
            Actor = actor,
            Objective = "Why did the service stop?",
            Status = DelegationStatus.Running,
            RootEnvelope = new AuthorityEnvelope(actor, 0, ["fbtest.skill"], ["fbtest.remediate"],
                ["fbtest.info", "fbtest.read", "fbtest.restart"], RiskLevel.High, BlastRadius.Single, ["local"], ["test"],
                new DelegationBudget(30, 300_000, DateTimeOffset.UtcNow.AddHours(1))),
            Roles = [],
            Journal = [],
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            PinnedProviderConfiguration = host.Coordinator.Current.Pin with { FallbackOrdinal = 1 },
        };
        await host.Delegations.StartAsync(run);

        var response = await host.Client.PostAsync(new Uri($"/api/delegations/{run.Id}/resume", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitForAsync(host.Client, run.Id, view => view.Status == nameof(DelegationStatus.DiagnosisCompleted));
        Assert.Equal(0, book.CallsTo(Primary));
        Assert.Equal(5, book.CallsTo(Secondary));
        Assert.Equal(1, (await host.Delegations.LoadAsync(run.Id))!.PinnedProviderConfiguration!.FallbackOrdinal);
    }
}
