// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using bOps.Abstractions;
using Microsoft.Agents.AI.Workflows;

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>
/// The diagnostic reviewer (C01-R23): a MAF agent that receives the goal, every specialist's report and the evidence ids, compares
/// the conclusions, looks for contradictions and unsupported interpretations, and either concludes or asks a registered specialist for a
/// follow-up, which sends another round back through the workflow. It runs once per round, after the last specialist's report. It has no
/// tool; it cannot add a tool, widen an authority or change the goal; and the rounds it may start are bounded by the run's limits.
/// </summary>
internal sealed class ReviewExecutor(MafRun run) : Executor<WorkflowMessages.SpecialistReport>(ExecutorId)
{
    internal const string ExecutorId = "review";

    private const int ExcerptLength = 1_200;

    /// <inheritdoc />
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder)
            .SendsMessage<WorkflowMessages.Round>()
            .YieldsOutput<MafDiagnosticReport>();

    /// <inheritdoc />
    public override async ValueTask HandleAsync(
        WorkflowMessages.SpecialistReport message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (run.TryCompleteRound(message) is not { } ofRound)
        {
            return;
        }

        var round = message.Round;
        if (ofRound.FirstOrDefault(report => report.Status == WorkflowMessages.SpecialistStatus.LimitReached) is { } stopped)
        {
            await FinishAsync(context, ReportAssembler.FromReports(run, MafWorkflowOutcome.LimitReached, stopped.Code ?? "limit", round, null), cancellationToken);
            return;
        }

        string reply;
        try
        {
            reply = await run.RunAgentAsync(
                run.ReviewerHandle, WorkflowPrompts.Reviewer(run.Limits.MaxInvestigationRounds), [], BuildInput(round), cancellationToken);
        }
        catch (WorkflowLimitException limit)
        {
            await FinishAsync(context, ReportAssembler.FromReports(run, MafWorkflowOutcome.LimitReached, limit.Code, round, null), cancellationToken);
            return;
        }

        var review = ParseReview(reply);
        if (review is null)
        {
            await FinishAsync(
                context,
                ReportAssembler.FromReports(
                    run, MafWorkflowOutcome.Completed, null, round, "The diagnostic review could not be read, so the specialists' findings are reported unreviewed."),
                cancellationToken);
            return;
        }

        run.Report(
            MafWorkflowEventKind.Reviewed, ExecutorId, round,
            review.Sufficient ? "evidence sufficient" : $"evidence insufficient; {review.FollowUps.Count} follow-up(s) requested");
        await run.AuditAsync(WorkflowRunStage.Reviewed, round, null, review.Sufficient ? "sufficient" : "insufficient", cancellationToken);

        var assignments = review.FollowUps
            .Where(followUp => run.Specialists.Any(candidate => string.Equals(candidate.Specialist.Name, followUp.Specialist, StringComparison.Ordinal)))
            .Select(followUp => new WorkflowMessages.Assignment(followUp.Specialist, followUp.Objective))
            .ToArray();
        if (!review.Sufficient && assignments.Length > 0 && round < run.Limits.MaxInvestigationRounds)
        {
            var next = round + 1;
            run.CurrentRound = next;
            run.Report(MafWorkflowEventKind.RoundStarted, null, next, string.Join(", ", assignments.Select(assignment => assignment.Specialist)));
            await run.AuditAsync(WorkflowRunStage.RoundStarted, next, [.. assignments.Select(assignment => assignment.Specialist)], null, cancellationToken);
            await context.SendMessageAsync(
                new WorkflowMessages.Round(next, run.Goal, assignments, string.Join("; ", review.Missing)), cancellationToken: cancellationToken);
            return;
        }

        var stoppedByRounds = !review.Sufficient && assignments.Length > 0;
        await FinishAsync(
            context,
            ReportAssembler.FromReview(
                run, review, stoppedByRounds ? MafWorkflowOutcome.LimitReached : MafWorkflowOutcome.Completed,
                stoppedByRounds ? "max_investigation_rounds" : null, round),
            cancellationToken);
    }

    private async ValueTask FinishAsync(IWorkflowContext context, MafDiagnosticReport report, CancellationToken ct)
    {
        run.Final = report;
        await context.YieldOutputAsync(report, ct);
    }

    private string BuildInput(int round)
    {
        var text = new StringBuilder();
        text.AppendLine($"Operator goal:\n{WorkflowPrompts.GoalOpen}\n{WorkflowPrompts.Neutralize(run.Goal)}\n{WorkflowPrompts.GoalClose}");
        text.AppendLine($"Investigation round {round} of at most {run.Limits.MaxInvestigationRounds}.");
        text.AppendLine($"Registered specialists you may name in a follow-up: {string.Join(", ", run.Specialists.Select(candidate => candidate.Specialist.Name))}.");
        text.AppendLine();
        foreach (var report in run.Reports.Where(report => report.Status != WorkflowMessages.SpecialistStatus.NotAssigned).OrderBy(report => report.Round))
        {
            text.AppendLine($"{WorkflowPrompts.ReportOpen} specialist={report.Specialist} round={report.Round} status={report.Status}>>>");
            text.AppendLine(WorkflowPrompts.Neutralize($"summary: {report.Summary}"));
            foreach (var claim in report.Observations)
            {
                text.AppendLine(WorkflowPrompts.Neutralize($"observation: {claim.Text} [{string.Join(", ", claim.EvidenceIds)}]"));
            }

            foreach (var claim in report.Hypotheses)
            {
                text.AppendLine(WorkflowPrompts.Neutralize($"hypothesis: {claim.Text} [{string.Join(", ", claim.EvidenceIds)}]"));
            }

            foreach (var limitation in report.Limitations)
            {
                text.AppendLine(WorkflowPrompts.Neutralize($"limitation: {limitation}"));
            }

            text.AppendLine(WorkflowPrompts.ReportClose);
        }

        text.AppendLine();
        text.AppendLine("Evidence collected (the only ids you may cite):");
        foreach (var entry in run.Session.Ledger)
        {
            text.AppendLine($"- {entry.EvidenceId} | agent={entry.AgentName} | tool={entry.ToolName} | status={entry.Status} | completeness={entry.Completeness}");
            foreach (var fact in entry.Facts.Take(8))
            {
                text.AppendLine($"    fact {fact.Type}/{fact.Key} = {fact.Value.ToJsonString()}");
            }

            var excerpt = entry.Observation.Length <= ExcerptLength
                ? entry.Observation
                : $"{entry.Observation[..ExcerptLength]}\n[excerpt truncated]\n{WorkflowPrompts.ToolOutputClose}";
            text.AppendLine(excerpt);
        }

        return text.ToString();
    }

    private static WorkflowMessages.Review? ParseReview(string reply)
    {
        if (ReplyParser.ExtractObject(reply) is not { } body)
        {
            return null;
        }

        var followUps = ReplyParser.Objects(body, "followUps")
            .Select(item => new WorkflowMessages.FollowUp(ReplyParser.Text(item, "specialist"), ReplyParser.Text(item, "objective")))
            .Where(followUp => followUp.Specialist.Length > 0)
            .ToArray();
        return new WorkflowMessages.Review(
            body["sufficient"] is System.Text.Json.Nodes.JsonValue sufficient && sufficient.TryGetValue<bool>(out var isSufficient) && isSufficient,
            ReplyParser.Text(body, "diagnosis"),
            ReplyParser.Claims(body, "facts"),
            ReplyParser.Claims(body, "hypotheses"),
            ReplyParser.Strings(body, "contradictions"),
            ReplyParser.Strings(body, "unsupported"),
            ReplyParser.Strings(body, "missing"),
            followUps,
            ReplyParser.Strings(body, "nextChecks"),
            ReplyParser.Strings(body, "interventionPlan"));
    }
}
