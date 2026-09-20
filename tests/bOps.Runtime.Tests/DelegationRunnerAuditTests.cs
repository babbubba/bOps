// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json;
using bOps.Abstractions;
using bOps.Audit;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0030 section 8 (V1.2-H): the audit trail of a delegated run is complete and self-sufficient. Every event of one carries
/// the correlation block, whichever way the run ends; the whole chain (roles, agents, envelope hashes, the human decision, the
/// journal, budgets, the end) and who gathered each piece of evidence can be rebuilt from the audit log alone; envelope contents,
/// arguments and tool output stay out of it and out of telemetry; and the hash chain still verifies across old and new events.
/// Fixtures are shared with <see cref="DelegationRunnerTests"/>.
/// </summary>
public sealed partial class DelegationRunnerTests
{
    private static readonly JsonSerializerOptions AuditJson = new() { WriteIndented = false };

    private static string Serialized(AuditEvent evt) => JsonSerializer.Serialize(evt, AuditJson);

    // ---- every event carries the block ----

    [Theory]
    [InlineData("completed")]
    [InlineData("denied")]
    [InlineData("plan-rejected")]
    [InlineData("step-rejected")]
    [InlineData("verification-failed")]
    [InlineData("capability-failed")]
    [InlineData("budget-exceeded")]
    [InlineData("deadline-exceeded")]
    [InlineData("cancelled")]
    public async Task Start_EveryAuditEventOfADelegatedRun_CarriesTheCorrelationOfThatRun_WhicheverWayItEnds(string scenario)
    {
        using var cts = new CancellationTokenSource();
        var clock = new FakeTimeProvider(Start);
        Harness h = scenario switch
        {
            "denied" => Create(profiles: ProfilesWithout(AgentRoleKind.Remediation)),
            "plan-rejected" => Create(approval: new RecordingPlanApproval(_ => new ApprovalDecision(false, Approver, "no"))),
            "step-rejected" => Create(policy: new SplitPolicy(PolicyMode.Approval), stepApproval: new StubApprovalProvider(approved: false)),
            "verification-failed" => Create(restart: new RestartTool(verdict: VerificationStatus.Refuted)),
            "capability-failed" => Create(capabilityThrows: true),
            "budget-exceeded" => Create(profiles: ProfilesWith(WithBudget(AgentRoleKind.Discovery, steps: 1)), script: [PlanningTestSupport.PlanResponse(), Call("host.info"), Call("host.info"), Final()]),
            "deadline-exceeded" => Create(clock: clock, model: new ActingModel(new FakeChatModel(HappyScript()), onCall: 1, () => clock.Advance(TimeSpan.FromHours(3)))),
            "cancelled" => Create(restart: new RestartTool(whileRunning: async ct =>
            {
                await cts.CancelAsync();
                await Task.Delay(Forever, ct);
            })),
            _ => Create(),
        };

        var run = await h.Runner.StartAsync(Request(), Operator, ct: cts.Token);

        Assert.NotEmpty(h.Audit.Events);
        Assert.All(h.Audit.Events, e =>
        {
            Assert.NotNull(e.Delegation);
            Assert.Equal(run.Id, e.Delegation.DelegationId);
        });
        Assert.Equal(run.Status, h.Audit.Events.OfType<DelegationLifecycleAuditEvent>().Single(e => e.Stage == DelegationStage.Terminal).Status);
    }

    [Fact]
    public async Task Resume_AndReconcile_AuditTheSameRunWithTheSameCorrelation()
    {
        var store = new CrashableStore { CrashBefore = OutcomeWritten };
        var restart = new RestartTool();
        var first = Create(store: store, restart: restart);
        var crashed = await first.Runner.StartAsync(Request(), Operator);
        store.Revive();
        restart.Verdict = VerificationStatus.Refuted;
        var second = Create([], store: store, restart: restart);
        await second.Runner.ResumeAsync(crashed.Id, Approver);
        await second.Runner.ReconcileAsync(crashed.Id, ReconciliationAction.OperatorAbandoned, Administrator, "not worth pursuing");

        var events = first.Audit.Events.Concat(second.Audit.Events).ToList();

        Assert.Contains(events, e => e is DelegationReconciliationAuditEvent { Action: ReconciliationAction.EscalatedToOperator });
        Assert.Contains(events, e => e is DelegationReconciliationAuditEvent { Action: ReconciliationAction.OperatorAbandoned });
        Assert.Contains(events, e => e is DelegationLifecycleAuditEvent { Stage: DelegationStage.Resumed });
        Assert.All(events, e => Assert.Equal(crashed.Id, e.Delegation!.DelegationId));
    }

    [Fact]
    public async Task RunAsync_ForARunThatIsNotDelegated_WritesNoCorrelationBlock_AndNoDelegationEvent()
    {
        var h = Create([PlanningTestSupport.PlanResponse(), Final("Nothing to do.")]);

        await h.Agent.RunAsync("Say hello.", Operator);

        Assert.NotEmpty(h.Audit.Events);
        Assert.All(h.Audit.Events, e =>
        {
            Assert.Null(e.Delegation);
            Assert.DoesNotContain("elegation", Serialized(e), StringComparison.Ordinal);
        });
    }

