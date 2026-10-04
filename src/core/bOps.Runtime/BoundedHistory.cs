// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>Deterministic HARDEN-8 model-facing history. Persisted steps are never changed.</summary>
internal static class BoundedHistory
{
    internal const int CompactHistorySteps = 12;
    internal const int CompactRecordMaxCharacters = 256;
    internal const int ArchiveSummaryMaxCharacters = 512;
    internal const int HeaderMaxCharacters = 128;
    internal const int ToolLabelMaxCharacters = 48;
    internal const int ArgumentDigestCharacters = 12;
    internal const int FixtureVerbatimTurnMaxCharacters = 4500;
    internal const int FixtureHistoricalMaxCharacters = 17_212;

    /// <summary>The label of a tool name that does not resolve to a registered manifest (ADR-0014 HARDEN-8 §1).</summary>
    internal const string UnknownToolLabel = "(unknown tool)";

    /// <summary>The fixed, mandatory end of the archive summary: how an individual archived step is addressed.</summary>
    internal const string ArchiveAddressabilityStatement =
        "An individual old tool-result step is addressable by ev1:<current-task-guid>:<known-persisted-step-index>";

    private const string OpenHeader = "<<<BOPS_HISTORY/v1>>>";
    private const string Header = "Bounded typed history; persisted evidence: EvidenceRead/v1.";
    private const string CloseHeader = "<<<END_BOPS_HISTORY/v1>>>";

    /// <param name="taskId">The current task; evidence ids are derived from it, never taken from model text.</param>
    /// <param name="goal">The goal turn, already projected when the request is an overflow recovery.</param>
    /// <param name="steps">The task's persisted steps.</param>
    /// <param name="verbatimSteps">K: the newest completed tool-call steps kept verbatim.</param>
    /// <param name="registeredToolName">The registered manifest name a tool-call step resolves to, or <c>null</c> when it does not.</param>
    /// <param name="requiredVerbatimStep">A step that must stay verbatim whatever K says (a replan's triggering step).</param>
    internal static ModelFacingHistory Build(
        Guid taskId,
        string goal,
        IReadOnlyList<PlanStep> steps,
        int verbatimSteps,
        Func<PlanStep, string?> registeredToolName,
        int? requiredVerbatimStep = null)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(registeredToolName);

        var toolSteps = steps.Where(step => step.ToolCall is not null).ToList();
        var required = requiredVerbatimStep is { } requiredIndex
            ? toolSteps.SingleOrDefault(step => step.Index == requiredIndex)
            : null;

        var recent = toolSteps.TakeLast(verbatimSteps).ToList();
        if (required is not null && recent.All(step => step.Index != required.Index))
        {
            recent.Add(required);
            recent.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        }

        var recentIndexes = recent.Select(step => step.Index).ToHashSet();
        var old = toolSteps.Where(step => !recentIndexes.Contains(step.Index)).ToList();
        var compact = old.TakeLast(CompactHistorySteps).ToList();
        var archive = old.Take(Math.Max(0, old.Count - compact.Count)).ToList();

        var history = new List<ChatTurn> { ChatTurn.FromUser(goal) };
        var boundedBlock = BuildBoundedBlock(taskId, archive, compact, registeredToolName);
        if (boundedBlock.Length > 0)
        {
            history.Add(ChatTurn.FromUser(boundedBlock));
        }

        foreach (var step in recent)
        {
            AddVerbatim(history, step);
        }

