// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>
/// The change an operator asked a delegated run to be able to make: which Capability prepares the plan and with what input,
/// target, environment and blast radius (ADR-0030 sections 2 and 7). Stored with the run so a run that is resumed after a
/// restart knows what it was asked to do without the caller having to supply it again.
/// </summary>
public sealed record DelegationRemediationRequest
{
    /// <summary>Creates a remediation request.</summary>
    /// <param name="SkillId">The Skill whose Capability prepares and realizes the plan.</param>
    /// <param name="CapabilityName">The Capability that prepares the plan.</param>
    /// <param name="Request">The Capability's input, target, environment and blast radius.</param>
    /// <exception cref="ArgumentException">A name is blank.</exception>
    public DelegationRemediationRequest(string SkillId, string CapabilityName, CapabilityRequest Request)
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
