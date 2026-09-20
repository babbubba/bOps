// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Audit;
using bOps.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

/// <summary>
/// The V1.2 release gate (V1.2-M): one objective taken end to end through the real host, over HTTP, with the real system and
/// filesystem tools and the real policy engine, on whichever platform the suite runs on (CI runs it on Windows and on Linux).
/// Discovery reads the machine with <c>system.info</c>; Diagnostic turns that into a finding and prepares a plan through a
/// Capability; a human approves the plan by its hash and then the <c>fs.write</c> step, which the real policy holds for approval;
/// Remediation writes a real file; a distinct Verification identity reads the file back with <c>fs.stat</c>. A second scenario
/// kills the host between the write and the record of its outcome, starts a new host over the same files, and shows the run
/// resumes, settles the step by its own verification and does not write the file twice. Only the model is scripted, and the
/// Capability that names the change is a small test one, as no shipped Skill writes a file.
/// </summary>
public sealed class V12ReleaseGateTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);
    private static readonly PackageId Package = new("bops.tests.v12gate");
    private const string Content = "fixed by a delegated run";
    private static readonly string[] PipelineOrder = ["Discovery", "Diagnostic", "Remediation", "Verification"];

    // The real policy engine: Read and Low run on their own, Medium and High wait for a human, Critical never runs. A policy file
    // that has no `defaults` fails closed, so the section the host would otherwise get from the built-in default is written out, and a
    // Skill step needs an exact rule of its own (ADR-0025), here one that holds it for approval.
    private const string PolicyYaml = """
        defaults:
          read: automatic
          low: automatic
          medium: approval
          high: approval
          critical: forbidden
        skills:
          - skill: gate.skill
            capability: gate.marker
            target: local
            environment: test
            blastRadius: single
            mode: approval
        delegation:
          roles:
            discovery:
              tools: [system.info]
              maxRisk: read
              maxBlastRadius: single
              targets: [local]
              environments: [test]
              maxSteps: 6
              maxTokens: 100000
              maxDuration: 00:05:00
            diagnostic:
              skills: [gate.skill]
              capabilities: [gate.marker]
              tools: [system.info]
              maxRisk: read
              maxBlastRadius: single
              targets: [local]
              environments: [test]
              maxSteps: 6
              maxTokens: 100000
              maxDuration: 00:05:00
            remediation:
              skills: [gate.skill]
              capabilities: [gate.marker]
              tools: [fs.write]
              maxRisk: high
              maxBlastRadius: single
              targets: [local]
              environments: [test]
              maxSteps: 6
              maxDuration: 00:05:00
            verification:
              tools: [fs.stat]
              maxRisk: read
              maxBlastRadius: single
              targets: [local]
              environments: [test]
              maxSteps: 6
              maxDuration: 00:05:00
        """;

    // ---- fixtures ----

    private sealed class MarkerCapability(string path) : ICapability
    {
        public CapabilityManifest Manifest { get; } = new(
            "gate.marker", "1.0.0", "Writes a marker file.", RiskLevel.High, [], [], [], TimeSpan.FromSeconds(5),
            SupportsDryRun: true, new VerificationSpec("fs.stat", ["path"], "Confirms the marker exists."));

        public Task<SkillReport> PrepareAsync(CapabilityRequest request, IToolInvoker toolInvoker, CancellationToken ct = default) =>
            Task.FromResult(new SkillReport(
                [new Evidence("gate-e1", EvidenceKind.Fact, "The marker is missing.", null, "fs.stat", DateTimeOffset.UtcNow)],
                [new Finding("gate-f1", "The marker file is missing.", ["gate-e1"], RiskLevel.High)],
                new ExecutionPlan(
                    "gate.marker", "1.0.0", "Write the marker.",
                    [new ExecutionPlanStep(0, "fs.write", ToolArguments.FromJson(new JsonObject { ["path"] = path, ["content"] = Content }), "Write the marker file.")])));
    }

    private sealed class GateSkill(string path) : ISkillProvider
    {
        public string SkillId => "gate.skill";

        public IReadOnlyList<ICapability> GetCapabilities() => [new MarkerCapability(path)];

        public IEnumerable<ITool> GetTools() => [];
    }

    private static ModelResponse Plan()
    {
        var steps = new JsonArray { new JsonObject { ["description"] = "look at the machine", ["expectedTool"] = null } };
        return new ModelResponse(new JsonObject { ["rationale"] = "A plan.", ["steps"] = steps }.ToJsonString(), [], false, null);
    }

    private static ModelResponse[] Script() =>
    [
        Plan(), new ModelResponse(null, [new ModelToolCall("c1", "system.info", ToolArguments.Empty)], false, null), new ModelResponse("Done.", [], true, null),
        Plan(), new ModelResponse("{\"findings\":[{\"summary\":\"The marker is missing.\",\"evidenceIds\":[\"discovery-0\"],\"severity\":\"high\"}]}", [], true, null),
    ];

    /// <summary>The real store, until the host "dies" as the outcome of the first step is about to be recorded: nothing after that is kept.</summary>
    private sealed class DiesBeforeTheOutcome(IDelegationStore inner) : IDelegationStore
    {
        private bool _dead;

        /// <summary>Completed at the moment the host "dies", so a test waits for it instead of guessing how long that takes.</summary>
        public TaskCompletionSource Died { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DelegationStartResult> StartAsync(DelegationRun run, CancellationToken ct = default) => inner.StartAsync(run, ct);

        public async Task SaveAsync(DelegationRun run, CancellationToken ct = default)
        {
            if (_dead)
            {
                return;
            }

            if (run.Journal.Any(entry => entry.Outcome is not null))
            {
                _dead = true;
                Died.TrySetResult();
                throw new OperationCanceledException("The host died here, after the write and before its outcome was recorded.");
            }

            await inner.SaveAsync(run, ct);
        }

        public Task<DelegationRun?> LoadAsync(Guid delegationId, CancellationToken ct = default) => inner.LoadAsync(delegationId, ct);

        public Task<IReadOnlyList<DelegationRun>> ListByStatusAsync(DelegationStatus status, CancellationToken ct = default) => inner.ListByStatusAsync(status, ct);

        public Task<IReadOnlyList<DelegationRun>> ListRecentAsync(int limit, CancellationToken ct = default) => inner.ListRecentAsync(limit, ct);
    }

    private sealed record Host(TestAppFactory Factory, HttpClient Client, string Marker) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private static Host NewHost(string? directory = null, bool keep = false, IChatModel? model = null, DiesBeforeTheOutcome? dyingStore = null)
    {
        var dir = directory ?? Directory.CreateTempSubdirectory("bops-v12gate-").FullName;
#pragma warning disable CA2000 // Ownership passes to the returned Host, which disposes it; a failure while building it disposes it here.
        var factory = new TestAppFactory
#pragma warning restore CA2000
        {
            TempDirectory = dir,
            KeepTempDirectory = keep,
            ChatModel = model ?? new QueueChatModel(Script()),
            Roles = ["viewer", "operator", "approver", "administrator"],
            ConfigureExtraServices = dyingStore is null ? null : services => services.AddSingleton<IDelegationStore>(dyingStore),
        };
        try
        {
            File.WriteAllText(Path.Combine(dir, "policy.yaml"), PolicyYaml);
            var marker = Path.Combine(dir, "marker.txt");
            factory.Services.GetRequiredService<ISkillRegistry>().Register(Package, new GateSkill(marker));
            return new Host(factory, factory.CreateClient(), marker);
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    private static async Task<Guid> StartChangeAsync(HttpClient client)
    {
        var body = new StartDelegationRequest(
            "Find out why the marker is missing and restore it", null, null,
            new DelegationRemediationBody("gate.skill", "gate.marker", "local", "test", null, false, null));
        var response = await client.PostAsJsonAsync("/api/delegations", body);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DelegationAcceptedResponse>())!.DelegationId;
    }

    /// <summary>Polls the run, deciding whatever a human is asked (its plan, and each step the real policy holds for approval) with an approval, until <paramref name="until"/> holds.</summary>
    private static async Task<DelegationView> ApproveUntilAsync(HttpClient client, Guid id, Func<DelegationView, bool> until)
    {
        using var timeout = new CancellationTokenSource(PollTimeout);
        DelegationView? last = null;
        try
        {
            while (true)
            {
                last = (await client.GetFromJsonAsync<DelegationView>($"/api/delegations/{id}", timeout.Token))!;
                if (until(last))
                {
                    return last;
                }

                if (last.AwaitingPlanApproval)
                {
                    foreach (var plan in (await client.GetFromJsonAsync<List<PendingPlanApproval>>("/api/delegations/approvals", timeout.Token))!.Where(p => p.DelegationId == id))
                    {
                        var decided = await client.PostAsJsonAsync($"/api/delegations/{id}/approval", new RespondToPlanApprovalRequest(plan.PlanHash, true, "gate"), timeout.Token);
                        Assert.Equal(HttpStatusCode.NoContent, decided.StatusCode);
                    }
                }
                else
                {
                    foreach (var step in (await client.GetFromJsonAsync<List<PendingApproval>>("/api/approvals/pending", timeout.Token))!)
                    {
                        var decided = await client.PostAsJsonAsync($"/api/approvals/{step.Id}/respond", new RespondToApprovalRequest(true, "gate"), timeout.Token);
                        Assert.True(decided.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound);
                    }
                }

                await Task.Delay(200, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"The run never got there; it was {last?.Status ?? "not visible"} {last?.ErrorMessage}, roles: {string.Join(", ", (last?.Roles ?? []).Select(r => r.Role + "=" + r.Status + " " + r.ErrorMessage))}");
        }
    }

    private static string Describe(DelegationView run) =>
        $"The run ended {run.Status}: {run.ErrorMessage} {run.Denial?.Reason} roles: {string.Join("; ", run.Roles.Select(r => $"{r.Role}={r.Status} {r.ErrorMessage}"))}";

    private static List<JsonElement> AuditEvents(string directory)
    {
        var events = new List<JsonElement>();
        foreach (var line in File.ReadAllLines(Path.Combine(directory, "audit.jsonl")).Where(l => l.Length > 0))
        {
            using var envelope = JsonDocument.Parse(line);
            var eventJson = envelope.RootElement.EnumerateObject().First(p => string.Equals(p.Name, "EventJson", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
            events.Add(JsonDocument.Parse(eventJson).RootElement.Clone());
        }

        return events;
    }

    private static JsonElement? Get(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: not JsonValueKind.Undefined } property
            ? property.Value
            : null;

    private static string? Text(JsonElement element, string name) => Get(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool Belongs(JsonElement evt, Guid delegation) =>
        Get(evt, "delegation") is { } block && string.Equals(Text(block, "delegationId"), delegation.ToString(), StringComparison.OrdinalIgnoreCase);

    private static string? RoleOf(JsonElement evt)
    {
        var agent = Get(evt, "delegation") is { } block ? Get(block, "agent") : null;
        if (agent is not { } found || Get(found, "role") is not { } role)
        {
            return null;
        }

        return role.ValueKind == JsonValueKind.Number ? ((AgentRoleKind)role.GetInt32()).ToString() : role.GetString();
    }

    private static string? AgentOf(JsonElement evt) =>
        Get(evt, "delegation") is { } block && Get(block, "agent") is { } agent && Get(agent, "id") is { } id ? id.GetRawText() : null;

    private static bool IsToolCall(JsonElement evt, string tool) =>
        string.Equals(Text(evt, "eventType"), "toolCall", StringComparison.Ordinal) && string.Equals(Text(evt, "tool"), tool, StringComparison.Ordinal);

    // ---- the objective, end to end ----

    [Fact]
    public async Task AnObjective_IsDiscovered_Diagnosed_Approved_Remediated_AndVerifiedByDistinctRoles_WithRealTools()
    {
        using var host = NewHost();
        Assert.False(File.Exists(host.Marker));

        var id = await StartChangeAsync(host.Client);
        var done = await ApproveUntilAsync(host.Client, id, run => run.Status != nameof(DelegationStatus.Running));

        Assert.True(done.Status == nameof(DelegationStatus.Completed), Describe(done));
        Assert.Equal(Content, await File.ReadAllTextAsync(host.Marker));
        Assert.Equal(PipelineOrder, done.Roles.Select(r => r.Role));
        Assert.Equal(4, done.Roles.Select(r => r.AgentId).Distinct().Count());
        Assert.Equal(nameof(VerificationStatus.Confirmed), done.Roles.Single(r => r.Role == "Verification").Verification!.Status);
        Assert.Equal("test-user", done.Approval!.ApproverId);
        Assert.Equal(nameof(StepOutcomeKind.Succeeded), Assert.Single(done.Journal).Outcome);
        Assert.Contains(done.Roles.Single(r => r.Role == "Diagnostic").Findings, f => f.Summary == "The marker is missing.");

        // The audit shows each role acting with its own identity, tied to the run, and the chain is intact.
        var events = AuditEvents(host.Factory.TempDirectory).Where(e => Belongs(e, id)).ToList();
        var discovery = events.Where(e => IsToolCall(e, "system.info")).ToList();
        var write = events.Where(e => IsToolCall(e, "fs.write")).ToList();
        var verify = events.Where(e => IsToolCall(e, "fs.stat")).ToList();
        Assert.NotEmpty(discovery);
        Assert.Single(write);
        Assert.NotEmpty(verify);
        Assert.All(discovery, e => Assert.Equal("Discovery", RoleOf(e)));
        Assert.Equal("Remediation", RoleOf(write[0]));
        Assert.Contains(verify, e => RoleOf(e) == "Verification");
        Assert.NotEqual(AgentOf(write[0]), AgentOf(verify.First(e => RoleOf(e) == "Verification")));
        Assert.True(AuditChainVerifier.VerifyFile(Path.Combine(host.Factory.TempDirectory, "audit.jsonl")).IsValid);
    }

    // ---- the crash ----

    [Fact]
    public async Task AHostThatDiesBetweenTheWriteAndItsRecord_IsResumedByANewHost_ThatSettlesTheStepByVerification_AndDoesNotWriteTwice()
    {
        var dir = Directory.CreateTempSubdirectory("bops-v12gate-").FullName;
        Guid id;
        string marker;
        var dying = new DiesBeforeTheOutcome(new SqliteDelegationStore(Path.Combine(dir, "delegations.db")));
        using (var first = NewHost(dir, keep: true, dyingStore: dying))
        {
            marker = first.Marker;
            id = await StartChangeAsync(first.Client);

            // The plan is approved and so is the write; the file is written; the host dies before the outcome is recorded.
            await ApproveUntilAsync(first.Client, id, _ => dying.Died.Task.IsCompleted);
        }

        Assert.Equal(Content, await File.ReadAllTextAsync(marker));

        using var second = NewHost(dir, keep: false, model: new QueueChatModel());
        var stored = await second.Client.GetFromJsonAsync<DelegationView>($"/api/delegations/{id}");
        Assert.Equal(nameof(DelegationStatus.Running), stored!.Status);
        Assert.False(stored.RunningInThisHost);
        Assert.Null(Assert.Single(stored.Journal).Outcome);

        var resumed = await second.Client.PostAsync(new Uri($"/api/delegations/{id}/resume", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
        var done = await ApproveUntilAsync(second.Client, id, run => run.Status != nameof(DelegationStatus.Running));

        Assert.True(done.Status == nameof(DelegationStatus.Completed), Describe(done));
        Assert.Equal(1, done.ResumeCount);
        Assert.Equal(nameof(ReconciliationAction.VerifiedDone), Assert.Single(done.Journal).Reconciliation);
        // The interrupted Remediation role is ended as failed, and the step it left unsettled is confirmed by a Verification identity of its
        // own (the read-back the tool declares), before the run's final Verification role, which is the last.
        var finalVerification = done.Roles[^1];
        Assert.Equal("Verification", finalVerification.Role);
        Assert.Equal(nameof(VerificationStatus.Confirmed), finalVerification.Verification!.Status);
        Assert.Equal(nameof(DelegationRoleStatus.Failed), done.Roles.Single(r => r.Role == "Remediation").Status);
        Assert.Equal(done.Roles.Count, done.Roles.Select(r => r.AgentId).Distinct().Count());
        Assert.Equal(Content, await File.ReadAllTextAsync(marker));

        // Written once, by the first host; the second one confirmed it and never ran the step again.
        var writes = AuditEvents(dir).Where(e => Belongs(e, id) && IsToolCall(e, "fs.write")).ToList();
        Assert.Single(writes);
        Assert.True(AuditChainVerifier.VerifyFile(Path.Combine(dir, "audit.jsonl")).IsValid);
    }
}
