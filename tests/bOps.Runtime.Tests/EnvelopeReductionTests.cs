// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0030 section 3 and ADR-0031 (V1.2-C1): computing a role's envelope as parent ∩ profile ∩ request,
/// dimension by dimension, and the objective's root envelope from the same inputs. The expectations that
/// depend on which role treats which dimension how come from <see cref="RoleRequirementsTests.Cell"/>,
/// which types the ADR's table out independently of the code under test.
/// </summary>
public sealed class EnvelopeReductionTests
{
    private static readonly ActorIdentity Operator = new("os-user", "alice", "Alice");
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly AgentRoleKind[] Pipeline =
        [AgentRoleKind.Discovery, AgentRoleKind.Diagnostic, AgentRoleKind.Remediation, AgentRoleKind.Verification];

    private static readonly DelegationAuthorityRequest NoRequest = new();

    public static TheoryData<AgentRoleKind> Roles()
    {
        var data = new TheoryData<AgentRoleKind>();
        foreach (var role in Pipeline)
        {
            data.Add(role);
        }

        return data;
    }

    public static TheoryData<AgentRoleKind, EnvelopeDimension> RoleAndSetDimension()
    {
        var data = new TheoryData<AgentRoleKind, EnvelopeDimension>();
        foreach (var role in Pipeline)
        {
            foreach (var dimension in SetDimensions)
            {
                data.Add(role, dimension);
            }
        }

        return data;
    }

    private static readonly EnvelopeDimension[] SetDimensions =
    [
        EnvelopeDimension.Skills, EnvelopeDimension.Capabilities, EnvelopeDimension.Tools,
        EnvelopeDimension.Targets, EnvelopeDimension.Environments,
    ];

    // ---- fixtures ----

    private sealed class FixedProfiles(params RoleProfile[] profiles) : IRoleProfileSource
    {
        private readonly Dictionary<AgentRoleKind, RoleProfile> _byRole = profiles.ToDictionary(p => p.Role);

        public RoleProfile? GetProfile(AgentRoleKind role) => _byRole.GetValueOrDefault(role);
    }

    private static RoleProfile DiscoveryProfile() => new(
        AgentRoleKind.Discovery, [], [], ["system.cpu", "system.memory", "system.disk"],
        RiskLevel.Read, BlastRadius.Single, ["node-1", "node-2"], ["staging", "production"],
        MaxSteps: 10, MaxTokens: 20_000, MaxDuration: TimeSpan.FromMinutes(10));

    private static RoleProfile DiagnosticProfile() => new(
        AgentRoleKind.Diagnostic, ["system.skill"], ["system.diagnose"], ["system.cpu", "system.memory"],
        RiskLevel.Read, BlastRadius.Single, ["node-1"], ["staging"],
        MaxSteps: 8, MaxTokens: 30_000, MaxDuration: TimeSpan.FromMinutes(10));

    private static RoleProfile RemediationProfile() => new(
        AgentRoleKind.Remediation, ["system.skill"], ["system.diagnose"], ["service.restart", "system.cpu"],
        RiskLevel.High, BlastRadius.Multiple, ["node-1"], ["staging"],
        MaxSteps: 5, MaxTokens: 0, MaxDuration: TimeSpan.FromMinutes(5));

    private static RoleProfile VerificationProfile() => new(
        AgentRoleKind.Verification, [], [], ["system.cpu", "system.memory"],
        RiskLevel.Read, BlastRadius.Single, ["node-1"], ["staging"],
        MaxSteps: 5, MaxTokens: 0, MaxDuration: TimeSpan.FromMinutes(5));

    private static RoleProfile ProfileFor(AgentRoleKind role) => role switch
    {
        AgentRoleKind.Discovery => DiscoveryProfile(),
        AgentRoleKind.Diagnostic => DiagnosticProfile(),
        AgentRoleKind.Remediation => RemediationProfile(),
        _ => VerificationProfile(),
    };

    private static FixedProfiles AllProfiles() => new(DiscoveryProfile(), DiagnosticProfile(), RemediationProfile(), VerificationProfile());

    /// <summary>A generous root envelope (depth 0) that every standard profile fits inside.</summary>
    private static AuthorityEnvelope Parent(
        IReadOnlyList<string>? skills = null,
        IReadOnlyList<string>? capabilities = null,
        IReadOnlyList<string>? tools = null,
        RiskLevel risk = RiskLevel.High,
        BlastRadius blast = BlastRadius.Fleet,
        IReadOnlyList<string>? targets = null,
        IReadOnlyList<string>? environments = null,
        int steps = 100,
        int tokens = 200_000,
        DateTimeOffset? deadline = null,
        MaintenanceWindow? window = null,
        int depth = 0) =>
        new(
            Operator,
            depth,
            skills ?? ["system.skill", "other.skill"],
            capabilities ?? ["system.diagnose", "other.capability"],
            tools ?? ["system.cpu", "system.memory", "system.disk", "service.restart", "other.tool"],
            risk,
            blast,
            targets ?? ["node-1", "node-2", "node-3"],
            environments ?? ["staging", "production"],
            new DelegationBudget(steps, tokens, deadline ?? T0.AddHours(1)),
            window);

    private static AuthorityEnvelope Granted(EnvelopeReduction reduction)
    {
        Assert.False(reduction.IsDenied, reduction.Denial?.Reason);
        return reduction.Envelope!;
    }

    private static DelegationDenial Denied(EnvelopeReduction reduction)
    {
        Assert.True(reduction.IsDenied, "Expected the reduction to be refused.");
        Assert.Null(reduction.Envelope);
        return reduction.Denial!;
    }

    private static IReadOnlyList<string> SetOf(AuthorityEnvelope envelope, EnvelopeDimension dimension) => dimension switch
    {
        EnvelopeDimension.Skills => envelope.AllowedSkills,
        EnvelopeDimension.Capabilities => envelope.AllowedCapabilities,
        EnvelopeDimension.Tools => envelope.AllowedTools,
        EnvelopeDimension.Targets => envelope.AllowedTargets,
        EnvelopeDimension.Environments => envelope.AllowedEnvironments,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };

