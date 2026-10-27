// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>
/// The correlation block carried by an <see cref="AuditEvent"/> of a MAF troubleshooting workflow run (C01-R23). It names the
/// workflow run (its <see cref="AuditEvent.TaskId"/>), the agent that caused the event and, for a tool call, the specific
/// invocation. <c>null</c> on every other event and then omitted from the serialized event, so such an event is byte-identical
/// to one written before workflow runs existed. Only the runtime assigns it: neither a model nor a package can supply one.
/// </summary>
/// <param name="RunId">The workflow run; the same value as the event's <see cref="AuditEvent.TaskId"/>.</param>
/// <param name="AgentName">The registered name of the agent that acted (for example the triage agent, a specialist, the reviewer).</param>
/// <param name="AgentInstanceId">The runtime-assigned identity of that agent's instance in this run.</param>
/// <param name="InvocationId">The call id the model assigned to the tool invocation, or <c>null</c> for an event that is not one.</param>
public sealed record WorkflowCorrelation(Guid RunId, string AgentName, Guid AgentInstanceId, string? InvocationId);

/// <summary>The points of a workflow run's life that are audited on their own, besides the tool and model calls it makes.</summary>
public enum WorkflowRunStage
{
    /// <summary>The run was admitted. Carries the registered agents and the frozen tool authority of each.</summary>
    Started = 0,

    /// <summary>The triage agent chose the specialists of the first investigation round.</summary>
    Triaged = 1,

    /// <summary>One investigation round began (round 1 is the first).</summary>
    RoundStarted = 2,

    /// <summary>The diagnostic reviewer judged the evidence of a round.</summary>
    Reviewed = 3,

    /// <summary>The run ended with a report.</summary>
    Completed = 4,

    /// <summary>The run ended because a limit was reached; the report says what was and was not established.</summary>
    LimitReached = 5,

    /// <summary>The run ended with an error. No resume exists: a retry is a new run.</summary>
    Failed = 6,

    /// <summary>The run was cancelled.</summary>
    Cancelled = 7,
}

/// <summary>
/// A lifecycle event of a MAF troubleshooting workflow run (C01-R23). It carries counts, names and closed codes only: never the
/// goal, a prompt, a model text, a tool argument or a tool output, which have their own events and redaction.
/// </summary>
public sealed record WorkflowRunAuditEvent : AuditEvent
{
    /// <summary>Which point of the run this is.</summary>
    public required WorkflowRunStage Stage { get; init; }

    /// <summary>The investigation round the event belongs to (1-based), or <c>0</c> before the first round.</summary>
    public int Round { get; init; }

    /// <summary>The registered agents or, for a round, the specialists it dispatched, by name.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Agents { get; init; }

    /// <summary>For <see cref="WorkflowRunStage.Started"/>: a hash of each agent's frozen tool set, by agent name, so the authority in force is reproducible.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? ToolAuthorityHashes { get; init; }

    /// <summary>A closed machine code for the outcome or the limit (for example <c>max_review_rounds</c>); never free text.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }

    /// <summary>The tool calls the run had made when the event was written.</summary>
    public int ToolCalls { get; init; }

    /// <summary>The model tokens the run had used when the event was written (a lower bound: a call whose provider reported none adds nothing).</summary>
    public long Tokens { get; init; }
}
