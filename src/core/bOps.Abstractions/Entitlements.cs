// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>Whether trusted host configuration requires entitlement evaluation for an operation.</summary>
public enum EntitlementApplicability
{
    /// <summary>Fail-closed default: entitlement is required unless trusted host code explicitly marks the operation otherwise.</summary>
    Governed,

    /// <summary>Trusted host code has explicitly marked entitlement as not applicable; no provider is consulted.</summary>
    NotGoverned,
}

/// <summary>The host-stamped entitlement requirement attached to a resolved execution context.</summary>
public sealed record EntitlementRequirement(EntitlementApplicability Applicability);

/// <summary>Opaque correlation value for one entitlement evaluation attempt; it is not authorization proof.</summary>
public readonly record struct RequestBinding(string Value);

/// <summary>Neutral constraints requested for an entitlement evaluation.</summary>
public sealed record EntitlementConstraints(IReadOnlyDictionary<string, string> Values);

/// <summary>A requested resource quantity evaluated by the entitlement provider.</summary>
public sealed record EntitlementResourceRequest(string Resource, decimal Amount, string Unit);

/// <summary>Requested freshness context for an entitlement evaluation.</summary>
public sealed record EntitlementValidityRequest(DateTimeOffset RequestedAtUtc, TimeSpan MaximumAge);

/// <summary>A neutral request for an entitlement decision for one invocation.</summary>
public sealed record EntitlementRequest(
    RequestBinding Binding,
    string Subject,
    string Installation,
    string Feature,
    string? SkillId,
    string? SkillVersion,
    string Capability,
    EntitlementResourceRequest? Resource,
    NodeId Node,
    string? Target,
    EntitlementValidityRequest Validity,
    EntitlementConstraints? Constraints);

/// <summary>The provider's final interpreted result for one entitlement request.</summary>
public enum EntitlementDecisionKind
{
    /// <summary>Fail-closed default and denied entitlement result.</summary>
    Denied,

    /// <summary>The provider permits this request subject to the returned validity and constraints.</summary>
    Allowed,
}

/// <summary>Neutral reason categories for an entitlement decision.</summary>
public enum EntitlementReasonCode
{
    /// <summary>Unspecified reason; consumers must not treat it as an allow explanation.</summary>
    Unknown,

    /// <summary>The requested operation is permitted.</summary>
    Permitted,

    /// <summary>No applicable entitlement permits the request.</summary>
    NotEntitled,

    /// <summary>A requested resource limit is not satisfied.</summary>
    LimitExceeded,

    /// <summary>The request is malformed or cannot be evaluated.</summary>
    InvalidRequest,

    /// <summary>The requested entitlement authority is unavailable.</summary>
    Unavailable,
}

/// <summary>Neutral category identifying the source that evaluated the request.</summary>
public enum EntitlementSourceCategory
{
    /// <summary>Unspecified source category.</summary>
    Unknown,

    /// <summary>A source local to the executing node.</summary>
    Local,

    /// <summary>A source accessed through a remote boundary.</summary>
    Remote,
}

/// <summary>The provider's decision and effective neutral constraints for a single evaluation attempt.</summary>
public sealed record EntitlementDecision(
    RequestBinding Binding,
    EntitlementDecisionKind Result,
    EntitlementReasonCode Reason,
    EntitlementSourceCategory Source,
    string AuthorityId,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    EntitlementConstraints? Constraints);

/// <summary>Evaluates entitlement for the current request on the node to which this service is scoped.</summary>
public interface IEntitlementService
{
    /// <summary>Returns the provider's interpreted decision for one fresh request.</summary>
    /// <param name="request">Neutral dimensions for this evaluation attempt.</param>
    /// <param name="ct">Cancellation token for the evaluation.</param>
    Task<EntitlementDecision> EvaluateAsync(EntitlementRequest request, CancellationToken ct = default);
}
