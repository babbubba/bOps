// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// The one door through which the agents of a MAF troubleshooting workflow reach a tool (C01-R23). A session belongs to one workflow
/// run: it carries the run's identity (the task id), the operator, and the run-wide tool-call budget, and it hands each registered
/// agent a <see cref="WorkflowAgentHandle"/> whose tool authority is frozen at registration. Every call goes through the runner's
/// one step pipeline (argument validation, policy, approval, entitlement, timeout, audit), so nothing here authorizes or executes
/// anything on its own, and nothing outside the runner can build a session.
/// </summary>
public sealed class WorkflowToolSession
{
    private readonly Func<WorkflowToolSession, WorkflowAgentHandle, ModelToolCall, int, CancellationToken, Task<WorkflowToolResult>> execute;
    private readonly Lock gate = new();
    private readonly List<WorkflowAgentHandle> agents = [];
    private readonly SortedDictionary<int, WorkflowToolResult> ledger = [];
    private int toolCalls;
    private int modelCalls;

    internal WorkflowToolSession(
        Guid runId,
        ActorIdentity actor,
        int maxToolCalls,
        IReadOnlyList<ToolManifest> visibleReadTools,
        Func<WorkflowToolSession, WorkflowAgentHandle, ModelToolCall, int, CancellationToken, Task<WorkflowToolResult>> execute)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(visibleReadTools);
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("A workflow run needs a non-empty task id.", nameof(runId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxToolCalls, 1);
        RunId = runId;
        Actor = actor;
        MaxToolCalls = maxToolCalls;
        this.execute = execute;
        VisibleReadTools = visibleReadTools;
    }

    /// <summary>The workflow run: the task id every event of the run carries.</summary>
    public Guid RunId { get; }

    /// <summary>The operator on whose authority the run executes.</summary>
    public ActorIdentity Actor { get; }

    /// <summary>The read-only tools visible on this node when the session opened, as the runner saw them: what a specialist registration can select from, and never anything above Read.</summary>
    public IReadOnlyList<ToolManifest> VisibleReadTools { get; }

    /// <summary>The hard ceiling of tool calls the run may attempt, executed or refused.</summary>
    public int MaxToolCalls { get; }

    /// <summary>How many tool calls the run has attempted so far.</summary>
    public int ToolCallsMade => Volatile.Read(ref toolCalls);

    /// <summary>The agents registered so far, in registration order.</summary>
    public IReadOnlyList<WorkflowAgentHandle> Agents
    {
        get
        {
            lock (gate)
            {
                return [.. agents];
            }
        }
    }

    /// <summary>The outcome of every tool call of the run in call order, each with the tool's original result preserved.</summary>
    public IReadOnlyList<WorkflowToolResult> Ledger
    {
        get
        {
            lock (gate)
            {
                return [.. ledger.Values];
            }
        }
    }

    /// <summary>
    /// Registers an agent with its exact tool authority. The runtime assigns the agent's identity and freezes the set; nothing can add
    /// to it later, and a name a model produces is never added. An agent without tools (a triage or review agent) is registered with an
    /// empty set and every call it tried would be refused.
    /// </summary>
    /// <param name="name">The registered name of the agent; unique in the run.</param>
    /// <param name="allowedTools">The exact canonical tool names the agent may call (no wildcards).</param>
    public WorkflowAgentHandle RegisterAgent(string name, IEnumerable<string> allowedTools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(allowedTools);
        var frozen = allowedTools.ToHashSet(StringComparer.Ordinal);
        lock (gate)
        {
            if (agents.Any(existing => string.Equals(existing.Name, name, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"A workflow agent named '{name}' is already registered in this run.");
            }

            var handle = new WorkflowAgentHandle(this, name, Guid.NewGuid(), frozen, HashAuthority(frozen));
            agents.Add(handle);
            return handle;
        }
    }

    /// <summary>Numbers the next model call of the run: a negative step index, distinct from every tool call's.</summary>
    public int NextModelCallStepIndex() => -(Interlocked.Increment(ref modelCalls));

    internal async Task<WorkflowToolResult> ExecuteAsync(WorkflowAgentHandle agent, ModelToolCall call, CancellationToken ct)
    {
        var sequence = Interlocked.Increment(ref toolCalls) - 1;
        var result = await execute(this, agent, call, sequence, ct);
        lock (gate)
        {
            ledger[sequence] = result;
        }

        return result;
    }

    private static string HashAuthority(IEnumerable<string> tools)
    {
        var canonical = string.Join('\n', tools.Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
