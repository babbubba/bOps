// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Runtime;

namespace bOps.Api;

/// <summary>
/// The body of <c>GET /api/delegations/readiness</c> (ADR-0044 section 6.2): the runtime evaluator's answer, with every enum as the
/// fixed string the contract names. Nothing here decides anything; clients use <see cref="Ready"/> and never recompute it.
/// </summary>
internal sealed record DelegationReadinessView(
    bool Remediation,
    bool Ready,
    string Policy,
    IReadOnlyList<RoleReadinessView> Roles,
    int ProfileDriftCount,
    DateTimeOffset EvaluatedAtUtc)
{
    public static DelegationReadinessView From(DelegationReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        return new DelegationReadinessView(
            readiness.Remediation,
            readiness.Ready,
            readiness.Policy switch
            {
                PolicyLoadState.NoFile => "noFile",
                PolicyLoadState.Loaded => "loaded",
                _ => "loadFailed",
            },
            [.. readiness.Roles.Select(role => new RoleReadinessView(
                role.Role.ToString(),
                role.State switch
                {
                    RoleReadinessState.Ready => "ready",
                    RoleReadinessState.Missing => "missing",
                    RoleReadinessState.NotRequired => "notRequired",
                    _ => "malformed",
                },
                role.Dimension?.ToString(),
                role.ReasonCode,
                role.Reason))],
            readiness.ProfileDriftCount,
            readiness.EvaluatedAtUtc);
    }
}

/// <summary>One role's readiness: <c>state</c> is <c>ready</c>, <c>missing</c>, <c>malformed</c> or <c>notRequired</c>; <c>reasonCode</c> is snake_case.</summary>
internal sealed record RoleReadinessView(string Role, string State, string? Dimension, string ReasonCode, string? Reason);
