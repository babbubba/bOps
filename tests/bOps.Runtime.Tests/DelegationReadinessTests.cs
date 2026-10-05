// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0044 sections 5–7 and 20 (P5): the readiness evaluator is a projection of the reducer's own loop. The matrix M1–M12
/// row by row, the HTTP-independent contract (four roles, order, states, reason codes, the <c>ready</c> rule), and readiness
/// predicting the real start for stable single-fault inputs and staying denied for multi-fault ones.
/// </summary>
public sealed class DelegationReadinessTests
{
    private static readonly ActorIdentity Operator = ActorIdentity.FromOperatingSystemUser("operator");
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class Profiles(params RoleProfile?[] profiles) : IRoleProfileSource
    {
        private readonly Dictionary<AgentRoleKind, RoleProfile> _byRole = profiles.OfType<RoleProfile>().ToDictionary(p => p.Role);

        public RoleProfile? GetProfile(AgentRoleKind role) => _byRole.GetValueOrDefault(role);
    }

    private sealed class WrongRoleProfiles(AgentRoleKind asked, RoleProfile served, IRoleProfileSource rest) : IRoleProfileSource
    {
        public RoleProfile? GetProfile(AgentRoleKind role) => role == asked ? served : rest.GetProfile(role);
    }

    private static RoleProfile ReadOnly(AgentRoleKind role, IReadOnlyList<string>? tools = null, int tokens = 150_000) => new(
        role, [], [], tools ?? ["system.cpu"], RiskLevel.Read, BlastRadius.Single, ["local"], ["local"], 15,
        role is AgentRoleKind.Discovery or AgentRoleKind.Diagnostic ? tokens : 0, TimeSpan.FromMinutes(30));

    private static RoleProfile Remediation(RiskLevel risk = RiskLevel.High) => new(
        AgentRoleKind.Remediation, ["sk.a"], ["cap.a"], ["service.restart"], risk, BlastRadius.Single, ["local"], ["local"], 5, 0, TimeSpan.FromMinutes(5));

    private static readonly IReadOnlyList<ToolManifest> NoTools = [];

    private static DelegationReadiness Evaluate(IRoleProfileSource source, bool remediation, PolicyLoadState policy = PolicyLoadState.Loaded) =>
        DelegationReadinessEvaluator.Evaluate(source, policy, NoTools, remediation, T0);

    private static void AssertRoles(DelegationReadiness readiness, params RoleReadinessState[] states)
    {
        Assert.Equal(
            [AgentRoleKind.Discovery, AgentRoleKind.Diagnostic, AgentRoleKind.Remediation, AgentRoleKind.Verification],
            readiness.Roles.Select(role => role.Role));
        Assert.Equal(states, readiness.Roles.Select(role => role.State));
    }

    // ---- the matrix of ADR-0044 section 7 ----

    [Fact]
    public void M1_FreshInstallDiagnosis_DiscoveryAndDiagnosticMissing_TheOthersNotRequired()
    {
        var readiness = Evaluate(new Profiles(), remediation: false, PolicyLoadState.NoFile);

        AssertRoles(readiness, RoleReadinessState.Missing, RoleReadinessState.Missing, RoleReadinessState.NotRequired, RoleReadinessState.NotRequired);
        Assert.False(readiness.Ready);
        Assert.Equal(PolicyLoadState.NoFile, readiness.Policy);
        Assert.Equal("profile_missing", readiness.Roles[0].ReasonCode);
        Assert.Equal(EnvelopeDimension.Profile, readiness.Roles[0].Dimension);
        Assert.Equal("Discovery role: no usable profile is configured.", readiness.Roles[0].Reason);
        Assert.Equal("Diagnostic role: no usable profile is configured.", readiness.Roles[1].Reason);
        Assert.All(readiness.Roles.Skip(2), role =>
        {
            Assert.Equal("not_required", role.ReasonCode);
            Assert.Null(role.Dimension);
            Assert.Null(role.Reason);
        });
        Assert.Equal(T0, readiness.EvaluatedAtUtc);
        Assert.False(readiness.Remediation);
    }

