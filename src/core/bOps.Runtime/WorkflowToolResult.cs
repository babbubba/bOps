// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>How one tool call of a workflow run ended at the bOps boundary.</summary>
public enum WorkflowToolStatus
{
    /// <summary>The tool ran and reported success.</summary>
    Executed = 0,

    /// <summary>The tool ran and reported an expected failure.</summary>
    Failed = 1,

    /// <summary>The tool ran out of time.</summary>
    TimedOut = 2,

    /// <summary>The call broke the declared tool contract (an unknown or malformed argument, a value out of range); nothing ran.</summary>
    Invalid = 3,

    /// <summary>The name did not resolve to a registered tool; nothing ran.</summary>
    UnknownTool = 4,

    /// <summary>The agent's authority, the read-only ceiling, policy, approval or entitlement refused the call; nothing ran.</summary>
    Denied = 5,

    /// <summary>The run had no tool call left in its budget; nothing ran.</summary>
    BudgetExhausted = 6,
}

/// <summary>
/// What a workflow agent gets back from one governed tool call. <see cref="Observation"/> is the only part that goes to a model:
/// bounded, and wrapped as untrusted tool output. <see cref="Original"/> is the tool's own result, preserved unmodified for the
/// evidence of the run and never rewritten by anything downstream.
/// </summary>
/// <param name="EvidenceId">The stable identifier of this call's evidence in the run (<c>ev1:&lt;run&gt;:&lt;sequence&gt;</c>).</param>
/// <param name="Sequence">The call's position in the run's tool calls (0-based); also its <see cref="AuditEvent.StepIndex"/>.</param>
/// <param name="AgentName">The registered agent that asked for the call.</param>
/// <param name="InvocationId">The call id the model assigned.</param>
/// <param name="ToolName">The canonical tool name the model asked for.</param>
/// <param name="Status">How the call ended.</param>
/// <param name="Authorization">The authorization the runtime recorded for it.</param>
/// <param name="Observation">The text the model sees: bounded and delimited as untrusted data.</param>
/// <param name="Original">The tool's own result, or <c>null</c> when the tool never ran.</param>
public sealed record WorkflowToolResult(
    string EvidenceId,
    int Sequence,
    string AgentName,
    string InvocationId,
    string ToolName,
    WorkflowToolStatus Status,
    AuthorizationKind Authorization,
    string Observation,
    ToolCallResult? Original)
{
    /// <summary>The typed facts the tool emitted, empty when none or when it never ran.</summary>
    public IReadOnlyList<EvidenceFact> Facts => Original?.Facts ?? [];

    /// <summary>The completeness the tool declared for its evidence.</summary>
    public ToolResultCompleteness Completeness => Original?.Completeness ?? ToolResultCompleteness.Unspecified;

    /// <summary>The tool's own output, preserved unmodified; <c>null</c> when it never ran or produced none.</summary>
    public string? OriginalOutput => Original?.Output;
}
