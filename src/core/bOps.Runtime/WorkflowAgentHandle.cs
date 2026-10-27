// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// One registered agent of a workflow run and the only way it can reach a tool (C01-R23). The runtime assigned its identity and
/// froze its tool authority when the session registered it; both are read-only here. A call made through the handle is stamped with
/// the run, this agent and the call's own id, and goes through the runner's step pipeline; the handle has no other capability.
/// </summary>
public sealed class WorkflowAgentHandle
{
    private readonly WorkflowToolSession session;

    internal WorkflowAgentHandle(
        WorkflowToolSession session, string name, Guid instanceId, IReadOnlySet<string> allowedTools, string authorityHash)
    {
        this.session = session;
        Name = name;
        InstanceId = instanceId;
        AllowedTools = allowedTools;
        AuthorityHash = authorityHash;
    }

    /// <summary>The registered name of the agent.</summary>
    public string Name { get; }

    /// <summary>The runtime-assigned identity of this agent in this run.</summary>
    public Guid InstanceId { get; }

    /// <summary>The exact canonical tool names this agent may call, frozen at registration.</summary>
    public IReadOnlySet<string> AllowedTools { get; }

    /// <summary>A hash of <see cref="AllowedTools"/>, audited when the run starts so the authority in force can be reproduced.</summary>
    public string AuthorityHash { get; }

    /// <summary>
    /// Asks the runtime to run one tool call proposed by this agent's model. Returns how the call ended; a refusal, an invalid call and a
    /// tool failure are results, not exceptions. Caller cancellation propagates.
    /// </summary>
    /// <param name="callId">The call id the model assigned.</param>
    /// <param name="toolName">The canonical tool name the model asked for.</param>
    /// <param name="arguments">The arguments the model built.</param>
    /// <param name="ct">Cancelled to abandon the call.</param>
    public Task<WorkflowToolResult> ExecuteAsync(string callId, string toolName, ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(arguments);
        return session.ExecuteAsync(this, new ModelToolCall(callId, toolName, arguments), ct);
    }

    /// <summary>
    /// Asks the runtime to refuse a call whose name or arguments the provider adapter could not map to an offered tool (C01-R10). The
    /// refusal is audited like any other and nothing is resolved or run.
    /// </summary>
    /// <param name="call">The call exactly as the adapter returned it, with its <see cref="ModelToolCall.ToolNameError"/> or <see cref="ModelToolCall.ArgumentsError"/> set.</param>
    /// <param name="ct">Cancelled to abandon the audit write.</param>
    public Task<WorkflowToolResult> RefuseAsync(ModelToolCall call, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        return session.ExecuteAsync(this, call, ct);
    }
}