    [Fact]
    public void M2_FreshInstallRemediation_AllFourMissing()
    {
        var readiness = Evaluate(new Profiles(), remediation: true, PolicyLoadState.NoFile);

        AssertRoles(readiness, RoleReadinessState.Missing, RoleReadinessState.Missing, RoleReadinessState.Missing, RoleReadinessState.Missing);
        Assert.False(readiness.Ready);
        Assert.True(readiness.Remediation);
    }

    [Fact]
    public void M3_ReadOnlySetup_IsReadyForADiagnosis()
    {
        var readiness = Evaluate(new Profiles(ReadOnly(AgentRoleKind.Discovery), ReadOnly(AgentRoleKind.Diagnostic)), remediation: false);

        AssertRoles(readiness, RoleReadinessState.Ready, RoleReadinessState.Ready, RoleReadinessState.NotRequired, RoleReadinessState.NotRequired);
        Assert.True(readiness.Ready);
        Assert.All(readiness.Roles.Take(2), role =>
        {
            Assert.Equal("ready", role.ReasonCode);
            Assert.Null(role.Dimension);
            Assert.Null(role.Reason);
        });
    }

    [Fact]
    public void M4_ReadOnlySetup_IsNotReadyForARemediation()
    {
        var readiness = Evaluate(new Profiles(ReadOnly(AgentRoleKind.Discovery), ReadOnly(AgentRoleKind.Diagnostic)), remediation: true);

        AssertRoles(readiness, RoleReadinessState.Ready, RoleReadinessState.Ready, RoleReadinessState.Missing, RoleReadinessState.Missing);
        Assert.False(readiness.Ready);
        Assert.Equal("Remediation role: no usable profile is configured.", readiness.Roles[2].Reason);
    }

    [Fact]
    public void M5_M6_AllFourValid_AreReadyForBothShapes()
    {
        var all = new Profiles(ReadOnly(AgentRoleKind.Discovery), ReadOnly(AgentRoleKind.Diagnostic), Remediation(), ReadOnly(AgentRoleKind.Verification));

        var diagnosis = Evaluate(all, remediation: false);
        var remediation = Evaluate(all, remediation: true);

        AssertRoles(diagnosis, RoleReadinessState.Ready, RoleReadinessState.Ready, RoleReadinessState.NotRequired, RoleReadinessState.NotRequired);
        Assert.True(diagnosis.Ready);
        AssertRoles(remediation, RoleReadinessState.Ready, RoleReadinessState.Ready, RoleReadinessState.Ready, RoleReadinessState.Ready);
        Assert.True(remediation.Ready);
    }

    [Fact]
    public void M7_M8_APolicyThatFailedToLoad_MakesEveryRequiredRoleNotUsable_WithAFixedReason()
    {
        var diagnosis = Evaluate(new Profiles(), remediation: false, PolicyLoadState.LoadFailed);
        var remediation = Evaluate(new Profiles(), remediation: true, PolicyLoadState.LoadFailed);

        AssertRoles(diagnosis, RoleReadinessState.Malformed, RoleReadinessState.Malformed, RoleReadinessState.NotRequired, RoleReadinessState.NotRequired);
        AssertRoles(remediation, RoleReadinessState.Malformed, RoleReadinessState.Malformed, RoleReadinessState.Malformed, RoleReadinessState.Malformed);
        Assert.All(remediation.Roles, role =>
        {
            Assert.Equal("policy_load_failed", role.ReasonCode);
            Assert.Equal(EnvelopeDimension.Profile, role.Dimension);
            Assert.Equal(DelegationReadinessEvaluator.PolicyLoadFailedReason, role.Reason);
        });
        Assert.False(diagnosis.Ready);
        Assert.Equal(PolicyLoadState.LoadFailed, diagnosis.Policy);
        Assert.Equal(1, diagnosis.ProfileDriftCount);
    }