    private static DelegationAuthorityRequest RequestNarrowingToNothing(EnvelopeDimension dimension) => dimension switch
    {
        EnvelopeDimension.Skills => new(AllowedSkills: []),
        EnvelopeDimension.Capabilities => new(AllowedCapabilities: []),
        EnvelopeDimension.Tools => new(AllowedTools: []),
        EnvelopeDimension.Targets => new(AllowedTargets: []),
        EnvelopeDimension.Environments => new(AllowedEnvironments: []),
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };

    // ---- a role's envelope: the plain case ----

    [Fact]
    public void ReduceForRole_Discovery_IsTheIntersectionOfParentProfileAndRequest()
    {
        var parent = Parent();
        var request = new DelegationAuthorityRequest(AllowedTools: ["system.cpu", "system.disk", "not.in.the.profile"], AllowedTargets: ["node-2", "node-9"]);

        var envelope = Granted(EnvelopeReducer.ReduceForRole(parent, AgentRoleKind.Discovery, DiscoveryProfile(), request, T0));

        Assert.Equal(1, envelope.Depth);
        Assert.Equal(Operator, envelope.Originator);
        Assert.Empty(envelope.AllowedSkills);
        Assert.Empty(envelope.AllowedCapabilities);
        Assert.Equal(["system.cpu", "system.disk"], envelope.AllowedTools);
        Assert.Equal(RiskLevel.Read, envelope.MaxRisk);
        Assert.Equal(BlastRadius.Single, envelope.MaxBlastRadius);
        Assert.Equal(["node-2"], envelope.AllowedTargets);
        Assert.Equal(["production", "staging"], envelope.AllowedEnvironments);
        Assert.Equal(10, envelope.Budget.MaxSteps);
        Assert.Equal(20_000, envelope.Budget.MaxTokens);
        Assert.Equal(T0.AddMinutes(10), envelope.Budget.DeadlineUtc);
        Assert.Null(envelope.Window);
    }

    [Fact]
    public void ReduceForRole_Remediation_KeepsSkillsCapabilitiesAndItsOwnTools()
    {
        var envelope = Granted(EnvelopeReducer.ReduceForRole(Parent(), AgentRoleKind.Remediation, RemediationProfile(), NoRequest, T0));

        Assert.Equal(["system.skill"], envelope.AllowedSkills);
        Assert.Equal(["system.diagnose"], envelope.AllowedCapabilities);
        Assert.Equal(["service.restart", "system.cpu"], envelope.AllowedTools);
        Assert.Equal(RiskLevel.High, envelope.MaxRisk);
        Assert.Equal(BlastRadius.Multiple, envelope.MaxBlastRadius);
        Assert.Equal(0, envelope.Budget.MaxTokens);
    }

    [Fact]
    public void ReduceForRole_ARequestNamingAToolNoProfileGrants_NeverGrantsIt()
    {
        var request = new DelegationAuthorityRequest(AllowedTools: ["system.cpu", "evil.tool"]);

        var envelope = Granted(EnvelopeReducer.ReduceForRole(Parent(tools: ["system.cpu", "system.memory", "evil.tool"]), AgentRoleKind.Verification, VerificationProfile(), request, T0));

        Assert.Equal(["system.cpu"], envelope.AllowedTools);
    }

    [Fact]
    public void ReduceForRole_IsDeterministic_WhateverTheOrderOfTheInputs()
    {
        var forward = Parent(tools: ["a", "b", "c", "d"], targets: ["n1", "n2"]);
        var backward = Parent(tools: ["d", "c", "b", "a"], targets: ["n2", "n1"]);
        var profile = new RoleProfile(
            AgentRoleKind.Verification, [], [], ["c", "a", "d", "b"], RiskLevel.Read, BlastRadius.Single, ["n2", "n1"], ["staging"], 5, 0, TimeSpan.FromMinutes(1));

        var first = Granted(EnvelopeReducer.ReduceForRole(forward, AgentRoleKind.Verification, profile, NoRequest, T0));
        var second = Granted(EnvelopeReducer.ReduceForRole(backward, AgentRoleKind.Verification, profile, NoRequest, T0));
        var again = Granted(EnvelopeReducer.ReduceForRole(forward, AgentRoleKind.Verification, profile, NoRequest, T0));

        Assert.Equal(DelegationHasher.ComputeEnvelopeHash(first), DelegationHasher.ComputeEnvelopeHash(second));
        Assert.Equal(DelegationHasher.ComputeEnvelopeHash(first), DelegationHasher.ComputeEnvelopeHash(again));
    }

    // ---- ADR-0031: what an empty dimension means, role by role ----

