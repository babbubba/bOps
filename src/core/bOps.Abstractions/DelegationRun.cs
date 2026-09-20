// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>
/// Where a delegated run stands or how it ended (ADR-0030 sections 2, 6 and 7). Budget exhaustion,
/// deadline expiry and cancellation are distinct terminal states, and so are a plan nobody approved and
/// a verification that did not confirm: none of them is reported as a success (rule S4). The numeric
/// values are persisted and must never be reordered.
/// </summary>
public enum DelegationStatus
{
    /// <summary>The orchestrator is executing a role.</summary>
    Running,

    /// <summary>A plan is prepared and the run waits for a human decision on its hash. Approvals are never persisted, so a restarted run returns here.</summary>
    AwaitingApproval,

    /// <summary>A side-effecting step has an ambiguous outcome that its verification could not settle. The run waits for an operator to accept the step as done or abandon the run; there is no automatic retry.</summary>
    RequiresReconciliation,

    /// <summary>Terminal. The approved plan ran and independent verification confirmed it.</summary>
    Completed,

    /// <summary>Terminal. The diagnosis finished and proposed no plan. Not an error.</summary>
    DiagnosisCompleted,

    /// <summary>Terminal. A human rejected the plan. Not an error.</summary>
    Rejected,

    /// <summary>Terminal. Verification refuted the outcome, could not tell, or did not apply. Never success.</summary>
    VerificationFailed,

    /// <summary>Terminal. Delegation was refused because an envelope dimension had an empty intersection.</summary>
    Denied,

    /// <summary>Terminal. Policy or the envelope forbade a step of the approved plan.</summary>
    PolicyBlocked,

    /// <summary>Terminal. The step or token budget was exhausted.</summary>
    BudgetExceeded,

    /// <summary>Terminal. The deadline passed.</summary>
    DeadlineExceeded,

    /// <summary>Terminal. The run was cancelled.</summary>
    Cancelled,

    /// <summary>Terminal. An operator abandoned a run that required reconciliation.</summary>
    Abandoned,

    /// <summary>Terminal. An unrecoverable runtime, tool or model failure.</summary>
    Failed,
}

/// <summary>Where one role of a delegated run stands. The numeric values are persisted and must never be reordered.</summary>
public enum DelegationRoleStatus
{
    /// <summary>The role has not started.</summary>
    Pending,

    /// <summary>The role is executing, or was executing when the process stopped.</summary>
    Running,

    /// <summary>The role finished and its result is recorded.</summary>
    Completed,

    /// <summary>The role ended in failure.</summary>
    Failed,

    /// <summary>The role was cancelled.</summary>
    Cancelled,
}

/// <summary>How a journaled side-effecting step ended. The numeric values are persisted and must never be reordered.</summary>
public enum StepOutcomeKind
{
    /// <summary>The tool reported success. It is never executed again.</summary>
    Succeeded,

    /// <summary>The tool reported failure.</summary>
    Failed,

    /// <summary>The step timed out; whether it took effect is unknown, so it is ambiguous.</summary>
    Timeout,

    /// <summary>The step was cancelled while running; whether it took effect is unknown, so it is ambiguous. Journaled as unknown, not as failed (ADR-0030 section 6).</summary>
    Cancelled,
}

/// <summary>The second durable write of a journaled step: how it ended (ADR-0030 section 7).</summary>
public sealed record StepOutcome
{
    /// <summary>Creates a step outcome.</summary>
    /// <param name="Kind">How the step ended.</param>
    /// <param name="AtUtc">When it ended.</param>
    /// <param name="Verification">The step's own post-action verification, when one ran.</param>
    /// <exception cref="ArgumentException"><paramref name="Kind"/> is not a defined outcome.</exception>
    public StepOutcome(StepOutcomeKind Kind, DateTimeOffset AtUtc, VerificationStatus? Verification = null)
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentException("Unknown step outcome.", nameof(Kind));
        }

        this.Kind = Kind;
        this.AtUtc = AtUtc;
        this.Verification = Verification;
    }

    /// <summary>How the step ended.</summary>
    public StepOutcomeKind Kind { get; init; }

    /// <summary>When it ended.</summary>
    public DateTimeOffset AtUtc { get; init; }

    /// <summary>The step's own post-action verification, when one ran.</summary>
    public VerificationStatus? Verification { get; init; }
}

