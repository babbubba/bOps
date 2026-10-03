// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>What became of the evidence-disclosure re-ask of a final answer (ADR-0042 §6, §13).</summary>
internal enum EvidenceDisclosureOutcome
{
    /// <summary>No re-ask was made: the persisted answer is the model's first final answer.</summary>
    NotAttempted,

    /// <summary>A re-ask was made and its answer, which carries the heading, is the persisted one.</summary>
    Accepted,

    /// <summary>A re-ask was made and failed or was unusable: the persisted answer is the original.</summary>
    ResultNotUsed,
}

/// <summary>Which logical model call of a step a group of recorded attempts is (ADR-0042 §13).</summary>
internal enum LogicalModelCallRole
{
    /// <summary>The call that produced the step's reply: the final answer of a final step, the tool call of a tool step.</summary>
    Answer,

    /// <summary>A call made after an empty reply (<see cref="AgentRunnerOptions.EmptyFinalResponseRetries"/>).</summary>
    EmptyAnswerRetry,

    /// <summary>The evidence-disclosure re-ask of a final step whose description carries a re-ask marker.</summary>
    EvidenceDisclosureReAsk,
}

/// <summary>One logical model call of a step: its attempts (ADR-0039) and the role the final-step marker assigns it.</summary>
/// <param name="Role">What the call was for.</param>
/// <param name="Attempts">The recorded attempts of the call, in persisted order.</param>
internal sealed record LogicalModelCall(LogicalModelCallRole Role, IReadOnlyList<ModelCallRecord> Attempts);

/// <summary>
/// The persisted final-response step: the three fixed <see cref="PlanStep.Description"/> markers, the one shared predicate
/// that recognizes a final-response step, and the reconstruction of its logical model calls (ADR-0042 §13). No model-call
/// kind is stored; the persisted order, <see cref="ModelCallRecord.ModelAttempt"/> and the marker are enough.
/// </summary>
internal static class FinalResponse
{
    /// <summary>No disclosure re-ask was made (unchanged for every row written before the re-ask existed).</summary>
    internal const string Marker = "Final response";

    /// <summary>A disclosure re-ask was made and its answer is the persisted one.</summary>
    internal const string ReAskAcceptedMarker = "Final response; evidence disclosure re-ask accepted";

    /// <summary>A disclosure re-ask was made and failed; the persisted answer is the original.</summary>
    internal const string ReAskNotUsedMarker = "Final response; evidence disclosure re-ask result not used";

    /// <summary>The <see cref="PlanStep.Description"/> a final step takes after <paramref name="outcome"/>.</summary>
    internal static string DescriptionFor(EvidenceDisclosureOutcome outcome) => outcome switch
    {
        EvidenceDisclosureOutcome.Accepted => ReAskAcceptedMarker,
        EvidenceDisclosureOutcome.ResultNotUsed => ReAskNotUsedMarker,
        _ => Marker,
    };

    /// <summary>
    /// Whether <paramref name="step"/> is the final-response step: its description is exactly one of the three markers
    /// (ordinal) <b>and</b> it has no tool call. A tool step whose description happens to equal a marker is never a final
    /// response; no substring or prefix match is used.
    /// </summary>
    internal static bool IsFinalStep(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.ToolCall is null && step.Description is Marker or ReAskAcceptedMarker or ReAskNotUsedMarker;
    }

    /// <summary>The disclosure outcome a final step's description records, or <see cref="EvidenceDisclosureOutcome.NotAttempted"/> for a step that is not a final step.</summary>
    internal static EvidenceDisclosureOutcome DisclosureOf(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (!IsFinalStep(step))
        {
            return EvidenceDisclosureOutcome.NotAttempted;
        }

        return step.Description switch
        {
            ReAskAcceptedMarker => EvidenceDisclosureOutcome.Accepted,
            ReAskNotUsedMarker => EvidenceDisclosureOutcome.ResultNotUsed,
            _ => EvidenceDisclosureOutcome.NotAttempted,
        };
    }

    /// <summary>
    /// Groups a step's recorded model calls into logical calls: a record with <see cref="ModelCallRecord.ModelAttempt"/>
    /// <c>1</c>, or <c>null</c> (a row written before attempts were recorded), starts a new logical call. The first is the
    /// step's answer call, the following ones are empty-reply retries, and when the step's description carries a re-ask
    /// marker the <b>last</b> logical call is the evidence-disclosure re-ask — also when that re-ask needed several attempts.
    /// </summary>
    internal static IReadOnlyList<LogicalModelCall> LogicalCalls(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var groups = new List<List<ModelCallRecord>>();
        foreach (var record in step.ModelCalls ?? [])
        {
            if (groups.Count == 0 || record.ModelAttempt is null or 1)
            {
                groups.Add([]);
            }

            groups[^1].Add(record);
        }

        var reAsked = DisclosureOf(step) != EvidenceDisclosureOutcome.NotAttempted && groups.Count >= 2;
        var calls = new List<LogicalModelCall>(groups.Count);
        for (var index = 0; index < groups.Count; index++)
        {
            var role = index == 0
                ? LogicalModelCallRole.Answer
                : reAsked && index == groups.Count - 1 ? LogicalModelCallRole.EvidenceDisclosureReAsk : LogicalModelCallRole.EmptyAnswerRetry;
            calls.Add(new LogicalModelCall(role, groups[index]));
        }

        return calls;
    }
}
