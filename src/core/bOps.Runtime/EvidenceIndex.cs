// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// The runtime-authored drill-down index (ADR-0052 AF-05, AF-05G): which persisted tool steps hold raw evidence the
/// model-facing history may no longer show, and the stable id that addresses each. Addressability is a property of the
/// persisted step alone — never of <c>MaxObservationCharacters</c>, which only shortens the runtime observation, and never of
/// whether the context layer happened to compact the step — so a result of any size stays exactly readable. It is protected
/// context built from persisted steps alone, so a resumed attempt sees what the interrupted one saw, and it names no evidence
/// text — only step, tool label, id and lengths. It replaces the addressing role the HARDEN-8 compact records had once old
/// tool results are compacted by the context layer.
/// </summary>
internal static class EvidenceIndex
{
    /// <summary>The marker that opens the block in a prompt; neutralized inside tool output.</summary>
    internal const string OpenMarker = "<<<BOPS_EVIDENCE_INDEX>>>";

    /// <summary>The marker that closes the block in a prompt; neutralized inside tool output.</summary>
    internal const string CloseMarker = "<<<END_BOPS_EVIDENCE_INDEX>>>";

    /// <summary>The most entries a block lists: the newest drill-down candidates.</summary>
    internal const int MaxEntries = 16;

    private const string Introduction =
        "Written by bOps, not by a tool. These persisted steps hold raw evidence the conversation may no longer show in full. "
        + "Address one by its step index (runtime.evidence_read) or by its id (EvidenceRead/v1 directive).";

    /// <summary>
    /// The fixed rule that makes ANY persisted step addressable, listed or not, as the HARDEN-8 archive summary stated it: the id is
    /// this task's id and the step index. Without it a step older than the listed window could be read only by the typed function.
    /// </summary>
    private static string Addressability(Guid taskId) =>
        string.Create(CultureInfo.InvariantCulture, $"Any persisted tool step n of this task is addressable as {EvidenceRead.EvidenceId(taskId, 0)[..^1]}n.");

    /// <summary>
    /// Whether <paramref name="step"/> holds persisted raw evidence <see cref="EvidenceRead.Read"/> can serve: an executed tool
    /// step with a persisted result whose output or observation is not empty. Size and compaction play no part.
    /// </summary>
    internal static bool IsAddressable(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.ToolCall is not null && step.Result is not null
            && ((step.Result.Output?.Length ?? 0) > 0 || (step.Observation?.Length ?? 0) > 0);
    }

    /// <summary>Whether any of <paramref name="steps"/> is <see cref="IsAddressable"/>, which is exactly when the runtime offers the control function.</summary>
    internal static bool HasAddressableEvidence(IReadOnlyList<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return steps.Any(IsAddressable);
    }

    /// <summary>
    /// The block for <paramref name="steps"/>, or <c>null</c> when no persisted step is addressable: the newest
    /// <see cref="MaxEntries"/> addressable steps are listed, and the fixed rule makes every older one addressable too.
    /// </summary>
    internal static string? Build(Guid taskId, IReadOnlyList<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var candidates = steps
            .Where(IsAddressable)
            .OrderBy(step => step.Index)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var listed = candidates.Count > MaxEntries ? candidates.GetRange(candidates.Count - MaxEntries, MaxEntries) : candidates;
        var text = new StringBuilder(OpenMarker).Append('\n').Append(Introduction).Append(' ').Append(Addressability(taskId));
        foreach (var step in listed)
        {
            var label = EvidenceLimitationsDigest.ToolLabel(step, EvidenceLimitationsDigest.IsUnknownToolRejection(step));
            text.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"\n- step {step.Index} {label}: id {EvidenceRead.EvidenceId(taskId, step.Index)}; result {step.Result!.Output?.Length ?? 0} chars; observation {step.Observation?.Length ?? 0} chars"));
        }

        if (candidates.Count > listed.Count)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture, $"\n{candidates.Count - listed.Count} earlier step(s) are not listed."));
        }

        return text.Append('\n').Append(CloseMarker).ToString();
    }
}