/// <summary>How an ambiguous step, or a run that required reconciliation, was settled. The numeric values are persisted and must never be reordered.</summary>
public enum ReconciliationAction
{
    /// <summary>The step's declared verification returned <see cref="VerificationStatus.Confirmed"/>; the step is recorded as done by reconciliation.</summary>
    VerifiedDone,

    /// <summary>An administrator accepted the step as done. Audited.</summary>
    OperatorAcceptedDone,

    /// <summary>The declared verification returned <see cref="VerificationStatus.Refuted"/> or <see cref="VerificationStatus.Inconclusive"/>; the run stops as <see cref="DelegationStatus.RequiresReconciliation"/>.</summary>
    EscalatedToOperator,

    /// <summary>An administrator abandoned the run. A retry is a new objective with a new plan and approval.</summary>
    OperatorAbandoned,
}

/// <summary>
/// How an ambiguous step was settled (ADR-0030 section 7). Only <see cref="ReconciliationAction.VerifiedDone"/>
/// and <see cref="ReconciliationAction.OperatorAcceptedDone"/> mark a step as done; the other actions end or
/// hold the run and are recorded in the audit log.
/// </summary>
public sealed record StepReconciliation
{
    /// <summary>Creates a reconciliation record.</summary>
    /// <param name="Action">How the step was settled.</param>
    /// <param name="Verification">The verification that settled it, when one ran.</param>
    /// <param name="ResolvedBy">Who settled it: the runtime for a verification, an administrator for an operator action.</param>
    /// <param name="AtUtc">When.</param>
    public StepReconciliation(ReconciliationAction Action, VerificationStatus? Verification, ActorIdentity ResolvedBy, DateTimeOffset AtUtc)
    {
        ArgumentNullException.ThrowIfNull(ResolvedBy);
        if (!Enum.IsDefined(Action))
        {
            throw new ArgumentException("Unknown reconciliation action.", nameof(Action));
        }

        this.Action = Action;
        this.Verification = Verification;
        this.ResolvedBy = ResolvedBy;
        this.AtUtc = AtUtc;
    }

    /// <summary>How the step was settled.</summary>
    public ReconciliationAction Action { get; init; }

    /// <summary>The verification that settled it, when one ran.</summary>
    public VerificationStatus? Verification { get; init; }

    /// <summary>Who settled it.</summary>
    public ActorIdentity ResolvedBy { get; init; }

    /// <summary>When.</summary>
    public DateTimeOffset AtUtc { get; init; }
}

/// <summary>
/// The journal record of one side-effecting step (ADR-0030 section 7). The intent is committed
/// <em>before</em> the step executes and <see cref="Outcome"/> after; an entry whose <see cref="Outcome"/> is
/// still <c>null</c> is the signature of a crash between the two and is treated as ambiguous. Read-only
/// work is never journaled. Only a hash of the arguments is kept, never the arguments.
/// </summary>
public sealed record StepJournalEntry
{
    /// <summary>The step's index within the approved plan.</summary>
    public required int StepIndex { get; init; }

    /// <summary>The tool the step calls.</summary>
    public required string ToolName { get; init; }

    /// <summary>The lowercase hex SHA-256 of the step's canonical arguments (<see cref="DelegationHasher.ComputeArgumentsHash"/>).</summary>
    public required string ArgumentsHash { get; init; }

    /// <summary>When the intent was committed, before execution.</summary>
    public required DateTimeOffset IntentAtUtc { get; init; }

    /// <summary>How the step ended, or <c>null</c> when the outcome was never written.</summary>
    public StepOutcome? Outcome { get; init; }

