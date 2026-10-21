// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>Deterministic evidence coverage derived only from existing persisted task state. It grants no execution authority.</summary>
public static class DiagnosticAssurance
{
    private static readonly JsonSerializerOptions FactValueOptions = new() { Encoder = JavaScriptEncoder.Default };

    /// <summary>Projects a completed task, including historical records; returns null while running or after another terminal outcome.</summary>
    public static DiagnosticAssuranceResult? From(TaskState task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Status != AgentTaskStatus.Completed)
        {
            return null;
        }

        var operational = task.Steps.Where(IsOperational).ToList();
        var projected = ExecutionPlanProjector.Project(task, executing: false);
        var planned = projected?.Revisions.SelectMany(revision => revision.Steps)
            .Where(step => step.ExpectedTool is null || !step.ExpectedTool.StartsWith(EvidenceRead.ControlNamespace, StringComparison.Ordinal))
            .ToList() ?? [];
        var deniedOnly = operational.Count == 0 && task.Steps.Any(step => step.ToolCall is not null
            && (step.Result?.Outcome == ToolOutcome.Denied || step.Result?.FailureKind == ToolFailureKind.Authorization));
        if (operational.Count == 0 && (planned.Count == 0 || deniedOnly))
        {
            return new DiagnosticAssuranceResult(DiagnosticAssuranceLevel.NotApplicable, []);
        }

        var counts = new Dictionary<AssuranceReasonCode, int>();
        void Add(AssuranceReasonCode code, int count = 1)
        {
            if (count > 0)
            {
                counts[code] = counts.GetValueOrDefault(code) + count;
            }
        }

        foreach (var step in operational)
        {
            if (step.Result is not { } result)
            {
                Add(AssuranceReasonCode.EvidenceUnavailable);
                continue;
            }

            if (result.Outcome != ToolOutcome.Success || step.VerificationStatus == VerificationStatus.Refuted)
            {
                Add(AssuranceReasonCode.PlannedStepFailed);
            }

            if (step.VerificationStatus == VerificationStatus.Inconclusive)
            {
                Add(AssuranceReasonCode.VerificationInconclusive);
            }

            switch (result.Completeness)
            {
                case ToolResultCompleteness.Partial:
                    Add(AssuranceReasonCode.EvidencePartial);
                    break;
                case ToolResultCompleteness.Unavailable:
                    Add(AssuranceReasonCode.EvidenceUnavailable);
                    break;
                case ToolResultCompleteness.Unspecified:
                    Add(AssuranceReasonCode.EvidenceCompletenessUnspecified);
                    break;
            }

            if (result.Outcome != ToolOutcome.Success)
            {
                continue;
            }

            if (!EvidenceGroundingLedger.Grounds(step))
            {
                Add(AssuranceReasonCode.NoTypedFactsForStep);
            }

            // Raw evidence reads are audit-only. Task state cannot prove that every omitted UTF-16 range was served.
            if (!string.IsNullOrEmpty(result.Output) && !(step.Observation ?? string.Empty).StartsWith(result.Output, StringComparison.Ordinal))
            {
                Add(AssuranceReasonCode.ObservationShortenedNotRead);
            }
        }

        Add(AssuranceReasonCode.PlannedStepNotExecuted, planned.Count(step => step.Status is ProjectedStepStatus.Pending
            or ProjectedStepStatus.Running or ProjectedStepStatus.Correcting or ProjectedStepStatus.OutcomeUnknown
            || step.Status == ProjectedStepStatus.Failed && !operational.Any(execution => execution.PlanRevision == step.Revision
                && execution.PlannedStepIndex == step.Index && execution.Result is not null)));

        PlanStep? final = task.Steps.Count == 0 ? null : task.Steps[^1];
        if (final is not null && !FinalResponse.IsFinalStep(final))
        {
            final = null;
        }
        Add(final is null ? AssuranceReasonCode.GroundingNotChecked : FinalResponse.GroundingOf(final) switch
        {
            EvidenceGroundingOutcome.NoContradictionCited => AssuranceReasonCode.GroundingNoContradictionCited,
            EvidenceGroundingOutcome.Corrected => AssuranceReasonCode.GroundingCorrected,
            EvidenceGroundingOutcome.CheckUnavailable => AssuranceReasonCode.GroundingCheckUnavailable,
            _ => AssuranceReasonCode.GroundingNotChecked,
        });

        var ledger = EvidenceGroundingLedger.Build(task.Steps, task.Plans);
        var shownIds = ledger?.Facts.Select(fact => fact.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        var hiddenValues = operational.Where(EvidenceGroundingLedger.Grounds).Sum(step => step.Result!.Facts
            .Select((fact, index) => (Fact: fact, Id: string.Create(CultureInfo.InvariantCulture, $"F{step.Index}.{index + 1}")))
            .Count(fact => shownIds.Contains(fact.Id)
                && fact.Fact.Value.ToJsonString(FactValueOptions).Length > EvidenceGroundingLedger.MaxValueCharacters));
        Add(AssuranceReasonCode.GroundingFactsOmitted, (ledger?.OmittedCount ?? 0) + hiddenValues);
        Add(AssuranceReasonCode.FinalAfterRecovery, task.Steps.Count(EarlyFinalRecovery.IsStep)
            + task.Steps.Count(step => PrematureFinal.IsStep(step) && step.EarlyFinalRecoveryDecision?.Accepted != true)
            + (final?.ModelCalls is { Count: > 1 } calls ? calls.Take(calls.Count - 1).Count(call => call.Outcome == ModelCallOutcome.Success
                && call.Output is { ToolCallCount: 0, Class: ModelOutputClass.Empty or ModelOutputClass.TruncatedBeforeContent }) : 0));

        var qualified = counts.Keys.Any(code => code is AssuranceReasonCode.PlannedStepFailed or AssuranceReasonCode.PlannedStepNotExecuted
            or AssuranceReasonCode.EvidencePartial or AssuranceReasonCode.EvidenceUnavailable
            or AssuranceReasonCode.ObservationShortenedNotRead or AssuranceReasonCode.GroundingCorrected
            or AssuranceReasonCode.VerificationInconclusive);
        var uncheckedEvidence = counts.Keys.Any(code => code is AssuranceReasonCode.NoTypedFactsForStep or AssuranceReasonCode.GroundingNotChecked
            or AssuranceReasonCode.GroundingCheckUnavailable or AssuranceReasonCode.GroundingFactsOmitted
            or AssuranceReasonCode.EvidenceCompletenessUnspecified);
        var level = qualified ? DiagnosticAssuranceLevel.Qualified : uncheckedEvidence ? DiagnosticAssuranceLevel.Unchecked : DiagnosticAssuranceLevel.Checked;
        return new DiagnosticAssuranceResult(level, [.. counts.OrderBy(pair => pair.Key).Select(pair => new AssuranceReason(pair.Key, pair.Value))]);
    }

    private static bool IsOperational(PlanStep step) => step.ToolCall is { } call
        && !call.ToolName.StartsWith(EvidenceRead.ControlNamespace, StringComparison.Ordinal)
        && step.Result?.Outcome != ToolOutcome.Denied && step.Result?.FailureKind != ToolFailureKind.Authorization
        && !EvidenceLimitationsDigest.IsUnknownToolRejection(step);
}
