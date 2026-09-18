// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

// The audit events of a delegated run (ADR-0030 section 8). Each carries the DelegationCorrelation
// block on the AuditEvent base type. For an event that belongs to the delegation itself rather than to
// an inner task, TaskId is the DelegationRun.Id; an event not tied to a step uses StepIndex -1, as
// SkillRunAuditEvent does. None of them records arguments, tool output or envelope contents: envelopes
// appear only as a hash plus the dimensions that were reduced or denied.

/// <summary>The lifecycle transition represented by a <see cref="DelegationLifecycleAuditEvent"/>.</summary>
public enum DelegationStage
{
    /// <summary>An operator asked for an objective to be delegated.</summary>
    Requested,

    /// <summary>A role started.</summary>
    RoleStarted,

    /// <summary>A role ended, whatever the outcome.</summary>
    RoleCompleted,

    /// <summary>A role's consumption was reconciled against the amount reserved for it from its parent.</summary>
    BudgetConsumed,

    /// <summary>The run reached a terminal status, or stopped waiting for an operator.</summary>
    Terminal,
}

/// <summary>
/// A delegated run was requested, a role started or ended, its budget was reconciled, or the run reached
/// an end state. Cancellation, budget exhaustion and deadline expiry are audited here as the distinct
/// <see cref="Status"/> values they are.
/// </summary>
public sealed record DelegationLifecycleAuditEvent : AuditEvent
{
    /// <summary>Which transition this is.</summary>
    public required DelegationStage Stage { get; init; }

    /// <summary>The run's status once the transition applied.</summary>
    public required DelegationStatus Status { get; init; }

    /// <summary>The role's status, for a role transition; <c>null</c> for a transition of the run itself.</summary>
    public DelegationRoleStatus? RoleStatus { get; init; }

    /// <summary>What the role consumed, for <see cref="DelegationStage.BudgetConsumed"/> and <see cref="DelegationStage.RoleCompleted"/>.</summary>
    public BudgetConsumption? Consumed { get; init; }

    /// <summary>A bounded failure explanation, absent otherwise.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// A child envelope was computed for a role, or delegation was refused. Records the hash of the parent
/// envelope and the dimensions that were narrowed or, for a denial, the dimension and reason
/// (<c>DelegationDenied</c>). For a granted reduction the <see cref="AuditEvent.Delegation"/> block carries
/// the new agent and the child's hash; for a denial no agent exists and it carries the parent's hash.
/// </summary>
public sealed record DelegationEnvelopeAuditEvent : AuditEvent
{
    /// <summary>The role the envelope was computed for.</summary>
    public required AgentRoleKind Role { get; init; }

    /// <summary>The hash of the envelope the child was reduced from.</summary>
    public required string ParentEnvelopeHash { get; init; }

    /// <summary>The dimensions in which the child is narrower than its parent. Empty for a denial.</summary>
    public required IReadOnlyList<EnvelopeDimension> ReducedDimensions { get; init; }

    /// <summary>Why delegation was refused, or <c>null</c> when the envelope was granted.</summary>
    public DelegationDenial? Denial { get; init; }
}

/// <summary>The write of a step journal entry that a <see cref="DelegationJournalAuditEvent"/> records.</summary>
public enum JournalPhase
{
    /// <summary>The intent, committed before the step executes.</summary>
    Intent,

    /// <summary>The outcome, committed after the step ends.</summary>
    Outcome,
}

/// <summary>
/// A step's intent or outcome was committed to the journal (ADR-0030 section 7).
/// <see cref="AuditEvent.StepIndex"/> is the step's index within the approved plan.
/// </summary>
public sealed record DelegationJournalAuditEvent : AuditEvent
{
    /// <summary>Which of the two durable writes this records.</summary>
    public required JournalPhase Phase { get; init; }

    /// <summary>The tool the step calls.</summary>
    public required string Tool { get; init; }

    /// <summary>The hash of the step's canonical arguments (<see cref="DelegationHasher.ComputeArgumentsHash"/>); never the arguments.</summary>
    public required string ArgumentsHash { get; init; }

    /// <summary>How the step ended, for <see cref="JournalPhase.Outcome"/>; <c>null</c> for an intent.</summary>
    public StepOutcomeKind? Outcome { get; init; }

    /// <summary>The step's own post-action verification, for <see cref="JournalPhase.Outcome"/> when one ran.</summary>
    public VerificationStatus? Verification { get; init; }
}

/// <summary>
/// An ambiguous step was settled, escalated to an operator, or its run was abandoned (ADR-0030 section 7).
/// <see cref="AuditEvent.StepIndex"/> is the step concerned, or -1 when an operator abandons the whole run.
/// </summary>
public sealed record DelegationReconciliationAuditEvent : AuditEvent
{
    /// <summary>What was done.</summary>
    public required ReconciliationAction Action { get; init; }

    /// <summary>Who did it: the runtime for a verification, an administrator for an operator action.</summary>
    public required ActorIdentity ResolvedBy { get; init; }

    /// <summary>The verification that informed the action, when one ran.</summary>
    public VerificationStatus? Verification { get; init; }

    /// <summary>An operator's note, when one was given.</summary>
    public string? Note { get; init; }
}
