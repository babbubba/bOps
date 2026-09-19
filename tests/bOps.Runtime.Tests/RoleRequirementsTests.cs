// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0031 section 1: the per-role table of envelope dimensions. The expected cells below are typed out
/// from the ADR, deliberately not derived from <c>RoleRequirements</c>, so this test fails when the code
/// and the ADR disagree in either direction. Changing a cell needs a new ADR.
/// </summary>
public sealed class RoleRequirementsTests
{
    // One column per role, in pipeline order: Discovery, Diagnostic, Remediation, Verification.
    // R = required, O = optional, N = not applicable.
    internal static readonly (EnvelopeDimension Dimension, string Cells)[] Adr0031Table =
    [
        (EnvelopeDimension.Originator, "RRRR"),
        (EnvelopeDimension.Depth, "RRRR"),
        (EnvelopeDimension.Skills, "NORN"),
        (EnvelopeDimension.Capabilities, "NORN"),
        (EnvelopeDimension.Tools, "RRRR"),
        (EnvelopeDimension.Risk, "RRRR"),
        (EnvelopeDimension.BlastRadius, "RRRR"),
        (EnvelopeDimension.Targets, "RRRR"),
        (EnvelopeDimension.Environments, "RRRR"),
        (EnvelopeDimension.MaintenanceWindow, "OOOO"),
        (EnvelopeDimension.Steps, "RRRR"),
        (EnvelopeDimension.Tokens, "RRNN"),
        (EnvelopeDimension.Deadline, "RRRR"),
    ];

    /// <summary>The ADR's cell for a role and a dimension: <c>R</c>, <c>O</c> or <c>N</c>.</summary>
    internal static char Cell(AgentRoleKind role, EnvelopeDimension dimension) =>
        Adr0031Table.Single(row => row.Dimension == dimension).Cells[(int)role];

    public static TheoryData<AgentRoleKind, EnvelopeDimension, string> Cells()
    {
        var data = new TheoryData<AgentRoleKind, EnvelopeDimension, string>();
        foreach (var (dimension, cells) in Adr0031Table)
        {
            foreach (var role in Enum.GetValues<AgentRoleKind>())
            {
                data.Add(role, dimension, cells[(int)role] switch
                {
                    'R' => nameof(EnvelopeRequirement.Required),
                    'O' => nameof(EnvelopeRequirement.Optional),
                    _ => nameof(EnvelopeRequirement.NotApplicable),
                });
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Of_MatchesTheAdr0031Table(AgentRoleKind role, EnvelopeDimension dimension, string expected)
    {
        Assert.Equal(expected, RoleRequirements.Of(role, dimension).ToString());
    }

    [Fact]
    public void Table_CoversEveryDimensionOfTheEnvelopeAndEveryRole()
    {
        var fieldsOfTheEnvelope = Enum.GetValues<EnvelopeDimension>().Where(d => d != EnvelopeDimension.Profile).ToList();

        Assert.Equal(fieldsOfTheEnvelope.Count, Adr0031Table.Length);
        Assert.Equal(fieldsOfTheEnvelope.OrderBy(d => d), Adr0031Table.Select(r => r.Dimension).OrderBy(d => d));
        Assert.All(Adr0031Table, row => Assert.Equal(Enum.GetValues<AgentRoleKind>().Length, row.Cells.Length));
    }

    [Fact]
    public void Of_RefusesTheProfileMarker_BecauseItIsNotAFieldOfTheEnvelope()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RoleRequirements.Of(AgentRoleKind.Discovery, EnvelopeDimension.Profile));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoleRequirements.Of(AgentRoleKind.Discovery, (EnvelopeDimension)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoleRequirements.Of((AgentRoleKind)99, EnvelopeDimension.Tools));
    }

    [Fact]
    public void OnlyRemediationActs_SoOnlyRemediationMayExceedRead()
    {
        Assert.Equal(
            [AgentRoleKind.Discovery, AgentRoleKind.Diagnostic, AgentRoleKind.Verification],
            RoleRequirements.Pipeline.Where(RoleRequirements.IsReadOnly));

        foreach (var role in RoleRequirements.Pipeline.Where(RoleRequirements.IsReadOnly))
        {
            Assert.Equal(RiskLevel.Read, RoleRequirements.RiskCap(role));
            Assert.Equal(RiskLevel.Read, RoleRequirements.RiskFloor(role));
        }

        Assert.Equal(RiskLevel.Critical, RoleRequirements.RiskCap(AgentRoleKind.Remediation));
        Assert.Equal(RiskLevel.Low, RoleRequirements.RiskFloor(AgentRoleKind.Remediation));
    }

    [Fact]
    public void Pipeline_IsTheFixedSequenceOfAdr0030()
    {
        Assert.Equal(
            [AgentRoleKind.Discovery, AgentRoleKind.Diagnostic, AgentRoleKind.Remediation, AgentRoleKind.Verification],
            RoleRequirements.Pipeline);
        Assert.Equal(1, RoleRequirements.RoleDepth);
    }
}
