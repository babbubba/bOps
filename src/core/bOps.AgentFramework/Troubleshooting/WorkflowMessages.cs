// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>
/// The typed messages the executors of the MAF troubleshooting workflow pass to one another along its edges (C01-R23). They are what
/// MAF routes; none of them carries authority. A claim is only text plus the evidence ids it cites, and the runtime checks every id
/// against the run's evidence before anything is reported as supported.
/// </summary>
internal static class WorkflowMessages
{
    /// <summary>The input of a run: the operator's goal, exactly as given.</summary>
    internal sealed record Goal(string Text);

    /// <summary>One diagnostic objective the triage agent or the reviewer assigns to a registered specialist.</summary>
    internal sealed record Assignment(string Specialist, string Objective);

    /// <summary>One investigation round: who is asked what. Every specialist receives it; a specialist with no assignment does nothing.</summary>
    internal sealed record Round(int Number, string Goal, IReadOnlyList<Assignment> Assignments, string? ReviewerGuidance);

    /// <summary>An observation or a hypothesis with the evidence ids it cites.</summary>
    internal sealed record Claim(string Text, IReadOnlyList<string> EvidenceIds);

    /// <summary>How a specialist's turn in a round ended.</summary>
    internal enum SpecialistStatus
    {
        Completed,
        NotAssigned,
        Unstructured,
        Failed,
        LimitReached,
    }

    /// <summary>What one specialist reports for one round.</summary>
    internal sealed record SpecialistReport(
        string Specialist,
        int Round,
        SpecialistStatus Status,
        string Summary,
        IReadOnlyList<Claim> Observations,
        IReadOnlyList<Claim> Hypotheses,
        IReadOnlyList<string> Limitations,
        IReadOnlyList<string> EvidenceIds,
        string? Code);

    /// <summary>A follow-up the reviewer asks of a registered specialist.</summary>
    internal sealed record FollowUp(string Specialist, string Objective);

    /// <summary>The reviewer's judgement of the evidence of a round.</summary>
    internal sealed record Review(
        bool Sufficient,
        string Diagnosis,
        IReadOnlyList<Claim> Facts,
        IReadOnlyList<Claim> Hypotheses,
        IReadOnlyList<string> Contradictions,
        IReadOnlyList<string> Unsupported,
        IReadOnlyList<string> Missing,
        IReadOnlyList<FollowUp> FollowUps,
        IReadOnlyList<string> NextChecks,
        IReadOnlyList<string> InterventionPlan);
}