    // ---- the chain can be rebuilt from the log ----

    private sealed record RebuiltRole(AgentIdentity Agent, string EnvelopeHash, string ParentHash, DelegationRoleStatus Status, BudgetConsumption Consumed, IReadOnlyList<string> EvidenceIds);

    /// <summary>Everything a reader of the audit log alone can say about a delegated run.</summary>
    private sealed record RebuiltRun(
        Guid Id, ActorIdentity Actor, string RootHash, IReadOnlyList<RebuiltRole> Roles, string? PlanHash, ActorIdentity? Approver,
        IReadOnlyList<(int Step, JournalPhase Phase, StepOutcomeKind? Outcome)> Journal, DelegationStatus Status);

    private static RebuiltRun Rebuild(IReadOnlyList<AuditEvent> events, Guid runId)
    {
        var lifecycle = events.OfType<DelegationLifecycleAuditEvent>().Where(e => e.TaskId == runId).ToList();
        var requested = Assert.Single(lifecycle, e => e.Stage == DelegationStage.Requested);
        var roles = new List<RebuiltRole>();
        foreach (var started in lifecycle.Where(e => e.Stage == DelegationStage.RoleStarted))
        {
            var agent = started.Delegation!.Agent!;
            var granted = Assert.Single(
                events.OfType<DelegationEnvelopeAuditEvent>(), e => e.Delegation!.Agent is { } a && a.Id == agent.Id);
            var completed = Assert.Single(lifecycle, e => e.Stage == DelegationStage.RoleCompleted && e.Delegation!.Agent!.Id == agent.Id);
            roles.Add(new RebuiltRole(
                agent, granted.Delegation!.EnvelopeHash, granted.ParentEnvelopeHash, completed.RoleStatus!.Value,
                completed.Consumed!, completed.EvidenceIds ?? []));
        }

        var decided = lifecycle.SingleOrDefault(e => e.Stage == DelegationStage.PlanDecided);
        return new RebuiltRun(
            runId, requested.Actor, requested.Delegation!.EnvelopeHash, roles, decided?.PlanHash,
            decided is { Status: DelegationStatus.Running } ? decided.Actor : null,
            [.. events.OfType<DelegationJournalAuditEvent>().Select(e => (e.StepIndex, e.Phase, e.Outcome))],
            lifecycle.Single(e => e.Stage == DelegationStage.Terminal).Status);
    }

    [Fact]
    public async Task Start_TheWholeChainCanBeRebuiltFromTheAuditLogAlone()
    {
        var h = Create(store: new CrashableStore());

        var run = await h.Runner.StartAsync(Request(), Operator);
        var rebuilt = Rebuild(h.Audit.Events, run.Id);

        Assert.Equal(run.Actor, rebuilt.Actor);
        Assert.Equal(DelegationHasher.ComputeEnvelopeHash(run.RootEnvelope), rebuilt.RootHash);
        Assert.Equal(run.Status, rebuilt.Status);
        Assert.Equal(run.PlanHash, rebuilt.PlanHash);
        Assert.Equal(run.Approval!.Approver, rebuilt.Approver);
        Assert.Equal(run.Roles.Count, rebuilt.Roles.Count);
        for (var i = 0; i < run.Roles.Count; i++)
        {
            Assert.Equal(run.Roles[i].Agent, rebuilt.Roles[i].Agent);
            Assert.Equal(DelegationHasher.ComputeEnvelopeHash(run.Roles[i].Envelope), rebuilt.Roles[i].EnvelopeHash);
            Assert.Equal(rebuilt.RootHash, rebuilt.Roles[i].ParentHash);
            Assert.Equal(run.Roles[i].Status, rebuilt.Roles[i].Status);
            Assert.Equal(run.Roles[i].Consumed, rebuilt.Roles[i].Consumed);
        }

        Assert.Equal(
            [(0, JournalPhase.Intent, (StepOutcomeKind?)null), (0, JournalPhase.Outcome, StepOutcomeKind.Succeeded)],
            rebuilt.Journal);
    }

    [Fact]
    public async Task Start_WhoGatheredEachPieceOfEvidence_CanBeReadFromTheAuditLog()
    {
        var h = Create(store: new CrashableStore());

        var run = await h.Runner.StartAsync(Request(), Operator);
        var rebuilt = Rebuild(h.Audit.Events, run.Id);

        var fromTheLog = rebuilt.Roles.SelectMany(role => role.EvidenceIds.Select(id => $"{id}|{role.Agent.Id}|{role.Agent.Role}")).Order().ToList();
        var stored = run.Roles
            .SelectMany(role => (role.Report?.Evidence ?? role.Verification?.Evidence ?? []).Where(e => e.Provenance?.AgentId == role.Agent.Id))
            .Select(e => $"{e.Id}|{e.Provenance!.AgentId}|{e.Provenance.Role}")
            .Order()
            .ToList();

        Assert.NotEmpty(stored);
        Assert.Equal(stored, fromTheLog);

        // What Discovery read, what the Capability found and what Verification read come from three different agents.
        Assert.True(rebuilt.Roles.Count(role => role.EvidenceIds.Count > 0) >= 3);
    }