    [Fact]
    public void M9_ADiagnosticWithNoTokens_IsNotUsable_OnTheTokensDimension()
    {
        var readiness = Evaluate(new Profiles(ReadOnly(AgentRoleKind.Discovery), ReadOnly(AgentRoleKind.Diagnostic, tokens: 0)), remediation: false);

        AssertRoles(readiness, RoleReadinessState.Ready, RoleReadinessState.Malformed, RoleReadinessState.NotRequired, RoleReadinessState.NotRequired);
        Assert.Equal("reduction_denied", readiness.Roles[1].ReasonCode);
        Assert.Equal(EnvelopeDimension.Tokens, readiness.Roles[1].Dimension);
        Assert.False(readiness.Ready);
    }

    [Fact]
    public void M10_M11_ARemediationWithAReadCeiling_DoesNotBlockADiagnosis_ButIsNotUsableForARemediation()
    {
        var source = new Profiles(
            ReadOnly(AgentRoleKind.Discovery), ReadOnly(AgentRoleKind.Diagnostic), Remediation(RiskLevel.Read), ReadOnly(AgentRoleKind.Verification));

        var diagnosis = Evaluate(source, remediation: false);
        var remediation = Evaluate(source, remediation: true);

        Assert.True(diagnosis.Ready);
        AssertRoles(remediation, RoleReadinessState.Ready, RoleReadinessState.Ready, RoleReadinessState.Malformed, RoleReadinessState.Ready);
        Assert.Equal(EnvelopeDimension.Risk, remediation.Roles[2].Dimension);
        Assert.Equal("reduction_denied", remediation.Roles[2].ReasonCode);
        Assert.False(remediation.Ready);
    }

    [Fact]
    public void M12_ADiscoveryWithNoTools_IsNotUsable_OnTheToolsDimension()
    {
        var readiness = Evaluate(new Profiles(ReadOnly(AgentRoleKind.Discovery, tools: []), ReadOnly(AgentRoleKind.Diagnostic)), remediation: false);

        AssertRoles(readiness, RoleReadinessState.Malformed, RoleReadinessState.Ready, RoleReadinessState.NotRequired, RoleReadinessState.NotRequired);
        Assert.Equal(EnvelopeDimension.Tools, readiness.Roles[0].Dimension);
    }

    [Fact]
    public void AProfileForAnotherRole_IsNotUsable_WithItsOwnReasonCode()
    {
        var rest = new Profiles(ReadOnly(AgentRoleKind.Discovery), ReadOnly(AgentRoleKind.Diagnostic));
        var source = new WrongRoleProfiles(AgentRoleKind.Diagnostic, ReadOnly(AgentRoleKind.Verification), rest);

        var readiness = Evaluate(source, remediation: false);

        Assert.Equal(RoleReadinessState.Malformed, readiness.Roles[1].State);
        Assert.Equal("profile_for_other_role", readiness.Roles[1].ReasonCode);
        Assert.Equal(EnvelopeDimension.Profile, readiness.Roles[1].Dimension);
    }

    [Fact]
    public void Ready_IsTrueExactlyWhenEveryRequiredRoleIsReady()
    {
        var rng = new DeterministicRandom(0x4811_0101);
        for (var i = 0; i < 300; i++)
        {
            var source = RandomSource(rng);
            foreach (var remediation in new[] { false, true })
            {
                var readiness = Evaluate(source, remediation);
                Assert.Equal(4, readiness.Roles.Count);
                Assert.Equal(
                    readiness.Roles.Where(r => r.State != RoleReadinessState.NotRequired).All(r => r.State == RoleReadinessState.Ready),
                    readiness.Ready);
                Assert.All(readiness.Roles, role => Assert.True(role.Reason is null || role.Reason.Length <= 500));
            }
        }
    }

