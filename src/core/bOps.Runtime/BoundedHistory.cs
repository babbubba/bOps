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
    internal const int FixtureVerbatimTurnMaxCharacters = 4500;
    internal const int FixtureHistoricalMaxCharacters = 17_212;

    private const string OpenHeader = "<<<BOPS_HISTORY/v1>>>";
    private const string Header = "Bounded typed history; persisted evidence: EvidenceRead/v1.";
    private const string CloseHeader = "<<<END_BOPS_HISTORY/v1>>>";

    internal static ModelFacingHistory Build(
        Guid taskId,
        string goal,
        IReadOnlyList<PlanStep> steps,
        int verbatimSteps,
        int? requiredVerbatimStep = null)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(steps);

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
        var boundedBlock = BuildBoundedBlock(taskId, archive, compact);
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

    internal static string CompactRecord(Guid taskId, PlanStep step)
    {
        var tool = step.ToolCall?.ToolNameError is null && IsCanonicalToolName(step.ToolCall?.ToolName)
            ? step.ToolCall!.ToolName
            : "(invalid)";
        var result = step.Result;
        var record = string.Create(CultureInfo.InvariantCulture,
            $"i={step.Index};tool={tool};o={result?.Outcome.ToString() ?? "None"};" +
            $"f={result?.FailureKind.ToString() ?? "None"};c={result?.Completeness.ToString() ?? "None"};" +
            $"v={step.VerificationStatus?.ToString() ?? "Unknown"};ev={EvidenceId(taskId, step.Index)};" +
            $"result={result?.Output?.Length.ToString(CultureInfo.InvariantCulture) ?? "null"};" +
            $"observation={step.Observation?.Length.ToString(CultureInfo.InvariantCulture) ?? "null"}");
        return TakeUtf16(record, CompactRecordMaxCharacters);
    }

    internal static string ArchiveSummary(IReadOnlyList<PlanStep> archived)
    {
        if (archived.Count == 0)
        {
            return string.Empty;
        }

        static string Counts<T>(IEnumerable<T> values) where T : struct, Enum =>
            string.Join(',', Enum.GetValues<T>().Select(value =>
                $"{value}={values.Count(candidate => EqualityComparer<T>.Default.Equals(candidate, value))}"));

        var outcomes = archived.Where(step => step.Result is not null).Select(step => step.Result!.Outcome);
        var failures = archived.Where(step => step.Result is not null && !step.Result.Succeeded)
            .Select(step => step.Result!.FailureKind);
        var completeness = archived.Where(step => step.Result is not null).Select(step => step.Result!.Completeness);
        var verification = archived.Where(step => step.VerificationStatus is not null).Select(step => step.VerificationStatus!.Value);
        var summary = FormattableString.Invariant(
            $"archive i={archived[0].Index}..{archived[^1].Index};n={archived.Count};individual evidence remains addressable via EvidenceRead/v1;outcome[{Counts(outcomes)}];failure[{Counts(failures)}];complete[{Counts(completeness)}];verification[{Counts(verification)}].");
        return TakeUtf16(summary, ArchiveSummaryMaxCharacters);
    }

    private static string BuildBoundedBlock(Guid taskId, List<PlanStep> archive, List<PlanStep> compact)
    {
        if (archive.Count == 0 && compact.Count == 0)
        {
            return string.Empty;
        }

        var fixedCharacters = OpenHeader.Length + Header.Length + CloseHeader.Length + 2;
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
            builder.Append(CompactRecord(taskId, step)).Append('\n');
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

    private static bool IsCanonicalToolName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= 128
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

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
