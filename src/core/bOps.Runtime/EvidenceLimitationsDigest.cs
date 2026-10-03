// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// The evidence-limitations digest (ADR-0042 §5): a deterministic, runtime-authored list of the persisted steps for which
/// the requested result was not obtained or was only partially available — to the tool or to the model. It is a pure
/// function of the persisted steps' typed fields (<see cref="ToolCallResult.Outcome"/>, <see cref="ToolCallResult.FailureKind"/>,
/// <see cref="ToolCallResult.Completeness"/>), their index, a resolved canonical tool name that passes a shape check, and the
/// length of the output. It never contains <see cref="ToolCallResult.Output"/>, an error message, observation text, an
/// argument or any text a tool or the model produced (rule S5), and the core never parses package JSON to build it.
/// </summary>
internal static class EvidenceLimitationsDigest
{
    /// <summary>The template version; the first line of every digest. Any change of the fixed text or the entry format is a new version.</summary>
    internal const string Version = "EvidenceLimitations/v1";

    /// <summary>The marker that opens the digest block in a step prompt; neutralized inside tool output.</summary>
    internal const string OpenMarker = "<<<BOPS_EVIDENCE_LIMITATIONS>>>";

    /// <summary>The marker that closes the digest block in a step prompt; neutralized inside tool output.</summary>
    internal const string CloseMarker = "<<<END_BOPS_EVIDENCE_LIMITATIONS>>>";

    /// <summary>The most entries a digest lists; a default attempt (<c>MaxSteps</c> 15) always fits.</summary>
    internal const int MaxEntries = 16;

    /// <summary>The most characters one entry takes.</summary>
    internal const int MaxEntryCharacters = 256;

    /// <summary>The most characters the fixed text around the entries takes.</summary>
    internal const int MaxFixedCharacters = 512;

    /// <summary>The most characters a digest takes: <see cref="MaxEntries"/> entries and the fixed text.</summary>
    internal const int MaxCharacters = (MaxEntries * MaxEntryCharacters) + MaxFixedCharacters;

    /// <summary>The label of a rejection of an unresolved tool name; the caller-supplied name is never shown.</summary>
    internal const string UnknownToolLabel = "(unknown tool)";

    /// <summary>The label of a resolved tool name that fails the shape check (defence in depth).</summary>
    internal const string OmittedToolLabel = "(tool name omitted)";

    private const int MaxToolNameCharacters = 128;

    private const string Introduction =
        "Written by bOps from typed tool results, not by a tool. Each tool's own result says which sources, periods or items are affected. " +
        "Disclose these limitations in the final answer under the heading Evidence limitations.";

    /// <summary>The digest as a step prompt carries it: delimited by the two fixed markers.</summary>
    internal static string Delimit(string digest) => $"{OpenMarker}\n{digest}\n{CloseMarker}";

    /// <summary>
    /// Builds the digest of <paramref name="steps"/> (all execution attempts), or <c>null</c> when no step qualifies.
    /// </summary>
    internal static EvidenceLimitations? Build(IReadOnlyList<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var ordered = steps
            .Where(step => step.ToolCall is not null && step.Result is not null)
            .OrderBy(step => step.Index)
            .ToList();

        var listed = new List<string>();
        for (var position = 0; position < ordered.Count; position++)
        {
            if (Entry(ordered, position) is { } entry)
            {
                listed.Add(entry);
            }
        }

        if (listed.Count == 0)
        {
            return null;
        }

        var shown = listed.Count > MaxEntries ? listed.GetRange(listed.Count - MaxEntries, MaxEntries) : listed;
        var omitted = listed.Count - shown.Count;

        var text = new StringBuilder();
        text.Append(Version).Append('\n').Append(Introduction);
        if (omitted > 0)
        {
            text.Append('\n').Append(string.Create(
                CultureInfo.InvariantCulture,
                $"{omitted} earlier listed step(s) are not shown, so this list is not complete."));
        }

        foreach (var entry in shown)
        {
            text.Append('\n').Append(entry);
        }

        return new EvidenceLimitations(text.ToString(), shown.Count, omitted);
    }

