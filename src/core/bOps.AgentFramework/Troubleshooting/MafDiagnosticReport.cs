// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Runtime;

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>How a MAF troubleshooting run ended.</summary>
public enum MafWorkflowOutcome
{
    /// <summary>The reviewer judged the evidence sufficient, or no further round was possible, and a report was produced.</summary>
    Completed,

    /// <summary>A limit (rounds, tool calls, model calls, tokens, time) ended the run; the report says what was and was not established.</summary>
    LimitReached,

    /// <summary>No specialist applied or was available, so nothing was investigated.</summary>
    NothingInvestigated,
}

/// <summary>A statement with the evidence ids it cites, every one of them the id of a tool call that ran in the run.</summary>
/// <param name="Text">The statement, as the agent made it. Whether the cited output says this is not verified.</param>
/// <param name="EvidenceIds">The ids of the run's evidence that it cites; never an id the run does not hold or a call that never ran.</param>
public sealed record MafClaim(string Text, IReadOnlyList<string> EvidenceIds);

/// <summary>One tool call of the run, as the evidence list of the report shows it.</summary>
/// <param name="EvidenceId">The stable evidence id.</param>
/// <param name="Agent">The agent that asked.</param>
/// <param name="Tool">The canonical tool name.</param>
/// <param name="Status">How the call ended at the bOps boundary.</param>
/// <param name="Completeness">The completeness the tool declared.</param>
public sealed record MafEvidenceEntry(string EvidenceId, string Agent, string Tool, WorkflowToolStatus Status, string Completeness);

/// <summary>
/// The result of a MAF troubleshooting run (C01-R23). It separates the observations that cite what the tools returned (<see cref="CitedObservations"/>) from what is only
/// suspected (<see cref="Hypotheses"/>), lists what contradicts, what is unsupported and what is missing, and gives an advisory
/// intervention plan that nothing in bOps executes.
/// </summary>
public sealed record MafDiagnosticReport
{
    /// <summary>The workflow run; the task id every audit event of the run carries.</summary>
    public required Guid RunId { get; init; }

    /// <summary>How the run ended.</summary>
    public required MafWorkflowOutcome Outcome { get; init; }

    /// <summary>The closed code of the limit that ended the run, when it ended on one.</summary>
    public string? LimitCode { get; init; }

    /// <summary>The investigation rounds that ran.</summary>
    public int Rounds { get; init; }

    /// <summary>The specialists the triage agent selected.</summary>
    public IReadOnlyList<string> SelectedSpecialists { get; init; } = [];

    /// <summary>The registered specialists that were not used and why, as short text.</summary>
    public IReadOnlyList<string> ExcludedSpecialists { get; init; } = [];

    /// <summary>The reasoned diagnosis, or what the evidence allows to be said when it could not be completed.</summary>
    public string Diagnosis { get; init; } = string.Empty;

    /// <summary>
    /// Observations that cite evidence of a call that ran. Only the citation is checked; that the cited output supports the statement
    /// is not, so these are cited, not verified. The verified content of a report is <see cref="Evidence"/> and the tools' own outputs.
    /// </summary>
    public IReadOnlyList<MafClaim> CitedObservations { get; init; } = [];

    /// <summary>Hypotheses that are not proven by the evidence.</summary>
    public IReadOnlyList<MafClaim> Hypotheses { get; init; } = [];

    /// <summary>Contradictions the reviewer found between agents or sources.</summary>
    public IReadOnlyList<string> Contradictions { get; init; } = [];

    /// <summary>Interpretations the evidence does not support.</summary>
    public IReadOnlyList<string> Unsupported { get; init; } = [];

    /// <summary>Information still missing.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>Verifications worth doing next.</summary>
    public IReadOnlyList<string> NextChecks { get; init; } = [];

    /// <summary>An advisory intervention plan. Nothing in bOps runs it: the workflow executes no operation with effects.</summary>
    public IReadOnlyList<string> InterventionPlan { get; init; } = [];

    /// <summary>Limitations of the run, as short text.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];

    /// <summary>Every tool call of the run in call order, refused ones included with their status; only a call that ran is evidence a claim can cite.</summary>
    public IReadOnlyList<MafEvidenceEntry> Evidence { get; init; } = [];

    /// <summary>The tool calls the run attempted.</summary>
    public int ToolCalls { get; init; }

    /// <summary>The model calls the run made.</summary>
    public int ModelCalls { get; init; }

    /// <summary>The known model tokens the run used (a lower bound).</summary>
    public long Tokens { get; init; }
}
