// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using bOps.Abstractions;
using bOps.AgentFramework.Tests.PromptAssembly;
using bOps.AgentFramework.Tests.Regression;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests.ScriptedAgentic;

/// <summary>
/// One provider request of a scripted run, classified from what the request IS (its system prompt, tools and last turns) and never from
/// what the script intended. <see cref="Cursor"/> is the plan position the request offers: the index of the plan step whose tool is the
/// only operational tool offered, the plan length when none is offered at plan end, <c>null</c> for a planning or replan request.
/// </summary>
internal sealed record AeCall(
    int Index,
    CallPurpose Purpose,
    string Reply,
    string? Recovery,
    bool Live05Continuation,
    int KnowledgeTurns,
    IReadOnlyList<string> Operational,
    IReadOnlyList<string> Control,
    int? Revision,
    int? PlanLength,
    int? Cursor);

/// <summary>One persisted step of a scripted run, with the cursor the runtime's own fold derives after it.</summary>
internal sealed record AeStep(
    int Index,
    string Kind,
    string? Tool,
    string? Outcome,
    int? Revision,
    int? PlannedIndex,
    string? Classification,
    int? CursorAfter,
    int ModelCalls);

/// <summary>What changed between two consecutive plan revisions, measured against the steps still owed when the first one ended.</summary>
internal sealed record AePlanDiff(int FromRevision, int ToRevision, string Rationale, IReadOnlyList<string> Owed, IReadOnlyList<string> Added, IReadOnlyList<string> Dropped);

/// <summary>
/// The runtime counts of one scripted run (section 6 of the AE-1 brief). Every count comes from persisted state, the audit log or the
/// requests the adapter received. <see cref="SyntheticTokens"/> are the 100 + 10 per call that the offline upstream declares: they are
/// not Qwen tokens and say nothing about its cost or latency.
/// </summary>
internal sealed record AeMetrics(
    int ModelCalls,
    IReadOnlyDictionary<CallPurpose, int> CallsByPurpose,
    int ToolExecutions,
    IReadOnlyList<string> ToolsExecuted,
    int RefusedToolCalls,
    int PrematureFinals,
    int ReplansAccepted,
    int ReplanCalls,
    int ReplansCharged,
    int ReplansSkipped,
    int Live05Continuations,
    int RecoveryTurns,
    int EvidenceReads,
    int RedundantEvidenceReads,
    int KnowledgeReads,
    int RepeatedKnowledgeReads,
    AgentTaskStatus FinalStatus,
    TaskTerminalKind? Terminal,
    long SyntheticTokens,
    string FinalClass);

/// <summary>Everything one AE-1 scenario produced: the PA-1 run, the classified requests and steps, the plan changes and the counts.</summary>
internal sealed class AeRun
{
    public required string Id { get; init; }

    public required PaRun Pa { get; init; }

    public required IReadOnlyList<PaReply> Script { get; init; }

    public required IReadOnlyList<AeCall> Calls { get; init; }

    public required IReadOnlyList<AeStep> Steps { get; init; }

    public required IReadOnlyList<AePlanDiff> PlanDiffs { get; init; }

    public required AeMetrics Metrics { get; init; }

    /// <summary>Audit events written by a host the runner does not own (the knowledge host writes its own, e.g. knowledge reads and index publications).</summary>
    public IReadOnlyList<AuditEvent> HostAudit { get; init; } = [];

    public TaskState State => Pa.State;

    /// <summary>The text the task ended with: the persisted final-response step, or <c>null</c> when none exists.</summary>
    public string? FinalText => State.Steps.LastOrDefault(FinalResponse.IsFinalStep)?.Observation;

    public IEnumerable<AuditEvent> AllAudit => Pa.Audit.Events.Concat(HostAudit);

    public IEnumerable<T> Audit<T>() where T : AuditEvent => AllAudit.OfType<T>();
}

/// <summary>AE-1: turns a PA-1 run into the observable record of AE-1 (requests by purpose, steps with cursors, plan changes, counts).</summary>
internal static partial class AeObserver
{
    private const string Rc1 = "Your last reply was empty";
    private const string Rc2 = "Your last reply reached the output limit";
    private const string Live05 = "Runtime control instruction: your previous final answer was rejected";

    [GeneratedRegex(@"^Plan \(revision (?<revision>\d+)\):", RegexOptions.Multiline)]
    private static partial Regex PlanHeader();

