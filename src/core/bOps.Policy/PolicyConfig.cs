// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Policy;

/// <summary>
/// A loaded, validated policy configuration — the data <see cref="PolicyEngine"/> evaluates
/// against. Never built directly from untrusted YAML text without going through
/// <see cref="PolicyConfigLoader"/>, which is where the <see cref="RiskLevel.Critical"/>
/// invariant (agentic/03-security-rules.md, rule S3) is enforced.
/// </summary>
public sealed class PolicyConfig
{
    /// <summary>Creates a policy configuration.</summary>
    /// <param name="defaults">The mode to use for a risk level with no more specific entry.</param>
    /// <param name="toolOverrides">A mode for one specific tool by name, taking precedence over <paramref name="defaults"/>.</param>
    /// <param name="packageCeilings">The highest risk level a package's tools may reach, regardless of what the tool/risk-default decision says (rule S3: ceilings can only lower a decision, never raise it).</param>
    public PolicyConfig(
        IReadOnlyDictionary<RiskLevel, PolicyMode> defaults,
        IReadOnlyDictionary<string, PolicyMode> toolOverrides,
        IReadOnlyDictionary<string, RiskLevel> packageCeilings,
        IReadOnlyList<SkillPolicyRule>? skillRules = null)
    {
        Defaults = defaults;
        ToolOverrides = toolOverrides;
        PackageCeilings = packageCeilings;
        SkillRules = skillRules ?? [];
    }

    /// <summary>The mode to use for a risk level with no more specific entry.</summary>
    public IReadOnlyDictionary<RiskLevel, PolicyMode> Defaults { get; }

    /// <summary>A mode for one specific tool by name, taking precedence over <see cref="Defaults"/>.</summary>
    public IReadOnlyDictionary<string, PolicyMode> ToolOverrides { get; }

    /// <summary>The highest risk level a package's tools may reach, regardless of what the tool/risk-default decision says.</summary>
    public IReadOnlyDictionary<string, RiskLevel> PackageCeilings { get; }

    /// <summary>Exact contextual authorization rules for Skill-originated tool calls.</summary>
    public IReadOnlyList<SkillPolicyRule> SkillRules { get; }

    /// <summary>
    /// The built-in policy used when no <c>policy.yaml</c> file exists at all: Read/Low run
    /// automatically, Medium/High require approval, Critical is forbidden (also structurally
    /// enforced by <see cref="PolicyEngine"/> itself, independent of this table — see rule S3).
    /// This is a sensible starting point for an operator who has not written a policy yet, not a
    /// silent default for a policy file that failed to load — see <see cref="AllForbidden"/> for
    /// that case.
    /// </summary>
    public static PolicyConfig SafeDefault { get; } = new(
        defaults: new Dictionary<RiskLevel, PolicyMode>
        {
            [RiskLevel.Read] = PolicyMode.Automatic,
            [RiskLevel.Low] = PolicyMode.Automatic,
            [RiskLevel.Medium] = PolicyMode.Approval,
            [RiskLevel.High] = PolicyMode.Approval,
            [RiskLevel.Critical] = PolicyMode.Forbidden,
        },
        toolOverrides: new Dictionary<string, PolicyMode>(StringComparer.Ordinal),
        packageCeilings: new Dictionary<string, RiskLevel>(StringComparer.Ordinal),
        skillRules: []);

    /// <summary>
    /// Used when a <c>policy.yaml</c> file exists but failed to load (malformed YAML, or it
    /// violated the <see cref="RiskLevel.Critical"/> invariant) — every risk level resolves to
    /// <see cref="PolicyMode.Forbidden"/> until the operator fixes the file (rule S3: "a
    /// malformed policy.yaml... resolves to Forbidden. Never to Automatic."). Deliberately more
    /// conservative than <see cref="SafeDefault"/>: an operator who wrote a broken policy clearly
    /// intended something other than the built-in default, so falling back to that default could
    /// silently be *more* permissive than what they thought they had configured.
    /// </summary>
    public static PolicyConfig AllForbidden { get; } = new(
        defaults: new Dictionary<RiskLevel, PolicyMode>
        {
            [RiskLevel.Read] = PolicyMode.Forbidden,
            [RiskLevel.Low] = PolicyMode.Forbidden,
            [RiskLevel.Medium] = PolicyMode.Forbidden,
            [RiskLevel.High] = PolicyMode.Forbidden,
            [RiskLevel.Critical] = PolicyMode.Forbidden,
        },
        toolOverrides: new Dictionary<string, PolicyMode>(StringComparer.Ordinal),
        packageCeilings: new Dictionary<string, RiskLevel>(StringComparer.Ordinal),
        skillRules: []);
}

/// <summary>One exact-match contextual rule for Skill-originated tool calls (ADR-0025).</summary>
public sealed record SkillPolicyRule(
    string SkillId,
    string CapabilityName,
    string Target,
    string Environment,
    BlastRadius BlastRadius,
    PolicyMode Mode);