        var compactableVerbatimSteps = recent.Count(step => required is null || step.Index != required.Index);
        return new ModelFacingHistory(
            history,
            boundedBlock,
            archive.Count,
            compact.Count,
            recent.Count,
            verbatimSteps > 0 && compactableVerbatimSteps > 0);
    }

    internal static string EvidenceId(Guid taskId, int stepIndex) =>
        string.Create(CultureInfo.InvariantCulture, $"ev1:{taskId:N}:{stepIndex}");

    /// <summary>
    /// One compact record, exactly <c>s=;e=;r=;t=;a=;o=;f=;c=;v=;nr=;no=\n</c> (ADR-0014 HARDEN-8 §1). The whole record, newline
    /// included, is at most <paramref name="maxCharacters"/> UTF-16 code units. Only whole optional fields are ever removed, lowest
    /// priority first (<c>a</c>, then <c>t</c>, then <c>r</c>); nothing is sliced. When the required fields alone do not fit, history
    /// construction fails closed instead of producing a malformed record.
    /// </summary>
    /// <param name="taskId">The current task.</param>
    /// <param name="step">A persisted tool-call step.</param>
    /// <param name="registeredToolName">The registered manifest name the step resolves to, or <c>null</c> for an unresolved name.</param>
    /// <param name="maxCharacters">The record bound; production always uses <see cref="CompactRecordMaxCharacters"/>.</param>
    /// <exception cref="InvalidOperationException">The required fields cannot fit in the bound.</exception>
    internal static string CompactRecord(
        Guid taskId, PlanStep step, string? registeredToolName, int maxCharacters = CompactRecordMaxCharacters)
    {
        ArgumentNullException.ThrowIfNull(step);
        var call = step.ToolCall ?? throw new ArgumentException("A compact record describes a tool-call step.", nameof(step));
        var result = step.Result;

        string Number(int? value) => value is { } number ? number.ToString(CultureInfo.InvariantCulture) : "-";

        var index = step.Index.ToString(CultureInfo.InvariantCulture);
        var evidenceId = EvidenceId(taskId, step.Index);
        var revision = step.PlanRevision is { } planRevision ? planRevision.ToString(CultureInfo.InvariantCulture) : "?";
        var tool = ToolLabel(registeredToolName);
        var digest = DelegationHasher.ComputeArgumentsHash(call.Arguments)[..ArgumentDigestCharacters];
        var outcome = result?.Outcome.ToString() ?? "-";
        var failure = result is { Succeeded: false } && result.FailureKind != ToolFailureKind.Unspecified
            ? result.FailureKind.ToString()
            : "-";
        var completeness = result is not null && result.Completeness != ToolResultCompleteness.Unspecified
            ? result.Completeness.ToString()
            : "-";
        var verification = step.VerificationStatus?.ToString() ?? "-";

        string Format(bool withRevision, bool withTool, bool withDigest) =>
            new StringBuilder()
                .Append("s=").Append(index).Append(';')
                .Append("e=").Append(evidenceId).Append(';')
                .Append(withRevision ? $"r={revision};" : string.Empty)
                .Append(withTool ? $"t={tool};" : string.Empty)
                .Append(withDigest ? $"a={digest};" : string.Empty)
                .Append("o=").Append(outcome).Append(';')
                .Append("f=").Append(failure).Append(';')
                .Append("c=").Append(completeness).Append(';')
                .Append("v=").Append(verification).Append(';')
                .Append("nr=").Append(Number(result?.Output?.Length)).Append(';')
                .Append("no=").Append(Number(step.Observation?.Length)).Append('\n')
                .ToString();

        // Priority r > t > a: the lowest-priority optional field goes first, whole, at its field boundary.
        foreach (var (withRevision, withTool, withDigest) in new[] { (true, true, true), (true, true, false), (true, false, false), (false, false, false) })
        {
            var record = Format(withRevision, withTool, withDigest);
            if (record.Length <= maxCharacters)
            {
                return record;
            }
        }

        throw new InvalidOperationException(
            $"The compact history record of step {step.Index} does not fit {maxCharacters} UTF-16 code units; history construction fails closed.");
    }

    /// <summary>
    /// The single aggregate of every step older than the compact window: archived range and step count (mandatory), typed counts
    /// where present (optional, dropped whole, last first), then the fixed addressability statement (mandatory, always last). At
    /// most <paramref name="maxCharacters"/> UTF-16 code units; it lists no evidence id and no evidence text.
    /// </summary>
    /// <param name="archived">The archived tool-call steps in ascending index order.</param>
    /// <param name="maxCharacters">The bound; production always uses <see cref="ArchiveSummaryMaxCharacters"/>.</param>
    /// <exception cref="InvalidOperationException">The mandatory fields cannot fit in the bound.</exception>
    internal static string ArchiveSummary(IReadOnlyList<PlanStep> archived, int maxCharacters = ArchiveSummaryMaxCharacters)
    {
        ArgumentNullException.ThrowIfNull(archived);
        if (archived.Count == 0)
        {
            return string.Empty;
        }

        static string Counts<T>(string name, IEnumerable<T> values) where T : struct, Enum
        {
            var grouped = values.GroupBy(value => value).ToDictionary(group => group.Key, group => group.Count());
            var parts = Enum.GetValues<T>().Where(grouped.ContainsKey)
                .Select(value => string.Create(CultureInfo.InvariantCulture, $"{value}={grouped[value]}"))
                .ToList();
            return parts.Count == 0 ? string.Empty : $"{name}[{string.Join(',', parts)}]";
        }

        var withResult = archived.Where(step => step.Result is not null).ToList();
        var optional = new[]
        {
            Counts("outcome", withResult.Select(step => step.Result!.Outcome)),
            Counts("failure", withResult.Where(step => !step.Result!.Succeeded && step.Result.FailureKind != ToolFailureKind.Unspecified)
                .Select(step => step.Result!.FailureKind)),
            Counts("complete", withResult.Where(step => step.Result!.Completeness != ToolResultCompleteness.Unspecified)
                .Select(step => step.Result!.Completeness)),
            Counts("verification", archived.Where(step => step.VerificationStatus is not null).Select(step => step.VerificationStatus!.Value)),
        }.Where(field => field.Length > 0).ToList();

        var mandatory = string.Create(CultureInfo.InvariantCulture,
            $"archive;range={archived[0].Index}..{archived[^1].Index};count={archived.Count}");
        while (true)
        {
            var summary = string.Join(';', [mandatory, .. optional, ArchiveAddressabilityStatement]);
            if (summary.Length <= maxCharacters)
            {
                return summary;
            }

            if (optional.Count == 0)
            {
                throw new InvalidOperationException(
                    $"The archive summary does not fit {maxCharacters} UTF-16 code units; history construction fails closed.");
            }

            optional.RemoveAt(optional.Count - 1);
        }
    }

    private static string ToolLabel(string? registeredToolName)
    {
        if (string.IsNullOrEmpty(registeredToolName))
        {
            return UnknownToolLabel;
        }

        var oneLine = new string([.. registeredToolName.Select(character => char.IsControl(character) || character == ';' ? '_' : character)]);
        return TakeUtf16(oneLine, ToolLabelMaxCharacters);
    }

    private static string BuildBoundedBlock(
        Guid taskId, List<PlanStep> archive, List<PlanStep> compact, Func<PlanStep, string?> registeredToolName)
    {
        if (archive.Count == 0 && compact.Count == 0)
        {
            return string.Empty;
        }

        // Open header, header, archive line and close header each end a line but the last: three delimiters. The compact
        // records carry their own newline inside their 256 units.
        var fixedCharacters = OpenHeader.Length + Header.Length + CloseHeader.Length + 3;
        if (fixedCharacters > HeaderMaxCharacters)
        {
            throw new InvalidOperationException("The bounded-history headers exceed their architecture limit.");
        }

        var builder = new StringBuilder().Append(OpenHeader).Append('\n').Append(Header).Append('\n');
        if (archive.Count > 0)
        {
            builder.Append(ArchiveSummary(archive)).Append('\n');
        }

        foreach (var step in compact)
        {
            builder.Append(CompactRecord(taskId, step, registeredToolName(step)));
        }

        return builder.Append(CloseHeader).ToString();
    }

    private static void AddVerbatim(List<ChatTurn> history, PlanStep step)
    {
        var emitted = new List<ModelToolCall> { step.ToolCall! };
        if (step.UnexecutedToolCalls is { Count: > 0 })
        {
            emitted.AddRange(step.UnexecutedToolCalls);
        }

        history.Add(ChatTurn.FromAssistantToolCalls(emitted));
        history.Add(ChatTurn.FromToolResult(step.ToolCall!.Id, AgentRunner.WrapToolOutput(step.Observation ?? string.Empty)));
        foreach (var call in step.UnexecutedToolCalls ?? [])
        {
            history.Add(ChatTurn.FromToolResult(call.Id, AgentRunner.WrapToolOutput(
                "Not executed: only one tool call is executed per step. Ask again next step if still needed.")));
        }
    }

    internal static string TakeUtf16(string value, int maximum)
    {
        if (value.Length <= maximum)
        {
            return value;
        }

        var length = maximum;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }
}

internal sealed record ModelFacingHistory(
    IReadOnlyList<ChatTurn> Turns,
    string BoundedBlock,
    int ArchiveSteps,
    int CompactSteps,
    int VerbatimSteps,
    bool HasCompactableVerbatimHistory)
{
    internal int HistoricalCharacters => Turns.Skip(1).Sum(turn =>
        (turn.Content?.Length ?? 0)
        + (turn.ToolCalls?.Sum(call => call.ToolName.Length + call.Arguments.ToJson().ToJsonString().Length) ?? 0));
}
