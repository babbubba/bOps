// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

/// <summary>
/// Drives <c>/api/delegations</c> (ADR-0030 section 9) end to end: the real <c>Program</c> composition (real policy file, real role
/// profiles, real SQLite delegation store, real <c>DelegationRunner</c> and plan approval queue), a scripted model, and small real
/// tools and a Skill registered into the host's own registries in place of a package.
/// </summary>
public sealed class DelegationsEndpointsTests
{
    private const string RunningReading = "service is running";
    private const string SecretReading = "cpu 91% token=hunter2";
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(20);
    private static readonly PackageId Package = new("bops.tests.delegation");

    private const string PolicyYaml = """
        delegation:
          roles:
            discovery:
              tools: [delegtest.info]
              maxRisk: read
              maxBlastRadius: single
              targets: [local]
              environments: [test]
              maxSteps: 6
              maxTokens: 100000
              maxDuration: 00:05:00
            diagnostic:
              skills: [delegtest.skill]
              capabilities: [delegtest.remediate]
              tools: [delegtest.info]
              maxRisk: read
              maxBlastRadius: single
              targets: [local]
              environments: [test]
              maxSteps: 6
              maxTokens: 100000
              maxDuration: 00:05:00
            remediation:
              skills: [delegtest.skill]
              capabilities: [delegtest.remediate]
              tools: [delegtest.restart]
              maxRisk: high
              maxBlastRadius: single
              targets: [local]
              environments: [test]
              maxSteps: 6
              maxDuration: 00:05:00
            verification:
              tools: [delegtest.read]
              maxRisk: read
              maxBlastRadius: single
              targets: [local]
              environments: [test]
              maxSteps: 6
              maxDuration: 00:05:00
        """;

    // ---- fixtures ----

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
        private int _executions;

        public int Executions => Volatile.Read(ref _executions);

