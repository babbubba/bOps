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

/// <summary>Which logical model call of a step a group of recorded attempts is (ADR-0042 §13, PRE-5 amendment §9).</summary>
internal enum LogicalModelCallRole
{
    /// <summary>The call that produced the step's reply: the final answer of a final step, the tool call of a tool step.</summary>
    Answer,

    /// <summary>A call made after an empty reply (<see cref="AgentRunnerOptions.EmptyFinalResponseRetries"/>).</summary>
    EmptyAnswerRetry,

    /// <summary>The evidence-disclosure re-ask of a final step whose description carries a re-ask marker.</summary>
    EvidenceDisclosureReAsk,

    /// <summary>The evidence-grounding check of a final step whose description carries a grounding marker.</summary>
    EvidenceGroundingCheck,

    /// <summary>The evidence-grounding correction of a final step whose description carries the corrected marker.</summary>
    EvidenceGroundingCorrection,
}

/// <summary>One logical model call of a step: its attempts (ADR-0039) and the role the final-step marker assigns it.</summary>
/// <param name="Role">What the call was for.</param>
/// <param name="Attempts">The recorded attempts of the call, in persisted order.</param>
internal sealed record LogicalModelCall(LogicalModelCallRole Role, IReadOnlyList<ModelCallRecord> Attempts);

/// <summary>
/// The persisted final-response step: the fixed <see cref="PlanStep.Description"/> markers, the one shared predicate that
/// recognizes a final-response step, and the reconstruction of its logical model calls (ADR-0042 §13, PRE-5 amendment §9). A
/// marker is <see cref="Marker"/>, an optional disclosure suffix and an optional grounding suffix — twelve fixed strings. No
/// model-call kind is stored; the persisted order, <see cref="ModelCallRecord.ModelAttempt"/> and the marker are enough.
/// </summary>
internal static class FinalResponse
{
    /// <summary>No disclosure re-ask and no grounding check was made (unchanged for every row written before they existed).</summary>
    internal const string Marker = "Final response";

    /// <summary>A disclosure re-ask was made and its answer is the persisted one.</summary>
    internal const string ReAskAcceptedMarker = "Final response; evidence disclosure re-ask accepted";

    /// <summary>A disclosure re-ask was made and failed; the persisted answer is the original.</summary>
    internal const string ReAskNotUsedMarker = "Final response; evidence disclosure re-ask result not used";

    /// <summary>The grounding suffix: the check found no contradiction.</summary>
    internal const string GroundingVerifiedSuffix = "; evidence grounding verified";

    /// <summary>The grounding suffix: the check cited a contradiction and the correction is the persisted answer.</summary>
    internal const string GroundingCorrectedSuffix = "; evidence grounding corrected";

    /// <summary>The grounding suffix: the check produced no usable verdict; the answer was persisted unclassified.</summary>
    internal const string GroundingCheckUnavailableSuffix = "; evidence grounding check unavailable";

    private static readonly Dictionary<string, (EvidenceDisclosureOutcome Disclosure, EvidenceGroundingOutcome Grounding)> Markers =
        BuildMarkers();

    /// <summary>The <see cref="PlanStep.Description"/> a final step takes after <paramref name="outcome"/>, with no grounding check.</summary>
    internal static string DescriptionFor(EvidenceDisclosureOutcome outcome) => DescriptionFor(outcome, EvidenceGroundingOutcome.NotChecked);

    /// <summary>The <see cref="PlanStep.Description"/> a final step takes after the disclosure and grounding outcomes.</summary>
    internal static string DescriptionFor(EvidenceDisclosureOutcome disclosure, EvidenceGroundingOutcome grounding)
    {
        var head = disclosure switch
        {
            EvidenceDisclosureOutcome.Accepted => ReAskAcceptedMarker,
            EvidenceDisclosureOutcome.ResultNotUsed => ReAskNotUsedMarker,
            _ => Marker,
        };

        return head + grounding switch
        {
            EvidenceGroundingOutcome.Verified => GroundingVerifiedSuffix,
            EvidenceGroundingOutcome.Corrected => GroundingCorrectedSuffix,
            EvidenceGroundingOutcome.CheckUnavailable => GroundingCheckUnavailableSuffix,
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Whether <paramref name="step"/> is the final-response step: its description is exactly one of the fixed markers
    /// (ordinal) <b>and</b> it has no tool call. A tool step whose description happens to equal a marker is never a final
    /// response; no substring or prefix match is used.
    /// </summary>
    internal static bool IsFinalStep(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.ToolCall is null && step.Description is { } description && Markers.ContainsKey(description);
    }

    /// <summary>The disclosure outcome a final step's description records, or <see cref="EvidenceDisclosureOutcome.NotAttempted"/> for a step that is not a final step.</summary>
    internal static EvidenceDisclosureOutcome DisclosureOf(PlanStep step) =>
        IsFinalStep(step) ? Markers[step.Description!].Disclosure : EvidenceDisclosureOutcome.NotAttempted;

    /// <summary>The grounding outcome a final step's description records, or <see cref="EvidenceGroundingOutcome.NotChecked"/> for a step that is not a final step.</summary>
    internal static EvidenceGroundingOutcome GroundingOf(PlanStep step) =>
        IsFinalStep(step) ? Markers[step.Description!].Grounding : EvidenceGroundingOutcome.NotChecked;

    /// <summary>
    /// Groups a step's recorded model calls into logical calls: a record with <see cref="ModelCallRecord.ModelAttempt"/>
    /// <c>1</c>, or <c>null</c> (a row written before attempts were recorded), starts a new logical call. The first is the
    /// step's answer call. Roles are then assigned from the end, in the fixed pipeline order: the grounding correction (when
    /// the description says <c>corrected</c>), before it the grounding check (any grounding marker), before that the
    /// disclosure re-ask (a re-ask marker); every other call is an empty-reply retry. A call that needed several attempts is
    /// still one logical call.
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

        var roles = new LogicalModelCallRole[groups.Count];
        Array.Fill(roles, LogicalModelCallRole.EmptyAnswerRetry);
        if (roles.Length > 0)
        {
            roles[0] = LogicalModelCallRole.Answer;
        }

        var trailing = new List<LogicalModelCallRole>(3);
        var grounding = GroundingOf(step);
        if (grounding == EvidenceGroundingOutcome.Corrected)
        {
            trailing.Add(LogicalModelCallRole.EvidenceGroundingCorrection);
        }

        if (grounding != EvidenceGroundingOutcome.NotChecked)
        {
            trailing.Add(LogicalModelCallRole.EvidenceGroundingCheck);
        }

        if (DisclosureOf(step) != EvidenceDisclosureOutcome.NotAttempted)
        {
            trailing.Add(LogicalModelCallRole.EvidenceDisclosureReAsk);
        }

        for (var offset = 0; offset < trailing.Count && groups.Count - 1 - offset >= 1; offset++)
        {
            roles[groups.Count - 1 - offset] = trailing[offset];
        }

        return groups.Select((group, index) => new LogicalModelCall(roles[index], group)).ToList();
    }

    private static Dictionary<string, (EvidenceDisclosureOutcome, EvidenceGroundingOutcome)> BuildMarkers()
    {
        var markers = new Dictionary<string, (EvidenceDisclosureOutcome, EvidenceGroundingOutcome)>(StringComparer.Ordinal);
        foreach (var disclosure in Enum.GetValues<EvidenceDisclosureOutcome>())
        {
            foreach (var grounding in Enum.GetValues<EvidenceGroundingOutcome>())
            {
                markers.Add(DescriptionFor(disclosure, grounding), (disclosure, grounding));
            }
        }

        return markers;
    }
}
