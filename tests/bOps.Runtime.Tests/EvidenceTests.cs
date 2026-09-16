// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

/// <summary>
/// ADR-0023: a <see cref="Finding"/> without evidence is rejected structurally, at construction —
/// there is no registration step for it the way there is for a non-Read <see cref="ITool"/>
/// (rule B3), so the invariant belongs on the type itself.
/// </summary>
public sealed class EvidenceTests
{
    [Fact]
    public void Finding_Rejects_EmptyEvidenceIds()
    {
        Assert.Throws<ArgumentException>(() => new Finding("finding-1", "Something is wrong.", []));
    }

    [Fact]
    public void Finding_Accepts_AtLeastOnePieceOfEvidence()
    {
        var finding = new Finding("finding-1", "Bloat detected.", ["evidence-1"], RiskLevel.Medium);

        Assert.Equal("finding-1", finding.Id);
        Assert.Single(finding.EvidenceIds);
        Assert.Equal(RiskLevel.Medium, finding.Severity);
    }
}