    /// <summary>One line for the step at <paramref name="position"/>, or <c>null</c> when it is not a limitation.</summary>
    private static string? Entry(List<PlanStep> ordered, int position)
    {
        var step = ordered[position];
        var result = step.Result!;
        var unknownTool = IsUnknownToolRejection(step);

        var facts = new List<string>(3);
        var listed = false;

        if (result.Completeness is ToolResultCompleteness.Partial or ToolResultCompleteness.Unavailable)
        {
            facts.Add($"completeness {result.Completeness}");
            listed = true;
        }

        if (result.Outcome != ToolOutcome.Success)
        {
            // Rule 2: every failure that is not a Validation one is listed and never superseded. Rule 3: a Validation
            // failure is listed unless a later step on the same resolved tool succeeded; an unknown-tool rejection has no
            // resolved name, so nothing supersedes it.
            if (result.FailureKind != ToolFailureKind.Validation || unknownTool || !IsSuperseded(ordered, position))
            {
                facts.Add($"outcome {result.Outcome}, failure {result.FailureKind}");
                listed = true;
            }
        }
        else if (IsShortened(step, result))
        {
            facts.Add(string.Create(CultureInfo.InvariantCulture, $"observation shortened from {result.Output!.Length} characters"));
            listed = true;
        }

        if (!listed)
        {
            return null;
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"- step {step.Index}: {ToolLabel(step, unknownTool)} — {string.Join("; ", facts)}");
        return line.Length <= MaxEntryCharacters ? line : line[..MaxEntryCharacters];
    }

    /// <summary>
    /// A pre-execution rejection of a name that resolves to no tool: the runtime records exactly the description
    /// <see cref="RuntimeStepTokens.Denied"/> with <see cref="ToolFailureKind.Validation"/> only for that case.
    /// </summary>
    internal static bool IsUnknownToolRejection(PlanStep step) =>
        string.Equals(step.Description, RuntimeStepTokens.Denied, StringComparison.Ordinal)
        && step.Result is { Outcome: not ToolOutcome.Success, FailureKind: ToolFailureKind.Validation };

    /// <summary>
    /// Rule 3, the only supersession: a later step (greater index) on the same resolved tool name — compared ordinally —
    /// whose outcome is <see cref="ToolOutcome.Success"/>. An earlier success never supersedes a later failure.
    /// </summary>
    private static bool IsSuperseded(List<PlanStep> ordered, int position)
    {
        var failedName = ordered[position].ToolCall!.ToolName;
        var failedIndex = ordered[position].Index;
        for (var later = position + 1; later < ordered.Count; later++)
        {
            var candidate = ordered[later];
            if (candidate.Index > failedIndex
                && candidate.Result!.Outcome == ToolOutcome.Success
                && string.Equals(candidate.ToolCall!.ToolName, failedName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rule 4: the persisted observation of a successful step is the full output, optionally followed by the verification
    /// suffix, unless the history budget cut it into head, marker and tail. So the observation <i>starts with the full
    /// output</i> when it was not shortened — not the reverse and not a substring test. A null or empty output is never
    /// shortened. Lengths are UTF-16 code units.
    /// </summary>
    private static bool IsShortened(PlanStep step, ToolCallResult result) =>
        !string.IsNullOrEmpty(result.Output)
        && !(step.Observation ?? string.Empty).StartsWith(result.Output, StringComparison.Ordinal);

    /// <summary>
    /// The tool label: a fixed token for an unknown-tool rejection, otherwise the resolved canonical name when it has at
    /// most 128 characters, all of them ASCII letters, digits, <c>.</c>, <c>_</c> or <c>-</c>; otherwise a fixed token.
    /// </summary>
    private static string ToolLabel(PlanStep step, bool unknownTool)
    {
        if (unknownTool)
        {
            return UnknownToolLabel;
        }

        var name = step.ToolCall!.ToolName;
        return IsCanonicalToolName(name) ? name : OmittedToolLabel;
    }

    private static bool IsCanonicalToolName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxToolNameCharacters)
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A built digest.</summary>
/// <param name="Text">The digest, starting with its version line; at most <see cref="EvidenceLimitationsDigest.MaxCharacters"/> characters.</param>
/// <param name="EntryCount">How many steps it lists.</param>
/// <param name="OmittedCount">How many earlier qualifying steps it does not list (also said in the text).</param>
internal sealed record EvidenceLimitations(string Text, int EntryCount, int OmittedCount);
