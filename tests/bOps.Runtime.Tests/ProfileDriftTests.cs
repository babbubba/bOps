// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0044 section 11: the one drift function. One item per unique (role, kind, subject), deterministic order, information
/// only — it never grants, removes or rewrites anything.
/// </summary>
public sealed class ProfileDriftTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class Profiles(params RoleProfile[] profiles) : IRoleProfileSource
    {
        private readonly Dictionary<AgentRoleKind, RoleProfile> _byRole = profiles.ToDictionary(p => p.Role);

        public RoleProfile? GetProfile(AgentRoleKind role) => _byRole.GetValueOrDefault(role);
    }

    private static ToolManifest Manifest(string name, RiskLevel risk) => new()
    {
        Name = name,
        Description = "x",
        Risk = risk,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [],
    };

    private static RoleProfile Profile(AgentRoleKind role, IReadOnlyList<string> tools, int steps = 15) => new(
        role,
        role == AgentRoleKind.Remediation ? ["sk"] : [],
        role == AgentRoleKind.Remediation ? ["cap"] : [],
        tools,
        role == AgentRoleKind.Remediation ? RiskLevel.High : RiskLevel.Read,
        BlastRadius.Single, ["local"], ["local"], steps,
        role is AgentRoleKind.Discovery or AgentRoleKind.Diagnostic ? 1000 : 0,
        TimeSpan.FromMinutes(30));

    private static readonly ToolManifest[] Available =
    [
        Manifest("system.cpu", RiskLevel.Read), Manifest("system.memory", RiskLevel.Read), Manifest("service.restart", RiskLevel.High),
    ];

    private static List<(AgentRoleKind?, string, string?)> Items(IRoleProfileSource source, PolicyLoadState policy = PolicyLoadState.Loaded, IReadOnlyList<ToolManifest>? tools = null) =>
        [.. ProfileDrift.Evaluate(source, policy, tools ?? Available, T0).Select(item => (item.Role, item.Kind, item.Subject))];

    [Fact]
    public void ACleanGeneratedReadOnlySetup_HasNoDrift()
    {
        string[] reads = ["system.cpu", "system.memory"];

        Assert.Empty(Items(new Profiles(Profile(AgentRoleKind.Discovery, reads), Profile(AgentRoleKind.Diagnostic, reads))));
    }

    [Fact]
    public void ANoFileSetup_WithNoProfiles_HasNoDrift_BecauseAbsenceIsReadinessNotDrift()
    {
        Assert.Empty(Items(new Profiles(), PolicyLoadState.NoFile));
    }

    [Fact]
    public void APolicyThatFailedToLoad_IsExactlyOneItem()
    {
        var item = Assert.Single(Items(new Profiles(), PolicyLoadState.LoadFailed));

        Assert.Equal((null, "policy_load_failed", null), item);
        Assert.True(ProfileDrift.Blocks(new ProfileDriftItem(null, ProfileDrift.PolicyLoadFailed, null)));
    }

    [Fact]
    public void ANewlyRegisteredReadTool_IsDriftForBothReadRoles_CountedOncePerRole()
    {
        string[] old = ["system.cpu"];

        var items = Items(new Profiles(Profile(AgentRoleKind.Discovery, old), Profile(AgentRoleKind.Diagnostic, old)));

        Assert.Equal(
            [
                (AgentRoleKind.Discovery, "read_tool_not_granted", "system.memory"),
                (AgentRoleKind.Diagnostic, "read_tool_not_granted", "system.memory"),
            ],
            items);
    }

    [Fact]
    public void AnUnknownConfiguredTool_IsUnavailableToolDrift_ForAnyConfiguredRole()
    {
        var items = Items(new Profiles(
            Profile(AgentRoleKind.Discovery, ["system.cpu", "system.memory", "removed.tool"]),
            Profile(AgentRoleKind.Verification, ["system.cpu", "removed.tool"])));

        Assert.Contains((AgentRoleKind.Discovery, "unavailable_tool", "removed.tool"), items);
        Assert.Contains((AgentRoleKind.Verification, "unavailable_tool", "removed.tool"), items);
        Assert.DoesNotContain(items, item => item.Item2 == "read_tool_not_granted" && item.Item1 == AgentRoleKind.Verification);
    }

    [Fact]
    public void AMutationToolInAReadOnlyRole_IsAboveTheRoleRiskCap_ButNotForRemediation()
    {
        var items = Items(new Profiles(
            Profile(AgentRoleKind.Discovery, ["system.cpu", "system.memory", "service.restart"]),
            Profile(AgentRoleKind.Remediation, ["service.restart"]),
            Profile(AgentRoleKind.Verification, ["service.restart"])));

        Assert.Contains((AgentRoleKind.Discovery, "above_role_risk_cap", "service.restart"), items);
        Assert.Contains((AgentRoleKind.Verification, "above_role_risk_cap", "service.restart"), items);
        Assert.DoesNotContain(items, item => item.Item1 == AgentRoleKind.Remediation && item.Item2 == "above_role_risk_cap");
    }

    [Fact]
    public void AProfileTheReducerRefuses_IsUnusableProfile_NamingTheDimension_WhetherOrNotAShapeRequiresIt()
    {
        var items = Items(new Profiles(
            Profile(AgentRoleKind.Discovery, ["system.cpu", "system.memory"]),
            Profile(AgentRoleKind.Diagnostic, ["system.cpu", "system.memory"]),
            Profile(AgentRoleKind.Remediation, ["service.restart"], steps: 0)));

        var item = Assert.Single(items);
        Assert.Equal((AgentRoleKind.Remediation, "unusable_profile", "Steps"), item);
        Assert.True(ProfileDrift.Blocks(new ProfileDriftItem(AgentRoleKind.Remediation, ProfileDrift.UnusableProfile, "Steps")));
        Assert.False(ProfileDrift.Blocks(new ProfileDriftItem(AgentRoleKind.Discovery, ProfileDrift.ReadToolNotGranted, "x")));
    }

    [Fact]
    public void Items_AreOrderedByRoleThenKindThenSubject_AndUnique()
    {
        var items = Items(new Profiles(
            Profile(AgentRoleKind.Verification, ["zz.gone", "aa.gone", "service.restart", "aa.gone"]),
            Profile(AgentRoleKind.Discovery, [], steps: 0)));

        Assert.Equal(
            [
                (AgentRoleKind.Discovery, "unusable_profile", "Tools"),
                (AgentRoleKind.Discovery, "read_tool_not_granted", "system.cpu"),
                (AgentRoleKind.Discovery, "read_tool_not_granted", "system.memory"),
                (AgentRoleKind.Verification, "unavailable_tool", "aa.gone"),
                (AgentRoleKind.Verification, "unavailable_tool", "zz.gone"),
                (AgentRoleKind.Verification, "above_role_risk_cap", "service.restart"),
            ],
            items);
        Assert.Equal(items.Count, items.Distinct().Count());
    }

    [Fact]
    public void Drift_IsDeterministic_AndChangesNothingInTheSource()
    {
        var discovery = Profile(AgentRoleKind.Discovery, ["system.cpu"]);
        var source = new Profiles(discovery);

        var first = Items(source);
        var second = Items(source);

        Assert.Equal(first, second);
        Assert.Equal(["system.cpu"], source.GetProfile(AgentRoleKind.Discovery)!.AllowedTools);
    }
}
