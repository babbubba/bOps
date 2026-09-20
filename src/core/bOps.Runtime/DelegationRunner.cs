// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Logging;

namespace bOps.Runtime;

/// <summary>
/// The deterministic orchestrator of a delegated objective (ADR-0030 sections 2 to 5): Discovery, Diagnostic, a human
/// approval of the plan's hash, Remediation, Verification, in that order and no other. It is runtime code, not a model:
/// the order of the roles, the approval and every terminal state are decided here, a model reasons only inside
/// Discovery and Diagnostic, and what one role passes to the next is only structured Evidence, Finding and
/// ExecutionPlan values delivered as delimited data (rule S5).
/// </summary>
/// <remarks>
/// <para>
/// It adds no way to execute anything. Every role acts through <see cref="AgentRunner"/>'s delegated entry points, so
/// each tool call goes through the one authorization path, with the role's envelope checked before policy, and every
/// event carries the run and the agent that acted. Roles cannot delegate further, so depth is 1 by construction, and
/// each role is a new agent with its own identity.
/// </para>
/// <para>
/// Authority is reduce-only and human approval is human only. The root envelope and each role's envelope come from
/// the C1 reduction over the operator's request and the role profiles; a plan is put to a human by its hash and runs
/// only if that human, not an agent and not the runtime, approves exactly it; Remediation makes no model call, so no
/// model can change a plan after approval; and Verification reads the system itself rather than trusting
/// Remediation's result, so no role approves or verifies its own work.
/// </para>
/// <para>
/// Budgets, deadlines and cancellation hold across the roles (V1.2-E, ADR-0030 section 6). Each role's budget is reserved
/// from what is left of the run's, its steps and tokens are counted while it runs and reconciled when it ends, and it is
/// stopped at its own deadline. One cancellation token tree covers the run (the caller's token and the run's deadline),
/// each role (its own deadline) and each step (the tool's timeout), and exhaustion, deadline expiry and cancellation are
/// distinct terminal states, each audited.
/// </para>
/// <para>
/// With an <see cref="IDelegationStore"/> a run is durable (V1.2-F, ADR-0030 section 7): it is saved at every transition, the
/// intent of each side-effecting step is committed before the step runs and its outcome after, and a run a crash left
/// <see cref="DelegationStatus.Running"/> can be resumed without repeating a completed side effect. Without one a run lives in
/// memory, as before.
/// </para>
/// <para>
/// No role approves or verifies its own work (V1.2-G, ADR-0030 section 5). Approvals are human only, at the plan and at every
/// step: a decision made by an agent, by the runtime or in the name of one of the run's agents is refused and audited as a
/// refusal. Every role has its own agent identity, and a run in which two share one is invalid, whether it is being run or
/// read back from a store. Verification is given only the approved plan and reads the system through its own envelope.
/// </para>
/// </remarks>
public sealed class DelegationRunner(
    AgentRunner runner,
    IRoleProfileSource profiles,
    IPlanApprovalProvider planApproval,
    IAuditSink audit,
    TimeProvider timeProvider,
    ILogger<DelegationRunner> logger,
    IDelegationStore? store = null,
    int maximumResumes = DelegationRunner.DefaultMaximumResumes)
{
    /// <summary>How many times a run may be resumed before it is ended as failed, so a run that crashes every time cannot loop forever (ADR-0030 section 6).</summary>
    public const int DefaultMaximumResumes = 3;

    private const int MaximumMessageLength = 500;

    /// <summary>Where the agent of each role comes from. The runtime is the only source of an agent id (ADR-0030 section 1); a test replaces it to make two roles collide.</summary>
    internal Func<AgentId> AgentIds { get; init; } = AgentId.New;

    // CancellationTokenSource takes a delay of at most about 49 days; a deadline further off than this is left to the clock checks.
    private static readonly TimeSpan MaximumTimer = TimeSpan.FromDays(30);

    /// <summary>Runs one objective through the fixed pipeline and returns how it ended.</summary>
    /// <param name="request">The objective, an optional narrowing of authority, and the change to prepare if any.</param>
    /// <param name="actor">The operator on whose authority the objective runs. Every envelope of the run is granted to them.</param>
    /// <param name="delegationId">The id to give the run; a fresh one when omitted. For a caller that must hand the id out before the run ends.</param>
    /// <param name="idempotencyKey">
    /// A caller-supplied key that makes starting the same objective again safe: with a store, a second start by the same
    /// operator with the same key returns the run the first one created and starts nothing. Ignored without a store.
    /// </param>
    /// <param name="ct">
    /// Cancelled to abandon the run. A run that is already under way ends as <see cref="DelegationStatus.Cancelled"/> and is
    /// returned, audited like every other end; a token that is cancelled before the run exists throws, because no run does.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or <paramref name="actor"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="delegationId"/> is empty.</exception>
    public async Task<DelegationRun> StartAsync(
        DelegationRequest request, ActorIdentity actor, Guid? delegationId = null, string? idempotencyKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        if (delegationId == Guid.Empty)
        {
            throw new ArgumentException("A delegation must have a non-empty id.", nameof(delegationId));
        }

        ct.ThrowIfCancellationRequested();
        using var state = new RunState(delegationId ?? Guid.NewGuid(), request, actor, timeProvider.GetUtcNow()) { IdempotencyKey = idempotencyKey };

        try
        {
            await RunPipelineAsync(state, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the caller, or stopped by the run's or the role's deadline: a terminal state either way.
            await EndInterruptedAsync(state, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Rule C1: nothing thrown escapes a run. A failure in the orchestrator itself is a terminal state, never a crash.
            logger.LogError(ex, "Delegation {DelegationId}: the orchestrator failed", state.Id);
            await FailActiveRoleAsync(state, ex.Message);
            state.End(DelegationStatus.Failed, Bounded(ex.Message));
        }

        // A start that an earlier one with the same key already made returns that run and has nothing to end.
        return state.Existing ?? await FinishAsync(state);
    }

    /// <summary>
    /// Continues a run that a crash or a restart left <see cref="DelegationStatus.Running"/> (ADR-0030 section 7), without
    /// repeating a side effect. It carries on from what the store holds: a completed role is not run again, an interrupted
    /// read-only role restarts from its beginning against what the run has left, and a step the journal shows as done is
    /// never executed again. A step whose outcome is not known is settled by its own declared verification; one it cannot
    /// confirm ends the run as <see cref="DelegationStatus.RequiresReconciliation"/> and waits for an operator. Approvals are
    /// never persisted, so a plan that has steps left to run is put to a human again, by the same hash.
    /// </summary>
    /// <param name="delegationId">The run to resume.</param>
    /// <param name="resumedBy">Who resumed it, recorded in the audit log. The authority stays the one the run was granted.</param>
    /// <param name="ct">Cancelled to abandon the resumed run, which then ends as <see cref="DelegationStatus.Cancelled"/>.</param>
    /// <returns>The run as it stands after this resume. A run that is not running, because it ended or waits for an operator, is returned unchanged.</returns>
    /// <exception cref="InvalidOperationException">The runner has no store, or no run with this id is stored.</exception>
    public async Task<DelegationRun> ResumeAsync(Guid delegationId, ActorIdentity resumedBy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resumedBy);

        var stored = await RequireStore().LoadAsync(delegationId, CancellationToken.None)
            ?? throw new InvalidOperationException($"No delegation run {delegationId} is stored.");
        if (stored.Status != DelegationStatus.Running)
        {
            return stored;
        }

        ct.ThrowIfCancellationRequested();
        using var state = RunState.FromStored(stored);

        try
        {
            if (SeparationOfDuties.SharedIdentity(stored.Roles.Select(role => role.Agent)) is { } shared)
            {
                // A stored run that fails the rule is not trusted to have been independently checked, so it is not continued.
                state.End(DelegationStatus.Failed, $"{shared} The stored run is not resumed.");
            }
            else if (stored.ResumeCount >= maximumResumes)
            {
                state.End(DelegationStatus.Failed, $"The run was resumed {stored.ResumeCount} times already, which is the most it may be.");
            }
            else
            {
                state.ResumeCount = stored.ResumeCount + 1;
                await WriteLifecycleAsync(state, DelegationStage.Resumed, DelegationStatus.Running, state.Orchestrator, CancellationToken.None, actor: resumedBy);
                await InterruptedRolesAsync(state);
                await PersistAsync(state);
                await ResumePipelineAsync(state, ct);
            }
        }
        catch (OperationCanceledException)
        {
            await EndInterruptedAsync(state, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Delegation {DelegationId}: resuming failed", state.Id);
            await FailActiveRoleAsync(state, ex.Message);
            state.End(DelegationStatus.Failed, Bounded(ex.Message));
        }

        return await FinishAsync(state);
    }

    /// <summary>
    /// An operator's decision on a run that <see cref="DelegationStatus.RequiresReconciliation"/> (ADR-0030 section 7): accept the
    /// steps whose outcome is unknown as done, after which the run can be resumed, or abandon the run. Audited. There is no
    /// automatic retry: what an abandoned run was meant to do is a new objective with a new plan and a new approval.
    /// </summary>
    /// <param name="delegationId">The run.</param>
    /// <param name="action"><see cref="ReconciliationAction.OperatorAcceptedDone"/> or <see cref="ReconciliationAction.OperatorAbandoned"/>.</param>
    /// <param name="administrator">The human deciding. Only a human identity is accepted; that they hold the administrator role is for the surface that authenticates them (V1.2-I and V1.2-J).</param>
    /// <param name="note">An optional note, kept in the audit log.</param>
    /// <param name="ct">Cancelled to abandon the call before anything is written.</param>
    /// <exception cref="InvalidOperationException">The runner has no store, no such run is stored, the run is not waiting for reconciliation, or the identity is not a human.</exception>
    /// <exception cref="ArgumentException"><paramref name="action"/> is not an operator action.</exception>
    public async Task<DelegationRun> ReconcileAsync(
        Guid delegationId, ReconciliationAction action, ActorIdentity administrator, string? note = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        if (action is not (ReconciliationAction.OperatorAcceptedDone or ReconciliationAction.OperatorAbandoned))
        {
            throw new ArgumentException("An operator can only accept a step as done or abandon the run.", nameof(action));
        }

        var runStore = RequireStore();
        var stored = await runStore.LoadAsync(delegationId, ct)
            ?? throw new InvalidOperationException($"No delegation run {delegationId} is stored.");
        if (stored.Status != DelegationStatus.RequiresReconciliation)
        {
            throw new InvalidOperationException($"Run {delegationId} is {stored.Status}, not waiting for reconciliation.");
        }

        if (!IsHuman(administrator, stored.Roles))
        {
            throw new InvalidOperationException("Reconciliation is a human decision; an agent or the runtime cannot make it.");
        }

        var now = timeProvider.GetUtcNow();
        var correlation = new DelegationCorrelation(stored.Id, DelegationHasher.ComputeEnvelopeHash(stored.RootEnvelope), null);
        DelegationRun updated;

        if (action == ReconciliationAction.OperatorAbandoned)
        {
            updated = stored with { Status = DelegationStatus.Abandoned, ErrorMessage = Bounded(note ?? "An operator abandoned the run."), UpdatedAtUtc = now };
            await WriteReconciliationAsync(stored.Id, -1, action, administrator, null, note, correlation);
        }
        else
        {
            var journal = new List<StepJournalEntry>();
            foreach (var entry in stored.Journal)
            {
                if (IsUnsettled(entry))
                {
                    journal.Add(entry with { Reconciliation = new StepReconciliation(action, entry.Reconciliation?.Verification, administrator, now) });
                    await WriteReconciliationAsync(stored.Id, entry.StepIndex, action, administrator, entry.Reconciliation?.Verification, note, correlation);
                }
                else
                {
                    journal.Add(entry);
                }
            }

            updated = stored with { Status = DelegationStatus.Running, Journal = journal, ErrorMessage = null, UpdatedAtUtc = now };
        }

        await runStore.SaveAsync(updated, CancellationToken.None);
        await audit.WriteAsync(
            new DelegationLifecycleAuditEvent
            {
                TimestampUtc = now,
                Node = NodeId.Local,
                TaskId = stored.Id,
                StepIndex = -1,
                Actor = administrator,
                Stage = DelegationStage.Terminal,
                Status = updated.Status,
                ErrorMessage = updated.ErrorMessage,
                Delegation = correlation,
            },
            CancellationToken.None);
        return updated;
    }

    // ---- the pipeline ----

    private async Task RunPipelineAsync(RunState state, CancellationToken ct)
    {
        var (reduction, deniedRole) = EnvelopeReducer.DeriveRootAttributed(profiles, state.Request.Authority, state.Actor, state.CreatedAt);
        if (reduction.IsDenied)
        {
            // No root exists, so the authority in force for the refusal is nothing at all.
            state.SetRoot(NoAuthority(state));
            if (!await RegisterAsync(state))
            {
                return;
            }

            await WriteLifecycleAsync(state, DelegationStage.Requested, DelegationStatus.Running, state.Orchestrator, ct);
            await WriteDenialAsync(state, deniedRole ?? RoleRequirements.Pipeline[0], reduction.Denial!, ct);
            state.End(DelegationStatus.Denied, reduction.Denial!.Reason, reduction.Denial);
            return;
        }

        state.SetRoot(reduction.Envelope!);
        if (!await RegisterAsync(state))
        {
            return;
        }

        // From here the run's token is the caller's and the run's deadline, whichever comes first.
        ct = state.BindDeadline(timeProvider, ct);
        await WriteLifecycleAsync(state, DelegationStage.Requested, DelegationStatus.Running, state.Orchestrator, ct);

        if (state.Request.Remediation is { } remediation && await PrepareCheckAsync(state, remediation, ct) is { } refused)
        {
            state.End(DelegationStatus.Denied, refused.Reason, refused);
            return;
        }

        await RunFromDiscoveryAsync(state, ct);
    }

    private async Task RunFromDiscoveryAsync(RunState state, CancellationToken ct)
    {
        // Discovery: gathers evidence with Read tools. Read-only, and the first of the two roles that use the model.
        if (await BeginRoleAsync(state, AgentRoleKind.Discovery, ct) is not { } discovery)
        {
            return;
        }

        var discoveryTask = await runner.RunDelegatedAsync(
            DelegationRoleData.DiscoveryGoal(state.Request.Objective), state.Actor, discovery.Scope, discovery.TaskId, discovery.Token);
        if (await EndModelRoleIfNotCompletedAsync(state, discovery, discoveryTask, ct))
        {
            return;
        }

        var discoveryEvidence = DelegationRoleData.EvidenceOf(
            discoveryTask, AgentRoleKind.Discovery, discovery.Provenance(state.Id), timeProvider.GetUtcNow());
        await CompleteRoleAsync(state, discovery, DelegationRoleStatus.Completed, new SkillReport(discoveryEvidence, [], null), null, null, ct);

        await RunFromDiagnosticAsync(state, discoveryEvidence, ct);
    }

    private async Task RunFromDiagnosticAsync(RunState state, IReadOnlyList<Evidence> discoveryEvidence, CancellationToken ct)
    {
        // Diagnostic: turns that evidence into findings and, through a Capability, may prepare an immutable plan.
        if (await BeginRoleAsync(state, AgentRoleKind.Diagnostic, ct) is not { } diagnostic)
        {
            return;
        }

        var diagnosticTask = await runner.RunDelegatedAsync(
            DelegationRoleData.DiagnosticGoal(state.Request.Objective, discoveryEvidence), state.Actor, diagnostic.Scope, diagnostic.TaskId, diagnostic.Token);
        if (await EndModelRoleIfNotCompletedAsync(state, diagnostic, diagnosticTask, ct))
        {
            return;
        }

        var provenance = diagnostic.Provenance(state.Id);
        List<Evidence> evidence =
        [
            .. discoveryEvidence,
            .. DelegationRoleData.EvidenceOf(diagnosticTask, AgentRoleKind.Diagnostic, provenance, timeProvider.GetUtcNow()),
        ];
        var findings = DelegationRoleData.FindingsOf(
            DelegationRoleData.FinalText(diagnosticTask), evidence.Select(e => e.Id).ToHashSet(StringComparer.Ordinal));

        PreparedSkillRun? prepared = null;
        if (state.Request.Remediation is { } toPrepare)
        {
            var preparedRun = await runner.PrepareDelegatedSkillAsync(
                diagnostic.TaskId, state.Actor, toPrepare.SkillId, toPrepare.CapabilityName, toPrepare.Request, diagnostic.Scope, diagnostic.Token);
            if (preparedRun.Status != SkillPreparationStatus.Prepared)
            {
                if (BudgetEnd(diagnostic) is (var budgetStatus, var budgetReason))
                {
                    await CompleteRoleAsync(state, diagnostic, DelegationRoleStatus.Failed, null, null, budgetReason, ct);
                    state.End(budgetStatus, budgetReason);
                    return;
                }

                var why = Bounded(preparedRun.ErrorMessage ?? "The Capability could not prepare a plan.");
                await CompleteRoleAsync(state, diagnostic, DelegationRoleStatus.Failed, null, null, why, ct);
                state.End(DelegationStatus.Failed, why);
                return;
            }

            evidence.AddRange(preparedRun.Report.Evidence.Select(e => e with { Provenance = provenance }));
            findings.AddRange(preparedRun.Report.Findings);
            prepared = Snapshot(preparedRun);
        }

        var plan = prepared?.Report.Plan;
        await CompleteRoleAsync(state, diagnostic, DelegationRoleStatus.Completed, new SkillReport(evidence, findings, plan), null, null, ct);

        // No plan, or a dry run that would execute nothing, ends as a completed diagnosis. It is a result, not an error.
        if (prepared is null || plan is null || prepared.Request.DryRun)
        {
            state.End(DelegationStatus.DiagnosisCompleted);
            return;
        }

        await RunApprovedPlanAsync(state, prepared, plan, findings, resuming: false, ct);
    }

    // ---- resuming ----

    /// <summary>Continues from the first stage whose result is not stored (ADR-0030 section 7).</summary>
    private async Task ResumePipelineAsync(RunState state, CancellationToken ct)
    {
        ct = state.BindDeadline(timeProvider, ct);

        var discovery = state.LastCompleted(AgentRoleKind.Discovery);
        if (discovery?.Report is null)
        {
            await RunFromDiscoveryAsync(state, ct);
            return;
        }

        var diagnostic = state.LastCompleted(AgentRoleKind.Diagnostic);
        if (diagnostic?.Report is null)
        {
            await RunFromDiagnosticAsync(state, discovery.Report.Evidence, ct);
            return;
        }

        // The Diagnostic role is done. What is left is decided by its stored plan and by the journal, not by a role's status.
        var plan = diagnostic.Report.Plan;
        if (state.Request.Remediation is not { } remediation || plan is null || remediation.Request.DryRun)
        {
            state.End(DelegationStatus.DiagnosisCompleted);
            return;
        }

        // The plan a human approves is the one whose hash was recorded; a stored plan that no longer has it is not run.
        var hash = ExecutionPlanHasher.ComputeHash(plan);
        if (state.PlanHash is { } recorded && !string.Equals(recorded, hash, StringComparison.Ordinal))
        {
            state.End(DelegationStatus.Failed, "The stored plan no longer matches the hash that was recorded for it, so it is not run.");
            return;
        }

        var prepared = new PreparedSkillRun(
            Guid.NewGuid(), remediation.SkillId, remediation.CapabilityName, remediation.Request,
            SkillPreparationStatus.Prepared, new SkillReport([], [], plan), hash, null);
        await RunApprovedPlanAsync(state, prepared, plan, diagnostic.Report.Findings, resuming: true, ct);
    }

    /// <summary>
    /// A role the crash interrupted did not finish, and what it spent before it stopped is not known. It is closed as failed
    /// and charged everything it was granted, so restarting it never gives its budget back (ADR-0030 section 6).
    /// </summary>
    private async Task InterruptedRolesAsync(RunState state)
    {
        foreach (var index in Enumerable.Range(0, state.Roles.Count).Where(i => state.Roles[i].Status == DelegationRoleStatus.Running))
        {
            var role = state.Roles[index];
            var charged = new BudgetConsumption(role.Envelope.Budget.MaxSteps, role.Envelope.Budget.MaxTokens);
            state.Roles[index] = role with
            {
                Status = DelegationRoleStatus.Failed,
                CompletedAtUtc = timeProvider.GetUtcNow(),
                Consumed = charged,
                ErrorMessage = "The process stopped while this role was running. It is restarted from its beginning and charged everything it was granted.",
            };
            await WriteLifecycleAsync(
                state, DelegationStage.RoleCompleted, DelegationStatus.Running,
                new DelegationCorrelation(state.Id, DelegationHasher.ComputeEnvelopeHash(role.Envelope), role.Agent), CancellationToken.None,
                roleStatus: DelegationRoleStatus.Failed, consumed: charged, error: state.Roles[index].ErrorMessage);
        }

        state.RecountSpent();
    }

    /// <summary>
    /// Settles the steps the journal cannot vouch for (ADR-0030 section 7): one with an intent and no outcome, or an outcome of
    /// cancelled or timed out. Each is checked by its own declared verification, read by a Verification role. Confirmed is done by
    /// reconciliation; refuted or inconclusive ends the run as <see cref="DelegationStatus.RequiresReconciliation"/>. There is no retry.
    /// </summary>
    /// <returns>The steps that are done and must not run again, or <c>null</c> when the run has ended.</returns>
    private async Task<HashSet<int>?> SettleJournalAsync(RunState state, ExecutionPlan plan, string hash, CancellationToken ct)
    {
        foreach (var entry in state.Journal.Where(IsUnsettled).OrderBy(e => e.StepIndex).ToList())
        {
            var planStep = plan.Steps.FirstOrDefault(step => step.Index == entry.StepIndex);
            if (planStep is null)
            {
                state.End(DelegationStatus.RequiresReconciliation, $"Step {entry.StepIndex} is in the journal but not in the plan, so it cannot be verified.");
                return null;
            }

            if (await BeginRoleAsync(state, AgentRoleKind.Verification, ct) is not { } verifier)
            {
                return null;
            }

            var single = new ExecutionPlan(plan.CapabilityName, plan.CapabilityVersion, plan.Rationale, [planStep]);
            var verified = await runner.VerifyPlanAsync(verifier.TaskId, state.Actor, single, hash, verifier.Scope, verifier.Token);
            if (BudgetEnd(verifier) is (var budgetStatus, var budgetReason))
            {
                await CompleteRoleAsync(state, verifier, DelegationRoleStatus.Failed, null, null, budgetReason, ct);
                state.End(budgetStatus, budgetReason);
                return null;
            }

            var provenance = verifier.Provenance(state.Id);
            var report = verified with { Evidence = [.. verified.Evidence.Select(e => e with { Provenance = provenance })] };
            await CompleteRoleAsync(state, verifier, DelegationRoleStatus.Completed, null, report, null, ct);

            var confirmed = report.Status == VerificationStatus.Confirmed;
            var action = confirmed ? ReconciliationAction.VerifiedDone : ReconciliationAction.EscalatedToOperator;
            state.SetReconciliation(entry.StepIndex, new StepReconciliation(action, report.Status, ActorIdentity.RuntimeSystem, timeProvider.GetUtcNow()));
            await WriteReconciliationAsync(state.Id, entry.StepIndex, action, ActorIdentity.RuntimeSystem, report.Status, null, state.Orchestrator);
            await PersistAsync(state);

            if (!confirmed)
            {
                state.End(
                    DelegationStatus.RequiresReconciliation,
                    Bounded($"Step {entry.StepIndex} ('{planStep.ToolName}') may or may not have taken effect, and its verification was {report.Status}. An operator must accept it as done or abandon the run."));
                return null;
            }
        }

        return [.. state.Journal.Where(e => IsSettledDone(e)).Select(e => e.StepIndex)];
    }

    private static bool IsUnsettled(StepJournalEntry entry) =>
        (entry.Reconciliation is null || entry.Reconciliation.Action == ReconciliationAction.EscalatedToOperator)
        && (entry.Outcome is null || entry.Outcome.Kind is StepOutcomeKind.Cancelled or StepOutcomeKind.Timeout);

    /// <summary>A step that ran and reported, or that reconciliation settled as done: it is never executed again.</summary>
    private static bool IsSettledDone(StepJournalEntry entry) =>
        entry.Outcome?.Kind is StepOutcomeKind.Succeeded or StepOutcomeKind.Failed
        || entry.Reconciliation?.Action is ReconciliationAction.VerifiedDone or ReconciliationAction.OperatorAcceptedDone;

    private async Task RunApprovedPlanAsync(
        RunState state, PreparedSkillRun prepared, ExecutionPlan plan, IReadOnlyList<Finding> findings, bool resuming, CancellationToken ct)
    {
        var hash = prepared.PlanHash!;
        state.PlanHash = hash;

        // A run that is resumed first settles what its journal cannot vouch for; a step done before the crash is not run again.
        var done = new HashSet<int>();
        if (resuming)
        {
            if (await SettleJournalAsync(state, plan, hash, ct) is not { } settled)
            {
                return;
            }

            done = settled;
        }

        var remainingSteps = plan.Steps.Count(step => !done.Contains(step.Index));
        if (remainingSteps == 0)
        {
            // Everything the plan asked for was done before the crash: nothing needs approving or running, only verifying.
            await RunVerificationAsync(state, plan, hash, ct);
            return;
        }

        // A plan the Remediation envelope is certain to refuse is rejected before a human is asked to approve it.
        if (await EnvelopeForPlanCheckAsync(state, ct) is not { } previewScope)
        {
            return;
        }

        if (runner.CheckPlanForDelegation(state.Actor, prepared, previewScope) is { } refusal)
        {
            state.End(DelegationStatus.PolicyBlocked, Bounded(refusal));
            return;
        }

        // A plan is applied whole or not at all: one the Remediation role has too few steps to finish would be left half
        // applied, so it is stopped here, before a human is asked to approve it and before anything is changed.
        if (remainingSteps > previewScope.Envelope!.Budget.MaxSteps)
        {
            state.End(
                DelegationStatus.BudgetExceeded,
                Bounded($"The approved plan has {remainingSteps} steps to run, but the Remediation role has only {previewScope.Envelope.Budget.MaxSteps} left to take."));
            return;
        }

        var remediation = state.Request.Remediation!;
        var approvalRequest = new PlanApprovalRequest(
            state.Id, hash, plan, remediation.SkillId, remediation.CapabilityName,
            remediation.Request.Target, remediation.Request.Environment, remediation.Request.BlastRadius, findings);
        var decision = await planApproval.RequestPlanApprovalAsync(approvalRequest, ct);

        // Approvals are human only: the runtime never asks on an agent's behalf, and never accepts a decision made in the
        // name of one or of the runtime itself.
        var byAHuman = IsHuman(decision.Actor, state);
        var approved = decision.Approved && byAHuman;
        await WriteLifecycleAsync(
            state, DelegationStage.PlanDecided, approved ? DelegationStatus.Running : DelegationStatus.Rejected, state.Orchestrator, ct,
            error: byAHuman ? null : "The decision was not made by a human identity and was refused.", planHash: hash, actor: decision.Actor);
        if (!approved)
        {
            state.End(
                DelegationStatus.Rejected,
                byAHuman ? Bounded(decision.Note ?? "The plan was rejected.") : "The approval did not come from a human identity and was refused.");
            return;
        }

        state.Approval = new DelegationApproval(hash, decision.Actor, timeProvider.GetUtcNow());
        await PersistAsync(state);

        // Remediation: executes exactly the approved plan through the ordinary step pipeline. It makes no model call.
        if (await BeginRoleAsync(state, AgentRoleKind.Remediation, ct) is not { } remediationRole)
        {
            return;
        }

        var execution = await runner.ExecuteDelegatedPlanAsync(
            remediationRole.TaskId, state.Actor, prepared, new ExecutionPlanApproval(hash, decision), remediationRole.Scope, done, remediationRole.Token);
        if (execution.Status != PlanExecutionStatus.Completed)
        {
            var (status, why) = ClassifyStoppedPlan(state, prepared, execution, remediationRole);
            await CompleteRoleAsync(state, remediationRole, DelegationRoleStatus.Failed, null, null, why, ct);
            state.End(status, why);
            return;
        }

        await CompleteRoleAsync(state, remediationRole, DelegationRoleStatus.Completed, null, null, null, ct);
        await RunVerificationAsync(state, plan, hash, ct);
    }

    private async Task RunVerificationAsync(RunState state, ExecutionPlan plan, string hash, CancellationToken ct)
    {
        // Verification: a distinct identity that reads the system itself. It makes no model call, and it does not look at
        // what Remediation reported.
        if (await BeginRoleAsync(state, AgentRoleKind.Verification, ct) is not { } verificationRole)
        {
            return;
        }

        var verified = await runner.VerifyPlanAsync(verificationRole.TaskId, state.Actor, plan, hash, verificationRole.Scope, verificationRole.Token);
        if (BudgetEnd(verificationRole) is (var verifiedStatus, var verifiedReason))
        {
            // A verifier that ran out of steps or time did not verify (rule S4), and says why it ended.
            await CompleteRoleAsync(state, verificationRole, DelegationRoleStatus.Failed, null, null, verifiedReason, ct);
            state.End(verifiedStatus, verifiedReason);
            return;
        }

        var provenance = verificationRole.Provenance(state.Id);
        var report = verified with { Evidence = [.. verified.Evidence.Select(e => e with { Provenance = provenance })] };
        await CompleteRoleAsync(state, verificationRole, DelegationRoleStatus.Completed, null, report, null, ct);

        // Inconclusive, refuted and not applicable are never success (rule S4).
        state.End(
            report.Status == VerificationStatus.Confirmed ? DelegationStatus.Completed : DelegationStatus.VerificationFailed,
            report.Status == VerificationStatus.Confirmed ? null : Bounded($"Verification was {report.Status}. {report.Detail}"));
    }

    // ---- envelopes and roles ----

    /// <summary>
    /// Before any role starts, and so before any model call: a named change must be one the Diagnostic role may prepare and
    /// the Remediation role may run, at that target, environment and blast radius, or delegation is refused.
    /// </summary>
    private async Task<DelegationDenial?> PrepareCheckAsync(RunState state, DelegationRemediation remediation, CancellationToken ct)
    {
        var skill = new SkillExecutionScope(
            Guid.NewGuid(), remediation.SkillId, remediation.CapabilityName,
            remediation.Request.Target, remediation.Request.Environment, remediation.Request.BlastRadius, PlanHash: null);
        var now = timeProvider.GetUtcNow();

        foreach (var role in new[] { AgentRoleKind.Diagnostic, AgentRoleKind.Remediation })
        {
            var reduction = EnvelopeReducer.ReduceForRole(state.Root, role, profiles.GetProfile(role), state.Request.Authority, now);
            if (reduction.IsDenied)
            {
                await WriteDenialAsync(state, role, reduction.Denial!, ct);
                return reduction.Denial;
            }

            var scope = DelegatedExecutionScope.For(state.Id, new AgentIdentity(AgentId.New(), role), reduction.Envelope!);
            if (EnvelopeEnforcer.CheckPreparation(scope, state.Actor, skill, now) is { } refusal)
            {
                var denial = new DelegationDenial(refusal.Dimension ?? EnvelopeDimension.Profile, refusal.Reason);
                await WriteDenialAsync(state, role, denial, ct);
                return denial;
            }
        }

        return null;
    }

    /// <summary>
    /// What is left of the run's budget for the next role (ADR-0030 section 6): the root envelope with what the roles before
    /// it spent taken off. Also says when there is nothing left for that role: no steps, or, for a role that calls the model,
    /// no tokens. A role that makes no model call needs no tokens, so an empty token budget does not stop it.
    /// </summary>
    private static (AuthorityEnvelope Parent, string? Exhausted) Reserve(RunState state, AgentRoleKind role)
    {
        var root = state.Root;
        var steps = (int)Math.Clamp(root.Budget.MaxSteps - state.SpentSteps, 0, int.MaxValue);
        var tokens = (int)Math.Clamp(root.Budget.MaxTokens - state.SpentTokens, 0, int.MaxValue);
        var parent = root with { Budget = new DelegationBudget(steps, tokens, root.Budget.DeadlineUtc) };

        if (steps < 1)
        {
            return (parent, $"The run has no steps left in its budget for the {role} role.");
        }

        if (tokens < 1 && RoleRequirements.Of(role, EnvelopeDimension.Tokens) != EnvelopeRequirement.NotApplicable)
        {
            return (parent, $"The run has no model tokens left in its budget for the {role} role.");
        }

        return (parent, null);
    }

    /// <summary>How a role's own budget ended it, when it did: the deadline, its steps or its tokens. <c>null</c> when it did not.</summary>
    private static (DelegationStatus Status, string Reason)? BudgetEnd(ActiveRole role) =>
        role.Scope.Meter?.Stopped switch
        {
            BudgetStop.Deadline => (DelegationStatus.DeadlineExceeded, $"The {role.Agent.Role} role reached its deadline."),
            BudgetStop.Steps => (DelegationStatus.BudgetExceeded, $"The {role.Agent.Role} role used every step it was granted."),
            BudgetStop.Tokens => (DelegationStatus.BudgetExceeded, $"The {role.Agent.Role} role used more model tokens than it was granted."),
            _ => null,
        };

    /// <summary>A throwaway scope over the envelope the Remediation role would get now, to test a plan against before asking a human.</summary>
    private async Task<DelegatedExecutionScope?> EnvelopeForPlanCheckAsync(RunState state, CancellationToken ct)
    {
        var (parent, exhausted) = Reserve(state, AgentRoleKind.Remediation);
        if (exhausted is not null)
        {
            state.End(DelegationStatus.BudgetExceeded, exhausted);
            return null;
        }

        var reduction = EnvelopeReducer.ReduceForRole(
            parent, AgentRoleKind.Remediation, profiles.GetProfile(AgentRoleKind.Remediation), state.Request.Authority, timeProvider.GetUtcNow());
        if (reduction.IsDenied)
        {
            await WriteDenialAsync(state, AgentRoleKind.Remediation, reduction.Denial!, ct);
            state.End(DelegationStatus.Denied, reduction.Denial!.Reason, reduction.Denial);
            return null;
        }

        return DelegatedExecutionScope.For(state.Id, new AgentIdentity(AgentId.New(), AgentRoleKind.Remediation), reduction.Envelope!);
    }

    /// <summary>
    /// Starts a role: checks the deadline, derives its envelope from the root, the profile and the request (a refusal ends the
    /// run), gives it a new identity and records both. Returns <c>null</c> when the run has ended instead.
    /// </summary>
    private async Task<ActiveRole?> BeginRoleAsync(RunState state, AgentRoleKind role, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();

        // Reaching the deadline is not a denial of authority: it ends the run (ADR-0031 section 3). Checked before the
        // role's envelope is asked for, so an exhausted parent is never handed to the reduction to be refused.
        if (now >= state.Root.Budget.DeadlineUtc)
        {
            state.End(DelegationStatus.DeadlineExceeded, $"The deadline passed before the {role} role could start.");
            return null;
        }

        // ADR-0030 section 6: the role's budget is reserved from what the run has left, not from what it started with. Nothing
        // left is exhaustion, not a denial of authority (ADR-0031 section 3).
        var (parent, exhausted) = Reserve(state, role);
        if (exhausted is not null)
        {
            state.End(DelegationStatus.BudgetExceeded, exhausted);
            return null;
        }

        var reduction = EnvelopeReducer.ReduceForRole(parent, role, profiles.GetProfile(role), state.Request.Authority, now);
        if (reduction.IsDenied)
        {
            await WriteDenialAsync(state, role, reduction.Denial!, ct);
            state.End(DelegationStatus.Denied, reduction.Denial!.Reason, reduction.Denial);
            return null;
        }

        var envelope = reduction.Envelope!;
        var agent = new AgentIdentity(AgentIds(), role);
        if (SeparationOfDuties.SharedIdentity([.. state.Roles.Select(r => r.Agent), agent]) is { } shared)
        {
            // ADR-0030 section 5: nothing has been granted to this role yet, and none will be.
            state.End(DelegationStatus.Failed, shared);
            return null;
        }

        var scope = DelegatedExecutionScope.For(state.Id, agent, envelope) with { PeerAgents = [.. state.Roles.Select(r => r.Agent.Id)] };
        if (store is not null)
        {
            scope = scope with { Journal = new StepJournal(this, state, scope.Correlation) };
        }

        var active = new ActiveRole(agent, envelope, scope, Guid.NewGuid(), envelope.Budget.DeadlineUtc - now, timeProvider, ct);

        await WriteAsync(
            new DelegationEnvelopeAuditEvent
            {
                TimestampUtc = now,
                Node = NodeId.Local,
                TaskId = state.Id,
                StepIndex = -1,
                Actor = state.Actor,
                Role = role,
                ParentEnvelopeHash = state.RootHash,
                ReducedDimensions = reduction.ReducedDimensions,
            },
            scope.Correlation, ct);

        state.Roles.Add(new DelegationRoleRun
        {
            Agent = agent,
            Envelope = envelope,
            Status = DelegationRoleStatus.Running,
            Consumed = BudgetConsumption.Empty,
            StartedAtUtc = now,
        });
        state.Active = active;
        await PersistAsync(state);
        await WriteLifecycleAsync(state, DelegationStage.RoleStarted, DelegationStatus.Running, scope.Correlation, ct, roleStatus: DelegationRoleStatus.Running);
        return active;
    }

    private async Task CompleteRoleAsync(
        RunState state, ActiveRole role, DelegationRoleStatus status, SkillReport? report, VerificationReport? verification, string? error, CancellationToken ct)
    {
        // Reconciled when the role ends (ADR-0030 section 6): what it spent comes off what the run has left for the next role.
        var consumed = role.Scope.Meter?.Consumed ?? BudgetConsumption.Empty;
        var index = state.Roles.FindIndex(r => r.Agent.Id == role.Agent.Id);
        state.Roles[index] = state.Roles[index] with
        {
            Status = status,
            CompletedAtUtc = timeProvider.GetUtcNow(),
            Consumed = consumed,
            Report = report,
            Verification = verification,
            ErrorMessage = error,
        };
        state.Spend(consumed);
        state.Active = null;
        role.Dispose();
        await PersistAsync(state);
        // Which evidence this role itself gathered, by id: enough to say who it came from, without what it says (ADR-0030 section 8).
        var gathered = (report?.Evidence ?? verification?.Evidence)?
            .Where(e => e.Provenance?.AgentId == role.Agent.Id).Select(e => e.Id).ToList();
        await WriteLifecycleAsync(
            state, DelegationStage.RoleCompleted, DelegationStatus.Running, role.Scope.Correlation, ct,
            roleStatus: status, consumed: consumed, error: error, evidenceIds: gathered is { Count: > 0 } ? gathered : null);
        await WriteLifecycleAsync(
            state, DelegationStage.BudgetConsumed, DelegationStatus.Running, role.Scope.Correlation, ct,
            roleStatus: status, consumed: consumed);
    }

    private async Task FailActiveRoleAsync(RunState state, string message, DelegationRoleStatus status = DelegationRoleStatus.Failed)
    {
        if (state.Active is not { } active)
        {
            return;
        }

        try
        {
            // Recording how a role ended is never itself cancelled.
            await CompleteRoleAsync(state, active, status, null, null, Bounded(message), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The audit sink failing while recording a failure must not hide the original one.
            logger.LogError(ex, "Delegation {DelegationId}: could not record the failed role", state.Id);
        }
    }

    /// <summary>Ends the run when a model role's loop did not complete, mapping how it ended to a terminal state.</summary>
    private async Task<bool> EndModelRoleIfNotCompletedAsync(RunState state, ActiveRole role, TaskState task, CancellationToken ct)
    {
        if (task.Status == AgentTaskStatus.Completed)
        {
            return false;
        }

        var status = task.Status switch
        {
            AgentTaskStatus.MaxStepsReached or AgentTaskStatus.BudgetExceeded => DelegationStatus.BudgetExceeded,
            AgentTaskStatus.PolicyBlocked => DelegationStatus.PolicyBlocked,
            AgentTaskStatus.Cancelled => DelegationStatus.Cancelled,
            _ => DelegationStatus.Failed,
        };
        var why = $"The {role.Agent.Role} role ended as {task.Status}.";

        // A role stopped by its own budget says which one ran out: its deadline is not its steps.
        if (task.Status == AgentTaskStatus.BudgetExceeded && BudgetEnd(role) is (var stoppedStatus, var stoppedReason))
        {
            status = stoppedStatus;
            why = stoppedReason;
        }

        await CompleteRoleAsync(state, role, status == DelegationStatus.Cancelled ? DelegationRoleStatus.Cancelled : DelegationRoleStatus.Failed, null, null, why, ct);
        state.End(status, why);
        return true;
    }

    /// <summary>Why an approved plan did not complete: forbidden by policy or the envelope, rejected by a human at a step, or refused for another reason.</summary>
    private (DelegationStatus Status, string Reason) ClassifyStoppedPlan(
        RunState state, PreparedSkillRun prepared, PlanExecution execution, ActiveRole role)
    {
        if (execution.Status == PlanExecutionStatus.OutOfBudget)
        {
            return BudgetEnd(role) ?? (DelegationStatus.BudgetExceeded, $"The {role.Agent.Role} role ran out of budget before the last step of the approved plan.");
        }

        if (execution.Status == PlanExecutionStatus.Stopped)
        {
            return execution.StoppedBy switch
            {
                AuthorizationKind.PolicyDenied => (DelegationStatus.PolicyBlocked, "Policy or the authority envelope forbade a step of the approved plan."),
                AuthorizationKind.UserRejected => (DelegationStatus.Rejected, "A human rejected a step of the approved plan."),
                _ => (DelegationStatus.Failed, "A step of the approved plan named a tool that could not be resolved."),
            };
        }

        // Refused before the first step. If the envelope is the reason, it is a block; anything else is a fault.
        return runner.CheckPlanForDelegation(state.Actor, prepared, role.Scope) is { } envelopeRefusal
            ? (DelegationStatus.PolicyBlocked, Bounded(envelopeRefusal))
            : (DelegationStatus.Failed, Bounded(execution.Reason ?? "The approved plan was refused."));
    }

    // ---- ending ----

    /// <summary>
    /// Ends a run that was interrupted: cancelled by its caller, or stopped by its own or its role's deadline. The two are
    /// distinct terminal states (ADR-0030 section 6). A side-effecting step that was running is said to have an unknown
    /// outcome, never a failed one.
    /// </summary>
    private async Task EndInterruptedAsync(RunState state, CancellationToken callerToken)
    {
        var role = state.Active?.Agent.Role;
        var where = role is null ? "between roles" : $"during the {role} role";
        var unknown = state.Active?.Scope.Meter?.UnknownOutcome;
        var unknownNote = unknown is null ? string.Empty : $" The {unknown} was running, so whether it took effect is unknown.";

        DelegationStatus status;
        DelegationRoleStatus roleStatus;
        string why;
        if (callerToken.IsCancellationRequested)
        {
            (status, roleStatus, why) = (DelegationStatus.Cancelled, DelegationRoleStatus.Cancelled, $"The run was cancelled {where}.{unknownNote}");
        }
        else if (state.DeadlineReached(timeProvider.GetUtcNow()))
        {
            (status, roleStatus, why) = (DelegationStatus.DeadlineExceeded, DelegationRoleStatus.Failed, $"The deadline passed {where}.{unknownNote}");
        }
        else
        {
            (status, roleStatus, why) = (DelegationStatus.Failed, DelegationRoleStatus.Failed, $"The run was interrupted {where}.{unknownNote}");
        }

        await FailActiveRoleAsync(state, why, roleStatus);
        state.End(status, Bounded(why));
    }

    private async Task<DelegationRun> FinishAsync(RunState state)
    {
        // A run that was never ended by a decision ran every role to the end.
        var status = state.Ended ?? DelegationStatus.Failed;
        await WriteLifecycleAsync(state, DelegationStage.Terminal, status, state.Orchestrator, CancellationToken.None, error: state.Error);

        var run = Snapshot(state, status);
        if (store is not null)
        {
            try
            {
                await store.SaveAsync(run, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Rule C1. The run is returned with how it ended; what the store holds is the last state it did save, which a
                // resume treats as interrupted, so nothing is lost and nothing is repeated.
                logger.LogError(ex, "Delegation {DelegationId}: could not store how the run ended", state.Id);
            }
        }

        return run;
    }

    // ---- storing ----

    private DelegationRun Snapshot(RunState state, DelegationStatus status) =>
        new()
        {
            Id = state.Id,
            Node = NodeId.Local,
            Actor = state.Actor,
            Objective = state.Request.Objective,
            IdempotencyKey = state.IdempotencyKey,
            Authority = state.Request.Authority,
            Remediation = state.Request.Remediation is { } remediation
                ? new DelegationRemediationRequest(remediation.SkillId, remediation.CapabilityName, remediation.Request)
                : null,
            Status = status,
            RootEnvelope = state.Root,
            Roles = [.. state.Roles],
            PlanHash = state.PlanHash,
            Approval = state.Approval,
            Journal = [.. state.Journal],
            ResumeCount = state.ResumeCount,
            Denial = state.Denial,
            ErrorMessage = state.Error,
            CreatedAtUtc = state.CreatedAt,
            UpdatedAtUtc = timeProvider.GetUtcNow(),
        };

    /// <summary>Stores the run as it stands. A write that fails stops the run: what it cannot store it cannot resume, so it must not go on. Never cancelled by the caller's token.</summary>
    private async Task PersistAsync(RunState state)
    {
        if (store is not null)
        {
            await store.SaveAsync(Snapshot(state, state.Ended ?? DelegationStatus.Running), CancellationToken.None);
        }
    }

    /// <summary>Stores a new run, or finds the one an earlier start with the same key made. <c>false</c> when the run already exists and nothing is to be started.</summary>
    private async Task<bool> RegisterAsync(RunState state)
    {
        if (store is null)
        {
            return true;
        }

        var result = await store.StartAsync(Snapshot(state, DelegationStatus.Running), CancellationToken.None);
        if (!result.Created)
        {
            state.Existing = result.Run;
        }

        return result.Created;
    }

    private DateTimeOffset UtcNow() => timeProvider.GetUtcNow();

    private IDelegationStore RequireStore() =>
        store ?? throw new InvalidOperationException("This runner has no delegation store, so a run cannot be resumed or reconciled.");

    private Task WriteReconciliationAsync(
        Guid runId, int stepIndex, ReconciliationAction action, ActorIdentity resolvedBy, VerificationStatus? verification, string? note,
        DelegationCorrelation correlation) =>
        audit.WriteAsync(
            new DelegationReconciliationAuditEvent
            {
                TimestampUtc = timeProvider.GetUtcNow(),
                Node = NodeId.Local,
                TaskId = runId,
                StepIndex = stepIndex,
                Actor = resolvedBy,
                Action = action,
                ResolvedBy = resolvedBy,
                Verification = verification,
                Note = note is null ? null : Bounded(note),
                Delegation = correlation,
            },
            CancellationToken.None);

    /// <summary>The runner's <see cref="IStepJournal"/> for one run: every write is a save of the whole run, durable before it returns.</summary>
    private sealed class StepJournal(DelegationRunner owner, RunState state, DelegationCorrelation correlation) : IStepJournal
    {
        public async Task BeginAsync(int stepIndex, string toolName, ToolArguments arguments)
        {
            var hash = DelegationHasher.ComputeArgumentsHash(arguments);
            state.Journal.Add(new StepJournalEntry
            {
                StepIndex = stepIndex,
                ToolName = toolName,
                ArgumentsHash = hash,
                IntentAtUtc = owner.UtcNow(),
            });
            await owner.PersistAsync(state);
            await owner.WriteJournalAuditAsync(state, stepIndex, JournalPhase.Intent, toolName, hash, null, null, correlation);
        }

        public async Task CompleteAsync(int stepIndex, StepOutcome outcome)
        {
            var index = state.Journal.FindLastIndex(e => e.StepIndex == stepIndex);
            if (index < 0)
            {
                return;
            }

            state.Journal[index] = state.Journal[index] with { Outcome = outcome };
            await owner.PersistAsync(state);
            var entry = state.Journal[index];
            await owner.WriteJournalAuditAsync(state, stepIndex, JournalPhase.Outcome, entry.ToolName, entry.ArgumentsHash, outcome.Kind, outcome.Verification, correlation);
        }
    }

    private Task WriteJournalAuditAsync(
        RunState state, int stepIndex, JournalPhase phase, string tool, string argumentsHash, StepOutcomeKind? outcome,
        VerificationStatus? verification, DelegationCorrelation correlation) =>
        audit.WriteAsync(
            new DelegationJournalAuditEvent
            {
                TimestampUtc = timeProvider.GetUtcNow(),
                Node = NodeId.Local,
                TaskId = state.Id,
                StepIndex = stepIndex,
                Actor = state.Actor,
                Phase = phase,
                Tool = tool,
                ArgumentsHash = argumentsHash,
                Outcome = outcome,
                Verification = verification,
                Delegation = correlation,
            },
            CancellationToken.None);

    private static bool IsHuman(ActorIdentity approver, RunState state) => IsHuman(approver, state.Roles);

    private static bool IsHuman(ActorIdentity approver, IReadOnlyList<DelegationRoleRun> roles) =>
        SeparationOfDuties.IsHumanApprover(approver, roles.Select(role => role.Agent.Id));

    /// <summary>
    /// A private copy of a prepared plan, so a list a Capability kept cannot change what a human approved. The hash the run
    /// binds to is recomputed from the copy and must be the one the runtime recorded when it prepared the plan.
    /// </summary>
    private static PreparedSkillRun Snapshot(PreparedSkillRun prepared)
    {
        if (prepared.Report.Plan is not { } plan)
        {
            return prepared;
        }

        var copy = new ExecutionPlan(plan.CapabilityName, plan.CapabilityVersion, plan.Rationale, [.. plan.Steps]);
        return string.Equals(ExecutionPlanHasher.ComputeHash(copy), prepared.PlanHash, StringComparison.Ordinal)
            ? prepared with { Report = prepared.Report with { Plan = copy } }
            : throw new InvalidOperationException("The prepared plan changed after its hash was recorded.");
    }

    private static string Bounded(string message) =>
        message.Length <= MaximumMessageLength ? message : message[..MaximumMessageLength];

    /// <summary>The authority of a run that was refused before any was granted: nothing at all.</summary>
    private static AuthorityEnvelope NoAuthority(RunState state) =>
        new(state.Actor, Depth: 0, [], [], [], RiskLevel.Read, BlastRadius.Single, [], [], new DelegationBudget(0, 0, state.CreatedAt));

    // ---- audit ----

    private Task WriteAsync(AuditEvent evt, DelegationCorrelation correlation, CancellationToken ct) =>
        audit.WriteAsync(evt with { Delegation = correlation }, ct);

    private Task WriteLifecycleAsync(
        RunState state,
        DelegationStage stage,
        DelegationStatus status,
        DelegationCorrelation correlation,
        CancellationToken ct,
        DelegationRoleStatus? roleStatus = null,
        BudgetConsumption? consumed = null,
        string? error = null,
        string? planHash = null,
        ActorIdentity? actor = null,
        IReadOnlyList<string>? evidenceIds = null) =>
        WriteAsync(
            new DelegationLifecycleAuditEvent
            {
                TimestampUtc = timeProvider.GetUtcNow(),
                Node = NodeId.Local,
                TaskId = state.Id,
                StepIndex = -1,
                Actor = actor ?? state.Actor,
                Stage = stage,
                Status = status,
                RoleStatus = roleStatus,
                Consumed = consumed,
                EvidenceIds = evidenceIds,
                ErrorMessage = error is null ? null : Bounded(error),
                PlanHash = planHash,
            },
            correlation, ct);

    private Task WriteDenialAsync(RunState state, AgentRoleKind role, DelegationDenial denial, CancellationToken ct) =>
        WriteAsync(
            new DelegationEnvelopeAuditEvent
            {
                TimestampUtc = timeProvider.GetUtcNow(),
                Node = NodeId.Local,
                TaskId = state.Id,
                StepIndex = -1,
                Actor = state.Actor,
                Role = role,
                ParentEnvelopeHash = state.RootHash,
                ReducedDimensions = [],
                Denial = denial,
            },
            state.Orchestrator, ct);

    // ---- state ----

    /// <summary>
    /// The role that is running. It owns the role's link in the cancellation token tree: the run's token, cut short at the
    /// role's own deadline, so a role stops when it reaches its deadline even in the middle of a model call or a tool.
    /// </summary>
    private sealed class ActiveRole : IDisposable
    {
        private readonly CancellationTokenSource _deadline;
        private readonly CancellationTokenSource _linked;

        internal ActiveRole(
            AgentIdentity agent, AuthorityEnvelope envelope, DelegatedExecutionScope scope, Guid taskId,
            TimeSpan untilDeadline, TimeProvider timeProvider, CancellationToken run)
        {
            Agent = agent;
            Envelope = envelope;
            Scope = scope;
            TaskId = taskId;
            _deadline = TimerFor(untilDeadline, timeProvider);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(run, _deadline.Token);
        }

        internal AgentIdentity Agent { get; }

        internal AuthorityEnvelope Envelope { get; }

        internal DelegatedExecutionScope Scope { get; }

        internal Guid TaskId { get; }

        /// <summary>The run's token and this role's deadline: cancelled by either.</summary>
        internal CancellationToken Token => _linked.Token;

        /// <summary>Whether the role's own deadline is what cancelled it.</summary>
        internal bool DeadlineFired => _deadline.IsCancellationRequested;

        internal EvidenceProvenance Provenance(Guid delegationId) => new(delegationId, Agent.Id, Agent.Role);

        public void Dispose()
        {
            _linked.Dispose();
            _deadline.Dispose();
        }
    }

    /// <summary>A source that cancels after <paramref name="delay"/> on <paramref name="timeProvider"/>'s clock, or never when that is too far off for a timer.</summary>
    private static CancellationTokenSource TimerFor(TimeSpan delay, TimeProvider timeProvider) =>
        delay > TimeSpan.Zero && delay < MaximumTimer
            ? new CancellationTokenSource(delay, timeProvider)
            : new CancellationTokenSource();

    private sealed class RunState(Guid id, DelegationRequest request, ActorIdentity actor, DateTimeOffset createdAt) : IDisposable
    {
        private AuthorityEnvelope? _root;
        private CancellationTokenSource? _deadline;
        private CancellationTokenSource? _linked;

        public Guid Id { get; } = id;

        public DelegationRequest Request { get; } = request;

        public ActorIdentity Actor { get; } = actor;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public AuthorityEnvelope Root => _root ?? throw new InvalidOperationException("The root envelope is not set yet.");

        public string RootHash { get; private set; } = string.Empty;

        public string? IdempotencyKey { get; init; }

        /// <summary>The run an earlier start with the same key created, when this start is a repeat and has nothing to run.</summary>
        public DelegationRun? Existing { get; set; }

        /// <summary>The journal of the side-effecting steps, in the order their intent was committed.</summary>
        public List<StepJournalEntry> Journal { get; } = [];

        public int ResumeCount { get; set; }

        /// <summary>Rebuilds the state of a stored run, for a resume. What a human approved is never carried over: an approval is not authority once the process that held it is gone.</summary>
        public static RunState FromStored(DelegationRun stored)
        {
            var remediation = stored.Remediation is { } request
                ? new DelegationRemediation(request.SkillId, request.CapabilityName, request.Request)
                : null;
            var state = new RunState(stored.Id, new DelegationRequest(stored.Objective, stored.Authority, remediation), stored.Actor, stored.CreatedAtUtc)
            {
                IdempotencyKey = stored.IdempotencyKey,
                ResumeCount = stored.ResumeCount,
                PlanHash = stored.PlanHash,
            };
            state.SetRoot(stored.RootEnvelope);
            state.Roles.AddRange(stored.Roles);
            state.Journal.AddRange(stored.Journal);
            return state;
        }

        /// <summary>The most recent role of a kind that ended as completed, or <c>null</c>.</summary>
        public DelegationRoleRun? LastCompleted(AgentRoleKind kind) =>
            Roles.LastOrDefault(role => role.Agent.Role == kind && role.Status == DelegationRoleStatus.Completed);

        public void SetReconciliation(int stepIndex, StepReconciliation reconciliation)
        {
            var index = Journal.FindLastIndex(e => e.StepIndex == stepIndex);
            if (index >= 0)
            {
                Journal[index] = Journal[index] with { Reconciliation = reconciliation };
            }
        }

        /// <summary>Adds up what every role has spent, from the roles as they are stored.</summary>
        public void RecountSpent()
        {
            SpentSteps = Roles.Sum(role => (long)role.Consumed.Steps);
            SpentTokens = Roles.Sum(role => (long)role.Consumed.Tokens);
        }

        /// <summary>What the roles that have ended spent, to reconcile against the root budget (ADR-0030 section 6).</summary>
        public long SpentSteps { get; private set; }

        public long SpentTokens { get; private set; }

        public void Spend(BudgetConsumption consumed)
        {
            SpentSteps += consumed.Steps;
            SpentTokens += consumed.Tokens;
        }

        /// <summary>
        /// The run's link in the token tree: the caller's token, cut short at the run's deadline. A human's decision on the plan
        /// is awaited under it, so an approval nobody gives cannot hold a run past its deadline.
        /// </summary>
        public CancellationToken BindDeadline(TimeProvider timeProvider, CancellationToken caller)
        {
            _deadline = TimerFor(Root.Budget.DeadlineUtc - timeProvider.GetUtcNow(), timeProvider);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _deadline.Token);
            return _linked.Token;
        }

        /// <summary>Whether the run's own deadline, or the running role's, has been reached, by timer or by the clock.</summary>
        public bool DeadlineReached(DateTimeOffset now) =>
            _root is not null
            && (_deadline?.IsCancellationRequested == true
                || Active?.DeadlineFired == true
                || now >= (Active?.Envelope.Budget.DeadlineUtc ?? _root.Budget.DeadlineUtc));

        public void Dispose()
        {
            Active?.Dispose();
            _linked?.Dispose();
            _deadline?.Dispose();
        }

        /// <summary>The correlation of an event of the orchestrator itself: the run and the root envelope, no agent.</summary>
        public DelegationCorrelation Orchestrator => new(Id, RootHash, null);

        public List<DelegationRoleRun> Roles { get; } = [];

        public ActiveRole? Active { get; set; }

        public string? PlanHash { get; set; }

        public DelegationApproval? Approval { get; set; }

        public DelegationStatus? Ended { get; private set; }

        public string? Error { get; private set; }

        public DelegationDenial? Denial { get; private set; }

        public void SetRoot(AuthorityEnvelope root)
        {
            _root = root;
            RootHash = DelegationHasher.ComputeEnvelopeHash(root);
        }

        /// <summary>Records how the run ended. The first ending stands.</summary>
        public void End(DelegationStatus status, string? error = null, DelegationDenial? denial = null)
        {
            if (Ended is not null)
            {
                return;
            }

            Ended = status;
            Error = error;
            Denial = denial;
        }
    }
}