    [Fact]
    public void ProfileDriftCount_IsInformationOnly_AndNeverChangesReady()
    {
        var tools = new List<ToolManifest> { Manifest("system.cpu", RiskLevel.Read), Manifest("system.memory", RiskLevel.Read) };
        var source = new Profiles(ReadOnly(AgentRoleKind.Discovery, tools: ["system.cpu", "gone.tool"]), ReadOnly(AgentRoleKind.Diagnostic));

        var readiness = DelegationReadinessEvaluator.Evaluate(source, PolicyLoadState.Loaded, tools, remediation: false, T0);

        Assert.True(readiness.Ready);
        Assert.Equal(ProfileDrift.Evaluate(source, PolicyLoadState.Loaded, tools, T0).Count, readiness.ProfileDriftCount);
        Assert.Equal(3, readiness.ProfileDriftCount);
    }

    // ---- P5: readiness predicts the real start ----

    [Fact]
    public void P5_ReadinessReady_IffTheRootIsGranted_AndANonReadyRoleIsTheStartsDenialWithTheSameDimension()
    {
        var rng = new DeterministicRandom(0x4811_0102);
        for (var i = 0; i < 600; i++)
        {
            var source = RandomSource(rng);
            foreach (var remediation in new[] { false, true })
            {
                var readiness = Evaluate(source, remediation);
                var (start, role) = EnvelopeReducer.DeriveRootAttributed(source, remediation, new DelegationAuthorityRequest(), Operator, T0);

                Assert.Equal(readiness.Ready, !start.IsDenied);
                if (!readiness.Ready)
                {
                    // Multi-fault inputs: both not ready and denied, attributed to the first non-ready role in pipeline order.
                    var first = readiness.Roles.First(r => r.State is not (RoleReadinessState.Ready or RoleReadinessState.NotRequired));
                    Assert.Equal(first.Role, role);
                    Assert.Equal(first.Dimension, start.Denial!.Dimension);
                    if (first.ReasonCode is "profile_missing" or "reduction_denied")
                    {
                        Assert.Equal(first.Reason, start.Denial.Reason);
                    }
                }
            }
        }
    }

    public static TheoryData<string, bool> SingleFaultInputs() => new()
    {
        { "no discovery", false }, { "no diagnostic", false }, { "diagnostic no tokens", false }, { "discovery no tools", false },
        { "discovery no steps", false }, { "no remediation", true }, { "no verification", true }, { "remediation read", true },
        { "verification window ended", true }, { "diagnostic window ended", false },
    };

    [Theory]
    [MemberData(nameof(SingleFaultInputs))]
    public void P5_SingleFault_ReadinessAndStartAgreeOnStateRoleDimensionAndReason(string fault, bool remediation)
    {
        var ended = new MaintenanceWindow(T0.AddHours(-2), T0.AddHours(-1));
        var profiles = new Dictionary<AgentRoleKind, RoleProfile?>
        {
            [AgentRoleKind.Discovery] = ReadOnly(AgentRoleKind.Discovery),
            [AgentRoleKind.Diagnostic] = ReadOnly(AgentRoleKind.Diagnostic),
            [AgentRoleKind.Remediation] = Remediation(),
            [AgentRoleKind.Verification] = ReadOnly(AgentRoleKind.Verification),
        };
        switch (fault)
        {
            case "no discovery": profiles[AgentRoleKind.Discovery] = null; break;
            case "no diagnostic": profiles[AgentRoleKind.Diagnostic] = null; break;
            case "diagnostic no tokens": profiles[AgentRoleKind.Diagnostic] = ReadOnly(AgentRoleKind.Diagnostic, tokens: 0); break;
            case "discovery no tools": profiles[AgentRoleKind.Discovery] = ReadOnly(AgentRoleKind.Discovery, tools: []); break;
            case "discovery no steps": profiles[AgentRoleKind.Discovery] = ReadOnly(AgentRoleKind.Discovery) with { MaxSteps = 0 }; break;
            case "no remediation": profiles[AgentRoleKind.Remediation] = null; break;
            case "no verification": profiles[AgentRoleKind.Verification] = null; break;
            case "remediation read": profiles[AgentRoleKind.Remediation] = Remediation(RiskLevel.Read); break;
            case "verification window ended": profiles[AgentRoleKind.Verification] = ReadOnly(AgentRoleKind.Verification) with { Window = ended }; break;
            case "diagnostic window ended": profiles[AgentRoleKind.Diagnostic] = ReadOnly(AgentRoleKind.Diagnostic) with { Window = ended }; break;
        }

        var source = new Profiles([.. profiles.Values]);
        var readiness = Evaluate(source, remediation);
        var (start, role) = EnvelopeReducer.DeriveRootAttributed(source, remediation, new DelegationAuthorityRequest(), Operator, T0);

        var notReady = Assert.Single(readiness.Roles, r => r.State is RoleReadinessState.Missing or RoleReadinessState.Malformed);
        Assert.True(start.IsDenied);
        Assert.Equal(notReady.Role, role);
        Assert.Equal(notReady.Dimension, start.Denial!.Dimension);
        Assert.Equal(notReady.Reason, start.Denial.Reason);
    }

