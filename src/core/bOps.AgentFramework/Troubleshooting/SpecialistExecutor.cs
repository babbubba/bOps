// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>
/// One specialist of the workflow (C01-R23): a MAF agent with function tools that go through the bOps gateway. In each round every
/// specialist receives the round; one with no assignment reports <c>NotAssigned</c> without a model call, and one with an objective
/// investigates it, deciding for itself which tools to call and following up on what they return. The specialists of a round run
/// concurrently, and their reports meet at the reviewer.
/// </summary>
internal sealed class SpecialistExecutor(MafRun run, AvailableSpecialist available, ILogger logger)
    : Executor<WorkflowMessages.Round>(IdOf(available.Specialist.Name))
{
    internal static string IdOf(string specialist) => $"specialist-{specialist}";

    /// <inheritdoc />
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder).SendsMessage<WorkflowMessages.SpecialistReport>();

    /// <inheritdoc />
    public override async ValueTask HandleAsync(WorkflowMessages.Round message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var name = available.Specialist.Name;
        var assignment = message.Assignments.FirstOrDefault(candidate => string.Equals(candidate.Specialist, name, StringComparison.Ordinal));
        if (assignment is null)
        {
            await context.SendMessageAsync(Empty(message.Number, WorkflowMessages.SpecialistStatus.NotAssigned, string.Empty, null), cancellationToken: cancellationToken);
            return;
        }

        var handle = run.SpecialistHandles[name];
        var before = run.Session.Ledger.Where(entry => entry.AgentName == handle.Name).Select(entry => entry.EvidenceId).ToHashSet(StringComparer.Ordinal);
        WorkflowMessages.SpecialistReport report;
        try
        {
            var reply = await run.RunAgentAsync(
                handle, WorkflowPrompts.Specialist(available.Specialist), available.Tools, BuildInput(message, assignment, handle), cancellationToken);
            var ids = run.Session.Ledger.Where(entry => entry.AgentName == handle.Name && !before.Contains(entry.EvidenceId)).Select(entry => entry.EvidenceId).ToArray();
            report = Parse(message.Number, reply, ids);
        }
        catch (WorkflowLimitException limit)
        {
            report = Empty(message.Number, WorkflowMessages.SpecialistStatus.LimitReached, "The specialist stopped at a run limit.", limit.Code);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A specialist that failed (a model failure, a protocol error) must not end the run: the reviewer sees that it failed and what
            // the others found. The closed code is all that is kept; the audit chain already holds the model-call failure.
            logger.LogWarning(ex, "Specialist {Specialist} failed in round {Round}", name, message.Number);
            report = Empty(message.Number, WorkflowMessages.SpecialistStatus.Failed, "The specialist failed before it could report.", "specialist_failed");
        }

        run.Report(MafWorkflowEventKind.SpecialistCompleted, name, message.Number, $"{report.Status}; {report.EvidenceIds.Count} evidence item(s)");
        await context.SendMessageAsync(report, cancellationToken: cancellationToken);
    }

    private WorkflowMessages.SpecialistReport Empty(int round, WorkflowMessages.SpecialistStatus status, string summary, string? code) =>
        new(available.Specialist.Name, round, status, summary, [], [], [], [], code);

    private string BuildInput(WorkflowMessages.Round round, WorkflowMessages.Assignment assignment, WorkflowAgentHandle handle)
    {
        var text = new StringBuilder();
        text.AppendLine($"Operator goal:\n{WorkflowPrompts.GoalOpen}\n{WorkflowPrompts.Neutralize(round.Goal)}\n{WorkflowPrompts.GoalClose}");
        text.AppendLine($"Your objective for round {round.Number}: {WorkflowPrompts.Neutralize(assignment.Objective)}");
        if (round.ReviewerGuidance is { Length: > 0 } guidance)
        {
            text.AppendLine($"The reviewer found these gaps in the evidence so far: {WorkflowPrompts.Neutralize(guidance)}");
        }

        var own = run.Session.Ledger.Where(entry => entry.AgentName == handle.Name).ToArray();
        if (own.Length > 0)
        {
            text.AppendLine("Evidence you already collected in earlier rounds (do not repeat these calls without a reason):");
            foreach (var entry in own)
            {
                text.AppendLine($"- {entry.EvidenceId}: {entry.ToolName} → {entry.Status}");
            }
        }

        return text.ToString();
    }

    private WorkflowMessages.SpecialistReport Parse(int round, string reply, IReadOnlyList<string> evidenceIds)
    {
        if (ReplyParser.ExtractObject(reply) is not { } body)
        {
            // An answer that is not the requested shape is kept as a bounded summary and flagged; it is never repaired into claims.
            return new(
                available.Specialist.Name, round, WorkflowMessages.SpecialistStatus.Unstructured, ReplyParser.Bound(reply ?? string.Empty),
                [], [], ["The specialist's reply was not the requested structure, so none of it is reported as an observation."], evidenceIds, null);
        }

        return new(
            available.Specialist.Name, round, WorkflowMessages.SpecialistStatus.Completed, ReplyParser.Text(body, "summary"),
            ReplyParser.Claims(body, "observations"), ReplyParser.Claims(body, "hypotheses"), ReplyParser.Strings(body, "limitations"), evidenceIds, null);
    }
}
