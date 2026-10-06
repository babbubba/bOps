// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0044 sections 1–3 and 20 (HARDEN-11): the roles a delegation requires depend on the request shape, and the root is built
/// from the required roles only. Covers the canonical selector, the diagnosis-only root (P1 at the reducer level), unused-role
/// noninterference (P2), remediation unchanged against a frozen copy of the pre-ADR reduction (P3) and the child-subset property
/// for both shapes (P4). Inputs come from a seeded generator so a failure can be reproduced from its message.
/// </summary>
public sealed class RequestDependentAuthorityTests
{
    private static readonly ActorIdentity Operator = new("os-user", "alice", "Alice");
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DelegationAuthorityRequest NoRequest = new();

    private static readonly string[] ToolUniverse = ["system.cpu", "system.memory", "system.disk", "service.restart", "fs.delete", "net.ping"];
    private static readonly string[] SkillUniverse = ["sk.a", "sk.b"];
    private static readonly string[] CapabilityUniverse = ["cap.a", "cap.b"];
    private static readonly string[] TargetUniverse = ["local", "node-1", "node-2"];
    private static readonly string[] EnvironmentUniverse = ["local", "staging", "production"];

    private const int Iterations = 400;

    // ---- fixtures ----

    /// <summary>A source that records every role it was asked for, so a test can prove a role was never looked up.</summary>
    private sealed class SpyProfiles(params RoleProfile?[] profiles) : IRoleProfileSource
    {
        private readonly Dictionary<AgentRoleKind, RoleProfile> _byRole =
            profiles.OfType<RoleProfile>().ToDictionary(profile => profile.Role);

        public List<AgentRoleKind> LookedUp { get; } = [];

        public RoleProfile? GetProfile(AgentRoleKind role)
        {
            LookedUp.Add(role);
            return _byRole.GetValueOrDefault(role);
        }
    }

    private static RoleProfile Discovery(IReadOnlyList<string>? tools = null, int steps = 15, int tokens = 150_000) => new(
        AgentRoleKind.Discovery, [], [], tools ?? ["system.cpu", "system.memory"], RiskLevel.Read, BlastRadius.Single,
        ["local"], ["local"], steps, tokens, TimeSpan.FromMinutes(30));

    private static RoleProfile Diagnostic(IReadOnlyList<string>? tools = null, IReadOnlyList<string>? skills = null, int tokens = 150_000) => new(
        AgentRoleKind.Diagnostic, skills ?? [], skills is null ? [] : ["cap.a"], tools ?? ["system.cpu"], RiskLevel.Read, BlastRadius.Single,
        ["local"], ["local"], 15, tokens, TimeSpan.FromMinutes(30));

    private static RoleProfile Remediation(
        IReadOnlyList<string>? tools = null, RiskLevel risk = RiskLevel.High, IReadOnlyList<string>? targets = null, int steps = 5, TimeSpan? duration = null) => new(
        AgentRoleKind.Remediation, ["sk.a"], ["cap.a"], tools ?? ["service.restart"], risk, BlastRadius.Multiple,
        targets ?? ["node-1"], targets ?? ["staging"], steps, 0, duration ?? TimeSpan.FromMinutes(5));

    private static RoleProfile Verification(IReadOnlyList<string>? tools = null, IReadOnlyList<string>? targets = null, int steps = 5) => new(
        AgentRoleKind.Verification, [], [], tools ?? ["system.cpu"], RiskLevel.Read, BlastRadius.Single,
        targets ?? ["node-1"], targets ?? ["staging"], steps, 0, TimeSpan.FromMinutes(5));

    private static AuthorityEnvelope GrantedRoot(IRoleProfileSource source, bool remediation, DelegationAuthorityRequest? request = null)
    {
        var reduction = EnvelopeReducer.DeriveRoot(source, remediation, request ?? NoRequest, Operator, T0);
        Assert.False(reduction.IsDenied, reduction.Denial?.Reason);
        return reduction.Envelope!;
    }

    // ---- the canonical selector (ADR-0044 section 1) ----

    [Fact]
    public void RequiredRoles_ForADiagnosis_AreDiscoveryAndDiagnostic_InPipelineOrder()
    {
        Assert.Equal([AgentRoleKind.Discovery, AgentRoleKind.Diagnostic], RoleRequirements.RequiredRoles(remediation: false));
    }

