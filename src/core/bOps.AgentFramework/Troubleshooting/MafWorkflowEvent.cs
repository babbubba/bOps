// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>The kinds of progress a MAF troubleshooting run reports to its host.</summary>
public enum MafWorkflowEventKind
{
    /// <summary>The workflow started. <see cref="MafWorkflowEvent.Detail"/> names the specialists available and the ones that are not.</summary>
    Started,

    /// <summary>The triage agent finished: <see cref="MafWorkflowEvent.Detail"/> names the selected and excluded specialists.</summary>
    Triaged,

    /// <summary>An investigation round began.</summary>
    RoundStarted,

    /// <summary>An agent asked for a tool.</summary>
    ToolRequested,

    /// <summary>A tool call ended at the bOps boundary (executed, failed, refused, invalid).</summary>
    ToolCompleted,

    /// <summary>A specialist finished its turn in a round.</summary>
    SpecialistCompleted,

    /// <summary>The reviewer judged a round.</summary>
    Reviewed,

    /// <summary>The run finished.</summary>
    Finished,
}

/// <summary>
/// One progress event of a run, for the host to show. It carries names, counts and short closed or bounded text, never a tool output
/// or a model reply; those have their own audit events and the final report.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Agent">The agent concerned, or <c>null</c> for the workflow itself.</param>
/// <param name="Round">The investigation round (1-based), or 0 before the first.</param>
/// <param name="Detail">A short description.</param>
public sealed record MafWorkflowEvent(MafWorkflowEventKind Kind, string? Agent, int Round, string Detail);
