// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>
/// What a human is asked to approve before a delegated run changes anything (ADR-0030 sections 2 and 5): one
/// immutable plan, identified by its canonical hash, with the Findings and the Skill scope it came from. It is
/// what the operator sees, never something they edit: a material change to the plan is a different hash and a
/// different request.
/// </summary>
/// <remarks>
/// Host-facing, like <see cref="IApprovalProvider"/>: a Tool or Skill package does not implement it and cannot ask
/// for an approval of its own plan. The runtime builds the request, so the hash is always the plan's own.
/// </remarks>
public sealed record PlanApprovalRequest
{
    /// <summary>Creates a plan approval request.</summary>
    /// <param name="DelegationId">The delegated run the plan belongs to.</param>
    /// <param name="PlanHash">The <see cref="ExecutionPlanHasher.ComputeHash"/> of <paramref name="Plan"/>. The approval binds to exactly this value.</param>
    /// <param name="Plan">The immutable plan the human is asked to approve.</param>
    /// <param name="SkillId">The Skill whose Capability prepared the plan.</param>
    /// <param name="CapabilityName">The Capability that prepared the plan.</param>
    /// <param name="Target">The target label the plan acts on.</param>
    /// <param name="Environment">The environment label the plan acts in.</param>
    /// <param name="BlastRadius">The declared scale of the plan's effect.</param>
    /// <param name="Findings">What the diagnosis found, each citing the evidence it rests on, so the operator can judge why this plan is proposed.</param>
    /// <exception cref="ArgumentException">The delegation id is empty, a name or label is blank, or <paramref name="PlanHash"/> is not the hash of <paramref name="Plan"/>.</exception>
    public PlanApprovalRequest(
        Guid DelegationId,
        string PlanHash,
        ExecutionPlan Plan,
        string SkillId,
        string CapabilityName,
        string Target,
        string Environment,
        BlastRadius BlastRadius,
        IReadOnlyList<Finding> Findings)
    {
        if (DelegationId == Guid.Empty)
        {
            throw new ArgumentException("A delegation must have a non-empty id.", nameof(DelegationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(PlanHash);
        ArgumentNullException.ThrowIfNull(Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(SkillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(CapabilityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(Target);
        ArgumentException.ThrowIfNullOrWhiteSpace(Environment);
        ArgumentNullException.ThrowIfNull(Findings);

        // The hash is what the approval is bound to, so a request whose hash is not its plan's own would ask the
        // human to approve one thing and authorize another.
        if (!string.Equals(PlanHash, ExecutionPlanHasher.ComputeHash(Plan), StringComparison.Ordinal))
        {
            throw new ArgumentException("The plan hash must be the canonical hash of the plan.", nameof(PlanHash));
        }

        this.DelegationId = DelegationId;
        this.PlanHash = PlanHash;
        this.Plan = Plan;
        this.SkillId = SkillId;
        this.CapabilityName = CapabilityName;
        this.Target = Target;
        this.Environment = Environment;
        this.BlastRadius = BlastRadius;
        this.Findings = Findings;
    }

    /// <summary>The delegated run the plan belongs to.</summary>
    public Guid DelegationId { get; init; }

    /// <summary>The canonical hash of <see cref="Plan"/>, which the approval binds to.</summary>
    public string PlanHash { get; init; }

    /// <summary>The immutable plan the human is asked to approve.</summary>
    public ExecutionPlan Plan { get; init; }

    /// <summary>The Skill whose Capability prepared the plan.</summary>
    public string SkillId { get; init; }

    /// <summary>The Capability that prepared the plan.</summary>
    public string CapabilityName { get; init; }

    /// <summary>The target label the plan acts on.</summary>
    public string Target { get; init; }

    /// <summary>The environment label the plan acts in.</summary>
    public string Environment { get; init; }

    /// <summary>The declared scale of the plan's effect.</summary>
    public BlastRadius BlastRadius { get; init; }

    /// <summary>What the diagnosis found, each citing the evidence it rests on.</summary>
    public IReadOnlyList<Finding> Findings { get; init; }

    /// <summary>
    /// The authority the plan will run under: the envelope the Remediation role will be granted, as it stands when the human is
    /// asked. What is approved is the plan and that authority together, so a surface shows both. <c>null</c> for a request built by
    /// a caller that has no envelope to show. Its budget and deadline are those the role will then be given, counted from when it starts.
    /// </summary>
    public AuthorityEnvelope? Authority { get; init; }
}

/// <summary>
/// Asks a human to approve or reject one plan by its hash (ADR-0030 section 5). Approvals are human only: the
/// runtime never calls this on behalf of an agent, refuses a decision whose <see cref="ApprovalDecision.Actor"/>
/// is an agent or the runtime itself, and never persists a decision across a restart, so a resumed run asks again.
/// The runtime, not the provider, binds the decision to the plan hash. The per-step approval of a tool that
/// requires one stays with <see cref="IApprovalProvider"/>; this is the decision about the plan as a whole.
/// </summary>
public interface IPlanApprovalProvider
{
    /// <summary>Asks an operator to approve or reject a plan.</summary>
    /// <param name="request">The plan, its hash and the findings behind it.</param>
    /// <param name="ct">Cancelled if the request should be abandoned.</param>
    Task<ApprovalDecision> RequestPlanApprovalAsync(PlanApprovalRequest request, CancellationToken ct = default);
}
