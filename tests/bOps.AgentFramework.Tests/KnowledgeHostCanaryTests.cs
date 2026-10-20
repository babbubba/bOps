// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

/// <summary>HOST-CANARY-01A: a small, explicitly activated host composition over a local signed pack and the governed model path.</summary>
public sealed class KnowledgeHostCanaryTests
{
    private const string Goal = "read the nginx runbook";

    [Fact]
    public async Task AnExplicitlyActivatedAuthorizedCanary_ServesThePinnedLocalPackContent()
    {
        using var canary = KnowledgeHostCanary.Create(KnowledgeCanaryActivation.Authorized(KnowledgeRunnerWorld.TenantA));
        var model = new ScriptedRunnerModel([
            Script.Plan("test.read"),
            Script.Knowledge("k1", "load", "nginx-triage"),
            Script.Knowledge("k2", "read_resource", "nginx-triage", "references/checklist.md"),
            Script.Tool("c1", "test.read"),
            Script.Final("done"),
        ]);
        var rig = canary.Compose(model);

        var state = await rig.Runner.RunAsync(Goal, KnowledgeRunnerWorld.ActorA);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(1, rig.Read.ExecutionCount);
        Assert.Contains(model.Requests[^2].History, turn =>
            turn.Provenance?.Origin == ContextOrigin.Skill &&
            turn.Content?.Contains("ss -ltnp :80", StringComparison.Ordinal) == true);
        Assert.Equal("tenant-a", state.KnowledgeBinding!.TenantId);
        Assert.Contains(canary.AuditEvents.OfType<KnowledgeReadAuditEvent>(), read => read.ResultCode == KnowledgeReadResultCode.Success);
    }

    [Fact]
    public async Task AnExplicitCanaryWithoutTheTenantGrant_RefusesThePackBeforeModelOrToolExecution()
    {
        using var canary = KnowledgeHostCanary.Create(KnowledgeCanaryActivation.Unauthorized(KnowledgeRunnerWorld.TenantA));
        var model = new ScriptedRunnerModel([]);
        var rig = canary.Compose(model);

        var state = await rig.Runner.RunAsync(Goal, KnowledgeRunnerWorld.ActorA);

        Assert.Equal(AgentTaskStatus.Failed, state.Status);
        Assert.Equal(TaskTerminalKind.KnowledgeUnavailable, state.TerminalReason!.Kind);
        Assert.Empty(model.Requests);
        Assert.Equal(0, rig.Read.ExecutionCount);
        Assert.Equal(0, canary.SourceOpens);
        Assert.Contains(canary.AuditEvents.OfType<KnowledgePackAdmissionAuditEvent>(), admission =>
            admission.Code == KnowledgeAdmissionCode.NotEntitled && admission.TenantId == "tenant-a");
    }

    [Fact]
    public async Task CanaryKnowledgeTurns_OnlyContinueThroughTheExistingGovernedBroker()
    {
        using var canary = KnowledgeHostCanary.Create(KnowledgeCanaryActivation.Authorized(KnowledgeRunnerWorld.TenantA));
        var model = new ScriptedRunnerModel([
            Script.Plan("test.read"), Script.Knowledge("k1", "discover"), Script.Tool("c1", "test.read"), Script.Final("done"),
        ]);
        var rig = canary.Compose(model);

        var state = await rig.Runner.RunAsync(Goal, KnowledgeRunnerWorld.ActorA);

        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.Equal(model.Requests.Count, rig.Maf.ComposedTurns);
        Assert.Equal(model.Requests.Count, canary.AuditEvents.OfType<ModelCallAuditEvent>().Count());
        Assert.Equal(1, rig.Read.ExecutionCount);
        Assert.Equal(0, rig.Other.ExecutionCount);
        Assert.DoesNotContain(canary.AuditEvents.OfType<ModelCallAuditEvent>(), call => call.ContextGateRejection is not null);
    }
}