    [Fact]
    public void RequiredRoles_ForARemediation_AreAllFour_InPipelineOrder()
    {
        Assert.Equal(
            [AgentRoleKind.Discovery, AgentRoleKind.Diagnostic, AgentRoleKind.Remediation, AgentRoleKind.Verification],
            RoleRequirements.RequiredRoles(remediation: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredRoles_KeepsTheSelectorInvariants(bool remediation)
    {
        var required = RoleRequirements.RequiredRoles(remediation);

        Assert.All(required, role => Assert.Contains(role, RoleRequirements.Pipeline));
        Assert.Equal(RoleRequirements.Pipeline.Where(required.Contains), required);
        Assert.Contains(AgentRoleKind.Discovery, required);
        Assert.Contains(AgentRoleKind.Diagnostic, required);
        Assert.Equal(remediation, required.Contains(AgentRoleKind.Remediation));
        Assert.Equal(remediation, required.Contains(AgentRoleKind.Verification));
        Assert.Equal(required, RoleRequirements.RequiredRoles(remediation));
    }

    // ---- the diagnosis-only root (ADR-0044 sections 2 and 3) ----

    [Fact]
    public void DiagnosisRoot_IsBuiltFromDiscoveryAndDiagnosticOnly_WithNoSkillOrCapability_AndNeverLooksUpTheOthers()
    {
        var source = new SpyProfiles(Discovery(), Diagnostic(skills: ["sk.a"]), Remediation(), Verification());

        var root = GrantedRoot(source, remediation: false);

        Assert.Equal([AgentRoleKind.Discovery, AgentRoleKind.Diagnostic], source.LookedUp.Distinct());
        Assert.Empty(root.AllowedSkills);
        Assert.Empty(root.AllowedCapabilities);
        Assert.Equal(RiskLevel.Read, root.MaxRisk);
        Assert.Equal(["system.cpu", "system.memory"], root.AllowedTools);
        Assert.Equal(["local"], root.AllowedTargets);
        Assert.Equal(["local"], root.AllowedEnvironments);
        Assert.Equal(30, root.Budget.MaxSteps);
        Assert.Equal(300_000, root.Budget.MaxTokens);
        Assert.Equal(T0.AddHours(1), root.Budget.DeadlineUtc);
        Assert.Null(root.Window);
    }

    [Fact]
    public void DiagnosisRoot_DoesNotNeedRemediationOrVerification_ButARemediationDoes()
    {
        var readOnly = new SpyProfiles(Discovery(), Diagnostic());

        GrantedRoot(readOnly, remediation: false);
        var (denied, role) = EnvelopeReducer.DeriveRootAttributed(readOnly, remediation: true, NoRequest, Operator, T0);

        Assert.Equal(EnvelopeDimension.Profile, denied.Denial!.Dimension);
        Assert.Equal(AgentRoleKind.Remediation, role);
        Assert.Equal("Remediation role: no usable profile is configured.", denied.Denial.Reason);
    }

    [Theory]
    [InlineData(AgentRoleKind.Discovery)]
    [InlineData(AgentRoleKind.Diagnostic)]
    public void DiagnosisRoot_IsRefused_WhenARequiredRoleHasNoProfile(AgentRoleKind missing)
    {
        RoleProfile?[] profiles = [missing == AgentRoleKind.Discovery ? null : Discovery(), missing == AgentRoleKind.Diagnostic ? null : Diagnostic(), Remediation(), Verification()];

        var (denied, role) = EnvelopeReducer.DeriveRootAttributed(new SpyProfiles(profiles), remediation: false, NoRequest, Operator, T0);

        Assert.Equal(EnvelopeDimension.Profile, denied.Denial!.Dimension);
        Assert.Equal(missing, role);
        Assert.Equal($"{missing} role: no usable profile is configured.", denied.Denial.Reason);
    }

    [Fact]
    public void DiagnosisRoles_AreHeldToReadEvenWhenAProfileNamesAMutationTool()
    {
        // S4: a mutation name in a D/D profile may enter the envelope's tools; the risk cap still holds the roles to Read.
        var source = new SpyProfiles(Discovery(tools: ["system.cpu", "service.restart"]), Diagnostic(tools: ["service.restart"]));
        var root = GrantedRoot(source, remediation: false);

        Assert.Contains("service.restart", root.AllowedTools);
        Assert.Equal(RiskLevel.Read, root.MaxRisk);
        foreach (var role in new[] { AgentRoleKind.Discovery, AgentRoleKind.Diagnostic })
        {
            var envelope = EnvelopeReducer.ReduceForRole(root, role, source.GetProfile(role), NoRequest, T0).Envelope!;
            Assert.Equal(RiskLevel.Read, envelope.MaxRisk);
            Assert.Empty(envelope.AllowedSkills);
            Assert.Empty(envelope.AllowedCapabilities);
        }
    }

    // ---- P1: diagnosis never exceeds Read ----

    [Fact]
    public void P1_EveryDiagnosisEnvelopeIsReadOnly_AndRefusesEveryManifestAboveRead()
    {
        var rng = new DeterministicRandom(0x4811_0001);
        var checkedEnvelopes = 0;
        for (var i = 0; i < Iterations; i++)
        {
            var source = new SpyProfiles(
                RandomProfile(rng, AgentRoleKind.Discovery), RandomProfile(rng, AgentRoleKind.Diagnostic),
                rng.Next(2) == 0 ? null : RandomProfile(rng, AgentRoleKind.Remediation),
                rng.Next(2) == 0 ? null : RandomProfile(rng, AgentRoleKind.Verification));
            var request = RandomRequest(rng);
            var reduction = EnvelopeReducer.DeriveRoot(source, remediation: false, request, Operator, T0);
            Assert.DoesNotContain(AgentRoleKind.Remediation, source.LookedUp);
            Assert.DoesNotContain(AgentRoleKind.Verification, source.LookedUp);
            if (reduction.IsDenied)
            {
                continue;
            }

            var root = reduction.Envelope!;
            Assert.True(root.MaxRisk <= RiskLevel.Read, $"iteration {i}: root risk {root.MaxRisk}");
            Assert.Empty(root.AllowedSkills);
            Assert.Empty(root.AllowedCapabilities);

            foreach (var role in RoleRequirements.RequiredRoles(remediation: false))
            {
                var envelope = EnvelopeReducer.ReduceForRole(root, role, source.GetProfile(role), request, T0).Envelope!;
                Assert.True(envelope.MaxRisk <= RiskLevel.Read, $"iteration {i}: {role} risk {envelope.MaxRisk}");
                var scope = DelegatedExecutionScope.For(Guid.NewGuid(), new AgentIdentity(AgentId.New(), role), envelope);

                // Every tool name the envelope or the universe knows, at every risk above Read, is refused before policy.
                foreach (var name in envelope.AllowedTools.Concat(ToolUniverse).Distinct(StringComparer.Ordinal))
                {
                    foreach (var risk in new[] { RiskLevel.Low, RiskLevel.Medium, RiskLevel.High, RiskLevel.Critical })
                    {
                        var refusal = EnvelopeEnforcer.CheckStep(scope, Operator, Manifest(name, risk), null, T0);
                        Assert.NotNull(refusal);
                        checkedEnvelopes++;
                    }
                }
            }
        }

        Assert.True(checkedEnvelopes > 1_000, $"Only {checkedEnvelopes} refusals were checked; the generator is too narrow.");
    }

    // ---- P2: no unused-role authority ----

    public static TheoryData<string> UnusedRoleVariants() =>
    [
        "absent", "minimal", "broad", "many tools", "broader risk", "broader targets", "larger budgets", "verification only", "remediation only",
    ];

    [Theory]
    [MemberData(nameof(UnusedRoleVariants))]
    public void P2_TheDiagnosisRootAndItsHash_DoNotDependOnRemediationOrVerificationProfiles(string variant)
    {
        var baseline = GrantedRoot(new SpyProfiles(Discovery(), Diagnostic()), remediation: false);

        var (remediation, verification) = UnusedProfiles(variant);
        var varied = GrantedRoot(new SpyProfiles(Discovery(), Diagnostic(), remediation, verification), remediation: false);

        Assert.Equal(DelegationHasher.ComputeEnvelopeHash(baseline), DelegationHasher.ComputeEnvelopeHash(varied));
        Assert.Equal(baseline.AllowedTools, varied.AllowedTools);
        Assert.Equal(baseline.Budget, varied.Budget);
        Assert.Equal(baseline.MaxRisk, varied.MaxRisk);
    }

    [Fact]
    public void P2_GeneratedRemediationAndVerificationProfiles_NeverChangeTheDiagnosisRoot()
    {
        var rng = new DeterministicRandom(0x4811_0002);
        for (var i = 0; i < Iterations; i++)
        {
            var discovery = RandomProfile(rng, AgentRoleKind.Discovery);
            var diagnostic = RandomProfile(rng, AgentRoleKind.Diagnostic);
            var request = RandomRequest(rng);
            var without = EnvelopeReducer.DeriveRootAttributed(new SpyProfiles(discovery, diagnostic), remediation: false, request, Operator, T0);
            var with = EnvelopeReducer.DeriveRootAttributed(
                new SpyProfiles(discovery, diagnostic, RandomProfile(rng, AgentRoleKind.Remediation), RandomProfile(rng, AgentRoleKind.Verification)),
                remediation: false, request, Operator, T0);

            Assert.Equal(without.Role, with.Role);
            Assert.Equal(without.Reduction.Denial, with.Reduction.Denial);
            if (!without.Reduction.IsDenied)
            {
                Assert.Equal(
                    DelegationHasher.ComputeEnvelopeHash(without.Reduction.Envelope!), DelegationHasher.ComputeEnvelopeHash(with.Reduction.Envelope!));
            }
        }
    }

    private static (RoleProfile? Remediation, RoleProfile? Verification) UnusedProfiles(string variant) => variant switch
    {
        "absent" => (null, null),
        "minimal" => (Remediation(tools: ["service.restart"], risk: RiskLevel.Low, steps: 1), Verification(steps: 1)),
        "broad" => (Remediation(tools: ToolUniverse, risk: RiskLevel.Critical, targets: TargetUniverse), Verification(tools: ToolUniverse, targets: TargetUniverse)),
        "many tools" => (Remediation(tools: [.. Enumerable.Range(0, 200).Select(i => $"tool.{i}")]), Verification(tools: [.. Enumerable.Range(0, 200).Select(i => $"read.{i}")])),
        "broader risk" => (Remediation(risk: RiskLevel.Critical), Verification()),
        "broader targets" => (Remediation(targets: ["local", "node-1", "node-2", "fleet"]), Verification(targets: ["local", "node-1", "node-2", "fleet"])),
        "larger budgets" => (Remediation(steps: 10_000, duration: TimeSpan.FromDays(30)), Verification(steps: 10_000)),
        "verification only" => (null, Verification(tools: ToolUniverse)),
        "remediation only" => (Remediation(tools: ToolUniverse, risk: RiskLevel.High), null),
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
    };

    // ---- P3: remediation unchanged (against a frozen copy of the pre-ADR reduction) ----

    [Fact]
    public void P3_ForEveryRemediation_GrantStaysGrant_DenyStaysDeny_AndAGrantedRootIsIdentical()
    {
        var rng = new DeterministicRandom(0x4811_0003);
        var granted = 0;
        for (var i = 0; i < Iterations * 2; i++)
        {
            var profiles = Enum.GetValues<AgentRoleKind>().Select(role => rng.Next(8) == 0 ? null : RandomProfile(rng, role)).ToArray();
            var source = new SpyProfiles(profiles);
            var request = RandomRequest(rng);

            var frozen = FrozenPreAdr.DeriveRootAttributed(source, request, Operator, T0);
            var current = EnvelopeReducer.DeriveRootAttributed(source, remediation: true, request, Operator, T0);

            Assert.Equal(frozen.Reduction.IsDenied, current.Reduction.IsDenied);
            if (!current.Reduction.IsDenied)
            {
                granted++;
                Assert.Equal(DelegationHasher.ComputeEnvelopeHash(frozen.Reduction.Envelope!), DelegationHasher.ComputeEnvelopeHash(current.Reduction.Envelope!));
                Assert.Equal(frozen.Reduction.ReducedDimensions, current.Reduction.ReducedDimensions);
            }
        }

        Assert.True(granted > 10, $"Only {granted} generated remediations were granted; the generator is too narrow.");
    }

    public static TheoryData<string> SingleFaults() =>
    [
        "no remediation", "no verification", "no discovery", "no diagnostic", "remediation read", "verification no steps",
        "diagnostic no tokens", "discovery no tools", "remediation window ended", "verification window ended", "request deadline past",
    ];

    [Theory]
    [MemberData(nameof(SingleFaults))]
    public void P3_SingleFaultRemediations_AreAttributedExactlyAsBefore(string fault)
    {
        var profiles = new Dictionary<AgentRoleKind, RoleProfile?>
        {
            [AgentRoleKind.Discovery] = Discovery(),
            [AgentRoleKind.Diagnostic] = Diagnostic(skills: ["sk.a"]),
            [AgentRoleKind.Remediation] = Remediation(targets: ["local"]),
            [AgentRoleKind.Verification] = Verification(targets: ["local"]),
        };
        var request = NoRequest;
        var ended = new MaintenanceWindow(T0.AddHours(-2), T0.AddHours(-1));
        switch (fault)
        {
            case "no remediation": profiles[AgentRoleKind.Remediation] = null; break;
            case "no verification": profiles[AgentRoleKind.Verification] = null; break;
            case "no discovery": profiles[AgentRoleKind.Discovery] = null; break;
            case "no diagnostic": profiles[AgentRoleKind.Diagnostic] = null; break;
            case "remediation read": profiles[AgentRoleKind.Remediation] = Remediation(targets: ["local"], risk: RiskLevel.Read); break;
            case "verification no steps": profiles[AgentRoleKind.Verification] = Verification(targets: ["local"], steps: 0); break;
            case "diagnostic no tokens": profiles[AgentRoleKind.Diagnostic] = Diagnostic(tokens: 0); break;
            case "discovery no tools": profiles[AgentRoleKind.Discovery] = Discovery(tools: []); break;
            case "remediation window ended": profiles[AgentRoleKind.Remediation] = profiles[AgentRoleKind.Remediation]! with { Window = ended }; break;
            case "verification window ended": profiles[AgentRoleKind.Verification] = profiles[AgentRoleKind.Verification]! with { Window = ended }; break;
            case "request deadline past": request = new DelegationAuthorityRequest(DeadlineUtc: T0.AddMinutes(-1)); break;
        }

        var source = new SpyProfiles([.. profiles.Values]);
        var frozen = FrozenPreAdr.DeriveRootAttributed(source, request, Operator, T0);
        var current = EnvelopeReducer.DeriveRootAttributed(source, remediation: true, request, Operator, T0);

        Assert.True(frozen.Reduction.IsDenied, fault);
        Assert.True(current.Reduction.IsDenied, fault);
        Assert.Equal(frozen.Role, current.Role);
        Assert.Equal(frozen.Reduction.Denial!.Dimension, current.Reduction.Denial!.Dimension);
    }

    [Fact]
    public void P3_AMultiFaultRemediation_StaysDenied_WithPipelineOrderAttribution()
    {
        // Discovery has no steps and Verification has no profile: the pre-ADR loop reported the missing profile first; the one
        // loop reports the first role in pipeline order. Either way it is denied.
        var source = new SpyProfiles(Discovery(steps: 0), Diagnostic(skills: ["sk.a"]), Remediation(targets: ["local"]), null);

        var frozen = FrozenPreAdr.DeriveRootAttributed(source, NoRequest, Operator, T0);
        var current = EnvelopeReducer.DeriveRootAttributed(source, remediation: true, NoRequest, Operator, T0);

        Assert.True(frozen.Reduction.IsDenied);
        Assert.Equal(AgentRoleKind.Verification, frozen.Role);
        Assert.True(current.Reduction.IsDenied);
        Assert.Equal(AgentRoleKind.Discovery, current.Role);
        Assert.Equal(EnvelopeDimension.Steps, current.Reduction.Denial!.Dimension);
    }

    // ---- P4: child subset, both shapes ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void P4_EveryRequiredRolesEnvelope_IsInsideRootProfileAndRequest(bool remediation)
    {
        var rng = new DeterministicRandom(remediation ? 0x4811_0005UL : 0x4811_0004UL);
        var checkedRoles = 0;
        for (var i = 0; i < Iterations; i++)
        {
            var source = new SpyProfiles([.. Enum.GetValues<AgentRoleKind>().Select(role => RandomProfile(rng, role))]);
            var request = RandomRequest(rng);
            var reduced = EnvelopeReducer.ReduceRequiredRoles(source, remediation, request, Operator, T0);
            Assert.Equal(RoleRequirements.RequiredRoles(remediation), reduced.Roles.Select(r => r.Role));

            foreach (var role in reduced.Roles.Where(r => !r.Reduction.IsDenied))
            {
                var child = role.Reduction.Envelope!;
                var root = reduced.Root!;
                var profile = source.GetProfile(role.Role)!;
                checkedRoles++;

                AssertSubset(child.AllowedSkills, root.AllowedSkills, profile.AllowedSkills, request.AllowedSkills);
                AssertSubset(child.AllowedCapabilities, root.AllowedCapabilities, profile.AllowedCapabilities, request.AllowedCapabilities);
                AssertSubset(child.AllowedTools, root.AllowedTools, profile.AllowedTools, request.AllowedTools);
                AssertSubset(child.AllowedTargets, root.AllowedTargets, profile.AllowedTargets, request.AllowedTargets);
                AssertSubset(child.AllowedEnvironments, root.AllowedEnvironments, profile.AllowedEnvironments, request.AllowedEnvironments);
                Assert.True(child.MaxRisk <= root.MaxRisk && child.MaxRisk <= profile.MaxRisk && child.MaxRisk <= (request.MaxRisk ?? RiskLevel.Critical));
                Assert.True(child.MaxRisk <= RoleRequirements.RiskCap(role.Role));
                Assert.True(child.MaxBlastRadius <= root.MaxBlastRadius && child.MaxBlastRadius <= profile.MaxBlastRadius
                    && child.MaxBlastRadius <= (request.MaxBlastRadius ?? BlastRadius.Fleet));
                Assert.True(child.Budget.MaxSteps <= root.Budget.MaxSteps && child.Budget.MaxSteps <= profile.MaxSteps && child.Budget.MaxSteps <= (request.MaxSteps ?? int.MaxValue));
                Assert.True(child.Budget.MaxTokens <= root.Budget.MaxTokens && child.Budget.MaxTokens <= profile.MaxTokens && child.Budget.MaxTokens <= (request.MaxTokens ?? int.MaxValue));
                Assert.True(child.Budget.DeadlineUtc <= root.Budget.DeadlineUtc && child.Budget.DeadlineUtc <= T0 + profile.MaxDuration);
                Assert.True(request.DeadlineUtc is not { } deadline || child.Budget.DeadlineUtc <= deadline);
                AssertWindowInside(child.Window, root.Window);
                AssertWindowInside(child.Window, profile.Window);
                AssertWindowInside(child.Window, request.Window);

                // Not applicable is forced empty, which is still a subset (ADR-0031 section 2).
                if (RoleRequirements.Of(role.Role, EnvelopeDimension.Skills) == EnvelopeRequirement.NotApplicable)
                {
                    Assert.Empty(child.AllowedSkills);
                }

                if (RoleRequirements.Of(role.Role, EnvelopeDimension.Tokens) == EnvelopeRequirement.NotApplicable)
                {
                    Assert.Equal(0, child.Budget.MaxTokens);
                }
            }
        }

        Assert.True(checkedRoles > 50, $"Only {checkedRoles} granted role envelopes were checked.");
    }

    // ---- the role-independence lemma (ADR-0044 section 5) ----

    [Fact]
    public void Lemma_ARolesResultWithANonNarrowingRequest_IsItsOwn_WhateverElseIsConfigured()
    {
        var rng = new DeterministicRandom(0x4811_0006);
        for (var i = 0; i < Iterations; i++)
        {
            var all = Enum.GetValues<AgentRoleKind>().Select(role => RandomProfile(rng, role)).ToArray();
            var reduced = EnvelopeReducer.ReduceRequiredRoles(new SpyProfiles(all), remediation: true, NoRequest, Operator, T0);
            foreach (var role in reduced.Roles)
            {
                var alone = EnvelopeReducer.ReduceConfiguredProfile(all.Single(p => p.Role == role.Role), T0);
                Assert.Equal(alone.IsDenied, role.Reduction.IsDenied);
                Assert.Equal(alone.Denial?.Dimension, role.Reduction.Denial?.Dimension);
            }
        }
    }

    // ---- generators and assertions ----

    private static ToolManifest Manifest(string name, RiskLevel risk) => new()
    {
        Name = name,
        Description = "x",
        Risk = risk,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [],
        Verification = risk == RiskLevel.Read ? null : new VerificationSpec("system.cpu", [], "x"),
    };

    private static string[] RandomSubset(DeterministicRandom rng, string[] universe) =>
        [.. universe.Where(_ => rng.Next(3) != 0)];

    private static MaintenanceWindow? RandomWindow(DeterministicRandom rng) => rng.Next(4) switch
    {
        0 => new MaintenanceWindow(T0.AddMinutes(-rng.Next(1, 60)), T0.AddMinutes(rng.Next(1, 240))),
        1 => new MaintenanceWindow(T0.AddMinutes(-rng.Next(61, 240)), T0.AddMinutes(-rng.Next(1, 60))),
        _ => null,
    };

    private static RoleProfile RandomProfile(DeterministicRandom rng, AgentRoleKind role)
    {
        var skills = RoleRequirements.Of(role, EnvelopeDimension.Skills) != EnvelopeRequirement.NotApplicable;
        var tokens = RoleRequirements.Of(role, EnvelopeDimension.Tokens) != EnvelopeRequirement.NotApplicable;
        return new RoleProfile(
            role,
            skills ? RandomSubset(rng, SkillUniverse) : [],
            skills ? RandomSubset(rng, CapabilityUniverse) : [],
            RandomSubset(rng, ToolUniverse),
            (RiskLevel)rng.Next(0, 5),
            (BlastRadius)rng.Next(0, 3),
            RandomSubset(rng, TargetUniverse),
            RandomSubset(rng, EnvironmentUniverse),
            rng.Next(0, 30),
            tokens ? rng.Next(0, 40_000) : 0,
            TimeSpan.FromMinutes(rng.Next(1, 90)),
            rng.Next(5) == 0 ? RandomWindow(rng) : null);
    }

    private static DelegationAuthorityRequest RandomRequest(DeterministicRandom rng) => new(
        rng.Next(3) == 0 ? RandomSubset(rng, SkillUniverse) : null,
        rng.Next(3) == 0 ? RandomSubset(rng, CapabilityUniverse) : null,
        rng.Next(3) == 0 ? RandomSubset(rng, ToolUniverse) : null,
        rng.Next(3) == 0 ? (RiskLevel)rng.Next(0, 5) : null,
        rng.Next(3) == 0 ? (BlastRadius)rng.Next(0, 3) : null,
        rng.Next(3) == 0 ? RandomSubset(rng, TargetUniverse) : null,
        rng.Next(3) == 0 ? RandomSubset(rng, EnvironmentUniverse) : null,
        rng.Next(6) == 0 ? new MaintenanceWindow(T0.AddMinutes(-10), T0.AddHours(2)) : null,
        rng.Next(3) == 0 ? rng.Next(1, 40) : null,
        rng.Next(3) == 0 ? rng.Next(1, 50_000) : null,
        rng.Next(6) == 0 ? T0.AddMinutes(rng.Next(1, 200)) : null);

    private static void AssertSubset(IReadOnlyList<string> child, IReadOnlyList<string> root, IReadOnlyList<string> profile, IReadOnlyList<string>? request)
    {
        Assert.All(child, member => Assert.Contains(member, root));
        Assert.All(child, member => Assert.Contains(member, profile));
        if (request is not null)
        {
            Assert.All(child, member => Assert.Contains(member, request));
        }
    }

    private static void AssertWindowInside(MaintenanceWindow? child, MaintenanceWindow? outer)
    {
        if (outer is null)
        {
            return;
        }

        Assert.NotNull(child);
        Assert.True(child!.StartUtc >= outer.StartUtc && child.EndUtc <= outer.EndUtc);
    }

    /// <summary>
    /// A frozen copy of the pre-ADR-0044 root derivation (<c>main</c> <c>9953050</c>, <c>EnvelopeReducer.DeriveRootAttributed</c>
    /// and <c>BuildRoot</c>): every role looked up first, then the request, then the four-role root and every role reduced from
    /// it. Kept only so P3 can show the remediation shape is unchanged; never used by production code.
    /// </summary>
    private static class FrozenPreAdr
    {
        public static (EnvelopeReduction Reduction, AgentRoleKind? Role) DeriveRootAttributed(
            IRoleProfileSource profiles, DelegationAuthorityRequest request, ActorIdentity originator, DateTimeOffset now)
        {
            var roleProfiles = new List<RoleProfile>();
            foreach (var role in RoleRequirements.Pipeline)
            {
                var profile = profiles.GetProfile(role);
                if (profile is null)
                {
                    return (EnvelopeReduction.Denied(EnvelopeDimension.Profile, "no profile"), role);
                }

                if (profile.Role != role)
                {
                    return (EnvelopeReduction.Denied(EnvelopeDimension.Profile, "wrong profile"), role);
                }

                roleProfiles.Add(profile);
            }

            if (request.DeadlineUtc is { } requestedDeadline && requestedDeadline <= now)
            {
                return (EnvelopeReduction.Denied(EnvelopeDimension.Deadline, "past"), null);
            }

            if (request.Window is { } requestedWindow && requestedWindow.EndUtc <= now)
            {
                return (EnvelopeReduction.Denied(EnvelopeDimension.MaintenanceWindow, "ended"), null);
            }

            var root = BuildRoot(roleProfiles, request, originator, now);
            foreach (var profile in roleProfiles)
            {
                var reduction = EnvelopeReducer.ReduceForRole(root, profile.Role, profile, request, now);
                if (reduction.IsDenied)
                {
                    return (reduction, profile.Role);
                }

                if (reduction.Envelope!.Window is { } window && window.EndUtc <= now)
                {
                    return (EnvelopeReduction.Denied(EnvelopeDimension.MaintenanceWindow, "role window ended"), profile.Role);
                }
            }

            var unnarrowed = BuildRoot(roleProfiles, new DelegationAuthorityRequest(), originator, now);
            return (EnvelopeReduction.Granted(root, Narrowed(unnarrowed, root)), null);
        }

        private static AuthorityEnvelope BuildRoot(
            List<RoleProfile> profiles, DelegationAuthorityRequest request, ActorIdentity originator, DateTimeOffset now)
        {
            var maxRisk = profiles.Max(p => Min(p.MaxRisk, RoleRequirements.RiskCap(p.Role)));
            var maxBlastRadius = profiles.Max(p => p.MaxBlastRadius);
            var steps = (int)Math.Min(profiles.Sum(p => (long)p.MaxSteps), int.MaxValue);
            var tokens = (int)Math.Min(profiles.Sum(p => (long)p.MaxTokens), int.MaxValue);
            var duration = TimeSpan.FromTicks(profiles.Sum(p => p.MaxDuration.Ticks));
            var deadline = now + duration;

            return new AuthorityEnvelope(
                originator,
                0,
                NarrowTo(Union(profiles.Select(p => p.AllowedSkills)), request.AllowedSkills),
                NarrowTo(Union(profiles.Select(p => p.AllowedCapabilities)), request.AllowedCapabilities),
                NarrowTo(Union(profiles.Select(p => p.AllowedTools)), request.AllowedTools),
                request.MaxRisk is { } risk ? Min(risk, maxRisk) : maxRisk,
                request.MaxBlastRadius is { } blast ? Min(blast, maxBlastRadius) : maxBlastRadius,
                NarrowTo(Union(profiles.Select(p => p.AllowedTargets)), request.AllowedTargets),
                NarrowTo(Union(profiles.Select(p => p.AllowedEnvironments)), request.AllowedEnvironments),
                new DelegationBudget(
                    request.MaxSteps is { } requestedSteps ? Math.Min(requestedSteps, steps) : steps,
                    request.MaxTokens is { } requestedTokens ? Math.Min(requestedTokens, tokens) : tokens,
                    request.DeadlineUtc is { } requestedDeadline && requestedDeadline < deadline ? requestedDeadline : deadline),
                request.Window);
        }

        // The dimensions the pre-ADR root reported as narrowed by the request, in EnvelopeDimension order.
        private static List<EnvelopeDimension> Narrowed(AuthorityEnvelope before, AuthorityEnvelope after)
        {
            var narrowed = new List<EnvelopeDimension>();
            void Set(EnvelopeDimension dimension, IReadOnlyList<string> b, IReadOnlyList<string> a)
            {
                if (a.Distinct(StringComparer.Ordinal).Count() < b.Distinct(StringComparer.Ordinal).Count())
                {
                    narrowed.Add(dimension);
                }
            }

            Set(EnvelopeDimension.Skills, before.AllowedSkills, after.AllowedSkills);
            Set(EnvelopeDimension.Capabilities, before.AllowedCapabilities, after.AllowedCapabilities);
            Set(EnvelopeDimension.Tools, before.AllowedTools, after.AllowedTools);
            if (after.MaxRisk < before.MaxRisk)
            {
                narrowed.Add(EnvelopeDimension.Risk);
            }

            if (after.MaxBlastRadius < before.MaxBlastRadius)
            {
                narrowed.Add(EnvelopeDimension.BlastRadius);
            }

            Set(EnvelopeDimension.Targets, before.AllowedTargets, after.AllowedTargets);
            Set(EnvelopeDimension.Environments, before.AllowedEnvironments, after.AllowedEnvironments);
            if (before.Window is null ? after.Window is not null : after.Window is not null && (after.Window.StartUtc > before.Window.StartUtc || after.Window.EndUtc < before.Window.EndUtc))
            {
                narrowed.Add(EnvelopeDimension.MaintenanceWindow);
            }

            if (after.Budget.MaxSteps < before.Budget.MaxSteps)
            {
                narrowed.Add(EnvelopeDimension.Steps);
            }

            if (after.Budget.MaxTokens < before.Budget.MaxTokens)
            {
                narrowed.Add(EnvelopeDimension.Tokens);
            }

            if (after.Budget.DeadlineUtc < before.Budget.DeadlineUtc)
            {
                narrowed.Add(EnvelopeDimension.Deadline);
            }

            return narrowed;
        }

        private static List<string> Union(IEnumerable<IReadOnlyList<string>> sets) =>
            [.. sets.SelectMany(set => set).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        private static List<string> NarrowTo(List<string> members, IReadOnlyList<string>? request) =>
            request is null ? members : [.. members.Intersect(request, StringComparer.Ordinal).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        private static T Min<T>(T first, T second)
            where T : struct, Enum => Comparer<T>.Default.Compare(first, second) <= 0 ? first : second;
    }
}
