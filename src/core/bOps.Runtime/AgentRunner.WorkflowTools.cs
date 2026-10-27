// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

public sealed partial class AgentRunner
{
    /// <summary>
    /// Opens the tool session of one MAF troubleshooting workflow run (C01-R23). The session is the only way the run's agents reach a
    /// tool, and every call it carries goes through <c>ExecuteStepAsync</c>, the one place a tool call is validated, put to policy,
    /// approval and entitlement, timed, executed and audited. The run is identified by <paramref name="runId"/>, a task id the host
    /// generated for it; in this version the run has no checkpoint and no resume and is not written to the task store (C01-R23 clause 7):
    /// a retry is a new run and no tool is re-executed automatically.
    /// </summary>
    /// <param name="runId">The task id of the workflow run. Not empty.</param>
    /// <param name="actor">The operator on whose authority the run executes.</param>
    /// <param name="maxToolCalls">The hard ceiling of tool calls the run may attempt, executed or refused. At least 1.</param>
    public WorkflowToolSession OpenWorkflowToolSession(Guid runId, ActorIdentity actor, int maxToolCalls) =>
        new(
            runId, actor, maxToolCalls,
            [.. registry.GetAvailableManifests().Where(manifest => manifest.Risk == RiskLevel.Read).OrderBy(manifest => manifest.Name, StringComparer.Ordinal)],
            ExecuteWorkflowToolCallAsync);

    private async Task<WorkflowToolResult> ExecuteWorkflowToolCallAsync(
        WorkflowToolSession session, WorkflowAgentHandle agent, ModelToolCall call, int sequence, CancellationToken ct)
    {
        var evidenceId = $"ev1:{session.RunId}:{sequence}";
        using var scope = WorkflowStepScope.Begin(new WorkflowCorrelation(session.RunId, agent.Name, agent.InstanceId, call.Id));

        // A name the adapter could not map, or one that is not registered, is the ordinary unknown-tool path: audited, nothing resolved.
        var registration = call.ToolNameError is null ? registry.ResolveForExecution(call.ToolName) : null;
        string? refusal = null;
        var status = WorkflowToolStatus.Denied;
        if (sequence >= session.MaxToolCalls)
        {
            refusal = $"The workflow has used its budget of {session.MaxToolCalls} tool calls; this call was not run.";
            status = WorkflowToolStatus.BudgetExhausted;
        }
        else if (registration is not null && !agent.AllowedTools.Contains(call.ToolName))
        {
            refusal = $"'{call.ToolName}' is not in the tool authority of agent '{agent.Name}'; this call was not run.";
        }
        else if (registration is not null && registration.Tool.Manifest.Risk != RiskLevel.Read)
        {
            refusal = $"'{call.ToolName}' is not a read-only tool and a troubleshooting workflow runs read-only tools only; this call was not run.";
        }

        if (refusal is not null)
        {
            // A refusal is a Forbidden decision like any other: audited as such and ended as PolicyDenied.
            var package = registration?.Package ?? PackageId.Unknown;
            await WriteAuditAsync(new PolicyDecisionAuditEvent
            {
                TimestampUtc = timeProvider.GetUtcNow(),
                Node = NodeId.Local,
                TaskId = session.RunId,
                StepIndex = sequence,
                Actor = session.Actor,
                Package = package,
                Tool = call.ToolName,
                Mode = PolicyMode.Forbidden,
                Reason = $"Workflow authority: {refusal}",
            }, delegation: null, ct);
            var rejected = await RejectAsync(
                session.RunId, sequence, session.Actor, call, package, registration?.Tool.Manifest.Risk ?? RiskLevel.Read,
                AuthorizationKind.PolicyDenied, refusal, planRevision: -1, ct);
            return new WorkflowToolResult(
                evidenceId, sequence, agent.Name, call.Id, call.ToolName, status, AuthorizationKind.PolicyDenied, rejected.Observation, null);
        }

        var (step, observation, authorization, _) = await ExecuteStepAsync(
            session.RunId, sequence, session.Actor, call, planRevision: -1, ct);
        var result = step.Result;
        var outcome = authorization switch
        {
            AuthorizationKind.UnknownTool => WorkflowToolStatus.UnknownTool,
            AuthorizationKind.PolicyDenied or AuthorizationKind.UserRejected or AuthorizationKind.EntitlementDenied => WorkflowToolStatus.Denied,
            _ when result is null => WorkflowToolStatus.Failed,
            _ when result.FailureKind == ToolFailureKind.Validation => WorkflowToolStatus.Invalid,
            _ when result.Outcome == ToolOutcome.Timeout => WorkflowToolStatus.TimedOut,
            _ when result.Outcome == ToolOutcome.Success => WorkflowToolStatus.Executed,
            _ => WorkflowToolStatus.Failed,
        };

        // A call that never ran has no tool result to preserve: the runtime's refusal text is the only thing it produced.
        var executed = outcome is WorkflowToolStatus.Executed or WorkflowToolStatus.Failed or WorkflowToolStatus.TimedOut;
        return new WorkflowToolResult(
            evidenceId, sequence, agent.Name, call.Id, call.ToolName, outcome, authorization, observation, executed ? result : null);
    }
}
