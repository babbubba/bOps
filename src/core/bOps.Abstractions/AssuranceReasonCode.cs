// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>Closed evidence-assurance classifications containing no model, tool or operator text.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssuranceReasonCode>))]
public enum AssuranceReasonCode
{
    /// <summary>An operational execution failed or timed out; count is affected executions.</summary>
    PlannedStepFailed,
    /// <summary>A required planned operational step was unresolved; count is affected planned positions.</summary>
    PlannedStepNotExecuted,
    /// <summary>An operational result declared partial evidence; count is affected results.</summary>
    EvidencePartial,
    /// <summary>An operational result was missing or declared unavailable; count is affected executions.</summary>
    EvidenceUnavailable,
    /// <summary>An observation omitted raw output and full read coverage cannot be established from persisted task state; count is affected observations.</summary>
    ObservationShortenedNotRead,
    /// <summary>A successful operational result had no usable typed facts; count is affected results.</summary>
    NoTypedFactsForStep,
    /// <summary>No runtime final marker records a grounding check; count is one final response.</summary>
    GroundingNotChecked,
    /// <summary>The model check cited no contradiction; this is its judgement, not proof. Count is one final response.</summary>
    GroundingNoContradictionCited,
    /// <summary>A model grounding correction was accepted; count is one final response.</summary>
    GroundingCorrected,
    /// <summary>The model grounding check returned no usable verdict; count is one final response.</summary>
    GroundingCheckUnavailable,
    /// <summary>A final followed a runtime recovery checkpoint or a typed empty-output retry; count is recovery episodes.</summary>
    FinalAfterRecovery,
    /// <summary>Valid typed facts or their values were not shown in the bounded grounding ledger; count is affected facts.</summary>
    GroundingFactsOmitted,
    /// <summary>An operational result did not declare completeness; count is affected results.</summary>
    EvidenceCompletenessUnspecified,
    /// <summary>A post-action verification could not establish success or failure; count is affected executions.</summary>
    VerificationInconclusive,
}
