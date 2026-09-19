// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// V1.2-D: the audit event of a refused delegation names the role it is about. <see cref="EnvelopeReducer.DeriveRootAttributed"/>
/// is the same derivation as <see cref="EnvelopeReducer.DeriveRoot"/> and says which role a refusal belongs to, or none when
/// the request itself was refused.
/// </summary>
public sealed class EnvelopeRootAttributionTests
{
    private static readonly ActorIdentity Operator = ActorIdentity.FromOperatingSystemUser("operator");
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private sealed class Profiles(Dictionary<AgentRoleKind, RoleProfile> byRole) : IRoleProfileSource
    {
        public RoleProfile? GetProfile(AgentRoleKind role) => byRole.GetValueOrDefault(role);
    }

    private static RoleProfile Profile(AgentRoleKind role, RiskLevel? risk = null, MaintenanceWindow? window = null)
    {
        var usesSkills = role is AgentRoleKind.Diagnostic or AgentRoleKind.Remediation;
        var usesModel = role is AgentRoleKind.Discovery or AgentRoleKind.Diagnostic;
        return new RoleProfile(
            role,
            usesSkills ? ["s"] : [], usesSkills ? ["c"] : [], ["t"],
            risk ?? (role == AgentRoleKind.Remediation ? RiskLevel.High : RiskLevel.Read),
            BlastRadius.Single, ["local"], ["test"], 5, usesModel ? 1000 : 0, TimeSpan.FromMinutes(5), window);
    }

    private static Dictionary<AgentRoleKind, RoleProfile> All() =>
        Enum.GetValues<AgentRoleKind>().ToDictionary(role => role, role => Profile(role));

    [Fact]
    public void AGrantedRoot_IsTheSameAsDeriveRoot_AndNamesNoRole()
    {
        var source = new Profiles(All());

        var (reduction, role) = EnvelopeReducer.DeriveRootAttributed(source, new DelegationAuthorityRequest(), Operator, Now);

        Assert.False(reduction.IsDenied);
        Assert.Null(role);
        Assert.Equal(
            DelegationHasher.ComputeEnvelopeHash(EnvelopeReducer.DeriveRoot(source, new DelegationAuthorityRequest(), Operator, Now).Envelope!),
            DelegationHasher.ComputeEnvelopeHash(reduction.Envelope!));
    }

    [Theory]
    [InlineData(AgentRoleKind.Discovery)]
    [InlineData(AgentRoleKind.Diagnostic)]
    [InlineData(AgentRoleKind.Remediation)]
    [InlineData(AgentRoleKind.Verification)]
    public void AMissingProfile_IsAttributedToThatRole(AgentRoleKind missing)
    {
        var byRole = All();
        byRole.Remove(missing);

        var (reduction, role) = EnvelopeReducer.DeriveRootAttributed(new Profiles(byRole), new DelegationAuthorityRequest(), Operator, Now);

        Assert.Equal(EnvelopeDimension.Profile, reduction.Denial!.Dimension);
        Assert.Equal(missing, role);
    }

    [Fact]
    public void ARoleThatCannotBeDerived_IsAttributedToThatRole()
    {
        // Remediation with a ceiling of Read could execute nothing (ADR-0031 section 1).
        var byRole = All();
        byRole[AgentRoleKind.Remediation] = Profile(AgentRoleKind.Remediation, risk: RiskLevel.Read);

        var (reduction, role) = EnvelopeReducer.DeriveRootAttributed(new Profiles(byRole), new DelegationAuthorityRequest(), Operator, Now);

        Assert.True(reduction.IsDenied);
        Assert.Equal(AgentRoleKind.Remediation, role);
    }

    [Fact]
    public void ARoleWhoseWindowHasAlreadyEnded_IsAttributedToThatRole()
    {
        var byRole = All();
        byRole[AgentRoleKind.Verification] = Profile(AgentRoleKind.Verification, window: new MaintenanceWindow(Now.AddHours(-2), Now.AddHours(-1)));

        var (reduction, role) = EnvelopeReducer.DeriveRootAttributed(new Profiles(byRole), new DelegationAuthorityRequest(), Operator, Now);

        Assert.True(reduction.IsDenied);
        Assert.Equal(AgentRoleKind.Verification, role);
    }

    [Fact]
    public void ARefusalOfTheRequestItself_BelongsToNoRole()
    {
        var source = new Profiles(All());

        var (pastDeadline, deadlineRole) = EnvelopeReducer.DeriveRootAttributed(
            source, new DelegationAuthorityRequest(DeadlineUtc: Now.AddMinutes(-1)), Operator, Now);
        var (pastWindow, windowRole) = EnvelopeReducer.DeriveRootAttributed(
            source, new DelegationAuthorityRequest(Window: new MaintenanceWindow(Now.AddHours(-2), Now.AddHours(-1))), Operator, Now);

        Assert.Equal(EnvelopeDimension.Deadline, pastDeadline.Denial!.Dimension);
        Assert.Null(deadlineRole);
        Assert.Equal(EnvelopeDimension.MaintenanceWindow, pastWindow.Denial!.Dimension);
        Assert.Null(windowRole);
    }
}
