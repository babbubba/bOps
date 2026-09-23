// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime.Tests;

public sealed class DiagnosticScenarioHarnessTests
{
    [Fact]
    public void EvidenceSequence_IsDeterministic_AndRetainsCompleteness()
    {
        var harness = new DiagnosticScenarioHarness(
            DiagnosticScenarioHarness.Available("network.sockets", "no listener on 5432"),
            DiagnosticScenarioHarness.Partial("firewall.rules", "visibility restricted"));

        var sockets = harness.Request("network.sockets");
        var firewall = harness.Request("firewall.rules");

        Assert.Equal(["network.sockets", "firewall.rules"], harness.RequestedCapabilities);
        Assert.True(sockets.IsComplete);
        Assert.False(firewall.IsComplete);
    }

    [Theory]
    [InlineData((int)DiagnosticEvidenceState.Partial)]
    [InlineData((int)DiagnosticEvidenceState.Unavailable)]
    [InlineData((int)DiagnosticEvidenceState.Truncated)]
    public void IncompleteEvidence_CannotBeTreatedAsACompleteInference(int stateValue)
    {
        var state = (DiagnosticEvidenceState)stateValue;
        var evidence = new DiagnosticEvidence("system.events", state, "limited observation");

        Assert.False(evidence.IsComplete);
    }
}
