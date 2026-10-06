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
    internal const string Version = "EvidenceLimitations/v2";

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
        "Written by bOps from typed tool results, not by a tool: each tool's own result says which sources, periods or items are affected, " +
        "and the final answer must disclose these limitations under the heading Evidence limitations.";

    internal const string DiagnosticIntroduction =
        "Required JSON only; no prose, Evidence limitations heading or fence. Replaces E8's prose section. " +
        "A limitation is not a Finding. Qualify only supported findings it materially affects. " +
        "Never create a finding for a limitation, add/change/invent evidenceIds or change severity " +
        "to carry one. Never attach unrelated limitations or drop supported findings. Unmatched limitations stay outside JSON.";

    /// <summary>The digest as a step prompt carries it: delimited by the two fixed markers.</summary>
    internal static string Delimit(string digest) => $"{OpenMarker}\n{digest}\n{CloseMarker}";

    /// <summary>
    /// Builds the digest of <paramref name="steps"/> (all execution attempts), or <c>null</c> when no step qualifies.
    /// </summary>
    internal static EvidenceLimitations? Build(IReadOnlyList<PlanStep> steps, bool diagnostic = false)
    {
        var listed = Classify(steps).Select(Render).ToList();
        if (listed.Count == 0)
        {
            return null;
        }

        var shown = listed.Count > MaxEntries ? listed.GetRange(listed.Count - MaxEntries, MaxEntries) : listed;
        var omitted = listed.Count - shown.Count;

        var text = new StringBuilder(FixedText(omitted, diagnostic));

        foreach (var entry in shown)
        {
            text.Append('\n').Append(entry);
        }

        return new EvidenceLimitations(text.ToString(), shown.Count, omitted);
    }

    /// <summary>The production fixed text, including the invariant decimal omission count.</summary>
    internal static string FixedText(int omitted, bool diagnostic)
    {
        var text = new StringBuilder(Version).Append('\n').Append(diagnostic ? DiagnosticIntroduction : Introduction);
        if (omitted > 0)
        {
            text.Append('\n').Append(string.Create(
                CultureInfo.InvariantCulture,
                $"{omitted} earlier listed step(s) are not shown, so this list is not complete."));
        }

        return text.ToString();
    }

    /// <summary>The most typed limitations a delegated role records (ADR-0044 section 16); the most recent are kept.</summary>
    internal const int MaxRecordedLimitations = 64;

    /// <summary>
    /// The listed steps of <paramref name="steps"/> (all execution attempts), in ascending index, as typed entries: the one
    /// classification both the digest text and the typed <see cref="EvidenceLimitation"/> metadata are built from, so the two
    /// cannot disagree. Uses typed fields and persisted step data only.
    /// </summary>
    internal static List<LimitedStep> Classify(IReadOnlyList<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var ordered = steps
            .Where(step => step.ToolCall is not null && step.Result is not null)
            .OrderBy(step => step.Index)
            .ToList();

        var listed = new List<LimitedStep>();
        for (var position = 0; position < ordered.Count; position++)
        {
            if (ClassifyAt(ordered, position) is { } entry)
            {
                listed.Add(entry);
            }
        }

        return listed;
    }

    /// <summary>
    /// The typed limitations of a delegated role's model loop (ADR-0044 section 16): at most <see cref="MaxRecordedLimitations"/>,
    /// the most recent kept, and how many earlier ones were left out. An entry's <see cref="EvidenceLimitation.EvidenceId"/> is the
    /// id of the Evidence the step produced (<c>"&lt;role&gt;-&lt;index&gt;"</c>, the id <see cref="DelegationRoleData.EvidenceOf"/>
    /// gives it) when it produced one, and <c>null</c> otherwise: no id is ever manufactured.
    /// </summary>
    internal static (IReadOnlyList<EvidenceLimitation> Limitations, int Omitted) Typed(IReadOnlyList<PlanStep> steps, AgentRoleKind role)
    {
        var listed = Classify(steps);
        var kept = listed.Count > MaxRecordedLimitations ? listed.GetRange(listed.Count - MaxRecordedLimitations, MaxRecordedLimitations) : listed;
        var prefix = DelegationRoleData.EvidencePrefix(role);
        var limitations = kept.Select(entry => new EvidenceLimitation
        {
            StepIndex = entry.Step.Index,
            ToolName = entry.UnknownTool || !IsCanonicalToolName(entry.Step.ToolCall!.ToolName) ? null : entry.Step.ToolCall.ToolName,
            UnknownTool = entry.UnknownTool,
            Outcome = entry.Step.Result!.Outcome,
            FailureKind = entry.Step.Result.FailureKind,
            Completeness = entry.Step.Result.Completeness,
            ShortenedFromCharacters = entry.ShortenedFromCharacters,
            EvidenceId = DelegationRoleData.ProducesEvidence(entry.Step) ? $"{prefix}-{entry.Step.Index}" : null,
        }).ToList();
        return (limitations, listed.Count - kept.Count);
    }

    /// <summary>The step at <paramref name="position"/> as a typed entry, or <c>null</c> when it is not a limitation.</summary>
    private static LimitedStep? ClassifyAt(List<PlanStep> ordered, int position)
    {
        var step = ordered[position];
        var result = step.Result!;
        var unknownTool = IsUnknownToolRejection(step);

        var completenessListed = result.Completeness is ToolResultCompleteness.Partial or ToolResultCompleteness.Unavailable;

        // Rule 2: every failure that is not a Validation one is listed and never superseded. Rule 3: a Validation failure is
        // listed unless a later step on the same resolved tool succeeded; an unknown-tool rejection has no resolved name, so
        // nothing supersedes it.
        var failureListed = result.Outcome != ToolOutcome.Success
            && (result.FailureKind != ToolFailureKind.Validation || unknownTool || !IsSuperseded(ordered, position));

        int? shortenedFrom = result.Outcome == ToolOutcome.Success && IsShortened(step, result) ? result.Output!.Length : null;

        return completenessListed || failureListed || shortenedFrom is not null
            ? new LimitedStep(step, unknownTool, completenessListed, failureListed, shortenedFrom)
            : null;
    }

    /// <summary>One digest line, rendered from a typed entry only.</summary>
    private static string Render(LimitedStep entry)
    {
        var result = entry.Step.Result!;
        var facts = new List<string>(3);
        if (entry.CompletenessListed)
        {
            facts.Add($"completeness {result.Completeness}");
        }

        if (entry.FailureListed)
        {
            facts.Add($"outcome {result.Outcome}, failure {result.FailureKind}");
        }

        if (entry.ShortenedFromCharacters is { } length)
        {
            facts.Add(string.Create(CultureInfo.InvariantCulture, $"observation shortened from {length} characters"));
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"- step {entry.Step.Index}: {ToolLabel(entry.Step, entry.UnknownTool)} — {string.Join("; ", facts)}");
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

/// <summary>One listed step, classified once: which facts make it a limitation. Both the digest line and the typed metadata come from it.</summary>
/// <param name="Step">The persisted step.</param>
/// <param name="UnknownTool">Whether it is a rejection of a name that resolved to no tool.</param>
/// <param name="CompletenessListed">Whether its completeness is <c>Partial</c> or <c>Unavailable</c>.</param>
/// <param name="FailureListed">Whether its failure is listed (not a superseded Validation failure).</param>
/// <param name="ShortenedFromCharacters">The output length when a successful result's observation was shortened.</param>
internal sealed record LimitedStep(PlanStep Step, bool UnknownTool, bool CompletenessListed, bool FailureListed, int? ShortenedFromCharacters);

/// <summary>A built digest.</summary>
/// <param name="Text">The digest, starting with its version line; at most <see cref="EvidenceLimitationsDigest.MaxCharacters"/> characters.</param>
/// <param name="EntryCount">How many steps it lists.</param>
/// <param name="OmittedCount">How many earlier qualifying steps it does not list (also said in the text).</param>
internal sealed record EvidenceLimitations(string Text, int EntryCount, int OmittedCount);