        public ToolManifest Manifest { get; } = new()
        {
            Name = "delegtest.restart",
            Description = "Restarts the service.",
            Risk = RiskLevel.High,
            Platforms = [CurrentPlatform.Id],
            Requires = [],
            Parameters = [],
            Verification = new VerificationSpec("delegtest.read", [], "Reads the service state."),
        };

        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _executions);
            return Task.FromResult(ToolCallResult.Success("restarted"));
        }

        public Task<VerificationOutcome> EvaluateVerificationAsync(
            ToolArguments originalArguments, ToolCallResult verificationToolResult, CancellationToken ct = default) =>
            Task.FromResult(new VerificationOutcome(
                verificationToolResult.Output == RunningReading ? VerificationStatus.Confirmed : VerificationStatus.Refuted, "read the service"));
    }

    private sealed class RestoreCapability : ICapability
    {
        public CapabilityManifest Manifest { get; } = new(
            "delegtest.remediate", "1.0.0", "Restores the sample service.", RiskLevel.High, [], [], [], TimeSpan.FromSeconds(5),
            SupportsDryRun: true, new VerificationSpec("delegtest.read", [], "Reads the service state."));

        public Task<SkillReport> PrepareAsync(CapabilityRequest request, IToolInvoker toolInvoker, CancellationToken ct = default) =>
            Task.FromResult(new SkillReport(
                [new Evidence("cap-e1", EvidenceKind.Fact, "The service is down.", SecretReading, "delegtest.info", DateTimeOffset.UtcNow)],
                [new Finding("cap-f1", "The service is down.", ["cap-e1"], RiskLevel.High)],
                new ExecutionPlan(
                    "delegtest.remediate", "1.0.0", "Restart the service.",
                    [new ExecutionPlanStep(0, "delegtest.restart", ToolArguments.Empty, "Restart the service.")])));
    }

    private sealed class SampleSkill : ISkillProvider
    {
        public string SkillId => "delegtest.skill";

        public IReadOnlyList<ICapability> GetCapabilities() => [new RestoreCapability()];

        public IEnumerable<ITool> GetTools() => [];
    }

    private sealed class BlockingModel : IChatModel
    {
        public ChatModelDescriptor Descriptor { get; } = new("test", "test-model");

        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private static ModelResponse Plan()
    {
        var steps = new JsonArray { new JsonObject { ["description"] = "look", ["expectedTool"] = null } };
        return new ModelResponse(new JsonObject { ["rationale"] = "A plan.", ["steps"] = steps }.ToJsonString(), [], false, null);
    }

    /// <summary>What Discovery and the Diagnostic role ask of the model when the run goes well.</summary>
    private static ModelResponse[] HappyScript() =>
    [
        Plan(), new ModelResponse(null, [new ModelToolCall("c1", "delegtest.info", ToolArguments.Empty)], false, null), new ModelResponse("Done.", [], true, null),
        Plan(), new ModelResponse("{\"findings\":[{\"summary\":\"The service has stopped.\",\"evidenceIds\":[\"discovery-0\"],\"severity\":\"high\"}]}", [], true, null),
    ];

    private sealed record Host(TestAppFactory Factory, HttpClient Client, RestartTool Restart) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private static Host NewHost(IChatModel? model = null, IReadOnlyList<string>? roles = null, string? policy = PolicyYaml)
    {
#pragma warning disable CA2000 // Ownership passes to the returned Host, which disposes it; a failure while building it disposes it here.
        var factory = new TestAppFactory
#pragma warning restore CA2000
        {
            ChatModel = model ?? new QueueChatModel(HappyScript()),
            PolicyEngine = new FixedPolicyEngine(PolicyMode.Automatic),
            Roles = roles ?? ["viewer", "operator", "approver"],
        };
        try
        {
            if (policy is not null)
            {
                File.WriteAllText(Path.Combine(factory.TempDirectory, "policy.yaml"), policy);
            }

            var restart = new RestartTool();
            var tools = factory.Services.GetRequiredService<IToolRegistry>();
            tools.Register(Package, new ReadTool("delegtest.info", SecretReading));
            tools.Register(Package, new ReadTool("delegtest.read", RunningReading));
            tools.Register(Package, restart);
            factory.Services.GetRequiredService<ISkillRegistry>().Register(Package, new SampleSkill());
            tools.RefreshCapabilitiesAsync().GetAwaiter().GetResult();
            return new Host(factory, factory.CreateClient(), restart);
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    private static StartDelegationRequest Diagnose(string objective = "Why did the service stop?") => new(objective, null, null, null);

    private static StartDelegationRequest Fix(string objective = "Find out why the service stopped and fix it") =>
        new(objective, null, null, new DelegationRemediationBody("delegtest.skill", "delegtest.remediate", "local", "test", null, false, null));

    private static async Task<Guid> StartAsync(HttpClient client, StartDelegationRequest body, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/delegations") { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DelegationAcceptedResponse>())!.DelegationId;
    }

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
                    if (until(last!))
                    {
                        return last!;
                    }
                }

                await Task.Delay(25, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"The run never got there; it was {last?.Status ?? "not visible"}: {last?.ErrorMessage} roles: {string.Join(", ", (last?.Roles ?? []).Select(r => r.Role + "=" + r.Status + " " + r.ErrorMessage))}");
        }
    }

    private static Task<DelegationView> WaitForStatusAsync(HttpClient client, Guid id, string status) =>
        WaitForAsync(client, id, view => view.Status == status);

    private static async Task<IReadOnlyList<PendingPlanApproval>> PendingAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<IReadOnlyList<PendingPlanApproval>>("/api/delegations/approvals"))!;

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid id, string planHash, bool approved, string? note = null) =>
        client.PostAsJsonAsync($"/api/delegations/{id}/approval", new RespondToPlanApprovalRequest(planHash, approved, note));

    private static async Task<PendingPlanApproval> WaitForPendingPlanAsync(HttpClient client, Guid id)
    {
        using var timeout = new CancellationTokenSource(PollTimeout);
        while (!timeout.IsCancellationRequested)
        {
            var found = (await PendingAsync(client)).FirstOrDefault(p => p.DelegationId == id);
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(25, timeout.Token);
        }

        throw new TimeoutException("No plan came up for approval.");
    }

    private static AuthorityEnvelope Root(ActorIdentity actor) => new(
        actor, Depth: 0, ["delegtest.skill"], ["delegtest.remediate"], ["delegtest.info", "delegtest.read", "delegtest.restart"], RiskLevel.High,
        BlastRadius.Single, ["local"], ["test"], new DelegationBudget(30, 300_000, DateTimeOffset.UtcNow.AddHours(1)));

    private static async Task<Guid> SeedAsync(
        Host host, DelegationStatus status, IReadOnlyList<StepJournalEntry>? journal = null, bool withChange = true)
    {
        var actor = new ActorIdentity("api-user", "test-user", "Test User");
        var run = new DelegationRun
        {
            Id = Guid.NewGuid(),
            Node = NodeId.Local,
            Actor = actor,
            Objective = "Find out why the service stopped and fix it",
            Remediation = withChange
                ? new DelegationRemediationRequest("delegtest.skill", "delegtest.remediate", new CapabilityRequest(ToolArguments.Empty, "local", "test", BlastRadius.Single))
                : null,
            Status = status,
            RootEnvelope = Root(actor),
            Roles = [],
            Journal = journal ?? [],
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await host.Factory.Services.GetRequiredService<IDelegationStore>().StartAsync(run);
        return run.Id;
    }

    private static StepJournalEntry Unsettled() => new()
    {
        StepIndex = 0,
        ToolName = "delegtest.restart",
        ArgumentsHash = DelegationHasher.ComputeArgumentsHash(ToolArguments.Empty),
        IntentAtUtc = DateTimeOffset.UtcNow,
        Reconciliation = new StepReconciliation(ReconciliationAction.EscalatedToOperator, VerificationStatus.Inconclusive, ActorIdentity.RuntimeSystem, DateTimeOffset.UtcNow),
    };

    // ---- authentication and roles ----

    [Fact]
    public async Task Anonymous_IsRefused_OnEveryEndpoint()
    {
        using var host = NewHost();
        using var anonymous = host.Factory.CreateAnonymousClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri("/api/delegations", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri($"/api/delegations/{id}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/delegations", Diagnose())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(new Uri($"/api/delegations/{id}/cancel", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(new Uri($"/api/delegations/{id}/resume", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/delegations/{id}/reconcile", new ReconcileDelegationRequest("abandon", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri("/api/delegations/approvals", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DecideAsync(anonymous, id, "hash", true)).StatusCode);
    }

    [Fact]
    public async Task Viewer_CanRead_ButCannotStartCancelResumeApproveOrReconcile()
    {
        using var host = NewHost(roles: ["viewer"]);
        var id = await SeedAsync(host, DelegationStatus.Running);

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(new Uri("/api/delegations", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(new Uri($"/api/delegations/{id}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsJsonAsync("/api/delegations", Diagnose())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsync(new Uri($"/api/delegations/{id}/cancel", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsync(new Uri($"/api/delegations/{id}/resume", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsJsonAsync($"/api/delegations/{id}/reconcile", new ReconcileDelegationRequest("abandon", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync(new Uri("/api/delegations/approvals", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(host.Client, id, "hash", true)).StatusCode);
        Assert.Equal(DelegationStatus.Running, (await host.Factory.Services.GetRequiredService<IDelegationStore>().LoadAsync(id))!.Status);
    }

    [Fact]
    public async Task Reconcile_NeedsTheAdministratorRole_AnOperatorAndApproverIsNotEnough()
    {
        using var host = NewHost();
        var id = await SeedAsync(host, DelegationStatus.RequiresReconciliation, [Unsettled()]);

        var response = await host.Client.PostAsJsonAsync($"/api/delegations/{id}/reconcile", new ReconcileDelegationRequest("accept", null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(DelegationStatus.RequiresReconciliation, (await host.Factory.Services.GetRequiredService<IDelegationStore>().LoadAsync(id))!.Status);
    }

    [Fact]
    public async Task ApprovingAPlan_NeedsTheApproverRole_AnOperatorAloneCannotApproveItsOwnPlan()
    {
        using var host = NewHost(roles: ["viewer", "operator"]);
        var id = await StartAsync(host.Client, Fix());
        await WaitForAsync(host.Client, id, view => view.AwaitingPlanApproval);

        var refused = await DecideAsync(host.Client, id, "anything", true);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(0, host.Restart.Executions);
        var still = await WaitForAsync(host.Client, id, _ => true);
        Assert.True(still.AwaitingPlanApproval);
        Assert.Equal(nameof(DelegationStatus.Running), still.Status);
    }

    // ---- start ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Start_WithoutAnObjective_Is400(string objective)
    {
        using var host = NewHost();

        var response = await host.Client.PostAsJsonAsync("/api/delegations", Diagnose(objective));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Start_WithABadChange_OrBudget_Is400()
    {
        using var host = NewHost();

        var noTarget = new StartDelegationRequest("fix", null, null, new DelegationRemediationBody("delegtest.skill", "delegtest.remediate", "", "test", null, false, null));
        var wideBlast = new StartDelegationRequest("fix", null, null, new DelegationRemediationBody("delegtest.skill", "delegtest.remediate", "local", "test", "everything", false, null));
        var numericBlast = new StartDelegationRequest("fix", null, null, new DelegationRemediationBody("delegtest.skill", "delegtest.remediate", "local", "test", "1", false, null));
        var noSteps = new StartDelegationRequest("fix", 0, null, null);
        var noTokens = new StartDelegationRequest("fix", null, -5, null);

        foreach (var bad in new[] { noTarget, wideBlast, numericBlast, noSteps, noTokens })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("/api/delegations", bad)).StatusCode);
        }

        Assert.Empty((await host.Client.GetFromJsonAsync<IReadOnlyList<DelegationView>>("/api/delegations"))!);
    }

    [Fact]
    public async Task Start_WithAnOverlongIdempotencyKey_Is400()
    {
        using var host = NewHost();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/delegations") { Content = JsonContent.Create(Diagnose()) };
        request.Headers.Add("Idempotency-Key", new string('k', 129));

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Start_WithoutAChange_DiagnosesOnly_AndAsksNobody()
    {
        using var host = NewHost();

        var id = await StartAsync(host.Client, Diagnose());
        var done = await WaitForStatusAsync(host.Client, id, nameof(DelegationStatus.DiagnosisCompleted));

        Assert.Empty(await PendingAsync(host.Client));
        Assert.Equal(0, host.Restart.Executions);
        Assert.Null(done.PlanHash);
        Assert.Equal("test-user", done.ActorId);
        Assert.Contains(done.Roles, r => r.Role == nameof(AgentRoleKind.Discovery) && r.Status == nameof(DelegationRoleStatus.Completed));
        Assert.Contains(done.Roles, r => r.Role == nameof(AgentRoleKind.Diagnostic) && r.Findings.Any(f => f.Summary == "The service has stopped."));
    }

    [Fact]
    public async Task Start_ARunOfAChange_WaitsForAnApprover_ThenRunsTheApprovedPlan_AndIsVerified()
    {
        using var host = NewHost();

        var id = await StartAsync(host.Client, Fix());
        var waiting = await WaitForAsync(host.Client, id, view => view.AwaitingPlanApproval);
        var pending = await WaitForPendingPlanAsync(host.Client, id);

        Assert.True(waiting.RunningInThisHost);
        Assert.Equal(nameof(DelegationStatus.Running), waiting.Status);
        Assert.Equal(0, host.Restart.Executions);
        Assert.Equal("delegtest.restart", Assert.Single(pending.Steps).Tool);
        Assert.Equal("delegtest.skill", pending.SkillId);
        Assert.Equal("local", pending.Target);
        Assert.Contains(pending.Findings, f => f.Summary == "The service has stopped.");
        Assert.Contains(pending.Findings, f => f.Id == "cap-f1" && f.EvidenceIds.Contains("cap-e1"));
        Assert.NotNull(pending.Authority);
        Assert.Contains("delegtest.restart", pending.Authority!.Tools);

        var decided = await DecideAsync(host.Client, id, pending.PlanHash, approved: true, note: "looks right");
        Assert.Equal(HttpStatusCode.NoContent, decided.StatusCode);

        var done = await WaitForStatusAsync(host.Client, id, nameof(DelegationStatus.Completed));
        Assert.Equal(1, host.Restart.Executions);
        Assert.Equal(pending.PlanHash, done.PlanHash);
        Assert.Equal("test-user", done.Approval!.ApproverId);
        Assert.Equal(pending.PlanHash, done.Approval.PlanHash);
        var verification = Assert.Single(done.Roles, r => r.Role == nameof(AgentRoleKind.Verification));
        Assert.Equal(nameof(VerificationStatus.Confirmed), verification.Verification!.Status);
        Assert.Equal(nameof(StepOutcomeKind.Succeeded), Assert.Single(done.Journal).Outcome);
        Assert.Empty(await PendingAsync(host.Client));
    }

    [Fact]
    public async Task ADecision_ForAnotherPlansHash_IsRefused_AndDecidesNothing()
    {
        using var host = NewHost();
        var id = await StartAsync(host.Client, Fix());
        var pending = await WaitForPendingPlanAsync(host.Client, id);

        var wrong = await DecideAsync(host.Client, id, "not-the-hash", approved: true);

        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal(pending.PlanHash, (await WaitForPendingPlanAsync(host.Client, id)).PlanHash);
        Assert.Equal(0, host.Restart.Executions);
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(host.Client, id, " ", approved: true)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DecideAsync(host.Client, Guid.NewGuid(), pending.PlanHash, approved: true)).StatusCode);
    }

    [Fact]
    public async Task ARejectedPlan_EndsRejected_AndNothingIsChanged()
    {
        using var host = NewHost();
        var id = await StartAsync(host.Client, Fix());
        var pending = await WaitForPendingPlanAsync(host.Client, id);

        var decided = await DecideAsync(host.Client, id, pending.PlanHash, approved: false);

        Assert.Equal(HttpStatusCode.NoContent, decided.StatusCode);
        var done = await WaitForStatusAsync(host.Client, id, nameof(DelegationStatus.Rejected));
        Assert.Equal(0, host.Restart.Executions);
        Assert.Empty(done.Journal);
    }

    [Fact]
    public async Task ADecision_CannotBeGivenTwice()
    {
        using var host = NewHost();
        var id = await StartAsync(host.Client, Fix());
        var pending = await WaitForPendingPlanAsync(host.Client, id);
        Assert.Equal(HttpStatusCode.NoContent, (await DecideAsync(host.Client, id, pending.PlanHash, approved: true)).StatusCode);
        await WaitForStatusAsync(host.Client, id, nameof(DelegationStatus.Completed));

        var again = await DecideAsync(host.Client, id, pending.PlanHash, approved: true);

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(1, host.Restart.Executions);
    }

    [Fact]
    public async Task Start_WithTheSameIdempotencyKey_ReturnsTheEarlierRun_AndStartsNothing()
    {
        using var host = NewHost();

        var first = await StartAsync(host.Client, Diagnose(), "ticket-42");
        await WaitForStatusAsync(host.Client, first, nameof(DelegationStatus.DiagnosisCompleted));
        var again = await StartAsync(host.Client, Diagnose(), "ticket-42");
        var other = await StartAsync(host.Client, Diagnose("Something else"), "ticket-43");

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public async Task Start_WithTheSameKeyWhileTheFirstIsStillRunning_ReturnsTheSameRun()
    {
        using var host = NewHost(model: new BlockingModel());

        var first = await StartAsync(host.Client, Diagnose(), "ticket-42");
        var again = await StartAsync(host.Client, Diagnose(), "ticket-42");

        Assert.Equal(first, again);
        var runs = await host.Client.GetFromJsonAsync<IReadOnlyList<DelegationView>>("/api/delegations");
        Assert.Single(runs!);
    }

    [Fact]
    public async Task Start_WithTheSameKeyFromAnotherOperator_IsAnotherRun()
    {
        using var host = NewHost();
        var first = await StartAsync(host.Client, Diagnose(), "ticket-42");
        await WaitForStatusAsync(host.Client, first, nameof(DelegationStatus.DiagnosisCompleted));

        var store = host.Factory.Services.GetRequiredService<IDelegationStore>();
        var stored = (await store.LoadAsync(first))!;
        var result = await store.StartAsync(stored with { Id = Guid.NewGuid(), Actor = new ActorIdentity("api-user", "someone-else", null), IdempotencyKey = "ticket-42" });

        Assert.NotEqual(first, result.Run.Id);
    }

    [Fact]
    public async Task Start_WhenTheHostIsAtCapacity_Is503()
    {
        using var host = NewHost(model: new BlockingModel());
        await StartAsync(host.Client, Diagnose("one"));
        await StartAsync(host.Client, Diagnose("two"));

        var response = await host.Client.PostAsJsonAsync("/api/delegations", Diagnose("three"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // ---- the profile dimension ----

    [Fact]
    public async Task Start_WithABrokenPolicyFile_EndsDeniedOnTheProfileDimension_BeforeAnyModelCall()
    {
        var model = new QueueChatModel();
        using var host = NewHost(model: model, policy: "delegation: [this is: not valid");

        var id = await StartAsync(host.Client, Fix());
        var done = await WaitForStatusAsync(host.Client, id, nameof(DelegationStatus.Denied));

        Assert.Equal(nameof(EnvelopeDimension.Profile), done.Denial!.Dimension);
        Assert.DoesNotContain(done.Roles, r => r.Status == nameof(DelegationRoleStatus.Completed));
        Assert.Equal(0, host.Restart.Executions);
    }

    [Fact]
    public async Task Start_WithNoPolicyFile_EndsDeniedToo_BecauseDelegationIsOffUntilGranted()
    {
        using var host = NewHost(model: new QueueChatModel(), policy: null);

        var id = await StartAsync(host.Client, Fix());
        var done = await WaitForStatusAsync(host.Client, id, nameof(DelegationStatus.Denied));

        Assert.Equal(nameof(EnvelopeDimension.Profile), done.Denial!.Dimension);
    }

    // ---- read and list ----

    [Fact]
    public async Task Read_OfARunThatIsNotStored_Is404()
    {
        using var host = NewHost();

        var response = await host.Client.GetAsync(new Uri($"/api/delegations/{Guid.NewGuid()}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_ShowsRecentRuns_AndFiltersByStatus()
    {
        using var host = NewHost();
        var done = await StartAsync(host.Client, Diagnose());
        await WaitForStatusAsync(host.Client, done, nameof(DelegationStatus.DiagnosisCompleted));
        var seeded = await SeedAsync(host, DelegationStatus.Running);

        var all = (await host.Client.GetFromJsonAsync<IReadOnlyList<DelegationView>>("/api/delegations"))!;
        var running = (await host.Client.GetFromJsonAsync<IReadOnlyList<DelegationView>>("/api/delegations?status=running"))!;

        Assert.Equal(2, all.Count);
        Assert.Equal(seeded, Assert.Single(running).Id);
        Assert.False(running[0].RunningInThisHost);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync(new Uri("/api/delegations?status=nonsense", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task List_HonoursTheLimit_WithAndWithoutAStatus()
    {
        using var host = NewHost();
        await SeedAsync(host, DelegationStatus.Running);
        await SeedAsync(host, DelegationStatus.Running);
        await SeedAsync(host, DelegationStatus.Running);

        var recent = (await host.Client.GetFromJsonAsync<IReadOnlyList<DelegationView>>("/api/delegations?limit=2"))!;
        var running = (await host.Client.GetFromJsonAsync<IReadOnlyList<DelegationView>>("/api/delegations?status=Running&limit=2"))!;
        var all = (await host.Client.GetFromJsonAsync<IReadOnlyList<DelegationView>>("/api/delegations?status=Running"))!;

        Assert.Equal(2, recent.Count);
        Assert.Equal(2, running.Count);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task Responses_NeverCarryWhatATooldReturned_OnlyWhichEvidenceExists()
    {
        using var host = NewHost();
        var id = await StartAsync(host.Client, Fix());
        var pending = await WaitForPendingPlanAsync(host.Client, id);
        await DecideAsync(host.Client, id, pending.PlanHash, approved: true);
        await WaitForStatusAsync(host.Client, id, nameof(DelegationStatus.Completed));

        var detail = await host.Client.GetStringAsync(new Uri($"/api/delegations/{id}", UriKind.Relative));
        var list = await host.Client.GetStringAsync(new Uri("/api/delegations", UriKind.Relative));

        foreach (var body in new[] { detail, list })
        {
            Assert.DoesNotContain("hunter2", body, StringComparison.Ordinal);
            Assert.DoesNotContain("cpu 91%", body, StringComparison.Ordinal);
            Assert.DoesNotContain(RunningReading, body, StringComparison.Ordinal);
        }

        using var document = JsonDocument.Parse(detail);
        var evidence = document.RootElement.GetProperty("roles").EnumerateArray()
            .SelectMany(role => role.GetProperty("evidence").EnumerateArray()).ToList();
        Assert.NotEmpty(evidence);
        Assert.All(evidence, e => Assert.False(e.TryGetProperty("data", out _)));
        Assert.False(document.RootElement.TryGetProperty("rootEnvelope", out _));
    }

    [Fact]
    public async Task ThePendingApprovalList_ShowsThePlan_NotTheEvidenceData()
    {
        using var host = NewHost();
        var id = await StartAsync(host.Client, Fix());
        await WaitForPendingPlanAsync(host.Client, id);

        var body = await host.Client.GetStringAsync(new Uri("/api/delegations/approvals", UriKind.Relative));

        Assert.DoesNotContain("hunter2", body, StringComparison.Ordinal);
        Assert.DoesNotContain("cpu 91%", body, StringComparison.Ordinal);
        Assert.Contains("delegtest.restart", body, StringComparison.Ordinal);
    }

    // ---- cancel ----

    [Fact]
    public async Task Cancel_OfARunThisHostIsExecuting_StopsItWhereItIs_AndAsksNothingMore()
    {
        using var host = NewHost();
        var id = await StartAsync(host.Client, Fix());
        await WaitForPendingPlanAsync(host.Client, id);

        var response = await host.Client.PostAsync(new Uri($"/api/delegations/{id}/cancel", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = (await response.Content.ReadFromJsonAsync<DelegationView>())!;
        Assert.Equal(nameof(DelegationStatus.Cancelled), view.Status);
        Assert.Empty(await PendingAsync(host.Client));
        Assert.Equal(0, host.Restart.Executions);
        Assert.False((await WaitForAsync(host.Client, id, v => !v.RunningInThisHost)).RunningInThisHost);
    }

    [Fact]
    public async Task Cancel_IsAudited_UnderTheOperatorWhoAskedForIt()
    {
        using var host = NewHost();
        var id = await StartAsync(host.Client, Fix());
        await WaitForPendingPlanAsync(host.Client, id);

        await host.Client.PostAsync(new Uri($"/api/delegations/{id}/cancel", UriKind.Relative), null);

        var audit = await File.ReadAllLinesAsync(Path.Combine(host.Factory.TempDirectory, "audit.jsonl"));
        Assert.Contains(audit, line => line.Contains("Cancelled on request through the API", StringComparison.Ordinal) && line.Contains("test-user", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_OfARunACrashLeftRunning_ClosesItInTheStore()
    {
        using var host = NewHost();
        var id = await SeedAsync(host, DelegationStatus.Running);

        var response = await host.Client.PostAsync(new Uri($"/api/delegations/{id}/cancel", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(nameof(DelegationStatus.Cancelled), (await response.Content.ReadFromJsonAsync<DelegationView>())!.Status);
    }

    [Fact]
    public async Task Cancel_OfARunWaitingForReconciliation_IsRefused_ItIsSettledByReconcile()
    {
        using var host = NewHost();
        var id = await SeedAsync(host, DelegationStatus.RequiresReconciliation, [Unsettled()]);

        var response = await host.Client.PostAsync(new Uri($"/api/delegations/{id}/cancel", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(DelegationStatus.RequiresReconciliation, (await host.Factory.Services.GetRequiredService<IDelegationStore>().LoadAsync(id))!.Status);
    }

    [Fact]
    public async Task Cancel_OfARunThatIsNotStored_Is404()
    {
        using var host = NewHost();

        var response = await host.Client.PostAsync(new Uri($"/api/delegations/{Guid.NewGuid()}/cancel", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- resume ----

    [Fact]
    public async Task Resume_ContinuesAStoredRun_AndPutsItsPlanToAnApproverAgain()
    {
        using var host = NewHost();
        var id = await SeedAsync(host, DelegationStatus.Running);

        var response = await host.Client.PostAsync(new Uri($"/api/delegations/{id}/resume", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var pending = await WaitForPendingPlanAsync(host.Client, id);
        Assert.Equal(0, host.Restart.Executions);
        Assert.Equal(HttpStatusCode.NoContent, (await DecideAsync(host.Client, id, pending.PlanHash, approved: true)).StatusCode);
        var done = await WaitForStatusAsync(host.Client, id, nameof(DelegationStatus.Completed));
        Assert.Equal(1, done.ResumeCount);
        Assert.Equal(1, host.Restart.Executions);
    }

    [Fact]
    public async Task Resume_OfARunThisHostIsAlreadyExecuting_Is409()
    {
        using var host = NewHost();
        var id = await StartAsync(host.Client, Fix());
        await WaitForPendingPlanAsync(host.Client, id);

        var response = await host.Client.PostAsync(new Uri($"/api/delegations/{id}/resume", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Resume_OfARunThatEnded_Is409_AndOfOneNotStored_404()
    {
        using var host = NewHost();
        var ended = await StartAsync(host.Client, Diagnose());
        await WaitForStatusAsync(host.Client, ended, nameof(DelegationStatus.DiagnosisCompleted));

        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsync(new Uri($"/api/delegations/{ended}/resume", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsync(new Uri($"/api/delegations/{Guid.NewGuid()}/resume", UriKind.Relative), null)).StatusCode);
    }

    // ---- reconcile ----

    [Fact]
    public async Task Reconcile_Abandon_EndsTheRun_AndIsAttributedToTheAdministrator()
    {
        using var host = NewHost(roles: ["viewer", "administrator"]);
        var id = await SeedAsync(host, DelegationStatus.RequiresReconciliation, [Unsettled()]);

        var response = await host.Client.PostAsJsonAsync($"/api/delegations/{id}/reconcile", new ReconcileDelegationRequest("abandon", "not needed"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(nameof(DelegationStatus.Abandoned), (await response.Content.ReadFromJsonAsync<DelegationView>())!.Status);
        var audit = await File.ReadAllLinesAsync(Path.Combine(host.Factory.TempDirectory, "audit.jsonl"));
        Assert.Contains(audit, line => line.Contains("delegationReconciliation", StringComparison.Ordinal) && line.Contains("test-user", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reconcile_Accept_MarksTheStepDone_AndTheRunCanBeResumedAfterwards_WithoutRunningTheStepAgain()
    {
        using var host = NewHost(roles: ["viewer", "operator", "approver", "administrator"]);
        var id = await SeedAsync(host, DelegationStatus.RequiresReconciliation, [Unsettled()]);

        var response = await host.Client.PostAsJsonAsync($"/api/delegations/{id}/reconcile", new ReconcileDelegationRequest("ACCEPT", null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = (await response.Content.ReadFromJsonAsync<DelegationView>())!;
        Assert.Equal(nameof(DelegationStatus.Running), view.Status);
        Assert.Equal(nameof(ReconciliationAction.OperatorAcceptedDone), Assert.Single(view.Journal).Reconciliation);
        Assert.Equal(0, host.Restart.Executions);
    }

    [Fact]
    public async Task Reconcile_WithABadDecision_OfARunNotWaiting_OrNotStored_IsRefused()
    {
        using var host = NewHost(roles: ["viewer", "administrator"]);
        var running = await SeedAsync(host, DelegationStatus.Running);
        var waiting = await SeedAsync(host, DelegationStatus.RequiresReconciliation, [Unsettled()]);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync($"/api/delegations/{waiting}/reconcile", new ReconcileDelegationRequest("retry", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsJsonAsync($"/api/delegations/{running}/reconcile", new ReconcileDelegationRequest("abandon", null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsJsonAsync($"/api/delegations/{Guid.NewGuid()}/reconcile", new ReconcileDelegationRequest("abandon", null))).StatusCode);
        Assert.Equal(DelegationStatus.RequiresReconciliation, (await host.Factory.Services.GetRequiredService<IDelegationStore>().LoadAsync(waiting))!.Status);
    }
}