    internal static AeRun Observe(string id, IReadOnlyList<PaReply> script, PaRun run, IReadOnlyList<AuditEvent>? hostAudit = null)
    {
        var calls = new List<AeCall>();
        for (var index = 0; index < run.Seen.Count; index++)
        {
            calls.Add(Classify(index, run.Seen[index], index < script.Count ? script[index] : null));
        }

        var steps = Steps(run.State);
        return new AeRun
        {
            Id = id,
            Pa = run,
            Script = script,
            Calls = calls,
            Steps = steps,
            PlanDiffs = PlanDiffs(run.State),
            Metrics = Measure(run, calls, hostAudit ?? []),
            HostAudit = hostAudit ?? [],
        };
    }

    private static AeCall Classify(int index, ModelRequest request, PaReply? reply)
    {
        var history = request.History;
        var lastUser = history.LastOrDefault(turn => turn.Role == ChatRole.User);
        var lastTurn = history.Count > 0 ? history[^1] : null;
        var purpose =
            request.SystemPrompt.Contains("lay out your plan", StringComparison.Ordinal) ? CallPurpose.Planning
            : request.SystemPrompt.Contains("Revise it", StringComparison.Ordinal) ? CallPurpose.Replanning
            : lastTurn is { Role: ChatRole.User } && string.Equals(lastUser?.Content, EvidenceGroundingLedger.CheckInstruction, StringComparison.Ordinal)
                ? CallPurpose.GroundingCheck
            : lastTurn is { Role: ChatRole.Tool } && lastTurn.Content?.StartsWith("EvidenceRead/v1", StringComparison.Ordinal) == true
                ? CallPurpose.EvidenceReadContinuation
            : CallPurpose.Step;

        var runtimeTurns = history
            .Where(turn => turn.Role == ChatRole.User && turn.Provenance?.Origin == ContextOrigin.Runtime)
            .Select(turn => turn.Content ?? string.Empty)
            .ToList();
        var recovery = runtimeTurns.Any(text => text.StartsWith(Rc2, StringComparison.Ordinal)) ? "RC2"
            : runtimeTurns.Any(text => text.StartsWith(Rc1, StringComparison.Ordinal)) ? "RC1"
            : null;
        var live05 = runtimeTurns.Any(text => text.StartsWith(Live05, StringComparison.Ordinal));
        var knowledgeTurns = history.Count(turn => turn.Provenance?.Origin == ContextOrigin.Knowledge);

        var operational = PaInvariants.Operational(request);
        int? revision = null;
        int? planLength = null;
        int? cursor = null;
        if (purpose is CallPurpose.Step or CallPurpose.EvidenceReadContinuation or CallPurpose.GroundingCheck
            && PlanHeader().Match(request.SystemPrompt) is { Success: true } header)
        {
            revision = int.Parse(header.Groups["revision"].Value, CultureInfo.InvariantCulture);
            var plan = PaInvariants.PlanTools(request.SystemPrompt);
            planLength = plan.Count;
            cursor = operational.Count == 1 ? plan.ToList().IndexOf(operational[0]) : plan.Count;
        }

        return new AeCall(
            index, purpose, Describe(reply), recovery, live05, knowledgeTurns, operational, PaInvariants.Control(request),
            revision, planLength, cursor);
    }

    /// <summary>What the scripted model did on a request, as a short label (the script is the fake model's side of the exchange).</summary>
    internal static string Describe(PaReply? reply) => reply switch
    {
        null => "(unscripted)",
        { Calls.Count: > 0 } => "call:" + reply.Calls[0].Tool,
        { Content: { } text } when text.StartsWith("{\"rationale\"", StringComparison.Ordinal) => "plan",
        { FinishReason: "length", Content: { Length: 0 } } => "length-empty",
        { FinishReason: "length" } => "length-truncated",
        { Content: { Length: 0 } } => "empty",
        _ => "text",
    };

    /// <summary>What a persisted step is, from the runtime's own markers.</summary>
    internal static string KindOf(PlanStep step) =>
        PrematureFinal.IsStep(step) ? "premature-final"
        : EarlyFinalRecovery.IsStep(step) ? "live05-continuation"
        : FinalResponse.IsFinalStep(step) ? "final"
        : step.ToolCall is { ToolNameError: not null } ? "refused-call"
        : step.ToolCall is not null ? "tool"
        : "other";

