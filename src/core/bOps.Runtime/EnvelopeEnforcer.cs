// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>Why the envelope refused a step, and in which dimension when one is to blame.</summary>
/// <param name="Dimension">The dimension whose limit the step exceeded, or <c>null</c> when the scope itself is unusable (no agent, no envelope, an envelope that is not the one recorded).</param>
/// <param name="Reason">In words for the operator, the model and the audit log. Names the role and what the step asked for, which the model already knows; never lists what the envelope does allow.</param>
internal sealed record EnvelopeRefusal(EnvelopeDimension? Dimension, string Reason);

/// <summary>
/// What an authority envelope says about one step (ADR-0030 section 3, ADR-0031 section 4): the check the runner
/// makes in the single step-execution path before policy is evaluated. It is a pure function of the scope, the
/// step and the time, and it can only refuse: policy remains the sole source of <c>Automatic</c> and
/// <c>Approval</c>, so the effective decision is the more restrictive of the two.
/// </summary>
/// <remarks>
/// <para>
/// The checks run in one fixed order and the first refusal is reported: the scope itself, the shape of the step,
/// the tool, the risk, the window, then, for a step that carries a Skill scope, the Skill, the Capability, the
/// blast radius, the target and the environment.
/// </para>
/// <para>
/// A tool declares no target or environment, so those are matched only for a step that carries a Skill scope
/// (the evidence calls of a Capability's preparation and the steps of an approved plan). A plain Read call is
/// bound by the tool set, the <c>Read</c> ceiling, the window and the budgets; what a Read tool touches stays
/// bound by its own argument validation and path policy (ADR-0031 section 4, rules S2 and S11).
/// </para>
/// <para>
/// Budgets are not checked here. Using up a granted budget or reaching the deadline is not a denial of authority:
/// it ends the run as <c>BudgetExceeded</c> or <c>DeadlineExceeded</c> (ADR-0031 section 3), which the
/// orchestrator decides (V1.2-E).
/// </para>
/// </remarks>
internal static class EnvelopeEnforcer
{
    /// <summary>Checks one step about to be authorized.</summary>
    /// <param name="scope">The delegated agent and envelope the step runs under.</param>
    /// <param name="actor">The operator the step is executed for; it must be the envelope's originator.</param>
    /// <param name="manifest">The tool the step calls.</param>
    /// <param name="skill">The Skill scope the step carries, or <c>null</c> for a plain model-proposed call.</param>
    /// <param name="now">The current time, compared with the maintenance window.</param>
    /// <returns><c>null</c> when the envelope has nothing against the step, otherwise why it is refused.</returns>
    internal static EnvelopeRefusal? CheckStep(
        DelegatedExecutionScope scope, ActorIdentity actor, ToolManifest manifest, SkillExecutionScope? skill, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(manifest);

        if (CheckScope(scope, actor, out var envelope, out var role) is { } unusable)
        {
            return unusable;
        }

        // Only the steps of an approved plan act, and they carry their Skill scope. Without one there is nothing
        // to match a target or an environment against, so a call above Read is not a step of any plan.
        if (manifest.Risk != RiskLevel.Read && skill is null)
        {
            return Refuse(
                EnvelopeDimension.Skills,
                $"Authority envelope: '{manifest.Name}' has a risk above Read and is not a step of a Skill plan; the {role} role "
                + "executes non-Read tools only as steps of an approved plan, which carry a Skill scope.");
        }

        if (!Contains(envelope.AllowedTools, manifest.Name))
        {
            return Refuse(EnvelopeDimension.Tools, $"Authority envelope: tool '{manifest.Name}' is not permitted for the {role} role.");
        }

        // Critical is never executed, whatever a ceiling says (rule S3). A read-only role is held to Read even by
        // an envelope that says otherwise: the reduction never builds one, but a value handed in by mistake must
        // not turn a role that only looks into one that acts.
        if (manifest.Risk == RiskLevel.Critical)
        {
            return Refuse(EnvelopeDimension.Risk, $"Authority envelope: tool '{manifest.Name}' has risk Critical, which is never executed.");
        }

        var roleCap = RoleRequirements.RiskCap(role);
        var ceiling = envelope.MaxRisk < roleCap ? envelope.MaxRisk : roleCap;
        if (manifest.Risk > ceiling)
        {
            return Refuse(
                EnvelopeDimension.Risk,
                $"Authority envelope: tool '{manifest.Name}' has risk {manifest.Risk}, above what the {role} role may reach.");
        }

        if (CheckWindow(envelope, role, now) is { } closed)
        {
            return closed;
        }

        return skill is null ? null : CheckSkillScope(envelope, role, skill);
    }

