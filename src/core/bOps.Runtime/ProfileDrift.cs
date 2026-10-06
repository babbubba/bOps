// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>One drift item (ADR-0044 section 11): a unique <c>(role, kind, subject)</c>.</summary>
/// <param name="Role">The role whose profile it is about; <c>null</c> for <see cref="ProfileDrift.PolicyLoadFailed"/>.</param>
/// <param name="Kind">The snake_case kind.</param>
/// <param name="Subject">A tool name, a dimension name, or <c>null</c>.</param>
public sealed record ProfileDriftItem(AgentRoleKind? Role, string Kind, string? Subject);

/// <summary>
/// The one drift function (ADR-0044 section 11), shared by the readiness <c>profileDriftCount</c> and <c>bops delegate profiles
/// check</c>. Drift is information only: it grants nothing, removes nothing, rewrites nothing and never affects readiness. It
/// compares the configured profiles with the host's available tool manifests and with the reducer's own verdict on each
/// profile, deterministically: policy failure first, then role in pipeline order, kind in the order below, subject ordinal.
/// </summary>
public static class ProfileDrift
{
    /// <summary>The policy failed to load. One item; it has no role and no subject.</summary>
    public const string PolicyLoadFailed = "policy_load_failed";

    /// <summary>A configured profile the reducer refuses with a non-narrowing request; the subject is the dimension.</summary>
    public const string UnusableProfile = "unusable_profile";

    /// <summary>An available Read tool absent from a configured Discovery or Diagnostic profile.</summary>
    public const string ReadToolNotGranted = "read_tool_not_granted";

    /// <summary>A tool name in a configured profile that is not among the available manifests.</summary>
    public const string UnavailableTool = "unavailable_tool";

    /// <summary>An available tool in a configured Discovery, Diagnostic or Verification profile whose risk is above the role's cap.</summary>
    public const string AboveRoleRiskCap = "above_role_risk_cap";

    /// <summary>Whether an item blocks delegation for some request shape (a failed load or an unusable profile), rather than only informing.</summary>
    public static bool Blocks(ProfileDriftItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Kind is PolicyLoadFailed or UnusableProfile;
    }

    /// <summary>The drift items of the host's configuration, in their deterministic order.</summary>
    /// <param name="profiles">The host's role profile source.</param>
    /// <param name="policy">The host's policy load state.</param>
    /// <param name="availableTools">The host's available tool manifests.</param>
    /// <param name="now">The instant the reducer's verdict on each profile is evaluated at.</param>
    public static IReadOnlyList<ProfileDriftItem> Evaluate(
        IRoleProfileSource profiles, PolicyLoadState policy, IReadOnlyList<ToolManifest> availableTools, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(availableTools);

        if (policy == PolicyLoadState.LoadFailed)
        {
            return [new ProfileDriftItem(null, PolicyLoadFailed, null)];
        }

        var available = new Dictionary<string, ToolManifest>(StringComparer.Ordinal);
        foreach (var manifest in availableTools)
        {
            available.TryAdd(manifest.Name, manifest);
        }

        var items = new List<ProfileDriftItem>();
        foreach (var role in RoleRequirements.Pipeline)
        {
            if (profiles.GetProfile(role) is not { } profile)
            {
                continue;
            }

            if (profile.Role != role)
            {
                items.Add(new ProfileDriftItem(role, UnusableProfile, nameof(EnvelopeDimension.Profile)));
                continue;
            }

            if (EnvelopeReducer.ReduceConfiguredProfile(profile, now).Denial is { } denial)
            {
                items.Add(new ProfileDriftItem(role, UnusableProfile, denial.Dimension.ToString()));
            }

            var granted = profile.AllowedTools.ToHashSet(StringComparer.Ordinal);
            if (role is AgentRoleKind.Discovery or AgentRoleKind.Diagnostic)
            {
                items.AddRange(available.Values
                    .Where(manifest => manifest.Risk == RiskLevel.Read && !granted.Contains(manifest.Name))
                    .Select(manifest => manifest.Name)
                    .Order(StringComparer.Ordinal)
                    .Select(name => new ProfileDriftItem(role, ReadToolNotGranted, name)));
            }

            items.AddRange(granted
                .Where(name => !available.ContainsKey(name))
                .Order(StringComparer.Ordinal)
                .Select(name => new ProfileDriftItem(role, UnavailableTool, name)));

            if (role != AgentRoleKind.Remediation)
            {
                var cap = RoleRequirements.RiskCap(role);
                items.AddRange(granted
                    .Where(name => available.TryGetValue(name, out var manifest) && manifest.Risk > cap)
                    .Order(StringComparer.Ordinal)
                    .Select(name => new ProfileDriftItem(role, AboveRoleRiskCap, name)));
            }
        }

        return items;
    }
}
