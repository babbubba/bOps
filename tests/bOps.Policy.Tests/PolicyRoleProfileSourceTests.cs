// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Policy.Tests;

/// <summary>
/// V1.2-C3 (ADR-0031 section 5): <c>bOps.Policy</c> serves the role profiles of <c>policy.yaml</c> through the
/// SDK's <see cref="IRoleProfileSource"/>, so <c>bOps.Runtime</c> depends on the interface only. A role with no
/// profile is <c>null</c>, which is how delegation is refused (rule S3); the source never guesses one and never
/// throws to say a role has none.
/// </summary>
public sealed class PolicyRoleProfileSourceTests
{
    private static RoleProfile Profile(AgentRoleKind role, params string[] tools) =>
        new(
            role,
            AllowedSkills: [],
            AllowedCapabilities: [],
            AllowedTools: tools,
            MaxRisk: RiskLevel.Read,
            MaxBlastRadius: BlastRadius.Single,
            AllowedTargets: ["host-a"],
            AllowedEnvironments: ["staging"],
            MaxSteps: 3,
            MaxTokens: role is AgentRoleKind.Discovery or AgentRoleKind.Diagnostic ? 1000 : 0,
            MaxDuration: TimeSpan.FromMinutes(5));

    private static PolicyConfig ConfigWith(params RoleProfile[] profiles) =>
        new(
            new Dictionary<RiskLevel, PolicyMode>(),
            new Dictionary<string, PolicyMode>(StringComparer.Ordinal),
            new Dictionary<string, RiskLevel>(StringComparer.Ordinal),
            roleProfiles: profiles);

    [Fact]
    public void TheSourceIsTheSdkContract()
    {
        Assert.IsAssignableFrom<IRoleProfileSource>(new PolicyRoleProfileSource(ConfigWith()));
    }

    [Fact]
    public void AConfiguredRoleReturnsItsProfile()
    {
        var discovery = Profile(AgentRoleKind.Discovery, "host.info");
        var verification = Profile(AgentRoleKind.Verification, "host.info");
        var source = new PolicyRoleProfileSource(ConfigWith(discovery, verification));

        Assert.Same(discovery, source.GetProfile(AgentRoleKind.Discovery));
        Assert.Same(verification, source.GetProfile(AgentRoleKind.Verification));
    }

