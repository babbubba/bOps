// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>
/// Builds the final report of a run. It is the one place a claim is checked, and what it checks is the citation, not the content:
/// every evidence id a claim cites must be the id of a tool call that really ran in this run. A claim that cites none is moved to the
/// unsupported list; a claim that does is reported as a cited observation, which says the citation holds and nothing more. That the
/// cited output says what the claim says is not verified here (no evaluator is run), so the evidence list and the tools' own outputs
/// stay the only verified content of a report. A refused call (denied, unknown, invalid, over budget) produced no evidence and is never citable.
/// </summary>
internal static class ReportAssembler
{
    internal static MafDiagnosticReport NothingInvestigated(MafRun run, IReadOnlyList<string> notes) =>
        Base(run, MafWorkflowOutcome.NothingInvestigated, null, 0) with
        {
            Diagnosis = "No specialist was assigned, so nothing was investigated and no diagnosis can be given.",
            Limitations = notes,
        };

    /// <summary>The report of a run that was reviewed: the reviewer's diagnosis, with every claim checked against the evidence.</summary>
    internal static MafDiagnosticReport FromReview(
        MafRun run, WorkflowMessages.Review review, MafWorkflowOutcome outcome, string? limitCode, int rounds)
    {
        var known = KnownEvidence(run);
        var (cited, uncited) = Verify(review.Facts, known);
        var limitations = Limitations(run);
        if (limitCode == "max_investigation_rounds")
        {
            limitations = [.. limitations, "The run reached its investigation-round limit while the reviewer still judged the evidence insufficient."];
        }

        return Base(run, outcome, limitCode, rounds) with
        {
            Diagnosis = review.Diagnosis,
            CitedObservations = cited,
            Hypotheses = [.. review.Hypotheses.Select(claim => new MafClaim(claim.Text, [.. claim.EvidenceIds.Where(known.Contains)]))],
            Contradictions = review.Contradictions,
            Unsupported = [.. review.Unsupported, .. uncited],
            Missing = review.Missing,
            NextChecks = review.NextChecks,
            InterventionPlan = review.InterventionPlan,
            Limitations = limitations,
        };
    }

    /// <summary>The report of a run that was not (or could not be) reviewed: the specialists' own claims, checked, and no diagnosis beyond them.</summary>
    internal static MafDiagnosticReport FromReports(MafRun run, MafWorkflowOutcome outcome, string? limitCode, int rounds, string? note)
    {
        var known = KnownEvidence(run);
        var claimed = run.Reports.SelectMany(report => report.Observations).ToArray();
        var (cited, unsupported) = Verify(claimed, known);
        var hypotheses = run.Reports
            .SelectMany(report => report.Hypotheses)
            .Select(claim => new MafClaim(claim.Text, [.. claim.EvidenceIds.Where(known.Contains)]))
            .ToArray();
        var limitations = Limitations(run).ToList();
        if (note is not null)
        {
            limitations.Add(note);
        }

        limitations.Add(limitCode is null
            ? "The findings below are the specialists' own, not reviewed."
            : $"The run ended on a limit ({limitCode}) before a review could conclude; the findings below are the specialists' own, not reviewed.");
        return Base(run, outcome, limitCode, rounds) with
        {
            Diagnosis = "No reviewed diagnosis: " + (limitCode is null ? "the review did not conclude" : $"the run ended on a limit ({limitCode})") + ".",
            CitedObservations = cited,
            Hypotheses = hypotheses,
            Unsupported = unsupported,
            Limitations = limitations,
        };
    }

    private static MafDiagnosticReport Base(MafRun run, MafWorkflowOutcome outcome, string? limitCode, int rounds)
    {
        // A tool call the budget refused means the investigation was cut short, so the run never reports a plain completion.
        if (outcome == MafWorkflowOutcome.Completed && run.Session.Ledger.Any(entry => entry.Status == Runtime.WorkflowToolStatus.BudgetExhausted))
        {
            outcome = MafWorkflowOutcome.LimitReached;
            limitCode = "tool_call_budget";
        }

        return Build(run, outcome, limitCode, rounds);
    }

    private static MafDiagnosticReport Build(MafRun run, MafWorkflowOutcome outcome, string? limitCode, int rounds) =>
        new()
        {
            RunId = run.RunId,
            Outcome = outcome,
            LimitCode = limitCode,
            Rounds = rounds,
            SelectedSpecialists = run.Selected,
            ExcludedSpecialists = run.Excluded,
            Evidence =
            [
                .. run.Session.Ledger.Select(entry => new MafEvidenceEntry(
                    entry.EvidenceId, entry.AgentName, entry.ToolName, entry.Status, entry.Completeness.ToString())),
            ],
            ToolCalls = run.Session.ToolCallsMade,
            ModelCalls = run.Budget.ModelCalls,
            Tokens = run.Budget.Tokens,
        };

    /// <summary>The ids of the calls that ran. A call refused at the boundary produced no evidence, so its id can be cited by nothing.</summary>
    private static HashSet<string> KnownEvidence(MafRun run) =>
        run.Session.Ledger
            .Where(entry => entry.Status is Runtime.WorkflowToolStatus.Executed or Runtime.WorkflowToolStatus.Failed or Runtime.WorkflowToolStatus.TimedOut)
            .Select(entry => entry.EvidenceId)
            .ToHashSet(StringComparer.Ordinal);

    private static (IReadOnlyList<MafClaim> Cited, IReadOnlyList<string> Uncited) Verify(
        IEnumerable<WorkflowMessages.Claim> claims, HashSet<string> known)
    {
        var cited = new List<MafClaim>();
        var uncited = new List<string>();
        foreach (var claim in claims)
        {
            var valid = claim.EvidenceIds.Where(known.Contains).ToArray();
            if (valid.Length > 0)
            {
                cited.Add(new MafClaim(claim.Text, valid));
            }
            else
            {
                uncited.Add($"Stated as an observation but citing no evidence from a tool call that ran: {claim.Text}");
            }
        }

        return (cited, uncited);
    }

    private static List<string> Limitations(MafRun run)
    {
        var limitations = new List<string>();
        foreach (var report in run.Reports)
        {
            if (report.Status is WorkflowMessages.SpecialistStatus.Failed or WorkflowMessages.SpecialistStatus.LimitReached)
            {
                limitations.Add($"Specialist '{report.Specialist}' did not complete round {report.Round} ({report.Status}).");
            }

            limitations.AddRange(report.Limitations.Select(limitation => $"{report.Specialist}: {limitation}"));
        }

        limitations.AddRange(run.Session.Ledger
            .Where(entry => entry.Status is not Runtime.WorkflowToolStatus.Executed)
            .Select(entry => $"{entry.EvidenceId} ({entry.ToolName}) did not produce evidence: {entry.Status}."));
        return limitations;
    }
}
