// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// Why a host's policy is what it is (ADR-0044 section 4): no file (the built-in safe default, which grants no delegation
/// profile), a file that loaded, or a file that failed to load (everything forbidden, no profile). The host keeps the loader's
/// message for its log and the local CLI; it is never part of this state and never sent over HTTP.
/// </summary>
public enum PolicyLoadState
{
    /// <summary>No policy file exists; the built-in safe default applies.</summary>
    NoFile = 0,

    /// <summary>The policy file loaded.</summary>
    Loaded = 1,

    /// <summary>A policy file exists but failed to load; the whole policy is forbidden until it is fixed.</summary>
    LoadFailed = 2,
}

/// <summary>The readiness of one role for a request shape (ADR-0044 section 6.2). Exactly four states.</summary>
public enum RoleReadinessState
{
    /// <summary>The role's profile is usable for this shape.</summary>
    Ready,

    /// <summary>The role is required and has no profile.</summary>
    Missing,

    /// <summary>The role is required and its profile or configuration is present but not usable ("Not usable"); the reason code says why.</summary>
    Malformed,

    /// <summary>The request shape does not require the role.</summary>
    NotRequired,
}

/// <summary>One role's readiness.</summary>
/// <param name="Role">The role.</param>
/// <param name="State">Its state.</param>
/// <param name="Dimension">The dimension that owns a non-ready result; <c>null</c> for ready and not required.</param>
/// <param name="ReasonCode">The stable snake_case reason clients branch on (see <see cref="DelegationReadinessEvaluator"/>).</param>
/// <param name="Reason">Bounded English text for a person, never parsed; <c>null</c> for ready and not required.</param>
public sealed record RoleReadiness(AgentRoleKind Role, RoleReadinessState State, EnvelopeDimension? Dimension, string ReasonCode, string? Reason);

/// <summary>
/// Whether the role profiles a request shape requires are usable (ADR-0044 sections 5 and 6): profile / role-infrastructure
/// readiness, not a promise that a particular Skill, Capability, target, environment or input will be accepted at submit.
/// </summary>
/// <param name="Remediation">The request shape evaluated.</param>
/// <param name="Ready">True iff every role that is not <see cref="RoleReadinessState.NotRequired"/> is <see cref="RoleReadinessState.Ready"/>. Clients use this and never recompute it.</param>
/// <param name="Policy">The host's policy load state.</param>
/// <param name="Roles">All four roles, once each, in pipeline order.</param>
/// <param name="ProfileDriftCount">The number of drift items; informational, it never affects <paramref name="Ready"/>.</param>
/// <param name="EvaluatedAtUtc">The instant the evaluation used.</param>
public sealed record DelegationReadiness(
    bool Remediation, bool Ready, PolicyLoadState Policy, IReadOnlyList<RoleReadiness> Roles, int ProfileDriftCount, DateTimeOffset EvaluatedAtUtc);

/// <summary>
/// The one readiness evaluator (ADR-0044 section 5), which the API and the CLI call and never re-implement. It is a projection
/// of the reducer's own loop: the required roles come from the canonical selector, and each required role's state is the
/// result <see cref="EnvelopeReducer.ReduceRequiredRoles"/> gives it under a non-narrowing request, so a role reported not ready
/// is refused by the real start of that shape with the same dimension (and, for <c>profile_missing</c> and
/// <c>reduction_denied</c>, the same reason).
/// </summary>
public static class DelegationReadinessEvaluator
{
    /// <summary>Reason code of a ready role.</summary>
    public const string ReadyCode = "ready";

    /// <summary>Reason code of a role the request shape does not require.</summary>
    public const string NotRequiredCode = "not_required";

    /// <summary>Reason code of a required role with no profile.</summary>
    public const string ProfileMissingCode = "profile_missing";

    /// <summary>Reason code of a required role when the policy file failed to load.</summary>
    public const string PolicyLoadFailedCode = "policy_load_failed";

    /// <summary>Reason code of a required role whose source returned a profile for another role.</summary>
    public const string ProfileForOtherRoleCode = "profile_for_other_role";

    /// <summary>Reason code of a required role whose well-formed profile the reducer refuses.</summary>
    public const string ReductionDeniedCode = "reduction_denied";

    /// <summary>The fixed reason of <see cref="PolicyLoadFailedCode"/>. The loader's own message never leaves the host.</summary>
    public const string PolicyLoadFailedReason = "policy.yaml could not be loaded; delegation is off until it is fixed (see the host log)";

    private const int MaximumReasonLength = 500;

    /// <summary>Evaluates readiness for a request shape at an explicit instant.</summary>
    /// <param name="profiles">The host's role profile source.</param>
    /// <param name="policy">The host's policy load state.</param>
    /// <param name="availableTools">The host's available tool manifests, for the drift count only.</param>
    /// <param name="remediation">The request shape: whether the request names a change.</param>
    /// <param name="now">The instant to evaluate at.</param>
    public static DelegationReadiness Evaluate(
        IRoleProfileSource profiles, PolicyLoadState policy, IReadOnlyList<ToolManifest> availableTools, bool remediation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(availableTools);

        var reduced = EnvelopeReducer.ReduceRequiredRoles(profiles, remediation, new DelegationAuthorityRequest(), ActorIdentity.RuntimeSystem, now);
        var byRole = reduced.Roles.ToDictionary(role => role.Role);

        var roles = new List<RoleReadiness>(RoleRequirements.Pipeline.Count);
        foreach (var role in RoleRequirements.Pipeline)
        {
            roles.Add(byRole.TryGetValue(role, out var authority)
                ? Readiness(authority, policy)
                : new RoleReadiness(role, RoleReadinessState.NotRequired, null, NotRequiredCode, null));
        }

        var ready = roles.All(role => role.State is RoleReadinessState.Ready or RoleReadinessState.NotRequired);
        var drift = ProfileDrift.Evaluate(profiles, policy, availableTools, now).Count;
        return new DelegationReadiness(remediation, ready, policy, roles, drift, now);
    }

    private static RoleReadiness Readiness(RoleAuthority authority, PolicyLoadState policy)
    {
        var role = authority.Role;
        var denial = authority.Reduction.Denial;
        return authority.Kind switch
        {
            RoleAuthorityKind.Reduced when denial is null =>
                new RoleReadiness(role, RoleReadinessState.Ready, null, ReadyCode, null),
            RoleAuthorityKind.Reduced =>
                new RoleReadiness(role, RoleReadinessState.Malformed, denial.Dimension, ReductionDeniedCode, Bounded(denial.Reason)),
            RoleAuthorityKind.WrongProfile =>
                new RoleReadiness(role, RoleReadinessState.Malformed, EnvelopeDimension.Profile, ProfileForOtherRoleCode, Bounded(denial!.Reason)),
            _ when policy == PolicyLoadState.LoadFailed =>
                new RoleReadiness(role, RoleReadinessState.Malformed, EnvelopeDimension.Profile, PolicyLoadFailedCode, PolicyLoadFailedReason),
            _ =>
                new RoleReadiness(role, RoleReadinessState.Missing, EnvelopeDimension.Profile, ProfileMissingCode, Bounded(denial!.Reason)),
        };
    }

    private static string Bounded(string reason) => reason.Length <= MaximumReasonLength ? reason : reason[..MaximumReasonLength];
}