    /// <summary>How an ambiguous step was settled, or <c>null</c> when it was never ambiguous or is still unsettled.</summary>
    public StepReconciliation? Reconciliation { get; init; }
}

/// <summary>
/// The record of a human's approval of a plan hash. History only, never authority: approvals are never
/// persisted across a restart (ADR-0022, ADR-0030 section 7), so a resumed run must obtain a fresh human
/// approval of the same hash and must not treat this value as one.
/// </summary>
public sealed record DelegationApproval
{
    /// <summary>Creates an approval record.</summary>
    /// <param name="PlanHash">The <see cref="ExecutionPlanHasher.ComputeHash"/> the human approved.</param>
    /// <param name="Approver">The human who approved. Never an agent.</param>
    /// <param name="ApprovedAtUtc">When.</param>
    public DelegationApproval(string PlanHash, ActorIdentity Approver, DateTimeOffset ApprovedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PlanHash);
        ArgumentNullException.ThrowIfNull(Approver);

        this.PlanHash = PlanHash;
        this.Approver = Approver;
        this.ApprovedAtUtc = ApprovedAtUtc;
    }

    /// <summary>The plan hash the human approved.</summary>
    public string PlanHash { get; init; }

    /// <summary>The human who approved.</summary>
    public ActorIdentity Approver { get; init; }

    /// <summary>When.</summary>
    public DateTimeOffset ApprovedAtUtc { get; init; }
}

/// <summary>
/// The verdict of the Verification role (ADR-0030 sections 2 and 5). Deterministic: no model produced it.
/// It is <see cref="VerificationStatus.Confirmed"/> only with evidence the verifier gathered itself, so a
/// confirmation can never rest on someone else's word.
/// </summary>
public sealed record VerificationReport
{
    /// <summary>Creates a verification report.</summary>
    /// <param name="PlanHash">The approved plan hash that was verified.</param>
    /// <param name="Status">The verdict. <see cref="VerificationStatus.Inconclusive"/>, <see cref="VerificationStatus.Refuted"/> and <see cref="VerificationStatus.NotApplicable"/> are never success (rule S4).</param>
    /// <param name="Evidence">Evidence the Verification agent read from the system itself, not taken from the Remediation role's results.</param>
    /// <param name="Detail">A bounded explanation, or <c>null</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="PlanHash"/> is blank, or <paramref name="Status"/> is <see cref="VerificationStatus.Confirmed"/> with no evidence.</exception>
    public VerificationReport(string PlanHash, VerificationStatus Status, IReadOnlyList<Evidence> Evidence, string? Detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PlanHash);
        ArgumentNullException.ThrowIfNull(Evidence);
        if (Status is VerificationStatus.Confirmed && Evidence.Count == 0)
        {
            throw new ArgumentException("A verification cannot be confirmed without evidence the verifier gathered itself.", nameof(Evidence));
        }

        this.PlanHash = PlanHash;
        this.Status = Status;
        this.Evidence = Evidence;
        this.Detail = Detail;
    }

    /// <summary>The approved plan hash that was verified.</summary>
    public string PlanHash { get; init; }

    /// <summary>The verdict.</summary>
    public VerificationStatus Status { get; init; }

    /// <summary>Evidence the Verification agent read from the system itself.</summary>
    public IReadOnlyList<Evidence> Evidence { get; init; }

    /// <summary>A bounded explanation, or <c>null</c>.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// One role of a delegated run and everything durable about it (ADR-0030 section 7): who it is, what it
/// may do, where it stands and what it has spent. The full envelope is kept, not just its hash, so a
/// resumed role runs under exactly the authority it was granted; the runtime may narrow it further from
/// the current role profile, never widen it.
/// </summary>
public sealed record DelegationRoleRun
{
    /// <summary>The agent performing the role.</summary>
    public required AgentIdentity Agent { get; init; }

    /// <summary>The authority the role was granted.</summary>
    public required AuthorityEnvelope Envelope { get; init; }