    /// <summary>
    /// Checks a Capability about to be prepared by a delegated agent: the Skill, the Capability, the blast radius, the
    /// target, the environment and the window. The Skills and Capabilities dimensions are about what an agent may
    /// prepare, and a Capability that makes no evidence call would never meet <see cref="CheckStep"/>, so its code is
    /// refused here, before it runs.
    /// </summary>
    /// <param name="scope">The delegated agent and envelope the preparation runs under.</param>
    /// <param name="actor">The operator the preparation is executed for; it must be the envelope's originator.</param>
    /// <param name="skill">The Skill scope the preparation runs in.</param>
    /// <param name="now">The current time, compared with the maintenance window.</param>
    internal static EnvelopeRefusal? CheckPreparation(
        DelegatedExecutionScope scope, ActorIdentity actor, SkillExecutionScope skill, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(skill);

        if (CheckScope(scope, actor, out var envelope, out var role) is { } unusable)
        {
            return unusable;
        }

        return CheckWindow(envelope, role, now) ?? CheckSkillScope(envelope, role, skill);
    }

    /// <summary>
    /// The scope must be usable before anything in it is trusted: an agent that acts, an envelope, the envelope the
    /// delegation records for that agent, at the fixed role depth, granted to the operator the step runs for.
    /// </summary>
    private static EnvelopeRefusal? CheckScope(
        DelegatedExecutionScope scope, ActorIdentity actor, out AuthorityEnvelope envelope, out AgentRoleKind role)
    {
        envelope = null!;
        role = default;

        var agent = scope.Correlation.Agent;
        if (agent is null)
        {
            return Refuse(null, "Authority envelope: a step is executed by an agent, and the delegation names none.");
        }

        role = agent.Role;
        if (scope.Envelope is null)
        {
            return Refuse(null, $"Authority envelope: the {role} role carries no authority envelope; a delegated step never runs without one.");
        }

        if (!string.Equals(DelegationHasher.ComputeEnvelopeHash(scope.Envelope), scope.Correlation.EnvelopeHash, StringComparison.Ordinal))
        {
            return Refuse(
                null,
                $"Authority envelope: the envelope supplied to the {role} role does not match the one the delegation records for it.");
        }

        if (scope.Envelope.Depth != RoleRequirements.RoleDepth)
        {
            return Refuse(
                EnvelopeDimension.Depth,
                $"Authority envelope: the {role} role's envelope is not at the depth roles run at; roles cannot delegate further.");
        }

        // The display name is cosmetic, as it is when an envelope is hashed.
        if (!string.Equals(scope.Envelope.Originator.Kind, actor.Kind, StringComparison.Ordinal)
            || !string.Equals(scope.Envelope.Originator.Id, actor.Id, StringComparison.Ordinal))
        {
            return Refuse(
                EnvelopeDimension.Originator,
                $"Authority envelope: the {role} role's envelope was granted to another operator than the one this step runs for.");
        }

        envelope = scope.Envelope;
        return null;
    }

    private static EnvelopeRefusal? CheckWindow(AuthorityEnvelope envelope, AgentRoleKind role, DateTimeOffset now) =>
        envelope.Window is { } window && (now < window.StartUtc || now >= window.EndUtc)
            ? Refuse(EnvelopeDimension.MaintenanceWindow, $"Authority envelope: the {role} role is outside its maintenance window.")
            : null;

    private static EnvelopeRefusal? CheckSkillScope(AuthorityEnvelope envelope, AgentRoleKind role, SkillExecutionScope skill)
    {
        if (!Contains(envelope.AllowedSkills, skill.SkillId))
        {
            return Refuse(EnvelopeDimension.Skills, $"Authority envelope: Skill '{skill.SkillId}' is not permitted for the {role} role.");
        }

        if (!Contains(envelope.AllowedCapabilities, skill.CapabilityName))
        {
            return Refuse(
                EnvelopeDimension.Capabilities,
                $"Authority envelope: Capability '{skill.CapabilityName}' is not permitted for the {role} role.");
        }

        if (skill.BlastRadius > envelope.MaxBlastRadius)
        {
            return Refuse(
                EnvelopeDimension.BlastRadius,
                $"Authority envelope: a blast radius of {skill.BlastRadius} is above what the {role} role may reach.");
        }

        if (!Contains(envelope.AllowedTargets, skill.Target))
        {
            return Refuse(EnvelopeDimension.Targets, $"Authority envelope: the target of this step is not permitted for the {role} role.");
        }

        if (!Contains(envelope.AllowedEnvironments, skill.Environment))
        {
            return Refuse(
                EnvelopeDimension.Environments,
                $"Authority envelope: the environment of this step is not permitted for the {role} role.");
        }

        return null;
    }

    // Exact, ordinal, case-sensitive: an envelope member is a name, never a pattern (ADR-0030 section 3). An empty
    // set contains nothing, so it permits nothing; it never means "unrestricted".
    private static bool Contains(IReadOnlyList<string> members, string name) =>
        members.Contains(name, StringComparer.Ordinal);

    private static EnvelopeRefusal Refuse(EnvelopeDimension? dimension, string reason) => new(dimension, reason);
}
