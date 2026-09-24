// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// What an operator needs from the runtime to drive a delegation from a surface (V1.2-I, ADR-0030 section 9): the plan a human
/// is asked about shows the authority the change will run under, and a run that no process is executing can be cancelled by a
/// human, audited, without repeating or losing what it already did. Fixtures are shared with <see cref="DelegationRunnerTests"/>.
/// </summary>
public sealed partial class DelegationRunnerTests
{
    // ---- the authority a human approves ----

    [Fact]
    public async Task Start_ShowsTheHumanTheAuthorityTheChangeWillRunUnder()
    {
        var h = Create();

        var run = await h.Runner.StartAsync(Request(), Operator);

        var shown = Assert.Single(h.Approval.Requests).Authority;
        var granted = Role(run, AgentRoleKind.Remediation).Envelope;
        Assert.NotNull(shown);
        Assert.Equal(granted.AllowedTools, shown.AllowedTools);
        Assert.Equal(granted.AllowedSkills, shown.AllowedSkills);
        Assert.Equal(granted.AllowedCapabilities, shown.AllowedCapabilities);
        Assert.Equal(granted.MaxRisk, shown.MaxRisk);
        Assert.Equal(granted.MaxBlastRadius, shown.MaxBlastRadius);
        Assert.Equal(granted.AllowedTargets, shown.AllowedTargets);
        Assert.Equal(granted.AllowedEnvironments, shown.AllowedEnvironments);
        Assert.Equal(granted.Budget.MaxSteps, shown.Budget.MaxSteps);
        Assert.Equal(["service.restart", "test.read"], shown.AllowedTools);
    }

    // ---- a stored run that contradicts itself ----

    [Fact]
    public async Task Resume_RefusesAStoredRunWhoseJournalHasNoStoredPlanToBelongTo_AndRunsNothing()
    {
        // Steps are journaled only after the plan they belong to was stored. A run that has the first without the second was
        // not written by this runtime, and starting its diagnosis again could put a different plan over a step already taken.
        var store = new CrashableStore { CrashBefore = OutcomeWritten };
        var restart = new RestartTool();
        var crashed = await Create(store: store, restart: restart).Runner.StartAsync(Request(), Operator);
        store.Revive();
        var stored = (await store.LoadAsync(crashed.Id))!;
        await store.SaveAsync(stored with { Roles = [] });
        var second = Create(store: store, restart: restart);

        var run = await second.Runner.ResumeAsync(crashed.Id, Approver);

        Assert.Equal(DelegationStatus.Failed, run.Status);
        Assert.Contains("no stored plan", run.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(second.Model.Requests);
        Assert.Empty(second.Approval.Requests);
        Assert.Equal(1, restart.ExecutionCount);
    }

    // ---- cancelling a run no process is executing ----

    [Fact]
    public async Task Cancel_EndsAStoredRunThatWasLeftRunning_AndAuditsWhoDidIt()
    {
        var store = new CrashableStore { CrashAfter = r => RoleRunning(r, AgentRoleKind.Discovery) };
        var crashed = await Create(store: store).Runner.StartAsync(Request(), Operator);
        store.Revive();
        var h = Create([], store: store);

        var run = await h.Runner.CancelAsync(crashed.Id, Approver);

        Assert.Equal(DelegationStatus.Cancelled, run.Status);
        Assert.Equal(DelegationStatus.Cancelled, (await store.LoadAsync(crashed.Id))!.Status);
        Assert.Equal(DelegationRoleStatus.Cancelled, Assert.Single(run.Roles).Status);
        var audited = Assert.Single(h.Audit.Events.OfType<DelegationLifecycleAuditEvent>());
        Assert.Equal(DelegationStage.Terminal, audited.Stage);
        Assert.Equal(DelegationStatus.Cancelled, audited.Status);
        Assert.Equal(Approver, audited.Actor);
        Assert.Equal(crashed.Id, audited.Delegation!.DelegationId);

        // Cancelled is an end: resuming it is not a retry.
        var again = await Create([], store: store).Runner.ResumeAsync(crashed.Id, Approver);
        Assert.Equal(DelegationStatus.Cancelled, again.Status);
    }

    [Fact]
    public async Task Cancel_SaysWhenAStepMayOrMayNotHaveTakenEffect_AndNeverRunsItAgain()
    {
        var store = new CrashableStore { CrashBefore = OutcomeWritten };
        var restart = new RestartTool();
        var crashed = await Create(store: store, restart: restart).Runner.StartAsync(Request(), Operator);
        store.Revive();

        var run = await Create([], store: store, restart: restart).Runner.CancelAsync(crashed.Id, Approver);

        Assert.Equal(DelegationStatus.Cancelled, run.Status);
        Assert.Contains("may or may not have taken effect", run.ErrorMessage, StringComparison.Ordinal);
        Assert.Single(run.Journal);
        Assert.Equal(1, restart.ExecutionCount);
    }

    [Fact]
    public async Task Cancel_LeavesARunThatAlreadyEndedAsItIs()
    {
        var store = new CrashableStore();
        var done = await Create(store: store).Runner.StartAsync(Request(), Operator);
        var h = Create([], store: store);

        var run = await h.Runner.CancelAsync(done.Id, Approver);

        Assert.Equal(DelegationStatus.Completed, run.Status);
        Assert.Empty(h.Audit.Events);
    }

    [Fact]
    public async Task Cancel_PointsAtReconcileForARunWaitingForAnOperator()
    {
        var (store, restart, id) = await WaitingForAnOperatorAsync();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Create([], store: store, restart: restart).Runner.CancelAsync(id, Approver));

        Assert.Contains("reconcil", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DelegationStatus.RequiresReconciliation, (await store.LoadAsync(id))!.Status);
    }

    [Fact]
    public async Task Cancel_RefusesAnAgentAndTheRuntime_AndNeedsAStoredRun()
    {
        var store = new CrashableStore { CrashAfter = r => RoleRunning(r, AgentRoleKind.Discovery) };
        var crashed = await Create(store: store).Runner.StartAsync(Request(), Operator);
        store.Revive();
        var h = Create([], store: store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.CancelAsync(crashed.Id, new ActorIdentity("agent", "diagnostic-1", null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.CancelAsync(crashed.Id, ActorIdentity.RuntimeSystem));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.CancelAsync(Guid.NewGuid(), Approver));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().Runner.CancelAsync(crashed.Id, Approver));
        Assert.Equal(DelegationStatus.Running, (await store.LoadAsync(crashed.Id))!.Status);
    }
}
