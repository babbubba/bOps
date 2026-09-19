// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// The change an operator wants a delegated run to be able to make: which Capability prepares the plan and with
/// what input, target, environment and blast radius (ADR-0030 section 2). Naming one is what makes the run able to
/// reach approval and Remediation at all; the model never chooses it, and it can only be one both the Diagnostic
/// and the Remediation role are allowed (ADR-0031), which is checked before any role starts.
/// </summary>
public sealed record DelegationRemediation
{
    /// <summary>Creates a remediation request.</summary>
    /// <param name="SkillId">The Skill whose Capability prepares and realizes the plan.</param>
    /// <param name="CapabilityName">The Capability that prepares the plan.</param>
    /// <param name="Request">The Capability's input, target, environment and blast radius.</param>
    /// <exception cref="ArgumentException">A name is blank.</exception>
    public DelegationRemediation(string SkillId, string CapabilityName, CapabilityRequest Request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SkillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(CapabilityName);
        ArgumentNullException.ThrowIfNull(Request);

        this.SkillId = SkillId;
        this.CapabilityName = CapabilityName;
        this.Request = Request;
    }

    /// <summary>The Skill whose Capability prepares and realizes the plan.</summary>
    public string SkillId { get; init; }

    /// <summary>The Capability that prepares the plan.</summary>
    public string CapabilityName { get; init; }

    /// <summary>The Capability's input, target, environment and blast radius.</summary>
    public CapabilityRequest Request { get; init; }
}

/// <summary>
/// What an operator asks of a delegated run (ADR-0030 sections 2 and 3): an objective in words, an optional
/// narrowing of the authority the role profiles grant, and optionally the change to prepare. The objective is the
/// operator's own text and reaches the two model roles as their goal; everything the roles pass on to each other is
/// structured data.
/// </summary>
public sealed record DelegationRequest
{
    /// <summary>Creates a delegation request.</summary>
    /// <param name="Objective">What the operator wants achieved, in words.</param>
    /// <param name="Authority">What the objective is limited to, or <c>null</c> for no narrowing beyond the role profiles. It can only narrow.</param>
    /// <param name="Remediation">The change to prepare, or <c>null</c> for a diagnosis only, which ends as a completed diagnosis (ADR-0030 section 2).</param>
    /// <exception cref="ArgumentException"><paramref name="Objective"/> is blank.</exception>
    public DelegationRequest(string Objective, DelegationAuthorityRequest? Authority = null, DelegationRemediation? Remediation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Objective);

        this.Objective = Objective;
        this.Authority = Authority ?? new DelegationAuthorityRequest();
        this.Remediation = Remediation;
    }

    /// <summary>What the operator wants achieved, in words.</summary>
    public string Objective { get; init; }

    /// <summary>What the objective is limited to. It can only narrow.</summary>
    public DelegationAuthorityRequest Authority { get; init; }

    /// <summary>The change to prepare, or <c>null</c> for a diagnosis only.</summary>
    public DelegationRemediation? Remediation { get; init; }
}