    private static List<AeStep> Steps(TaskState state)
    {
        var result = new List<AeStep>();
        foreach (var step in state.Steps)
        {
            var kind = KindOf(step);
            int? cursorAfter = null;
            if (step.PlanRevision is { } revision && revision < state.Plans.Count)
            {
                cursorAfter = PlannedStepPosition.Derive(state.Plans[revision], state.Steps.Where(item => item.Index <= step.Index)).Cursor;
            }

            result.Add(new AeStep(
                step.Index, kind, step.ToolCall?.ToolName, step.Result?.Outcome.ToString(), step.PlanRevision, step.PlannedStepIndex,
                step.ExecutionClassification?.ToString(), cursorAfter, step.ModelCalls?.Count ?? 0));
        }

        return result;
    }

    private static List<AePlanDiff> PlanDiffs(TaskState state)
    {
        var result = new List<AePlanDiff>();
        for (var revision = 1; revision < state.Plans.Count; revision++)
        {
            var previous = state.Plans[revision - 1];
            var current = state.Plans[revision];
            var cursor = PlannedStepPosition.Derive(previous, state.Steps).Cursor;
            var owed = previous.Steps.Skip(Math.Max(0, cursor)).Select(step => step.ExpectedTool ?? "(none)").ToList();
            var proposed = current.Steps.Select(step => step.ExpectedTool ?? "(none)").ToList();
            var added = proposed.ToList();
            foreach (var tool in owed)
            {
                added.Remove(tool);
            }

            var dropped = owed.ToList();
            foreach (var tool in proposed)
            {
                dropped.Remove(tool);
            }

            result.Add(new AePlanDiff(revision - 1, revision, current.Rationale, owed, added, dropped));
        }

        return result;
    }

    private static AeMetrics Measure(PaRun run, IReadOnlyList<AeCall> calls, IReadOnlyList<AuditEvent> hostAudit)
    {
        var state = run.State;
        var audit = run.Audit.Events.Concat(hostAudit).ToList();
        var toolEvents = audit.OfType<ToolCallAuditEvent>().ToList();
        var executed = toolEvents.Where(evt => evt.EffectiveTimeout is not null).Select(evt => evt.Tool).ToList();
        var reads = audit.OfType<EvidenceReadAuditEvent>().Where(evt => evt.ResultCode == EvidenceReadResultCode.Success).ToList();
        var knowledge = audit.OfType<KnowledgeReadAuditEvent>().Where(evt => evt.ResultCode == KnowledgeReadResultCode.Success).ToList();
        var byPurpose = Enum.GetValues<CallPurpose>().ToDictionary(purpose => purpose, purpose => calls.Count(call => call.Purpose == purpose));
        var finalStep = state.Steps.LastOrDefault(FinalResponse.IsFinalStep);
        return new AeMetrics(
            run.Seen.Count,
            byPurpose,
            run.Tools.Values.Sum(tool => tool.Invocations),
            executed,
            toolEvents.Count(evt => evt.EffectiveTimeout is null),
            state.Steps.Count(PrematureFinal.IsStep),
            state.Plans.Count - 1,
            calls.Count(call => call.Purpose == CallPurpose.Replanning),
            state.Accounting?.LifetimeReplans ?? 0,
            state.Steps.Count(step => step.EarlyFinalRecoveryDecision is { PaidReplanSkipped: true }),
            state.Steps.Count(EarlyFinalRecovery.IsStep),
            RecoveryTurnCount(calls),
            reads.Count,
            reads.Count - reads.Select(evt => (evt.EvidenceId, evt.Source, evt.Offset, evt.RequestedLength)).Distinct().Count(),
            knowledge.Count,
            knowledge.Count(evt => evt.Repeat == true),
            state.Status,
            state.TerminalReason?.Kind,
            state.Accounting?.TokensUsed ?? 0,
            finalStep is null
                ? "none (no final-response step persisted)"
                : HasAnswerContent(finalStep.Observation) ? "usable final" : "unusable final");
    }

    /// <summary>
    /// The test side's own, deliberately independent notion of an answer: at least one letter or digit. It does not call the runtime's
    /// classifier, so a regression of that classifier cannot make this measurement agree with it.
    /// </summary>
    internal static bool HasAnswerContent(string? text) => ModelAnswer(text).Any(char.IsLetterOrDigit);

