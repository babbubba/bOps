// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Cli.Tests;

public sealed class DiagnosticAssuranceTextTests
{
    [Fact]
    public void QualifiedSummary_ShowsClosedReasonsAndTheirCounts_WithScopeOfTheIndicator()
    {
        var result = new DiagnosticAssuranceResult(DiagnosticAssuranceLevel.Qualified,
            [new AssuranceReason(AssuranceReasonCode.EvidencePartial, 2), new AssuranceReason(AssuranceReasonCode.GroundingNotChecked, 1)]);

        var text = DiagnosticAssuranceText.Format(result);

        Assert.Contains("Evidence assurance: Qualified", text, StringComparison.Ordinal);
        Assert.Contains("EvidencePartial=2, GroundingNotChecked=1", text, StringComparison.Ordinal);
        Assert.Contains("does not certify diagnostic truth", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DiagnosticAssuranceLevel.Checked)]
    [InlineData(DiagnosticAssuranceLevel.Unchecked)]
    [InlineData(DiagnosticAssuranceLevel.NotApplicable)]
    public void EveryLevel_IsExplicitlyLimitedToCoverage(DiagnosticAssuranceLevel level)
    {
        var text = DiagnosticAssuranceText.Format(new DiagnosticAssuranceResult(level, []));
        Assert.Contains(level.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("coverage and check status", text, StringComparison.Ordinal);
        Assert.Contains("does not certify diagnostic truth", text, StringComparison.Ordinal);
    }
}
