// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>
/// The state one workflow run shares among its executors: the identity (the task id), the governed gateway session, the budget, the
/// registered agents and the reports collected so far. It is created for one run and used by no other, which is what isolates
/// concurrent runs. It holds working state for the framework's run only; the operational record is the audit chain.
/// </summary>
internal sealed class MafRun
{
    private readonly Lock gate = new();
    private readonly List<WorkflowMessages.SpecialistReport> reports = [];
    private readonly HashSet<int> reviewedRounds = [];

    internal required Guid RunId { get; init; }

    internal required string Goal { get; init; }

    internal required MafWorkflowLimits Limits { get; init; }

    internal required WorkflowBudget Budget { get; init; }

    internal required WorkflowToolSession Session { get; init; }

    internal required IModelInvocationBroker Broker { get; init; }

    internal required IAuditSink Audit { get; init; }

    internal required TimeProvider Time { get; init; }

    internal required IReadOnlyList<AvailableSpecialist> Specialists { get; init; }

    internal required IReadOnlyList<string> Unavailable { get; init; }

    internal required WorkflowAgentHandle TriageHandle { get; init; }

    internal required WorkflowAgentHandle ReviewerHandle { get; init; }

    internal required IReadOnlyDictionary<string, WorkflowAgentHandle> SpecialistHandles { get; init; }

    internal Action<MafWorkflowEvent>? Progress { get; init; }

    /// <summary>The investigation round in progress (1-based); 0 before the first.</summary>
    internal int CurrentRound { get; set; }

    /// <summary>The specialists the triage agent selected, in the order it gave them.</summary>
    internal IReadOnlyList<string> Selected { get; set; } = [];

    /// <summary>The registered specialists that were not used, each with a short reason.</summary>
    internal IReadOnlyList<string> Excluded { get; set; } = [];

    /// <summary>The report the run ends with, once there is one.</summary>
    internal MafDiagnosticReport? Final { get; set; }

    internal IReadOnlyList<WorkflowMessages.SpecialistReport> Reports
    {
        get
        {
            lock (gate)
            {
                return [.. reports];
            }
        }
    }

    internal void Report(MafWorkflowEventKind kind, string? agent, int round, string detail) =>
        Progress?.Invoke(new MafWorkflowEvent(kind, agent, round, detail));

    /// <summary>
    /// Stores one specialist's report and, when it is the last one of its round, returns every report of that round exactly once, so the
    /// reviewer runs once per round however the framework delivers the messages.
    /// </summary>
    internal IReadOnlyList<WorkflowMessages.SpecialistReport>? TryCompleteRound(WorkflowMessages.SpecialistReport report)
    {
        lock (gate)
        {
            reports.Add(report);
            var ofRound = reports.Where(candidate => candidate.Round == report.Round).ToArray();
            return ofRound.Length == Specialists.Count && reviewedRounds.Add(report.Round) ? ofRound : null;
        }
    }

    internal Task AuditAsync(
        WorkflowRunStage stage, int round, IReadOnlyList<string>? agents, string? code, CancellationToken ct,
        IReadOnlyDictionary<string, string>? hashes = null) =>
        Audit.WriteAsync(
            new WorkflowRunAuditEvent
            {
                TimestampUtc = Time.GetUtcNow(),
                Node = NodeId.Local,
                TaskId = RunId,
                Actor = Session.Actor,
                Stage = stage,
                Round = round,
                Agents = agents,
                ToolAuthorityHashes = hashes,
                Code = code,
                ToolCalls = Session.ToolCallsMade,
                Tokens = Budget.Tokens,
            },
            ct);

    /// <summary>
    /// Runs one agent turn as a Microsoft Agent Framework <see cref="ChatClientAgent"/>: its function-invocation layer calls the
    /// <see cref="GovernedToolFunction"/>s (the gateway), and every model call goes through the governed broker. The agent is created
    /// for the turn and hosts a fresh session, so nothing carries from one turn to another except what the workflow passes explicitly.
    /// </summary>
    internal async Task<string> RunAgentAsync(
        WorkflowAgentHandle handle, string systemPrompt, IReadOnlyList<ToolManifest> tools, string userMessage, CancellationToken ct)
    {
        using var leaf = new WorkflowGovernedChatClient(Broker, Session, handle, systemPrompt, tools, Budget);
        var maxIterations = Limits.MaxToolIterationsPerAgentTurn;
        using var client = leaf.AsBuilder()
            .UseFunctionInvocation(configure: invoker =>
            {
                invoker.MaximumIterationsPerRequest = maxIterations;
                invoker.AllowConcurrentInvocation = false;
                invoker.IncludeDetailedErrors = false;
                invoker.TerminateOnUnknownCalls = false;
            })
            .Build();
        var functions = tools.Select(manifest => (AITool)new GovernedToolFunction(manifest, handle, Progress, () => CurrentRound)).ToArray();
        var agent = new ChatClientAgent(
            client,
            new ChatClientAgentOptions
            {
                Name = handle.Name,
                ChatOptions = new ChatOptions { Tools = functions.Length == 0 ? null : functions },
                UseProvidedChatClientAsIs = true,
            });
        var session = await agent.CreateSessionAsync(ct);
        var response = await agent.RunAsync([new ChatMessage(Microsoft.Extensions.AI.ChatRole.User, userMessage)], session, cancellationToken: ct);
        return response.Text;
    }
}