    /// <summary>
    /// The part of a persisted final that the model wrote: everything before the runtime's own "Evidence limitations" section. That section
    /// is appended by the runtime (ADR-0042 §6) and is never an answer; counting it would let a vacuous reply look usable, which is exactly
    /// how a lone "&gt;" once ended as a Completed task.
    /// </summary>
    internal static string ModelAnswer(string? text)
    {
        if (text is null)
        {
            return string.Empty;
        }

        var kept = new List<string>();
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (string.Equals(line.TrimStart('#', ' ', '\t').TrimEnd(), "Evidence limitations", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    /// <summary>
    /// The recovery turns the runtime added: a recovery instruction enters the logical call once and is dropped when the retry is answered
    /// (PRA-R4); the turns are still counted by the first request that carries each kind, not by every request that repeats it.
    /// </summary>
    private static int RecoveryTurnCount(IReadOnlyList<AeCall> calls)
    {
        var count = 0;
        for (var index = 0; index < calls.Count; index++)
        {
            if (calls[index].Recovery is not null && (index == 0 || calls[index - 1].Recovery != calls[index].Recovery))
            {
                count++;
            }
        }

        return count;
    }

    // ---- rendering (report and review) ----

    internal static string RenderCalls(AeRun run)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"| # | purpose | scripted reply | offered operational | offered control | revision | cursor | tags |");
        text.AppendLine("|---:|---|---|---|---|---:|---:|---|");
        foreach (var call in run.Calls)
        {
            var tags = string.Join(' ', new[] { call.Recovery, call.Live05Continuation ? "LIVE-05" : null, call.KnowledgeTurns > 0 ? $"K-turns={call.KnowledgeTurns}" : null }.OfType<string>());
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {call.Index} | {call.Purpose} | {call.Reply} | {Join(call.Operational)} | {Join(call.Control)} | {call.Revision?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {Cursor(call)} | {tags} |");
        }

        return text.ToString();
    }

    private static string Cursor(AeCall call) => call.Cursor is { } cursor ? $"{cursor}/{call.PlanLength}" : "-";

    private static string Join(IReadOnlyList<string> items) => items.Count == 0 ? "-" : string.Join(", ", items);

    internal static string RenderSteps(AeRun run)
    {
        var text = new StringBuilder();
        text.AppendLine("| step | kind | tool | outcome | revision | planned | classification | cursor after | model calls |");
        text.AppendLine("|---:|---|---|---|---:|---:|---|---:|---:|");
        foreach (var step in run.Steps)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {step.Index} | {step.Kind} | {step.Tool ?? "-"} | {step.Outcome ?? "-"} | {step.Revision?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {step.PlannedIndex?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {step.Classification ?? "-"} | {step.CursorAfter?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {step.ModelCalls} |");
        }

        return text.ToString();
    }

    internal static string MetricsRow(AeRun run, AeVerdict verdict)
    {
        var m = run.Metrics;
        string Purpose(CallPurpose purpose) => m.CallsByPurpose[purpose].ToString(CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture,
            $"| {run.Id} | {m.ModelCalls} | P{Purpose(CallPurpose.Planning)} / R{Purpose(CallPurpose.Replanning)} / S{Purpose(CallPurpose.Step)} / E{Purpose(CallPurpose.EvidenceReadContinuation)} / G{Purpose(CallPurpose.GroundingCheck)} "
            + $"| {m.ToolExecutions} | {m.RefusedToolCalls} | {m.PrematureFinals} | {m.ReplansAccepted} ({m.ReplanCalls} calls, {m.ReplansCharged} charged, {m.ReplansSkipped} skipped) | {m.RecoveryTurns} "
            + $"| {m.EvidenceReads} ({m.RedundantEvidenceReads} redundant) | {m.KnowledgeReads} | {m.FinalStatus}{(m.Terminal is { } t ? $"/{t}" : string.Empty)} | {m.SyntheticTokens} | **{verdict.Label}** |");
    }

    internal const string MetricsHeader =
        "| Scenario | modelCalls | callsByPurpose | toolExecutions | refusedCalls | prematureFinals | replans | recoveryTurns | evidenceReads | knowledgeReads | finalStatus | syntheticTokens | diagnosticOutcome |\n"
        + "|---|---:|---|---:|---:|---:|---|---:|---|---:|---|---:|---|";
}