    // ---- what the log and telemetry do not hold ----

    [Fact]
    public async Task Start_EnvelopeContentsAreAuditedAsAHashAndReducedDimensionsOnly()
    {
        var h = Create();

        var run = await h.Runner.StartAsync(Request(), Operator);

        var envelopeEvents = h.Audit.Events.OfType<DelegationEnvelopeAuditEvent>().ToList();
        Assert.Equal(4, envelopeEvents.Count);
        foreach (var evt in envelopeEvents)
        {
            var json = Serialized(evt);
            foreach (var member in new[] { "AllowedTools", "AllowedSkills", "AllowedCapabilities", "AllowedTargets", "AllowedEnvironments", "MaxRisk", "Budget", "Window" })
            {
                Assert.DoesNotContain(member, json, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.All(envelopeEvents, e => Assert.Equal(DelegationHasher.ComputeEnvelopeHash(run.RootEnvelope), e.ParentEnvelopeHash));
        Assert.Contains(
            envelopeEvents, e => e.Delegation!.EnvelopeHash == DelegationHasher.ComputeEnvelopeHash(Role(run, e.Role).Envelope));
    }

    [Fact]
    public async Task Start_NoToolOutputOrArgumentReachesADelegationEventOrTelemetry()
    {
        var secret = "s3cr3t-" + Guid.NewGuid().ToString("N");
        var tags = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BOpsTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (tags)
                {
                    tags.Add(activity.DisplayName);
                    tags.AddRange(activity.TagObjects.Select(t => $"{t.Key}={t.Value}"));
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        var h = Create(
            restart: new RestartTool(parameters: [new ToolParameter("token", ToolParameterType.String, "A secret.", Sensitive: true)]),
            discoveryOutput: secret,
            verifyRead: new FakeReadTool("test.read", secret),
            plan: () => new ExecutionPlan(
                "sample.remediate", "1.0.0", "Restart with a token.",
                [new ExecutionPlanStep(0, "service.restart", ToolArguments.FromJson(new System.Text.Json.Nodes.JsonObject { ["token"] = secret }), "Restart.")]),
            store: new CrashableStore());

        var run = await h.Runner.StartAsync(Request(), Operator);

        Assert.Equal(DelegationStatus.Completed, run.Status);
        foreach (var evt in h.Audit.Events.Where(e => e is DelegationLifecycleAuditEvent or DelegationEnvelopeAuditEvent or DelegationJournalAuditEvent or DelegationReconciliationAuditEvent))
        {
            Assert.DoesNotContain(secret, Serialized(evt), StringComparison.Ordinal);
        }

        var journal = h.Audit.Events.OfType<DelegationJournalAuditEvent>().First();
        Assert.Equal(DelegationHasher.ComputeArgumentsHash(ToolArguments.FromJson(new System.Text.Json.Nodes.JsonObject { ["token"] = secret })), journal.ArgumentsHash);
        lock (tags)
        {
            Assert.NotEmpty(tags);
            Assert.DoesNotContain(tags, tag => tag.Contains(secret, StringComparison.Ordinal));
        }
    }

    // ---- the hash chain across old and new events ----

    [Fact]
    public async Task Start_TheAuditFileOfADelegatedRun_VerifiesAsAChain_AndABrokenLinkIsFound()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bops-audit-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var file = new JsonLinesAuditSink(path);
            var h = Create(sink: file, store: new CrashableStore());

            // An event of a run that is not delegated first, so old and new events share one chain.
            await Create(sink: file, script: [PlanningTestSupport.PlanResponse(), Final("Nothing to do.")]).Agent.RunAsync("Say hello.", Operator);
            var run = await h.Runner.StartAsync(Request(), Operator);

            Assert.Equal(DelegationStatus.Completed, run.Status);
            Assert.True(AuditChainVerifier.VerifyFile(path).IsValid);
            var lines = await File.ReadAllLinesAsync(path);
            Assert.Contains(lines, line => line.Contains("delegationLifecycle", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("delegationJournal", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("toolCall", StringComparison.Ordinal));

            // One delegation event altered after the fact: the chain no longer verifies, and says where.
            var index = Array.FindIndex(lines, line => line.Contains("delegationJournal", StringComparison.Ordinal));
            lines[index] = lines[index].Replace("delegationJournal", "delegationJournaI", StringComparison.Ordinal);
            await File.WriteAllLinesAsync(path, lines);
            var broken = AuditChainVerifier.VerifyFile(path);

            Assert.False(broken.IsValid);
            Assert.Equal(index, broken.BrokenAtSequence);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Dictionary<AgentRoleKind, RoleProfile> ProfilesWithout(AgentRoleKind role)
    {
        var profiles = AllProfiles();
        profiles.Remove(role);
        return profiles;
    }
}