    [Theory]
    [MemberData(nameof(RoleAndSetDimension))]
    public void ReduceForRole_WhenTheRequestNarrowsASetToNothing_FollowsTheRolesRequirement(AgentRoleKind role, EnvelopeDimension dimension)
    {
        var reduction = EnvelopeReducer.ReduceForRole(Parent(), role, ProfileFor(role), RequestNarrowingToNothing(dimension), T0);

        if (RoleRequirementsTests.Cell(role, dimension) == 'R')
        {
            var denial = Denied(reduction);
            Assert.Equal(dimension, denial.Dimension);
            Assert.Contains(role.ToString(), denial.Reason, StringComparison.Ordinal);
            Assert.Contains("request", denial.Reason, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Optional and not applicable: an empty set means nothing is permitted there, which is not a refusal.
            Assert.Empty(SetOf(Granted(reduction), dimension));
        }
    }

    [Theory]
    [MemberData(nameof(RoleAndSetDimension))]
    public void ReduceForRole_WhenTheProfileGrantsNothingInASet_FollowsTheRolesRequirement(AgentRoleKind role, EnvelopeDimension dimension)
    {
        // Rebuild the role's profile with one set emptied. A profile may legitimately be empty in a
        // dimension that is not applicable, or optional; only a required one is refused.
        var source = ProfileFor(role);
        var profile = new RoleProfile(
            role,
            dimension == EnvelopeDimension.Skills ? [] : source.AllowedSkills,
            dimension == EnvelopeDimension.Capabilities ? [] : source.AllowedCapabilities,
            dimension == EnvelopeDimension.Tools ? [] : source.AllowedTools,
            source.MaxRisk,
            source.MaxBlastRadius,
            dimension == EnvelopeDimension.Targets ? [] : source.AllowedTargets,
            dimension == EnvelopeDimension.Environments ? [] : source.AllowedEnvironments,
            source.MaxSteps,
            source.MaxTokens,
            source.MaxDuration);

        var reduction = EnvelopeReducer.ReduceForRole(Parent(), role, profile, NoRequest, T0);

        if (RoleRequirementsTests.Cell(role, dimension) == 'R')
        {
            var denial = Denied(reduction);
            Assert.Equal(dimension, denial.Dimension);
            Assert.Contains("profile", denial.Reason, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Empty(SetOf(Granted(reduction), dimension));
        }
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void ReduceForRole_WhenTheParentGrantsNothingInASet_IsRefusedOnlyWhereTheRoleNeedsIt(AgentRoleKind role)
    {
        foreach (var dimension in SetDimensions)
        {
            var parent = dimension switch
            {
                EnvelopeDimension.Skills => Parent(skills: []),
                EnvelopeDimension.Capabilities => Parent(capabilities: []),
                EnvelopeDimension.Tools => Parent(tools: []),
                EnvelopeDimension.Targets => Parent(targets: []),
                _ => Parent(environments: []),
            };

            var reduction = EnvelopeReducer.ReduceForRole(parent, role, ProfileFor(role), NoRequest, T0);

            if (RoleRequirementsTests.Cell(role, dimension) == 'R')
            {
                var denial = Denied(reduction);
                Assert.Equal(dimension, denial.Dimension);
                Assert.Contains("parent", denial.Reason, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Assert.Empty(SetOf(Granted(reduction), dimension));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void ReduceForRole_WhenNothingIsInCommon_IsRefusedWithoutBlamingAnyOneParty(AgentRoleKind role)
    {
        // Each party grants something, but no member is in all three: the parent has only tool "x".
        var reduction = EnvelopeReducer.ReduceForRole(Parent(tools: ["x"]), role, ProfileFor(role), NoRequest, T0);

        var denial = Denied(reduction);
        Assert.Equal(EnvelopeDimension.Tools, denial.Dimension);
        Assert.Contains("in common", denial.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void ReduceForRole_StepsGrantedAsZero_AreRefusedForEveryRole(AgentRoleKind role)
    {
        var denial = Denied(EnvelopeReducer.ReduceForRole(Parent(), role, ProfileFor(role), new DelegationAuthorityRequest(MaxSteps: 0), T0));

        Assert.Equal(EnvelopeDimension.Steps, denial.Dimension);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void ReduceForRole_TokensGrantedAsZero_AreRefusedOnlyForRolesThatCallTheModel(AgentRoleKind role)
    {
        var reduction = EnvelopeReducer.ReduceForRole(Parent(), role, ProfileFor(role), new DelegationAuthorityRequest(MaxTokens: 0), T0);

        if (RoleRequirementsTests.Cell(role, EnvelopeDimension.Tokens) == 'R')
        {
            Assert.Equal(EnvelopeDimension.Tokens, Denied(reduction).Dimension);
        }
        else
        {
            Assert.Equal(0, Granted(reduction).Budget.MaxTokens);
        }
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void ReduceForRole_ATokenlessParent_StillGrantsTheRolesThatMakeNoModelCall(AgentRoleKind role)
    {
        // The case ADR-0031 exists for: once Discovery and Diagnostic have used the tokens, Remediation and
        // Verification must still be able to run an approved plan. (The caller ends the run as
        // BudgetExceeded before asking for a role that does need tokens; see EnvelopeReducer.)
        var reduction = EnvelopeReducer.ReduceForRole(Parent(tokens: 0), role, ProfileFor(role), NoRequest, T0);

        if (RoleRequirementsTests.Cell(role, EnvelopeDimension.Tokens) == 'N')
        {
            Assert.Equal(0, Granted(reduction).Budget.MaxTokens);
        }
        else
        {
            Assert.Equal(EnvelopeDimension.Tokens, Denied(reduction).Dimension);
        }
    }

    [Fact]
    public void ReduceForRole_ForcesANotApplicableSetToEmpty_EvenWhenTheParentAndRequestAreGenerous()
    {
        // The profile cannot grant Skills to Verification (it will not even build), and the request and the
        // parent both name some. The role still ends up with none.
        var request = new DelegationAuthorityRequest(AllowedSkills: ["system.skill"], AllowedCapabilities: ["system.diagnose"]);

        var envelope = Granted(EnvelopeReducer.ReduceForRole(Parent(), AgentRoleKind.Verification, VerificationProfile(), request, T0));

        Assert.Empty(envelope.AllowedSkills);
        Assert.Empty(envelope.AllowedCapabilities);
    }

    [Fact]
    public void ReduceForRole_Diagnostic_WithNoSkillOrCapability_CanStillProduceFindings()
    {
        var request = new DelegationAuthorityRequest(AllowedSkills: [], AllowedCapabilities: []);

        var envelope = Granted(EnvelopeReducer.ReduceForRole(Parent(), AgentRoleKind.Diagnostic, DiagnosticProfile(), request, T0));

        Assert.Empty(envelope.AllowedSkills);
        Assert.Empty(envelope.AllowedCapabilities);
        Assert.NotEmpty(envelope.AllowedTools);
    }

    // ---- profile, depth ----

    [Theory]
    [MemberData(nameof(Roles))]
    public void ReduceForRole_WithoutAProfile_IsRefused(AgentRoleKind role)
    {
        var denial = Denied(EnvelopeReducer.ReduceForRole(Parent(), role, null, NoRequest, T0));

        Assert.Equal(EnvelopeDimension.Profile, denial.Dimension);
        Assert.Contains(role.ToString(), denial.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ReduceForRole_WithAProfileForAnotherRole_IsRefused()
    {
        var denial = Denied(EnvelopeReducer.ReduceForRole(Parent(), AgentRoleKind.Remediation, DiscoveryProfile(), NoRequest, T0));

        Assert.Equal(EnvelopeDimension.Profile, denial.Dimension);
    }

    [Fact]
    public void ReduceForRole_FromARoleEnvelope_IsRefused_BecauseRolesCannotDelegateFurther()
    {
        var denial = Denied(EnvelopeReducer.ReduceForRole(Parent(depth: 1), AgentRoleKind.Verification, VerificationProfile(), NoRequest, T0));

        Assert.Equal(EnvelopeDimension.Depth, denial.Dimension);
    }

    // ---- risk and blast radius ----

    [Theory]
    [InlineData(AgentRoleKind.Discovery)]
    [InlineData(AgentRoleKind.Diagnostic)]
    [InlineData(AgentRoleKind.Verification)]
    public void ReduceForRole_CapsAReadOnlyRoleAtRead_WhateverTheProfileAndParentAllow(AgentRoleKind role)
    {
        var source = ProfileFor(role);
        var generous = new RoleProfile(
            role, source.AllowedSkills, source.AllowedCapabilities, source.AllowedTools, RiskLevel.High, source.MaxBlastRadius,
            source.AllowedTargets, source.AllowedEnvironments, source.MaxSteps, source.MaxTokens, source.MaxDuration);

        var envelope = Granted(EnvelopeReducer.ReduceForRole(Parent(risk: RiskLevel.Critical), role, generous, NoRequest, T0));

        Assert.Equal(RiskLevel.Read, envelope.MaxRisk);
    }

    [Fact]
    public void ReduceForRole_Remediation_NeedsACeilingAboveRead()
    {
        var readParent = Denied(EnvelopeReducer.ReduceForRole(Parent(risk: RiskLevel.Read), AgentRoleKind.Remediation, RemediationProfile(), NoRequest, T0));
        var readRequest = Denied(EnvelopeReducer.ReduceForRole(Parent(), AgentRoleKind.Remediation, RemediationProfile(), new DelegationAuthorityRequest(MaxRisk: RiskLevel.Read), T0));
        var low = Granted(EnvelopeReducer.ReduceForRole(Parent(risk: RiskLevel.Low), AgentRoleKind.Remediation, RemediationProfile(), NoRequest, T0));

        Assert.Equal(EnvelopeDimension.Risk, readParent.Dimension);
        Assert.Equal(EnvelopeDimension.Risk, readRequest.Dimension);
        Assert.Equal(RiskLevel.Low, low.MaxRisk);
    }

    [Fact]
    public void ReduceForRole_TakesTheLowestRiskAndBlastRadiusOfTheThreeParties()
    {
        var envelope = Granted(EnvelopeReducer.ReduceForRole(
            Parent(risk: RiskLevel.High, blast: BlastRadius.Fleet),
            AgentRoleKind.Remediation,
            RemediationProfile(),
            new DelegationAuthorityRequest(MaxRisk: RiskLevel.Medium, MaxBlastRadius: BlastRadius.Single),
            T0));

        Assert.Equal(RiskLevel.Medium, envelope.MaxRisk);
        Assert.Equal(BlastRadius.Single, envelope.MaxBlastRadius);
    }

    // ---- maintenance window ----

    [Fact]
    public void ReduceForRole_WithNoWindowAnywhere_LeavesTheRoleUnrestrictedInTime()
    {
        Assert.Null(Granted(EnvelopeReducer.ReduceForRole(Parent(), AgentRoleKind.Discovery, DiscoveryProfile(), NoRequest, T0)).Window);
    }

    [Fact]
    public void ReduceForRole_IntersectsEveryWindowThatIsPresent()
    {
        var parent = Parent(window: new MaintenanceWindow(T0, T0.AddHours(3)));
        var source = DiscoveryProfile();
        var profile = source with { Window = new MaintenanceWindow(T0.AddHours(1), T0.AddHours(4)) };
        var request = new DelegationAuthorityRequest(Window: new MaintenanceWindow(T0.AddMinutes(30), T0.AddHours(2)));

        var window = Granted(EnvelopeReducer.ReduceForRole(parent, AgentRoleKind.Discovery, profile, request, T0)).Window;

        Assert.Equal(new MaintenanceWindow(T0.AddHours(1), T0.AddHours(2)), window);
    }

    [Fact]
    public void ReduceForRole_AWindowOnOnlyOneSide_BecomesTheRolesWindow()
    {
        var profile = DiscoveryProfile() with { Window = new MaintenanceWindow(T0, T0.AddHours(1)) };

        var window = Granted(EnvelopeReducer.ReduceForRole(Parent(), AgentRoleKind.Discovery, profile, NoRequest, T0)).Window;

        Assert.Equal(new MaintenanceWindow(T0, T0.AddHours(1)), window);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void ReduceForRole_WindowsThatDoNotOverlap_AreRefused_EvenWhenTheyOnlyTouch(int gapMinutes)
    {
        // A window ends exclusively, so one ending exactly when the next starts shares no instant.
        var parent = Parent(window: new MaintenanceWindow(T0, T0.AddHours(1)));
        var request = new DelegationAuthorityRequest(Window: new MaintenanceWindow(T0.AddHours(1).AddMinutes(gapMinutes), T0.AddHours(2)));

        var denial = Denied(EnvelopeReducer.ReduceForRole(parent, AgentRoleKind.Discovery, DiscoveryProfile(), request, T0));

        Assert.Equal(EnvelopeDimension.MaintenanceWindow, denial.Dimension);
    }

    // ---- budget and deadline ----

    [Fact]
    public void ReduceForRole_TakesTheSmallestStepAndTokenCeiling()
    {
        var envelope = Granted(EnvelopeReducer.ReduceForRole(
            Parent(steps: 6, tokens: 15_000), AgentRoleKind.Discovery, DiscoveryProfile(), new DelegationAuthorityRequest(MaxSteps: 8, MaxTokens: 90_000), T0));

        Assert.Equal(6, envelope.Budget.MaxSteps);
        Assert.Equal(15_000, envelope.Budget.MaxTokens);
    }

    [Fact]
    public void ReduceForRole_TheDeadlineIsTheEarliestOfTheParentTheRequestAndTheProfilesDuration()
    {
        var byDuration = Granted(EnvelopeReducer.ReduceForRole(Parent(deadline: T0.AddHours(5)), AgentRoleKind.Discovery, DiscoveryProfile(), NoRequest, T0.AddMinutes(1)));
        var byParent = Granted(EnvelopeReducer.ReduceForRole(Parent(deadline: T0.AddMinutes(3)), AgentRoleKind.Discovery, DiscoveryProfile(), NoRequest, T0));
        var byRequest = Granted(EnvelopeReducer.ReduceForRole(Parent(deadline: T0.AddHours(5)), AgentRoleKind.Discovery, DiscoveryProfile(), new DelegationAuthorityRequest(DeadlineUtc: T0.AddMinutes(2)), T0));

        Assert.Equal(T0.AddMinutes(11), byDuration.Budget.DeadlineUtc);
        Assert.Equal(T0.AddMinutes(3), byParent.Budget.DeadlineUtc);
        Assert.Equal(T0.AddMinutes(2), byRequest.Budget.DeadlineUtc);
    }

    [Fact]
    public void ReduceForRole_AnElapsedParentDeadline_IsNotADenialOfAuthority()
    {
        // ADR-0031 section 3: reaching the deadline ends the run as DeadlineExceeded (V1.2-E). Whether the
        // instant has passed is not a question about what the role is allowed to do.
        var envelope = Granted(EnvelopeReducer.ReduceForRole(Parent(deadline: T0.AddMinutes(-5)), AgentRoleKind.Discovery, DiscoveryProfile(), NoRequest, T0));

        Assert.Equal(T0.AddMinutes(-5), envelope.Budget.DeadlineUtc);
    }

    [Fact]
    public void ReduceForRole_ALongDurationNearTheEndOfTime_DoesNotOverflow()
    {
        var farFuture = DateTimeOffset.MaxValue;
        var profile = DiscoveryProfile() with { MaxDuration = TimeSpan.FromDays(3650) };

        var envelope = Granted(EnvelopeReducer.ReduceForRole(
            Parent(deadline: farFuture), AgentRoleKind.Discovery, profile, NoRequest, farFuture.AddDays(-1)));

        Assert.Equal(farFuture, envelope.Budget.DeadlineUtc);
    }

    // ---- what was narrowed ----

    [Fact]
    public void ReduceForRole_ReportsExactlyTheDimensionsItNarrowed_InOrder()
    {
        // A parent that equals Diagnostic's profile in every dimension: nothing is narrowed.
        var same = Parent(
            skills: ["system.skill"], capabilities: ["system.diagnose"], tools: ["system.cpu", "system.memory"],
            risk: RiskLevel.Read, blast: BlastRadius.Single, targets: ["node-1"], environments: ["staging"],
            steps: 8, tokens: 30_000, deadline: T0.AddMinutes(10));
        var wider = Parent(
            skills: ["system.skill", "other.skill"], capabilities: ["system.diagnose"], tools: ["system.cpu", "system.memory", "other.tool"],
            risk: RiskLevel.High, blast: BlastRadius.Single, targets: ["node-1"], environments: ["staging"],
            steps: 9, tokens: 30_000, deadline: T0.AddMinutes(10));

        var unchanged = EnvelopeReducer.ReduceForRole(same, AgentRoleKind.Diagnostic, DiagnosticProfile(), NoRequest, T0);
        var narrowed = EnvelopeReducer.ReduceForRole(wider, AgentRoleKind.Diagnostic, DiagnosticProfile(), NoRequest, T0);

        Assert.Empty(unchanged.ReducedDimensions);
        Assert.Equal(
            [EnvelopeDimension.Skills, EnvelopeDimension.Tools, EnvelopeDimension.Risk, EnvelopeDimension.Steps],
            narrowed.ReducedDimensions);
    }

    [Fact]
    public void ReduceForRole_ANotApplicableTokenBudget_CountsAsNarrowed()
    {
        var reduction = EnvelopeReducer.ReduceForRole(Parent(), AgentRoleKind.Verification, VerificationProfile(), NoRequest, T0);

        Assert.Contains(EnvelopeDimension.Tokens, reduction.ReducedDimensions);
        Assert.DoesNotContain(EnvelopeDimension.Depth, reduction.ReducedDimensions);
        Assert.DoesNotContain(EnvelopeDimension.Originator, reduction.ReducedDimensions);
    }

    // ---- the guarantee: a child never broadens ----

    [Fact]
    public void ReduceForRole_NeverBroadensAnyDimension_OverManyRandomInputs()
    {
        var rng = new DeterministicRandom(20260919);
        var granted = 0;
        var refused = 0;

        for (var i = 0; i < 600; i++)
        {
            var parent = RandomParent(rng);
            var role = Pipeline[rng.Next(Pipeline.Length)];
            var profile = RandomProfile(rng, role);
            var request = rng.Next(3) == 0 ? NoRequest : RandomRequest(rng);

            var reduction = EnvelopeReducer.ReduceForRole(parent, role, profile, request, T0);

            if (reduction.IsDenied)
            {
                refused++;
                Assert.Null(reduction.Envelope);
                continue;
            }

            granted++;
            AssertNarrowerThanEveryOperand(parent, reduction.Envelope!, profile, request, T0);
        }

        // Guard against a vacuous pass: both outcomes must actually have been exercised.
        Assert.True(granted >= 150, $"Only {granted} grants; the generator no longer exercises the granted path.");
        Assert.True(refused >= 20, $"Only {refused} refusals; the generator no longer exercises the refused path.");
    }

    // ---- the objective's root envelope ----

    [Fact]
    public void DeriveRoot_IsTheUnionOfWhatTheRolesMayDo()
    {
        var root = Granted(EnvelopeReducer.DeriveRoot(AllProfiles(), NoRequest, Operator, T0));

        Assert.Equal(0, root.Depth);
        Assert.Equal(Operator, root.Originator);
        Assert.Equal(["system.skill"], root.AllowedSkills);
        Assert.Equal(["system.diagnose"], root.AllowedCapabilities);
        Assert.Equal(["service.restart", "system.cpu", "system.disk", "system.memory"], root.AllowedTools);
        Assert.Equal(["node-1", "node-2"], root.AllowedTargets);
        Assert.Equal(["production", "staging"], root.AllowedEnvironments);
        Assert.Equal(RiskLevel.High, root.MaxRisk);
        Assert.Equal(BlastRadius.Multiple, root.MaxBlastRadius);
        Assert.Null(root.Window);

        // The roles run one after another, each reserving from the run's total, so the total is the sum.
        Assert.Equal(10 + 8 + 5 + 5, root.Budget.MaxSteps);
        Assert.Equal(20_000 + 30_000, root.Budget.MaxTokens);
        Assert.Equal(T0.AddMinutes(10 + 10 + 5 + 5), root.Budget.DeadlineUtc);
    }

    [Fact]
    public void DeriveRoot_ARequestOnlyNarrows_AndReportsWhatItNarrowed()
    {
        var request = new DelegationAuthorityRequest(AllowedTools: ["system.cpu", "system.memory", "evil.tool"], MaxSteps: 20, AllowedEnvironments: ["staging"]);

        var reduction = EnvelopeReducer.DeriveRoot(AllProfiles(), request, Operator, T0);

        var root = Granted(reduction);
        Assert.Equal(["system.cpu", "system.memory"], root.AllowedTools);
        Assert.DoesNotContain("evil.tool", root.AllowedTools);
        Assert.Equal(["staging"], root.AllowedEnvironments);
        Assert.Equal(20, root.Budget.MaxSteps);
        Assert.Equal(
            [EnvelopeDimension.Tools, EnvelopeDimension.Environments, EnvelopeDimension.Steps],
            reduction.ReducedDimensions);
    }

    [Fact]
    public void DeriveRoot_WithNoRequest_ReducesNothing()
    {
        Assert.Empty(EnvelopeReducer.DeriveRoot(AllProfiles(), NoRequest, Operator, T0).ReducedDimensions);
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void DeriveRoot_WhenAnyRoleHasNoProfile_RefusesTheWholeDelegation(AgentRoleKind missing)
    {
        var present = Pipeline.Where(r => r != missing).Select(ProfileFor).ToArray();

        var denial = Denied(EnvelopeReducer.DeriveRoot(new FixedProfiles(present), NoRequest, Operator, T0));

        Assert.Equal(EnvelopeDimension.Profile, denial.Dimension);
        Assert.Contains(missing.ToString(), denial.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DeriveRoot_WhenASourceReturnsTheWrongRolesProfile_RefusesTheWholeDelegation()
    {
        var denial = Denied(EnvelopeReducer.DeriveRoot(new WrongRoleSource(), NoRequest, Operator, T0));

        Assert.Equal(EnvelopeDimension.Profile, denial.Dimension);
    }

    [Fact]
    public void DeriveRoot_FailsAtTheStart_WhenAnyRoleWouldBeRefusedLater()
    {
        // Remediation would be refused only after Discovery and Diagnostic had spent tokens and the operator
        // had approved a plan. Deriving the root checks every role up front instead.
        var readOnlyRequest = new DelegationAuthorityRequest(MaxRisk: RiskLevel.Read);

        var denial = Denied(EnvelopeReducer.DeriveRoot(AllProfiles(), readOnlyRequest, Operator, T0));

        Assert.Equal(EnvelopeDimension.Risk, denial.Dimension);
        Assert.Contains(nameof(AgentRoleKind.Remediation), denial.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DeriveRoot_RefusesARequestThatLeavesARequiredDimensionEmpty()
    {
        var noTargets = Denied(EnvelopeReducer.DeriveRoot(AllProfiles(), new DelegationAuthorityRequest(AllowedTargets: []), Operator, T0));
        var noSteps = Denied(EnvelopeReducer.DeriveRoot(AllProfiles(), new DelegationAuthorityRequest(MaxSteps: 0), Operator, T0));
        var noTokens = Denied(EnvelopeReducer.DeriveRoot(AllProfiles(), new DelegationAuthorityRequest(MaxTokens: 0), Operator, T0));

        Assert.Equal(EnvelopeDimension.Targets, noTargets.Dimension);
        Assert.Equal(EnvelopeDimension.Steps, noSteps.Dimension);
        Assert.Equal(EnvelopeDimension.Tokens, noTokens.Dimension);
    }

    [Fact]
    public void DeriveRoot_RefusesADeadlineAlreadyPast_ButAcceptsOneInTheFuture()
    {
        var past = Denied(EnvelopeReducer.DeriveRoot(AllProfiles(), new DelegationAuthorityRequest(DeadlineUtc: T0), Operator, T0));
        var future = Granted(EnvelopeReducer.DeriveRoot(AllProfiles(), new DelegationAuthorityRequest(DeadlineUtc: T0.AddMinutes(20)), Operator, T0));

        Assert.Equal(EnvelopeDimension.Deadline, past.Dimension);
        Assert.Equal(T0.AddMinutes(20), future.Budget.DeadlineUtc);
    }

    [Fact]
    public void DeriveRoot_RefusesAWindowThatHasAlreadyEnded_ButAcceptsAnOpenOne()
    {
        var ended = Denied(EnvelopeReducer.DeriveRoot(
            AllProfiles(), new DelegationAuthorityRequest(Window: new MaintenanceWindow(T0.AddHours(-2), T0.AddHours(-1))), Operator, T0));
        var open = Granted(EnvelopeReducer.DeriveRoot(
            AllProfiles(), new DelegationAuthorityRequest(Window: new MaintenanceWindow(T0.AddHours(-1), T0.AddHours(1))), Operator, T0));

        Assert.Equal(EnvelopeDimension.MaintenanceWindow, ended.Dimension);
        Assert.Equal(new MaintenanceWindow(T0.AddHours(-1), T0.AddHours(1)), open.Window);
    }

    [Fact]
    public void DeriveRoot_RefusesAProfileWindowThatHasAlreadyEnded()
    {
        var expired = RemediationProfile() with { Window = new MaintenanceWindow(T0.AddHours(-2), T0.AddHours(-1)) };
        var source = new FixedProfiles(DiscoveryProfile(), DiagnosticProfile(), expired, VerificationProfile());

        var denial = Denied(EnvelopeReducer.DeriveRoot(source, NoRequest, Operator, T0));

        Assert.Equal(EnvelopeDimension.MaintenanceWindow, denial.Dimension);
        Assert.Contains(nameof(AgentRoleKind.Remediation), denial.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DeriveRoot_EveryRolesEnvelopeIsInsideTheRoot()
    {
        var request = new DelegationAuthorityRequest(AllowedTargets: ["node-1"], MaxTokens: 45_000);
        var root = Granted(EnvelopeReducer.DeriveRoot(AllProfiles(), request, Operator, T0));

        foreach (var role in Pipeline)
        {
            var profile = ProfileFor(role);
            var child = Granted(EnvelopeReducer.ReduceForRole(root, role, profile, request, T0));

            AssertNarrowerThanEveryOperand(root, child, profile, request, T0);
        }
    }

    [Fact]
    public void DeriveRoot_SumsBudgetsWithoutOverflowing()
    {
        var huge = DiscoveryProfile() with { MaxSteps = int.MaxValue, MaxTokens = int.MaxValue };
        var alsoHuge = DiagnosticProfile() with { MaxSteps = int.MaxValue, MaxTokens = int.MaxValue };
        var source = new FixedProfiles(huge, alsoHuge, RemediationProfile(), VerificationProfile());

        var root = Granted(EnvelopeReducer.DeriveRoot(source, NoRequest, Operator, T0));

        Assert.Equal(int.MaxValue, root.Budget.MaxSteps);
        Assert.Equal(int.MaxValue, root.Budget.MaxTokens);
    }

    [Fact]
    public void Reducer_RefusesNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => EnvelopeReducer.ReduceForRole(null!, AgentRoleKind.Discovery, DiscoveryProfile(), NoRequest, T0));
        Assert.Throws<ArgumentNullException>(() => EnvelopeReducer.ReduceForRole(Parent(), AgentRoleKind.Discovery, DiscoveryProfile(), null!, T0));
        Assert.Throws<ArgumentNullException>(() => EnvelopeReducer.DeriveRoot(null!, NoRequest, Operator, T0));
        Assert.Throws<ArgumentNullException>(() => EnvelopeReducer.DeriveRoot(AllProfiles(), null!, Operator, T0));
        Assert.Throws<ArgumentNullException>(() => EnvelopeReducer.DeriveRoot(AllProfiles(), NoRequest, null!, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EnvelopeReducer.ReduceForRole(Parent(), (AgentRoleKind)99, null, NoRequest, T0));
    }

    private sealed class WrongRoleSource : IRoleProfileSource
    {
        public RoleProfile? GetProfile(AgentRoleKind role) => ProfileFor(AgentRoleKind.Discovery);
    }

    // ---- property-test helpers ----

    private static readonly string[] SkillUniverse = ["s1", "s2", "s3", "s4"];
    private static readonly string[] CapabilityUniverse = ["c1", "c2", "c3", "c4"];
    private static readonly string[] ToolUniverse = ["t1", "t2", "t3", "t4", "t5", "t6"];
    private static readonly string[] TargetUniverse = ["n1", "n2", "n3", "n4"];
    private static readonly string[] EnvironmentUniverse = ["e1", "e2", "e3"];

    private static IReadOnlyList<string> RandomSubset(DeterministicRandom rng, string[] universe, double inclusion = 0.7) =>
        [.. universe.Where(_ => rng.NextDouble() < inclusion)];

    private static MaintenanceWindow? RandomWindow(DeterministicRandom rng)
    {
        if (rng.Next(3) != 0)
        {
            return null;
        }

        var start = T0.AddMinutes(rng.Next(-30, 60));
        return new MaintenanceWindow(start, start.AddMinutes(rng.Next(5, 180)));
    }

    private static AuthorityEnvelope RandomParent(DeterministicRandom rng) => new(
        Operator,
        Depth: 0,
        RandomSubset(rng, SkillUniverse),
        RandomSubset(rng, CapabilityUniverse),
        RandomSubset(rng, ToolUniverse),
        (RiskLevel)rng.Next(0, 5),
        (BlastRadius)rng.Next(0, 3),
        RandomSubset(rng, TargetUniverse),
        RandomSubset(rng, EnvironmentUniverse),
        new DelegationBudget(rng.Next(0, 50), rng.Next(0, 60_000), T0.AddMinutes(rng.Next(1, 240))),
        RandomWindow(rng));

    private static RoleProfile RandomProfile(DeterministicRandom rng, AgentRoleKind role) => new(
        role,
        RoleRequirementsTests.Cell(role, EnvelopeDimension.Skills) == 'N' ? [] : RandomSubset(rng, SkillUniverse),
        RoleRequirementsTests.Cell(role, EnvelopeDimension.Capabilities) == 'N' ? [] : RandomSubset(rng, CapabilityUniverse),
        RandomSubset(rng, ToolUniverse),
        (RiskLevel)rng.Next(0, 5),
        (BlastRadius)rng.Next(0, 3),
        RandomSubset(rng, TargetUniverse),
        RandomSubset(rng, EnvironmentUniverse),
        rng.Next(0, 30),
        RoleRequirementsTests.Cell(role, EnvelopeDimension.Tokens) == 'N' ? 0 : rng.Next(0, 40_000),
        TimeSpan.FromMinutes(rng.Next(1, 90)),
        RandomWindow(rng));

    private static DelegationAuthorityRequest RandomRequest(DeterministicRandom rng) => new(
        rng.Next(2) == 0 ? null : RandomSubset(rng, SkillUniverse),
        rng.Next(2) == 0 ? null : RandomSubset(rng, CapabilityUniverse),
        rng.Next(2) == 0 ? null : RandomSubset(rng, ToolUniverse),
        rng.Next(2) == 0 ? null : (RiskLevel)rng.Next(0, 5),
        rng.Next(2) == 0 ? null : (BlastRadius)rng.Next(0, 3),
        rng.Next(2) == 0 ? null : RandomSubset(rng, TargetUniverse),
        rng.Next(2) == 0 ? null : RandomSubset(rng, EnvironmentUniverse),
        rng.Next(2) == 0 ? null : RandomWindow(rng) ?? new MaintenanceWindow(T0.AddMinutes(-10), T0.AddHours(2)),
        rng.Next(2) == 0 ? null : rng.Next(0, 40),
        rng.Next(2) == 0 ? null : rng.Next(0, 50_000),
        rng.Next(2) == 0 ? null : T0.AddMinutes(rng.Next(1, 200)));

    /// <summary>The monotonicity guarantee: in every dimension the child is at most as much as each operand.</summary>
    private static void AssertNarrowerThanEveryOperand(
        AuthorityEnvelope parent, AuthorityEnvelope child, RoleProfile profile, DelegationAuthorityRequest request, DateTimeOffset now)
    {
        Assert.Equal(RoleRequirements.RoleDepth, child.Depth);
        Assert.Equal(parent.Originator, child.Originator);

        AssertSubset(child.AllowedSkills, parent.AllowedSkills, profile.AllowedSkills, request.AllowedSkills);
        AssertSubset(child.AllowedCapabilities, parent.AllowedCapabilities, profile.AllowedCapabilities, request.AllowedCapabilities);
        AssertSubset(child.AllowedTools, parent.AllowedTools, profile.AllowedTools, request.AllowedTools);
        AssertSubset(child.AllowedTargets, parent.AllowedTargets, profile.AllowedTargets, request.AllowedTargets);
        AssertSubset(child.AllowedEnvironments, parent.AllowedEnvironments, profile.AllowedEnvironments, request.AllowedEnvironments);

        Assert.True(child.MaxRisk <= parent.MaxRisk && child.MaxRisk <= profile.MaxRisk && child.MaxRisk <= (request.MaxRisk ?? RiskLevel.Critical));
        Assert.True(child.MaxBlastRadius <= parent.MaxBlastRadius && child.MaxBlastRadius <= profile.MaxBlastRadius && child.MaxBlastRadius <= (request.MaxBlastRadius ?? BlastRadius.Fleet));
        if (RoleRequirements.IsReadOnly(profile.Role))
        {
            Assert.Equal(RiskLevel.Read, child.MaxRisk);
        }

        AssertWindowInside(child.Window, parent.Window);
        AssertWindowInside(child.Window, profile.Window);
        AssertWindowInside(child.Window, request.Window);

        Assert.True(child.Budget.MaxSteps <= parent.Budget.MaxSteps && child.Budget.MaxSteps <= profile.MaxSteps && child.Budget.MaxSteps <= (request.MaxSteps ?? int.MaxValue));
        Assert.True(child.Budget.MaxTokens <= parent.Budget.MaxTokens && child.Budget.MaxTokens <= profile.MaxTokens && child.Budget.MaxTokens <= (request.MaxTokens ?? int.MaxValue));
        Assert.True(child.Budget.DeadlineUtc <= parent.Budget.DeadlineUtc);
        Assert.True(child.Budget.DeadlineUtc <= now + profile.MaxDuration);
        if (request.DeadlineUtc is { } requested)
        {
            Assert.True(child.Budget.DeadlineUtc <= requested);
        }
    }

    private static void AssertSubset(IReadOnlyList<string> child, IReadOnlyList<string> parent, IReadOnlyList<string> profile, IReadOnlyList<string>? request)
    {
        Assert.All(child, member => Assert.Contains(member, parent));
        Assert.All(child, member => Assert.Contains(member, profile));
        if (request is not null)
        {
            Assert.All(child, member => Assert.Contains(member, request));
        }

        Assert.Equal(child.Count, child.Distinct(StringComparer.Ordinal).Count());
    }

    private static void AssertWindowInside(MaintenanceWindow? inner, MaintenanceWindow? outer)
    {
        if (outer is null)
        {
            return;
        }

        Assert.NotNull(inner);
        Assert.True(inner.StartUtc >= outer.StartUtc && inner.EndUtc <= outer.EndUtc, "A child's window must lie inside every window that constrains it.");
    }
}
