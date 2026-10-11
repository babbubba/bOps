// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// The runtime-authored drill-down index (ADR-0052 AF-05): which persisted steps hold more evidence than the model-facing
/// history shows, and the stable id that addresses each. It is protected context built from persisted steps alone, so a
/// resumed attempt sees what the interrupted one saw, and it names no evidence text — only step, tool label, id and lengths.
/// It replaces the addressing role the HARDEN-8 compact records had once old tool results are compacted by the context layer.
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
        "Written by bOps, not by a tool. These persisted steps hold more evidence than the conversation shows. "
        + "Address one by its step index (runtime.evidence_read) or by its id (EvidenceRead/v1 directive).";

    /// <summary>
    /// The block for <paramref name="steps"/>, or <c>null</c> when no persisted step is a drill-down candidate: an executed tool
    /// step whose persisted result or observation is longer than <paramref name="maxObservationCharacters"/>, which is exactly
    /// when the runtime offers the control function.
    /// </summary>
    internal static string? Build(Guid taskId, IReadOnlyList<PlanStep> steps, int maxObservationCharacters)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var candidates = steps
            .Where(step => step.ToolCall is not null && step.Result is not null
                && Math.Max(step.Result.Output?.Length ?? 0, step.Observation?.Length ?? 0) > maxObservationCharacters)
            .OrderBy(step => step.Index)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var listed = candidates.Count > MaxEntries ? candidates.GetRange(candidates.Count - MaxEntries, MaxEntries) : candidates;
        var text = new StringBuilder(OpenMarker).Append('\n').Append(Introduction);
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
