// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Agents.AI.Workflows;

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>
/// The first executor of the workflow (C01-R23): a MAF agent that reads the operator's goal, identifies what it concerns and chooses
/// among the registered, available specialists, assigning each a diagnostic objective. It proposes a plan and authorizes nothing: a
/// name it gives that is not an available specialist is dropped, and it has no tool.
/// </summary>
internal sealed class TriageExecutor(MafRun run) : Executor<WorkflowMessages.Goal>("triage")
{
    internal const string ExecutorId = "triage";

    /// <inheritdoc />
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder)
            .SendsMessage<WorkflowMessages.Round>()
            .YieldsOutput<MafDiagnosticReport>();

    /// <inheritdoc />
    public override async ValueTask HandleAsync(WorkflowMessages.Goal message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var reply = await run.RunAgentAsync(
            run.TriageHandle,
            WorkflowPrompts.Triage(run.Specialists),
            [],
            $"{WorkflowPrompts.GoalOpen}\n{WorkflowPrompts.Neutralize(message.Text)}\n{WorkflowPrompts.GoalClose}",
            cancellationToken);

        var assignments = new List<WorkflowMessages.Assignment>();
        var excluded = new Dictionary<string, string>(StringComparer.Ordinal);
        var dropped = new List<string>();
        if (ReplyParser.ExtractObject(reply) is { } plan)
        {
            foreach (var item in ReplyParser.Objects(plan, "assignments"))
            {
                var name = ReplyParser.Text(item, "specialist");
                if (!run.Specialists.Any(candidate => string.Equals(candidate.Specialist.Name, name, StringComparison.Ordinal)))
                {
                    dropped.Add($"the triage agent named '{ReplyParser.Bound(name)}', which is not an available specialist");
                    continue;
                }

                if (assignments.Any(existing => string.Equals(existing.Specialist, name, StringComparison.Ordinal)))
                {
                    continue;
                }

                var objective = ReplyParser.Text(item, "objective");
                assignments.Add(new WorkflowMessages.Assignment(name, objective.Length == 0 ? "Investigate the operator's goal in your domain." : objective));
            }

            foreach (var item in ReplyParser.Objects(plan, "excluded"))
            {
                excluded[ReplyParser.Text(item, "specialist")] = ReplyParser.Text(item, "reason");
            }
        }
        else
        {
            dropped.Add("the triage reply was not a readable plan");
        }

        run.Selected = [.. assignments.Select(assignment => assignment.Specialist)];
        run.Excluded =
        [
            .. run.Specialists
                .Where(candidate => !run.Selected.Contains(candidate.Specialist.Name, StringComparer.Ordinal))
                .Select(candidate => $"{candidate.Specialist.Name}: {(excluded.TryGetValue(candidate.Specialist.Name, out var reason) && reason.Length > 0 ? reason : "not relevant to the goal")}"),
            .. run.Unavailable.Select(name => $"{name}: no tool is available on this node"),
        ];
        run.Report(
            MafWorkflowEventKind.Triaged, TriageExecutor.ExecutorId, 0,
            $"selected: {(run.Selected.Count == 0 ? "none" : string.Join(", ", run.Selected))}; excluded: {(run.Excluded.Count == 0 ? "none" : string.Join("; ", run.Excluded))}");
        await run.AuditAsync(WorkflowRunStage.Triaged, 0, run.Selected, null, cancellationToken);

        if (assignments.Count == 0)
        {
            run.Final = ReportAssembler.NothingInvestigated(run, dropped);
            await context.YieldOutputAsync(run.Final, cancellationToken);
            return;
        }

        run.CurrentRound = 1;
        run.Report(MafWorkflowEventKind.RoundStarted, null, 1, string.Join(", ", assignments.Select(assignment => assignment.Specialist)));
        await run.AuditAsync(WorkflowRunStage.RoundStarted, 1, run.Selected, null, cancellationToken);
        await context.SendMessageAsync(new WorkflowMessages.Round(1, message.Text, assignments, null), cancellationToken: cancellationToken);
    }
}
