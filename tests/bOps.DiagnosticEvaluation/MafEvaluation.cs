// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace bOps.DiagnosticEvaluation;

/// <summary>The outcome of one MAF check on one run, as the evaluation framework reported it.</summary>
internal sealed record CheckOutcome(string Evaluator, string Name, bool? Passed, string? Reason);

/// <summary>
/// Runs the bOps scorers through the Microsoft Agent Framework evaluation framework: one <see cref="EvalItem"/> per recorded run (the
/// goal as query, the persisted final answer as response, the conversation of the answer turn, the fact ledger as grounding context) and
/// two <see cref="LocalEvaluator"/>s whose checks are <see cref="FunctionEvaluator"/>s over <see cref="RunScores"/>. Nothing calls a model.
/// The P2 judges (MEAI evaluators through <see cref="MeaiEvaluatorAdapter"/>) will consume the same items.
/// </summary>
internal static class MafEvaluation
{
    public const string CompletionEvaluator = "bops-completion";
    public const string DiagnosisEvaluator = "bops-diagnosis";

    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<CheckOutcome>>> EvaluateAsync(
        IReadOnlyList<(RunCapture Run, RunScores Scores)> runs, CancellationToken cancellationToken)
    {
        var scoresByItem = new Dictionary<EvalItem, (string Code, RunScores Scores)>(ReferenceEqualityComparer.Instance);
        var all = new List<EvalItem>();
        var answered = new List<EvalItem>();
        foreach (var (run, scores) in runs)
        {
            var item = BuildItem(run);
            scoresByItem[item] = (run.Planned.BlindCode, scores);
            all.Add(item);
            if (scores.NotApplicableReason is null)
            {
                answered.Add(item);
            }
        }

        EvalCheckResult Check(EvalItem item, string name, Func<RunScores, (bool Passed, string Reason)> rule)
        {
            var (passed, reason) = rule(scoresByItem[item].Scores);
            return new EvalCheckResult(passed, reason, name);
        }

        var completion = new LocalEvaluator(
            FunctionEvaluator.Create("finalized", item => Check(item, "finalized", s =>
                (s.Completion.Finalized, $"status {s.Completion.Status}, final answer {(s.Completion.Finalized ? "present" : "absent")}"))),
            FunctionEvaluator.Create("plan-within-budget", item => Check(item, "plan-within-budget", s =>
                (!s.Completion.PlanExceedsBudget, $"plan {s.Completion.PlannedSteps} steps, budget {s.Completion.MaxSteps?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}"))),
            FunctionEvaluator.Create("knowledge-pertinent", item => Check(item, "knowledge-pertinent", s => s.Knowledge is not { } k
                ? (true, "knowledge not offered")
                : (k.CitedNotLoaded.Count == 0 && (k.RecallListed is null || k.RecallListed > 0),
                   $"read {k.Read.Count} of {k.Applicable.Count} applicable (listed recall {Format(k.RecallListed)}), cited but not loaded {k.CitedNotLoaded.Count}, not executed {k.ReadsNotExecuted}"))));

        var diagnosis = new LocalEvaluator(
            FunctionEvaluator.Create("live-dump-kept-distinct", item => Check(item, "live-dump-kept-distinct", s =>
            {
                var c = s.Classification!;
                return (c.LiveDumpSentencesAsCrash.Count == 0 && c.LiveDumpCountAsCrash == 0 && c.CrashCountAsLiveDump == 0,
                    $"sentences equating live dump and crash {c.LiveDumpSentencesAsCrash.Count}, live-dump count given as crashes {c.LiveDumpCountAsCrash}, crash count given as live dumps {c.CrashCountAsLiveDump}");
            })),
            FunctionEvaluator.Create("counts-consistent", item => Check(item, "counts-consistent", s =>
            {
                var c = s.Counts!;
                return (c.WrongFamily == 0 && c.Unverified == 0,
                    $"exact {c.Exact}, wrong family {c.WrongFamily}, unverified {c.Unverified}, accuracy {Format(c.Accuracy)}");
            })),
            FunctionEvaluator.Create("limitations-disclosed", item => Check(item, "limitations-disclosed", s =>
                (!s.Support!.PartialSourcesUndisclosed, $"partial sources {s.Support.PartialSources}, limitation wording {(s.Support.LimitationWording ? "present" : "absent")}"))));

        var outcomes = runs.ToDictionary(r => r.Run.Planned.BlindCode, _ => new List<CheckOutcome>(), StringComparer.Ordinal);
        Collect(await completion.EvaluateAsync(all, CompletionEvaluator, cancellationToken).ConfigureAwait(false), CompletionEvaluator, all, scoresByItem, outcomes);
        if (answered.Count > 0)
        {
            Collect(await diagnosis.EvaluateAsync(answered, DiagnosisEvaluator, cancellationToken).ConfigureAwait(false), DiagnosisEvaluator, answered, scoresByItem, outcomes);
        }

        return outcomes.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<CheckOutcome>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>Reads the per-item MEAI results back from the framework: one metric per check, its boolean value and its reason.</summary>
    private static void Collect(AgentEvaluationResults results, string evaluator, List<EvalItem> items,
        Dictionary<EvalItem, (string Code, RunScores Scores)> scoresByItem, Dictionary<string, List<CheckOutcome>> outcomes)
    {
        if (results.Items.Count != items.Count)
        {
            throw new InvalidOperationException($"{evaluator}: {results.Items.Count} results for {items.Count} items");
        }

        for (var i = 0; i < items.Count; i++)
        {
            var code = scoresByItem[items[i]].Code;
            foreach (var (name, metric) in results.Items[i].Metrics.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                outcomes[code].Add(new CheckOutcome(evaluator, name, (metric as BooleanMetric)?.Value, metric.Reason));
            }
        }
    }

    internal static EvalItem BuildItem(RunCapture run)
    {
        var conversation = Conversation(run);
        return new EvalItem(run.Goal, run.FinalAnswer, conversation) { Context = FactLedger(run) };
    }

    /// <summary>The answer turn as the model saw it (system and user turns, tool calls and results), followed by the persisted answer.</summary>
    private static List<ChatMessage> Conversation(RunCapture run)
    {
        var messages = new List<ChatMessage>();
        foreach (var message in run.AnswerTurnRequest?["messages"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var role = (string?)message["role"];
            var text = message["content"] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
            switch (role)
            {
                case "system":
                    messages.Add(new ChatMessage(ChatRole.System, text ?? string.Empty));
                    break;
                case "user":
                    messages.Add(new ChatMessage(ChatRole.User, text ?? string.Empty));
                    break;
                case "assistant":
                    var contents = new List<AIContent>();
                    if (!string.IsNullOrEmpty(text))
                    {
                        contents.Add(new TextContent(text));
                    }

                    foreach (var call in message["tool_calls"]?.AsArray().OfType<JsonObject>() ?? [])
                    {
                        contents.Add(new FunctionCallContent((string?)call["id"] ?? string.Empty, (string?)call["function"]?["name"] ?? string.Empty,
                            ParseArguments((string?)call["function"]?["arguments"])));
                    }

                    messages.Add(new ChatMessage(ChatRole.Assistant, contents));
                    break;
                case "tool":
                    messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent((string?)message["tool_call_id"] ?? string.Empty, text)]));
                    break;
            }
        }

        if (messages.Count == 0)
        {
            messages.Add(new ChatMessage(ChatRole.User, run.Goal));
        }

        messages.Add(new ChatMessage(ChatRole.Assistant, run.FinalAnswer));
        return messages;
    }

    private static Dictionary<string, object?>? ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The typed facts as a ledger (<c>F&lt;step&gt;.&lt;n&gt; type key=value</c>), the grounding context a P2 judge will receive.</summary>
    internal static string FactLedger(RunCapture run)
    {
        var builder = new StringBuilder();
        foreach (var fact in run.Facts)
        {
            builder.Append(fact.Id).Append(' ').Append(fact.Type).Append(' ').Append(fact.Key).Append('=')
                .Append(fact.Number?.ToString(CultureInfo.InvariantCulture) ?? "(non-numeric)").Append('\n');
        }

        return builder.ToString();
    }

    private static string Format(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a";
}
