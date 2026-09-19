// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>How one role treats one envelope dimension (ADR-0031 section 1).</summary>
internal enum EnvelopeRequirement
{
    /// <summary>Must be non-empty after reduction; otherwise delegation is refused (<c>DelegationDenied</c>).</summary>
    Required,

    /// <summary>May be empty. Empty means nothing is permitted in that dimension, which is not a refusal.</summary>
    Optional,

    /// <summary>The role structurally does not use it: forced to empty (or zero) whatever the parent, profile and request say.</summary>
    NotApplicable,
}

/// <summary>
/// The per-role table of ADR-0031, as code. It is the single definition: a role profile refuses to be
/// built when it grants a dimension that is not applicable to its role, and the runtime's reduction
/// reads the same table, so the documentation, the profile check and the enforcement cannot disagree.
/// Changing a cell needs a new ADR (ADR-0031, Consequences).
/// </summary>
/// <remarks>
/// Internal on purpose: the published SDK carries the profile contracts, not the table. The runtime is an
/// <c>InternalsVisibleTo</c> friend, and the policy loader learns of a malformed profile from
/// <see cref="RoleProfile"/>'s own constructor.
/// </remarks>
internal static class RoleRequirements
{
    /// <summary>The delegation depth of a role's envelope. The orchestrator's root envelope is depth 0, and roles cannot delegate further (ADR-0030 section 2).</summary>
    internal const int RoleDepth = 1;

    /// <summary>The roles of the fixed pipeline, in the order they run (ADR-0030 section 2).</summary>
    internal static IReadOnlyList<AgentRoleKind> Pipeline { get; } =
        [AgentRoleKind.Discovery, AgentRoleKind.Diagnostic, AgentRoleKind.Remediation, AgentRoleKind.Verification];

    /// <summary>Whether the role only ever reads. Everything but Remediation does (ADR-0030 section 2).</summary>
    internal static bool IsReadOnly(AgentRoleKind role)
    {
        ThrowIfUndefined(role);
        return role != AgentRoleKind.Remediation;
    }

    /// <summary>The highest risk the role may ever reach, whatever its parent, profile and request allow: <see cref="RiskLevel.Read"/> for a read-only role.</summary>
    internal static RiskLevel RiskCap(AgentRoleKind role) => IsReadOnly(role) ? RiskLevel.Read : RiskLevel.Critical;

    /// <summary>The lowest risk ceiling that still lets the role do its job: a Remediation role with a ceiling of <see cref="RiskLevel.Read"/> could execute nothing.</summary>
    internal static RiskLevel RiskFloor(AgentRoleKind role) => IsReadOnly(role) ? RiskLevel.Read : RiskLevel.Low;

    /// <summary>How <paramref name="role"/> treats <paramref name="dimension"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The role or the dimension is not a defined value, or the dimension is <see cref="EnvelopeDimension.Profile"/>, which is not a field of the envelope.</exception>
    internal static EnvelopeRequirement Of(AgentRoleKind role, EnvelopeDimension dimension)
    {
        ThrowIfUndefined(role);

        return dimension switch
        {
            // Discovery and Verification never invoke a Capability; Diagnostic only may, to prepare a plan
            // (and ends as a completed diagnosis when it has none); Remediation executes an approved one.
            EnvelopeDimension.Skills or EnvelopeDimension.Capabilities => role switch
            {
                AgentRoleKind.Remediation => EnvelopeRequirement.Required,
                AgentRoleKind.Diagnostic => EnvelopeRequirement.Optional,
                _ => EnvelopeRequirement.NotApplicable,
            },

            // Only Discovery and Diagnostic call the model (ADR-0030 section 2).
            EnvelopeDimension.Tokens => role is AgentRoleKind.Discovery or AgentRoleKind.Diagnostic
                ? EnvelopeRequirement.Required
                : EnvelopeRequirement.NotApplicable,

            // Absent means unrestricted; two windows that do not overlap are refused, which is not emptiness.
            EnvelopeDimension.MaintenanceWindow => EnvelopeRequirement.Optional,

            EnvelopeDimension.Originator
                or EnvelopeDimension.Depth
                or EnvelopeDimension.Tools
                or EnvelopeDimension.Risk
                or EnvelopeDimension.BlastRadius
                or EnvelopeDimension.Targets
                or EnvelopeDimension.Environments
                or EnvelopeDimension.Steps
                or EnvelopeDimension.Deadline => EnvelopeRequirement.Required,

            _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "Not a dimension of an authority envelope."),
        };
    }

    private static void ThrowIfUndefined(AgentRoleKind role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role kind.");
        }
    }
}
