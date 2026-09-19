// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>
/// What one role of a delegated run may do, as configured by the operator (ADR-0030 section 1, ADR-0031
/// section 5). It is the role's own contribution to the intersection that becomes its
/// <see cref="AuthorityEnvelope"/>: the parent's, the profile's and the request's, dimension by dimension.
/// It is data, never code and never something a plugin contributes.
/// </summary>
/// <remarks>
/// A dimension that is not applicable to the role (ADR-0031 section 1: Skills and Capabilities for
/// Discovery and Verification, tokens for Remediation and Verification) must be empty or zero. A profile
/// that grants one is malformed and cannot be constructed, so a source reports it as "no profile" and
/// delegation is refused as a whole; it is never silently ignored. A read-only role's risk ceiling is
/// capped at <see cref="RiskLevel.Read"/> by the runtime whatever this says. Time is a maximum duration,
/// because an absolute deadline can only be derived once the run starts. An empty set means nothing is
/// permitted in that dimension, never "unrestricted".
/// </remarks>
public sealed record RoleProfile
{
    /// <summary>Creates a role profile.</summary>
    /// <param name="Role">The role this profile is for.</param>
    /// <param name="AllowedSkills">Exact Skill ids the role may use. Must be empty for a role that does not use Skills.</param>
    /// <param name="AllowedCapabilities">Exact Capability names the role may prepare or execute. Must be empty for a role that does not use Capabilities.</param>
    /// <param name="AllowedTools">Exact tool names the role may invoke through the runtime.</param>
    /// <param name="MaxRisk">The highest risk the role may reach. <see cref="RiskLevel.Critical"/> stays forbidden by policy whatever this says.</param>
    /// <param name="MaxBlastRadius">The largest blast radius the role may reach.</param>
    /// <param name="AllowedTargets">Exact target labels the role may act on.</param>
    /// <param name="AllowedEnvironments">Exact environment labels the role may act in.</param>
    /// <param name="MaxSteps">The steps the role may take.</param>
    /// <param name="MaxTokens">The model tokens the role may consume. Must be zero for a role that makes no model call.</param>
    /// <param name="MaxDuration">How long the role may run, counted from the moment it starts.</param>
    /// <param name="Window">When the role may act, or <c>null</c> for no time restriction beyond its deadline.</param>
    /// <exception cref="ArgumentException">A set contains a null, blank, padded or wildcard member, or a dimension that is not applicable to <paramref name="Role"/> is granted.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The role or a ceiling is not a defined value, an amount is negative, or the duration is not positive.</exception>
    public RoleProfile(
        AgentRoleKind Role,
        IReadOnlyList<string> AllowedSkills,
        IReadOnlyList<string> AllowedCapabilities,
        IReadOnlyList<string> AllowedTools,
        RiskLevel MaxRisk,
        BlastRadius MaxBlastRadius,
        IReadOnlyList<string> AllowedTargets,
        IReadOnlyList<string> AllowedEnvironments,
        int MaxSteps,
        int MaxTokens,
        TimeSpan MaxDuration,
        MaintenanceWindow? Window = null)
    {
        if (!Enum.IsDefined(Role))
        {
            throw new ArgumentOutOfRangeException(nameof(Role), Role, "Unknown role kind.");
        }

        if (!Enum.IsDefined(MaxRisk))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRisk), MaxRisk, "Unknown risk level.");
        }

        if (!Enum.IsDefined(MaxBlastRadius))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBlastRadius), MaxBlastRadius, "Unknown blast radius.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(MaxSteps);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxTokens);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxDuration, TimeSpan.Zero);

        this.Role = Role;
        this.AllowedSkills = EnvelopeMembers.ValidateExact(AllowedSkills, nameof(AllowedSkills));
        this.AllowedCapabilities = EnvelopeMembers.ValidateExact(AllowedCapabilities, nameof(AllowedCapabilities));
        this.AllowedTools = EnvelopeMembers.ValidateExact(AllowedTools, nameof(AllowedTools));
        this.MaxRisk = MaxRisk;
        this.MaxBlastRadius = MaxBlastRadius;
        this.AllowedTargets = EnvelopeMembers.ValidateExact(AllowedTargets, nameof(AllowedTargets));
        this.AllowedEnvironments = EnvelopeMembers.ValidateExact(AllowedEnvironments, nameof(AllowedEnvironments));
        this.MaxSteps = MaxSteps;
        this.MaxTokens = MaxTokens;
        this.MaxDuration = MaxDuration;
        this.Window = Window;

        RejectNotApplicableGrant(Role, EnvelopeDimension.Skills, this.AllowedSkills.Count != 0, nameof(AllowedSkills));
        RejectNotApplicableGrant(Role, EnvelopeDimension.Capabilities, this.AllowedCapabilities.Count != 0, nameof(AllowedCapabilities));
        RejectNotApplicableGrant(Role, EnvelopeDimension.Tokens, MaxTokens != 0, nameof(MaxTokens));
    }

    /// <summary>The role this profile is for.</summary>
    public AgentRoleKind Role { get; init; }

    /// <summary>Exact Skill ids the role may use.</summary>
    public IReadOnlyList<string> AllowedSkills { get; init; }

    /// <summary>Exact Capability names the role may prepare or execute.</summary>
    public IReadOnlyList<string> AllowedCapabilities { get; init; }

    /// <summary>Exact tool names the role may invoke through the runtime.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; }

    /// <summary>The highest risk the role may reach.</summary>
    public RiskLevel MaxRisk { get; init; }

    /// <summary>The largest blast radius the role may reach.</summary>
    public BlastRadius MaxBlastRadius { get; init; }

    /// <summary>Exact target labels the role may act on.</summary>
    public IReadOnlyList<string> AllowedTargets { get; init; }

    /// <summary>Exact environment labels the role may act in.</summary>
    public IReadOnlyList<string> AllowedEnvironments { get; init; }

    /// <summary>The steps the role may take.</summary>
    public int MaxSteps { get; init; }

    /// <summary>The model tokens the role may consume.</summary>
    public int MaxTokens { get; init; }

    /// <summary>How long the role may run, counted from the moment it starts.</summary>
    public TimeSpan MaxDuration { get; init; }

    /// <summary>When the role may act, or <c>null</c> for no time restriction beyond its deadline.</summary>
    public MaintenanceWindow? Window { get; init; }

    private static void RejectNotApplicableGrant(AgentRoleKind role, EnvelopeDimension dimension, bool granted, string parameterName)
    {
        if (granted && RoleRequirements.Of(role, dimension) == EnvelopeRequirement.NotApplicable)
        {
            throw new ArgumentException(
                $"{dimension} is not applicable to the {role} role; a profile must not grant it.",
                parameterName);
        }
    }
}

