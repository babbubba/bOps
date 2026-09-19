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
/// This version keeps a run in memory. Counting steps and tokens, cancellation as a terminal state, the durable
/// journal and resume, and the independence checks that go beyond this construction are V1.2-E to V1.2-G; the
/// deadline is checked before every role.
/// </para>
/// </remarks>
public sealed class DelegationRunner(
    AgentRunner runner,
    IRoleProfileSource profiles,
    IPlanApprovalProvider planApproval,
    IAuditSink audit,
    TimeProvider timeProvider,
    ILogger<DelegationRunner> logger)
{
    private const int MaximumMessageLength = 500;

    /// <summary>Runs one objective through the fixed pipeline and returns how it ended.</summary>
    /// <param name="request">The objective, an optional narrowing of authority, and the change to prepare if any.</param>
    /// <param name="actor">The operator on whose authority the objective runs. Every envelope of the run is granted to them.</param>
    /// <param name="delegationId">The id to give the run; a fresh one when omitted. For a caller that must hand the id out before the run ends.</param>
    /// <param name="ct">Cancelled to abandon the run; the cancellation propagates, it does not end the run as a status yet (V1.2-E).</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or <paramref name="actor"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="delegationId"/> is empty.</exception>
    public async Task<DelegationRun> StartAsync(
        DelegationRequest request, ActorIdentity actor, Guid? delegationId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        if (delegationId == Guid.Empty)
        {
            throw new ArgumentException("A delegation must have a non-empty id.", nameof(delegationId));
        }

        ct.ThrowIfCancellationRequested();
        var state = new RunState(delegationId ?? Guid.NewGuid(), request, actor, timeProvider.GetUtcNow());

        try
        {
            await RunPipelineAsync(state, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Rule C1: nothing thrown escapes a run. A failure in the orchestrator itself is a terminal state, never a crash.
            logger.LogError(ex, "Delegation {DelegationId}: the orchestrator failed", state.Id);
            await FailActiveRoleAsync(state, ex.Message, ct);
            state.End(DelegationStatus.Failed, Bounded(ex.Message));
        }

        return await FinishAsync(state, ct);
    }

    // ---- the pipeline ----

    private async Task RunPipelineAsync(RunState state, CancellationToken ct)
    {
        var (reduction, deniedRole) = EnvelopeReducer.DeriveRootAttributed(profiles, state.Request.Authority, state.Actor, state.CreatedAt);
        if (reduction.IsDenied)
        {
            // No root exists, so the authority in force for the refusal is nothing at all.
            state.SetRoot(NoAuthority(state));
            await WriteLifecycleAsync(state, DelegationStage.Requested, DelegationStatus.Running, state.Orchestrator, ct);
            await WriteDenialAsync(state, deniedRole ?? RoleRequirements.Pipeline[0], reduction.Denial!, ct);
            state.End(DelegationStatus.Denied, reduction.Denial!.Reason, reduction.Denial);
            return;
        }

        state.SetRoot(reduction.Envelope!);
        await WriteLifecycleAsync(state, DelegationStage.Requested, DelegationStatus.Running, state.Orchestrator, ct);

        if (state.Request.Remediation is { } remediation && await PrepareCheckAsync(state, remediation, ct) is { } refused)
        {
            state.End(DelegationStatus.Denied, refused.Reason, refused);
            return;
        }

        // Discovery: gathers evidence with Read tools. Read-only, and the first of the two roles that use the model.
        if (await BeginRoleAsync(state, AgentRoleKind.Discovery, ct) is not { } discovery)
        {
            return;
        }

        var discoveryTask = await runner.RunDelegatedAsync(
            DelegationRoleData.DiscoveryGoal(state.Request.Objective), state.Actor, discovery.Scope, discovery.TaskId, ct);
        if (await EndModelRoleIfNotCompletedAsync(state, discovery, discoveryTask, ct))
        {
            return;
        }

        var discoveryEvidence = DelegationRoleData.EvidenceOf(
            discoveryTask, AgentRoleKind.Discovery, discovery.Provenance(state.Id), timeProvider.GetUtcNow());
        await CompleteRoleAsync(state, discovery, DelegationRoleStatus.Completed, new SkillReport(discoveryEvidence, [], null), null, null, ct);

        // Diagnostic: turns that evidence into findings and, through a Capability, may prepare an immutable plan.
        if (await BeginRoleAsync(state, AgentRoleKind.Diagnostic, ct) is not { } diagnostic)
        {
            return;
        }

        var diagnosticTask = await runner.RunDelegatedAsync(
            DelegationRoleData.DiagnosticGoal(state.Request.Objective, discoveryEvidence), state.Actor, diagnostic.Scope, diagnostic.TaskId, ct);
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
                diagnostic.TaskId, state.Actor, toPrepare.SkillId, toPrepare.CapabilityName, toPrepare.Request, diagnostic.Scope, ct);
            if (preparedRun.Status != SkillPreparationStatus.Prepared)
            {
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

        await RunApprovedPlanAsync(state, prepared, plan, findings, ct);
    }

    private async Task RunApprovedPlanAsync(
        RunState state, PreparedSkillRun prepared, ExecutionPlan plan, IReadOnlyList<Finding> findings, CancellationToken ct)
    {
        var hash = prepared.PlanHash!;
        state.PlanHash = hash;

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

        // Remediation: executes exactly the approved plan through the ordinary step pipeline. It makes no model call.
        if (await BeginRoleAsync(state, AgentRoleKind.Remediation, ct) is not { } remediationRole)
        {
            return;
        }

        var execution = await runner.ExecuteDelegatedPlanAsync(
            remediationRole.TaskId, state.Actor, prepared, new ExecutionPlanApproval(hash, decision), remediationRole.Scope, ct);
        if (execution.Status != PlanExecutionStatus.Completed)
        {
            var (status, why) = ClassifyStoppedPlan(state, prepared, execution, remediationRole);
            await CompleteRoleAsync(state, remediationRole, DelegationRoleStatus.Failed, null, null, why, ct);
            state.End(status, why);
            return;
        }

        await CompleteRoleAsync(state, remediationRole, DelegationRoleStatus.Completed, null, null, null, ct);

        // Verification: a distinct identity that reads the system itself. It makes no model call, and it does not look at
        // what Remediation reported.
        if (await BeginRoleAsync(state, AgentRoleKind.Verification, ct) is not { } verificationRole)
        {
            return;
        }

        var verified = await runner.VerifyPlanAsync(verificationRole.TaskId, state.Actor, plan, hash, verificationRole.Scope, ct);
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

    /// <summary>A throwaway scope over the envelope the Remediation role would get now, to test a plan against before asking a human.</summary>
    private async Task<DelegatedExecutionScope?> EnvelopeForPlanCheckAsync(RunState state, CancellationToken ct)
    {
        var reduction = EnvelopeReducer.ReduceForRole(
            state.Root, AgentRoleKind.Remediation, profiles.GetProfile(AgentRoleKind.Remediation), state.Request.Authority, timeProvider.GetUtcNow());
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

        var reduction = EnvelopeReducer.ReduceForRole(state.Root, role, profiles.GetProfile(role), state.Request.Authority, now);
        if (reduction.IsDenied)
        {
            await WriteDenialAsync(state, role, reduction.Denial!, ct);
            state.End(DelegationStatus.Denied, reduction.Denial!.Reason, reduction.Denial);
            return null;
        }

        var envelope = reduction.Envelope!;
        var agent = new AgentIdentity(AgentId.New(), role);
        var scope = DelegatedExecutionScope.For(state.Id, agent, envelope);
        var active = new ActiveRole(agent, envelope, scope, Guid.NewGuid());

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
        await WriteLifecycleAsync(state, DelegationStage.RoleStarted, DelegationStatus.Running, scope.Correlation, ct, roleStatus: DelegationRoleStatus.Running);
        return active;
    }

    private async Task CompleteRoleAsync(
        RunState state, ActiveRole role, DelegationRoleStatus status, SkillReport? report, VerificationReport? verification, string? error, CancellationToken ct)
    {
        var index = state.Roles.FindIndex(r => r.Agent.Id == role.Agent.Id);
        state.Roles[index] = state.Roles[index] with
        {
            Status = status,
            CompletedAtUtc = timeProvider.GetUtcNow(),
            Report = report,
            Verification = verification,
            ErrorMessage = error,
        };
        state.Active = null;
        await WriteLifecycleAsync(
            state, DelegationStage.RoleCompleted, DelegationStatus.Running, role.Scope.Correlation, ct,
            roleStatus: status, consumed: BudgetConsumption.Empty, error: error);
    }

    private async Task FailActiveRoleAsync(RunState state, string message, CancellationToken ct)
    {
        if (state.Active is not { } active)
        {
            return;
        }

        try
        {
            await CompleteRoleAsync(state, active, DelegationRoleStatus.Failed, null, null, Bounded(message), ct);
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
        await CompleteRoleAsync(state, role, status == DelegationStatus.Cancelled ? DelegationRoleStatus.Cancelled : DelegationRoleStatus.Failed, null, null, why, ct);
        state.End(status, why);
        return true;
    }

    /// <summary>Why an approved plan did not complete: forbidden by policy or the envelope, rejected by a human at a step, or refused for another reason.</summary>
    private (DelegationStatus Status, string Reason) ClassifyStoppedPlan(
        RunState state, PreparedSkillRun prepared, PlanExecution execution, ActiveRole role)
    {
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

    private async Task<DelegationRun> FinishAsync(RunState state, CancellationToken ct)
    {
        // A run that was never ended by a decision ran every role to the end.
        var status = state.Ended ?? DelegationStatus.Failed;
        await WriteLifecycleAsync(state, DelegationStage.Terminal, status, state.Orchestrator, ct, error: state.Error);

        return new DelegationRun
        {
            Id = state.Id,
            Node = NodeId.Local,
            Actor = state.Actor,
            Objective = state.Request.Objective,
            Status = status,
            RootEnvelope = state.Root,
            Roles = [.. state.Roles],
            PlanHash = state.PlanHash,
            Approval = state.Approval,
            Journal = [],
            Denial = state.Denial,
            ErrorMessage = state.Error,
            CreatedAtUtc = state.CreatedAt,
            UpdatedAtUtc = timeProvider.GetUtcNow(),
        };
    }

    private static bool IsHuman(ActorIdentity approver, RunState state) =>
        !string.Equals(approver.Kind, "agent", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(approver.Kind, ActorIdentity.RuntimeSystem.Kind, StringComparison.OrdinalIgnoreCase)
        && !state.Roles.Any(role => string.Equals(role.Agent.Id.ToString(), approver.Id, StringComparison.OrdinalIgnoreCase));

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
        ActorIdentity? actor = null) =>
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

    private sealed record ActiveRole(AgentIdentity Agent, AuthorityEnvelope Envelope, DelegatedExecutionScope Scope, Guid TaskId)
    {
        internal EvidenceProvenance Provenance(Guid delegationId) => new(delegationId, Agent.Id, Agent.Role);
    }

    private sealed class RunState(Guid id, DelegationRequest request, ActorIdentity actor, DateTimeOffset createdAt)
    {
        private AuthorityEnvelope? _root;

        public Guid Id { get; } = id;

        public DelegationRequest Request { get; } = request;

        public ActorIdentity Actor { get; } = actor;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public AuthorityEnvelope Root => _root ?? throw new InvalidOperationException("The root envelope is not set yet.");

        public string RootHash { get; private set; } = string.Empty;

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
