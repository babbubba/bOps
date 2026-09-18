// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0022: the 1.0 contract is additive-only. The C# compiler inlines an enum member's numeric value
/// into every assembly built against it, so inserting a member in the middle of a 1.0 enum silently
/// changes what an already-built package declares — and a name-based API diff cannot see it. That is
/// exactly how <c>ToolParameterType.PathList</c> once renumbered <c>Duration</c> and <c>Enum</c>. This pins
/// every 1.0 enum member to the value it had at the V1.0 close-out (commit 891dae2); new members must be
/// appended.
/// </summary>
public sealed class FrozenContractValuesTests
{
    /// <summary>Each 1.0 enum with its members in their 1.0 order; the value of a member is its index.</summary>
    public static TheoryData<Type, string[]> FrozenEnums => new()
    {
        { typeof(AgentTaskStatus), ["Running", "Completed", "MaxStepsReached", "BudgetExceeded", "PolicyBlocked", "ReplanLimitReached", "Failed", "Cancelled"] },
        { typeof(AuthorizationKind), ["Automatic", "UserApproved", "UserRejected", "PolicyDenied", "UnknownTool"] },
        { typeof(ChatRole), ["System", "User", "Assistant", "Tool"] },
        { typeof(ModelCallOutcome), ["Success", "Failure"] },
        { typeof(PackageTrustLevel), ["Unverified", "Community", "Verified", "Official"] },
        { typeof(PolicyMode), ["Automatic", "Approval", "Forbidden"] },
        { typeof(RiskLevel), ["Read", "Low", "Medium", "High", "Critical"] },
        { typeof(ToolOutcome), ["Success", "Failure", "Denied", "Timeout"] },
        { typeof(ToolParameterType), ["String", "Integer", "Number", "Boolean", "Path", "Duration", "Enum"] },
        { typeof(VerificationStatus), ["Confirmed", "Refuted", "Inconclusive", "NotApplicable"] },
    };

    [Theory]
    [MemberData(nameof(FrozenEnums))]
    public void Frozen10Enum_KeepsEveryMemberAtItsVersion10Value(Type enumType, string[] membersInVersion10Order)
    {
        ArgumentNullException.ThrowIfNull(enumType);
        ArgumentNullException.ThrowIfNull(membersInVersion10Order);

        for (var expected = 0; expected < membersInVersion10Order.Length; expected++)
        {
            var name = membersInVersion10Order[expected];
            Assert.True(Enum.IsDefined(enumType, name), $"{enumType.Name}.{name} was removed or renamed.");
            Assert.Equal(expected, Convert.ToInt32(Enum.Parse(enumType, name), System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void ToolParameterType_PathList_IsAppendedAfterTheVersion10Members()
    {
        Assert.True((int)ToolParameterType.PathList > (int)ToolParameterType.Enum);
    }
}