    /// <summary>Where the role stands.</summary>
    public required DelegationRoleStatus Status { get; init; }

    /// <summary>What the role has spent. Persisted, so restarting the role never resets it.</summary>
    public required BudgetConsumption Consumed { get; init; }

    /// <summary>When the role started, or <c>null</c> if it has not.</summary>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>When the role ended, or <c>null</c> if it has not.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>
    /// What a Discovery or Diagnostic role produced: evidence, findings and an optional plan. Stored when
    /// the role completes; an interrupted read-only role restarts from its beginning. <c>null</c> for the
    /// other roles.
    /// </summary>
    public SkillReport? Report { get; init; }

    /// <summary>The verdict of the Verification role, or <c>null</c> for the other roles.</summary>
    public VerificationReport? Verification { get; init; }

    /// <summary>A bounded failure explanation, or <c>null</c>.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// The durable aggregate of one delegated objective (ADR-0030 section 7): the objective, who asked, its
/// status, the root envelope, each role, the plan hash, the approval history and the step journal.
/// Persisted whole, one row per run. <c>TaskState</c> and <c>ITaskStore</c> are unchanged by delegation.
/// </summary>
/// <remarks>
/// The runtime is the only writer of a live run, so the store needs no version check; an operator action
/// on a run that requires reconciliation happens while nothing else writes it.
/// </remarks>
public sealed record DelegationRun
{
    /// <summary>The run's unique id. Also the <see cref="AuditEvent.TaskId"/> of events that belong to the delegation itself rather than to an inner task.</summary>
    public required Guid Id { get; init; }

    /// <summary>The node the run executes on (rule A3).</summary>
    public required NodeId Node { get; init; }

    /// <summary>The operator on whose authority the objective runs.</summary>
    public required ActorIdentity Actor { get; init; }

    /// <summary>The operator's objective, in words.</summary>
    public required string Objective { get; init; }

    /// <summary>
    /// The caller-supplied key that makes starting this objective idempotent, or <c>null</c> when none
    /// was supplied. Unique per <see cref="Actor"/> among stored runs.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// What the operator limited the objective to, or <c>null</c> for no narrowing. Stored so a resumed run derives each role's
    /// envelope from the same request it started with, and can only narrow it further.
    /// </summary>
    public DelegationAuthorityRequest? Authority { get; init; }

    /// <summary>The change the operator asked the run to be able to make, or <c>null</c> for a diagnosis only. Stored so a resumed run needs no request from its caller.</summary>
    public DelegationRemediationRequest? Remediation { get; init; }

    /// <summary>Where the run stands or how it ended.</summary>
    public required DelegationStatus Status { get; init; }

    /// <summary>The objective's root envelope, derived from the operator's request and the role profile. Depth 0.</summary>
    public required AuthorityEnvelope RootEnvelope { get; init; }

    /// <summary>The roles that have been created, in pipeline order.</summary>
    public required IReadOnlyList<DelegationRoleRun> Roles { get; init; }

    /// <summary>The hash of the plan the Diagnostic role prepared, or <c>null</c> when it prepared none.</summary>
    public string? PlanHash { get; init; }

    /// <summary>History of the human approval of <see cref="PlanHash"/>. Never authority: see <see cref="DelegationApproval"/>.</summary>
    public DelegationApproval? Approval { get; init; }

    /// <summary>The step journal: one entry per side-effecting step whose intent was committed.</summary>
    public required IReadOnlyList<StepJournalEntry> Journal { get; init; }

    /// <summary>How many times the run was resumed after an interruption. Bounds crash loops.</summary>
    public int ResumeCount { get; init; }

    /// <summary>Why delegation was refused, when <see cref="Status"/> is <see cref="DelegationStatus.Denied"/>.</summary>
    public DelegationDenial? Denial { get; init; }

    /// <summary>A bounded failure explanation, or <c>null</c>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>When the run was created.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>When the run was last written.</summary>
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