    [Fact]
    public void ARoleWithNoProfileIsNullNeverADefault()
    {
        var source = new PolicyRoleProfileSource(ConfigWith(Profile(AgentRoleKind.Discovery, "host.info")));

        Assert.Null(source.GetProfile(AgentRoleKind.Diagnostic));
        Assert.Null(source.GetProfile(AgentRoleKind.Remediation));
        Assert.Null(source.GetProfile(AgentRoleKind.Verification));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void AnUndefinedRoleIsNullAndDoesNotThrow(int value)
    {
        var source = new PolicyRoleProfileSource(ConfigWith(Profile(AgentRoleKind.Discovery, "host.info")));

        Assert.Null(source.GetProfile((AgentRoleKind)value));
    }

    [Theory]
    [InlineData(AgentRoleKind.Discovery)]
    [InlineData(AgentRoleKind.Diagnostic)]
    [InlineData(AgentRoleKind.Remediation)]
    [InlineData(AgentRoleKind.Verification)]
    public void TheBuiltInPoliciesShipNoProfile(AgentRoleKind role)
    {
        // ADR-0030 section 1 and ADR-0031 section 5: delegation is off until an operator grants it, exactly like
        // contextual Skill rules. A policy that failed to load is the most restrictive one, so it has none either.
        Assert.Null(new PolicyRoleProfileSource(PolicyConfig.SafeDefault).GetProfile(role));
        Assert.Null(new PolicyRoleProfileSource(PolicyConfig.AllForbidden).GetProfile(role));
        Assert.Null(new PolicyRoleProfileSource(PolicyConfigLoader.Load(string.Empty)).GetProfile(role));
    }

    [Fact]
    public void AReturnedProfileIsAlwaysForTheRoleAskedFor()
    {
        var all = Enum.GetValues<AgentRoleKind>().Select(role => Profile(role, "host.info")).ToArray();
        var source = new PolicyRoleProfileSource(ConfigWith(all));

        foreach (var role in Enum.GetValues<AgentRoleKind>())
        {
            Assert.Equal(role, source.GetProfile(role)!.Role);
        }
    }

    [Fact]
    public void AProfileLoadedFromYamlIsServedByTheSource()
    {
        const string yaml = """
            delegation:
              roles:
                verification:
                  tools: [host.info]
                  maxRisk: read
                  maxBlastRadius: single
                  targets: [host-a]
                  environments: [staging]
                  maxSteps: 4
                  maxDuration: 00:05:00
            """;

        var source = new PolicyRoleProfileSource(PolicyConfigLoader.Load(yaml));

        Assert.Equal(["host.info"], source.GetProfile(AgentRoleKind.Verification)!.AllowedTools);
        Assert.Null(source.GetProfile(AgentRoleKind.Discovery));
    }

    [Fact]
    public void TheSourceNeedsAConfiguration()
    {
        Assert.Throws<ArgumentNullException>(() => new PolicyRoleProfileSource(null!));
    }

    [Fact]
    public void TwoProfilesForOneRoleCannotBeConfigured()
    {
        // One profile per role is the contract; a configuration that carried two would have to pick one.
        var ex = Assert.Throws<ArgumentException>(
            () => ConfigWith(Profile(AgentRoleKind.Discovery, "a.one"), Profile(AgentRoleKind.Discovery, "a.two")));

        Assert.Contains("Discovery", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullProfileCannotBeConfigured()
    {
        Assert.Throws<ArgumentException>(() => ConfigWith(Profile(AgentRoleKind.Discovery, "a.one"), null!));
    }

    [Fact]
    public void TheConfigurationIsASnapshotOfTheProfilesItWasGiven()
    {
        // Same posture as the contracts: a list the caller keeps and edits afterwards must not change what a
        // loaded policy says.
        List<RoleProfile> profiles = [Profile(AgentRoleKind.Discovery, "host.info")];
        var config = new PolicyConfig(
            new Dictionary<RiskLevel, PolicyMode>(),
            new Dictionary<string, PolicyMode>(StringComparer.Ordinal),
            new Dictionary<string, RiskLevel>(StringComparer.Ordinal),
            roleProfiles: profiles);
        var source = new PolicyRoleProfileSource(config);

        profiles.Add(Profile(AgentRoleKind.Remediation, "service.restart"));
        profiles.RemoveAt(0);

        // One profile before and one after the caller's edits, so the count alone proves nothing: it must still be
        // the Discovery one.
        Assert.Equal(AgentRoleKind.Discovery, Assert.Single(config.RoleProfiles).Role);
        Assert.NotNull(source.GetProfile(AgentRoleKind.Discovery));
        Assert.Null(source.GetProfile(AgentRoleKind.Remediation));
    }

    [Fact]
    public void ThePolicyEngineDoesNotChangeBecauseOfTheSection()
    {
        // The section is read by the source and never by the policy decision: tool authorization is decided
        // from the same defaults, tools and ceilings whether or not profiles are configured.
        const string policy = "defaults:\n  read: automatic\n  medium: approval\n";
        var without = PolicyConfigLoader.Load(policy);
        var with = PolicyConfigLoader.Load(
            policy + "delegation:\n  roles:\n    verification:\n      tools: [host.info]\n      maxRisk: read\n"
            + "      maxBlastRadius: single\n      targets: [host-a]\n      environments: [staging]\n      maxSteps: 4\n      maxDuration: 00:05:00\n");

        Assert.Equal(without.Defaults, with.Defaults);
        Assert.Equal(without.ToolOverrides, with.ToolOverrides);
        Assert.Equal(without.PackageCeilings, with.PackageCeilings);
        Assert.Equal(without.SkillRules, with.SkillRules);
    }
}