    [Fact]
    public void P5_PolicyLoadFailed_StartReportsTheProfileMissingTextOnTheSameRoleAndDimension()
    {
        var readiness = Evaluate(new Profiles(), remediation: false, PolicyLoadState.LoadFailed);
        var (start, role) = EnvelopeReducer.DeriveRootAttributed(new Profiles(), remediation: false, new DelegationAuthorityRequest(), Operator, T0);

        Assert.Equal(AgentRoleKind.Discovery, role);
        Assert.Equal(readiness.Roles[0].Dimension, start.Denial!.Dimension);
        Assert.Equal("Discovery role: no usable profile is configured.", start.Denial.Reason);
        Assert.Equal("policy_load_failed", readiness.Roles[0].ReasonCode);
    }

    // ---- helpers ----

    private static ToolManifest Manifest(string name, RiskLevel risk) => new()
    {
        Name = name,
        Description = "x",
        Risk = risk,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [],
    };

    private static Profiles RandomSource(DeterministicRandom rng)
    {
        RoleProfile? Maybe(AgentRoleKind role) => rng.Next(5) == 0 ? null : RandomProfile(rng, role);
        return new Profiles(Maybe(AgentRoleKind.Discovery), Maybe(AgentRoleKind.Diagnostic), Maybe(AgentRoleKind.Remediation), Maybe(AgentRoleKind.Verification));
    }

    private static RoleProfile RandomProfile(DeterministicRandom rng, AgentRoleKind role)
    {
        var skills = RoleRequirements.Of(role, EnvelopeDimension.Skills) != EnvelopeRequirement.NotApplicable;
        var tokens = RoleRequirements.Of(role, EnvelopeDimension.Tokens) != EnvelopeRequirement.NotApplicable;
        string[] Subset(string[] universe) => [.. universe.Where(_ => rng.Next(4) != 0)];
        return new RoleProfile(
            role,
            skills ? Subset(["sk.a", "sk.b"]) : [],
            skills ? Subset(["cap.a"]) : [],
            Subset(["system.cpu", "service.restart"]),
            (RiskLevel)rng.Next(0, 5),
            (BlastRadius)rng.Next(0, 3),
            Subset(["local", "node-1"]),
            Subset(["local"]),
            rng.Next(0, 6),
            tokens ? rng.Next(0, 3) * 1000 : 0,
            TimeSpan.FromMinutes(rng.Next(1, 60)),
            rng.Next(6) == 0 ? new MaintenanceWindow(T0.AddHours(-2), T0.AddMinutes(rng.Next(-60, 60) | 1)) : null);
    }
}