/// <summary>
/// What the operator asks a delegated objective to be limited to (ADR-0031 section 5). It only ever
/// <em>narrows</em>: every dimension is optional, an absent one means "no narrowing", and a request can
/// never grant anything the role profiles do not already allow. It states authority only; the objective
/// text and the idempotency key belong to the delegation start request.
/// </summary>
/// <remarks>
/// One request covers the whole objective, not one role, so it is not refused for naming a dimension that
/// some role does not use; that role's dimension is simply forced empty. An explicit empty set narrows to
/// nothing, which is different from an absent one. Budgets narrow the run's total and, being minimums,
/// each role's ceiling too.
/// </remarks>
public sealed record DelegationAuthorityRequest
{
    /// <summary>Creates an authority request.</summary>
    /// <param name="AllowedSkills">Narrow the Skills to these exact ids, or <c>null</c> for no narrowing.</param>
    /// <param name="AllowedCapabilities">Narrow the Capabilities to these exact names, or <c>null</c> for no narrowing.</param>
    /// <param name="AllowedTools">Narrow the tools to these exact names, or <c>null</c> for no narrowing.</param>
    /// <param name="MaxRisk">Lower the risk ceiling to this, or <c>null</c> for no narrowing.</param>
    /// <param name="MaxBlastRadius">Lower the blast-radius ceiling to this, or <c>null</c> for no narrowing.</param>
    /// <param name="AllowedTargets">Narrow the targets to these exact labels, or <c>null</c> for no narrowing.</param>
    /// <param name="AllowedEnvironments">Narrow the environments to these exact labels, or <c>null</c> for no narrowing.</param>
    /// <param name="Window">Restrict to this UTC interval, or <c>null</c> for no narrowing.</param>
    /// <param name="MaxSteps">Cap the steps, or <c>null</c> for no narrowing.</param>
    /// <param name="MaxTokens">Cap the model tokens, or <c>null</c> for no narrowing.</param>
    /// <param name="DeadlineUtc">Finish by this absolute UTC instant, or <c>null</c> for no narrowing.</param>
    /// <exception cref="ArgumentException">A given set contains a null, blank, padded or wildcard member.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A given ceiling is not a defined value or a given amount is negative.</exception>
    public DelegationAuthorityRequest(
        IReadOnlyList<string>? AllowedSkills = null,
        IReadOnlyList<string>? AllowedCapabilities = null,
        IReadOnlyList<string>? AllowedTools = null,
        RiskLevel? MaxRisk = null,
        BlastRadius? MaxBlastRadius = null,
        IReadOnlyList<string>? AllowedTargets = null,
        IReadOnlyList<string>? AllowedEnvironments = null,
        MaintenanceWindow? Window = null,
        int? MaxSteps = null,
        int? MaxTokens = null,
        DateTimeOffset? DeadlineUtc = null)
    {
        if (MaxRisk is { } risk && !Enum.IsDefined(risk))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRisk), MaxRisk, "Unknown risk level.");
        }

        if (MaxBlastRadius is { } blast && !Enum.IsDefined(blast))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBlastRadius), MaxBlastRadius, "Unknown blast radius.");
        }

        if (MaxSteps is { } steps)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(steps, nameof(MaxSteps));
        }

        if (MaxTokens is { } tokens)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(tokens, nameof(MaxTokens));
        }

        this.AllowedSkills = AllowedSkills is null ? null : EnvelopeMembers.ValidateExact(AllowedSkills, nameof(AllowedSkills));
        this.AllowedCapabilities = AllowedCapabilities is null ? null : EnvelopeMembers.ValidateExact(AllowedCapabilities, nameof(AllowedCapabilities));
        this.AllowedTools = AllowedTools is null ? null : EnvelopeMembers.ValidateExact(AllowedTools, nameof(AllowedTools));
        this.MaxRisk = MaxRisk;
        this.MaxBlastRadius = MaxBlastRadius;
        this.AllowedTargets = AllowedTargets is null ? null : EnvelopeMembers.ValidateExact(AllowedTargets, nameof(AllowedTargets));
        this.AllowedEnvironments = AllowedEnvironments is null ? null : EnvelopeMembers.ValidateExact(AllowedEnvironments, nameof(AllowedEnvironments));
        this.Window = Window;
        this.MaxSteps = MaxSteps;
        this.MaxTokens = MaxTokens;
        this.DeadlineUtc = DeadlineUtc;
    }

    /// <summary>Narrow the Skills to these exact ids, or <c>null</c> for no narrowing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AllowedSkills { get; init; }

    /// <summary>Narrow the Capabilities to these exact names, or <c>null</c> for no narrowing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AllowedCapabilities { get; init; }

    /// <summary>Narrow the tools to these exact names, or <c>null</c> for no narrowing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AllowedTools { get; init; }

    /// <summary>Lower the risk ceiling to this, or <c>null</c> for no narrowing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RiskLevel? MaxRisk { get; init; }

    /// <summary>Lower the blast-radius ceiling to this, or <c>null</c> for no narrowing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BlastRadius? MaxBlastRadius { get; init; }

    /// <summary>Narrow the targets to these exact labels, or <c>null</c> for no narrowing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AllowedTargets { get; init; }

    /// <summary>Narrow the environments to these exact labels, or <c>null</c> for no narrowing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AllowedEnvironments { get; init; }

    /// <summary>Restrict to this UTC interval, or <c>null</c> for no narrowing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MaintenanceWindow? Window { get; init; }

    /// <summary>Cap the steps, or <c>null</c> for no narrowing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxSteps { get; init; }

    /// <summary>Cap the model tokens, or <c>null</c> for no narrowing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; init; }

    /// <summary>Finish by this absolute UTC instant, or <c>null</c> for no narrowing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? DeadlineUtc { get; init; }
}

/// <summary>
/// Where the runtime finds the role profiles the operator configured (ADR-0031 section 5). The host
/// implements it over its configuration — <c>bOps.Policy</c> reads the optional <c>delegation</c> section
/// of <c>policy.yaml</c> — so <c>bOps.Runtime</c> depends on this interface only, exactly as it does for
/// <see cref="IPolicyEngine"/>. Profiles are never contributed by a package.
/// </summary>
public interface IRoleProfileSource
{
    /// <summary>
    /// The profile for <paramref name="role"/>, or <c>null</c> when none is configured or the configuration
    /// that would define it is malformed. Either way delegation is refused (rule S3, ADR-0030 section 1):
    /// an implementation never guesses a profile and never throws to signal absence. A returned profile
    /// has <see cref="RoleProfile.Role"/> equal to <paramref name="role"/>.
    /// </summary>
    /// <param name="role">The role to look up.</param>
    RoleProfile? GetProfile(AgentRoleKind role);
}
