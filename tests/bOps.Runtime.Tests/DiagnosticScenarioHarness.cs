// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime.Tests;

/// <summary>
/// Small deterministic evidence fixture for V1.3-L scenarios.  It models observations, not OS
/// collectors: package tests remain the authority for collector behaviour.
/// </summary>
internal sealed class DiagnosticScenarioHarness
{
    private readonly Queue<DiagnosticEvidence> _evidence;
    private readonly List<string> _requested = [];

    public DiagnosticScenarioHarness(params DiagnosticEvidence[] evidence) => _evidence = new(evidence);

    public IReadOnlyList<string> RequestedCapabilities => _requested;

    public DiagnosticEvidence Request(string capability)
    {
        _requested.Add(capability);
        var evidence = _evidence.Dequeue();
        Assert.Equal(capability, evidence.Capability);
        return evidence;
    }

    public static DiagnosticEvidence Available(string capability, string observation) =>
        new(capability, DiagnosticEvidenceState.Available, observation);

    public static DiagnosticEvidence Partial(string capability, string observation) =>
        new(capability, DiagnosticEvidenceState.Partial, observation);

    public static DiagnosticEvidence Unavailable(string capability, string observation) =>
        new(capability, DiagnosticEvidenceState.Unavailable, observation);

    public static DiagnosticEvidence NotApplicable(string capability, string observation) =>
        new(capability, DiagnosticEvidenceState.NotApplicable, observation);

    public static DiagnosticEvidence Truncated(string capability, string observation) =>
        new(capability, DiagnosticEvidenceState.Truncated, observation);
}

internal enum DiagnosticEvidenceState { Available, Partial, Unavailable, NotApplicable, Truncated }

internal sealed record DiagnosticEvidence(string Capability, DiagnosticEvidenceState State, string Observation)
{
    public bool IsComplete => State is DiagnosticEvidenceState.Available or DiagnosticEvidenceState.NotApplicable;
}
