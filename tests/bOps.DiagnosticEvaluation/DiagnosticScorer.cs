// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace bOps.DiagnosticEvaluation;

/// <summary>One number of the answer and what the run's facts say about it. No answer text: the sentence index only.</summary>
internal sealed record CountFinding(int Sentence, long Value, string Verdict, string? Family, IReadOnlyList<string> ValueFamilies);

/// <summary>M1 — crash vs live-dump classification.</summary>
internal sealed record ClassificationScore(
    IReadOnlyDictionary<string, double> EvidenceCounts,
    bool LiveDumpInEvidence,
    bool CrashInEvidence,
    bool AnswerMentionsLiveDump,
    IReadOnlyList<int> LiveDumpSentencesAsCrash,
    int LiveDumpCountAsCrash,
    int CrashCountAsLiveDump,
    bool LiveDumpNotMentioned);

/// <summary>M2 — count accuracy. Each number goes to the family named closest to it. <c>Accuracy</c> = exact ÷ (exact + wrong family +
/// unverified); numbers near no family term, and numbers found in the tool output but in no typed fact of that family or derived as the
/// sum of two such numbers (<c>InEvidence</c>), are reported but not scored.</summary>
internal sealed record CountScore(int Exact, int WrongFamily, int Unverified, int InEvidence, int Unattributed, double? Accuracy,
    IReadOnlyList<CountFinding> Findings);

/// <summary>M3 — the structural signals available without a judge.</summary>
internal sealed record SupportScore(int PartialSources, bool LimitationWording, bool PartialSourcesUndisclosed, int UnverifiedCounts);

/// <summary>M4 — pertinent use of Knowledge. Recall is given against the applicable entries the index listed (what the model could read)
/// and against every applicable entry.</summary>
internal sealed record KnowledgeScore(
    string? Pack,
    IReadOnlyList<string> Listed,
    IReadOnlyList<string> Applicable,
    IReadOnlyList<string> Read,
    int ReadsServed,
    int ReadsRefused,
    int ReadsInAnswerTurn,
    int ReadsNotExecuted,
    double? Precision,
    double? RecallListed,
    double? RecallAll,
    IReadOnlyList<string> CitedNotLoaded);

/// <summary>M5 — task completion and finalization reliability.</summary>
internal sealed record CompletionScore(
    string Status,
    int? TerminalKind,
    bool Finalized,
    string Disclosure,
    string Grounding,
    int PlannedSteps,
    int? MaxSteps,
    bool PlanExceedsBudget,
    int PlanRevisions,
    int StepsPersisted,
    int UnexecutedToolCalls,
    int ModelCalls,
    int ModelCallFailures);

/// <summary>Every score of one run. A run without a final answer has no M1–M3 (null = not applicable, never 0); a run whose Knowledge was not
/// offered has no M4.</summary>
internal sealed record RunScores(
    ClassificationScore? Classification,
    CountScore? Counts,
    SupportScore? Support,
    KnowledgeScore? Knowledge,
    CompletionScore Completion,
    string? NotApplicableReason);

/// <summary>The deterministic (class C/H) scorers of BOPS-0126 P1. Pure functions of a <see cref="RunCapture"/> and a <see cref="ScenarioKey"/>.</summary>
internal static partial class DiagnosticScorer
{
    [GeneratedRegex(@"\bk(\d+)\b")]
    private static partial Regex KnowledgeRef();

    private static readonly (string Suffix, string Name)[] GroundingSuffixes =
    [
        ("; evidence grounding check cited no contradiction", "noContradictionCited"),
        ("; evidence grounding corrected", "corrected"),
        ("; evidence grounding check unavailable", "checkUnavailable"),
        ("; evidence claims corrected", "claimsCorrected"),
    ];

    private static readonly (string Prefix, string Name)[] DisclosurePrefixes =
    [
        ("Final response; evidence disclosure re-ask accepted", "reAskAccepted"),
        ("Final response; evidence disclosure re-ask result not used", "reAskNotUsed"),
        ("Final response; evidence limitations appended by runtime", "appended"),
    ];

    public static RunScores Score(RunCapture run, ScenarioKey key)
    {
        var completion = Completion(run);
        var knowledge = Knowledge(run, key);
        if (!run.HasState)
        {
            return new RunScores(null, null, null, knowledge, completion, "no task state");
        }

        if (string.IsNullOrWhiteSpace(run.FinalAnswer))
        {
            return new RunScores(null, null, null, knowledge, completion, "no final answer");
        }

        var sentences = AnswerText.Sentences(run.FinalAnswer);
        var familyValues = FamilyValues(run, key);
        var counts = Counts(run, key, sentences, familyValues);
        return new RunScores(
            Classification(key, sentences, familyValues, counts),
            counts,
            Support(run, key, sentences, counts),
            knowledge,
            completion,
            null);
    }

    internal static Dictionary<string, HashSet<long>> FamilyValues(RunCapture run, ScenarioKey key)
    {
        var values = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        foreach (var family in key.Families)
        {
            values[family.Id] = run.Facts
                .Where(f => f.Number is { } n && n == Math.Floor(n) && family.Facts.Any(s => s.Matches(f)))
                .Select(f => (long)f.Number!.Value)
                .ToHashSet();
        }

        return values;
    }

    private static CountScore Counts(RunCapture run, ScenarioKey key, IReadOnlyList<string> sentences, Dictionary<string, HashSet<long>> familyValues)
    {
        var factValues = run.Facts.Where(f => f.Number is { } n && n == Math.Floor(n)).Select(f => (long)f.Number!.Value).ToHashSet();
        var windows = run.Facts.Where(f => f.Number is not null && key.WindowFacts.Any(s => s.Matches(f))).Select(f => (long)f.Number!.Value).ToHashSet();
        var families = key.Families.ToDictionary(f => f.Id, StringComparer.Ordinal);

        var findings = new List<CountFinding>();
        var numbers = AnswerText.Numbers(sentences, v => run.OutputNumbers.Contains(v) || factValues.Contains(v));
        var sentenceNumbers = numbers.ToLookup(n => n.Sentence);
        foreach (var number in numbers)
        {
            var sentence = sentences[number.Sentence];
            if (AnswerText.PrecededBy(sentence, number.Offset, StrongIdentifierWords, 10)
                || (run.EventIds.Contains(number.Value) && IsIdentifier(sentence, number, key)))
            {
                continue; // "event id 193", "Kernel-Power 41", "whea-logger 19", "evento 6008": an identifier, not a count
            }

            if (windows.Contains(number.Value) && (sentence.Contains("giorn", StringComparison.Ordinal) || sentence.Contains("day", StringComparison.Ordinal)))
            {
                continue; // the observation window, not a count
            }

            var family = AnswerText.NearestFamily(sentence, number, key.Families, key.Negations);
            var valueFamilies = familyValues.Where(kv => kv.Value.Contains(number.Value)).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
            string verdict;
            if (family is null)
            {
                verdict = "unattributed";
            }
            else if (familyValues[family].Contains(number.Value))
            {
                verdict = "exact";
            }
            else if (families[family].ConfusableWith?.Any(g => IsHeadline(familyValues[g], number.Value)) == true)
            {
                verdict = "wrongFamily"; // the count of a family this one is often mistaken for (a live-dump count given as BSODs)
            }
            else if (run.OutputNumbers.Contains(number.Value) || factValues.Contains(number.Value))
            {
                verdict = "inEvidence"; // supported by the tool output, but no typed fact of this family carries it
            }
            else if (IsSumOfEvidence(number, sentenceNumbers[number.Sentence], v => run.OutputNumbers.Contains(v) || factValues.Contains(v)))
            {
                verdict = "derived"; // the sum of two numbers of the same sentence that the evidence holds ("486 (442 + 44)")
            }
            else
            {
                verdict = "unverified";
            }

            findings.Add(new CountFinding(number.Sentence, number.Value, verdict, family, valueFamilies));
        }

        int Count(string verdict) => findings.Count(f => f.Verdict == verdict);
        var exact = Count("exact");
        var wrong = Count("wrongFamily");
        var unverified = Count("unverified");
        var scored = exact + wrong + unverified;
        return new CountScore(exact, wrong, unverified, Count("inEvidence") + Count("derived"), Count("unattributed"),
            scored == 0 ? null : Math.Round((double)exact / scored, 3), findings);
    }

    // Words after which a number is always a code or an identifier ("event id 193", "codice 0x…"), whatever the evidence holds.
    private static readonly string[] StrongIdentifierWords = ["id ", "id:", "id=", "codice", "code "];

    private static bool IsSumOfEvidence(AnswerNumber number, IEnumerable<AnswerNumber> sameSentence, Func<long, bool> inEvidence)
    {
        var parts = sameSentence.Where(n => n != number && inEvidence(n.Value)).Select(n => n.Value).ToList();
        for (var i = 0; i < parts.Count; i++)
        {
            for (var j = i + 1; j < parts.Count; j++)
            {
                if (parts[i] + parts[j] == number.Value)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A count that stands for a whole family: its largest value, or any of its values from 10 up. Small sub-counts (a code seen
    /// twice, a week with three crashes) coincide across families by chance and are not taken as a misclassification.</summary>
    private static bool IsHeadline(HashSet<long> values, long value) =>
        values.Contains(value) && (value >= 10 || value == values.Max());

    /// <summary>An event id is an identifier, not a count, when an identifier word or a source name (a hyphenated token such as
    /// <c>kernel-power</c> or <c>whea-logger</c>) stands right before it.</summary>
    private static bool IsIdentifier(string sentence, AnswerNumber number, ScenarioKey key)
    {
        if (AnswerText.PrecededBy(sentence, number.Offset, key.IdentifierContext, 24))
        {
            return true;
        }

        var before = sentence[..number.Offset].TrimEnd(' ', '`', '*', '_', '"', '\'', '(', ':');
        var start = before.LastIndexOfAny([' ', '`', '*', '(', '|', '\t']) + 1;
        var token = before[start..];
        return token.IndexOfAny(['-', '‐', '‑', '‒', '–']) > 0 || token.Contains("runtime", StringComparison.Ordinal);
    }

    private static ClassificationScore Classification(ScenarioKey key, IReadOnlyList<string> sentences, Dictionary<string, HashSet<long>> familyValues,
        CountScore counts)
    {
        var liveFamilies = key.Families.Where(f => f.LiveDump).ToList();
        var crashFamilies = key.Families.Where(f => f.CrashLike).ToList();
        var liveIds = liveFamilies.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var crashIds = crashFamilies.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var liveTerms = liveFamilies.SelectMany(f => f.Terms).ToList();

        var asCrash = new List<int>();
        for (var i = 0; i < sentences.Count; i++)
        {
            var s = sentences[i];
            if (!AnswerText.ContainsAny(s, liveTerms) || !AnswerText.ContainsAny(s, key.CrashWords) || AnswerText.ContainsAnyWord(s, key.Negations))
            {
                continue;
            }

            // A sentence (or table row) that gives each family its own correct count is keeping them apart, not equating them.
            var exactFamilies = counts.Findings.Where(f => f.Sentence == i && f.Verdict == "exact").Select(f => f.Family!).ToHashSet();
            if (exactFamilies.Overlaps(liveIds) && exactFamilies.Overlaps(crashIds))
            {
                continue;
            }

            asCrash.Add(i);
        }

        var liveAsCrash = counts.Findings.Count(f => f.Verdict == "wrongFamily" && crashIds.Contains(f.Family!) && f.ValueFamilies.Any(liveIds.Contains));
        var crashAsLive = counts.Findings.Count(f => f.Verdict == "wrongFamily" && liveIds.Contains(f.Family!) && f.ValueFamilies.Any(crashIds.Contains));
        var liveInEvidence = familyValues.Where(kv => liveIds.Contains(kv.Key)).Any(kv => kv.Value.Any(v => v > 0));
        var crashInEvidence = familyValues.Where(kv => crashIds.Contains(kv.Key)).Any(kv => kv.Value.Any(v => v > 0));
        var mentionsLive = sentences.Any(s => AnswerText.ContainsAny(s, liveTerms));
        var evidence = familyValues.Where(kv => kv.Value.Count > 0)
            .ToDictionary(kv => kv.Key, kv => (double)kv.Value.Max(), StringComparer.Ordinal);
        return new ClassificationScore(evidence, liveInEvidence, crashInEvidence, mentionsLive, asCrash, liveAsCrash, crashAsLive,
            liveInEvidence && !mentionsLive);
    }

    private static SupportScore Support(RunCapture run, ScenarioKey key, IReadOnlyList<string> sentences, CountScore counts)
    {
        var limitation = sentences.Any(s => AnswerText.ContainsAny(s, key.LimitationWords));
        return new SupportScore(run.PartialSources, limitation, run.PartialSources > 0 && !limitation, counts.Unverified);
    }

    private static KnowledgeScore? Knowledge(RunCapture run, ScenarioKey key)
    {
        var index = run.AuditEvents.Where(e => (string?)e["eventType"] == "knowledgeIndex").ToList();
        if (index.Count == 0)
        {
            return null; // Knowledge was not offered to this run: M4 does not apply
        }

        var refToSkill = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var listed in index.SelectMany(e => e["Listed"]?.AsArray().OfType<JsonObject>() ?? []))
        {
            if ((string?)listed["Ref"] is { } reference && (string?)listed["Skill"] is { } skill)
            {
                refToSkill[reference] = skill;
            }
        }

        var listedSkills = refToSkill.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var pack = index.Select(e => (string?)e["PackId"] is { } id ? $"{id}@{(string?)e["PackVersion"]}" : null).FirstOrDefault(p => p is not null);

        var reads = run.AuditEvents.Where(e => (string?)e["eventType"] == "knowledgeRead").ToList();
        var served = reads.Where(e => RunCapture.ReadInt(e["ResultCode"]) == 0).ToList();
        var readSkills = served.Select(e => (string?)e["Skill"]).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var inAnswerTurn = run.FinalStepIndex is { } finalIndex ? served.Count(e => RunCapture.ReadInt(e["StepIndex"]) == finalIndex) : 0;

        var familyValues = FamilyValues(run, key);
        var present = familyValues.Where(kv => kv.Value.Any(v => v > 0)).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var applicable = key.Knowledge.Entries.Where(entry =>
                entry.Always
                || (entry.AnyFamily?.Any(present.Contains) ?? false)
                || (entry.MinFamilies is { } min && present.Count >= min)
                || (entry.AnyFact?.Any(s => run.Facts.Any(f => s.Matches(f) && f.Number > 0)) ?? false))
            .Select(entry => entry.Skill).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        var applicableListed = applicable.Where(listedSkills.Contains).ToList();
        var readApplicable = readSkills.Where(applicable.Contains).ToList();
        double? Ratio(int numerator, int denominator) => denominator == 0 ? null : Math.Round((double)numerator / denominator, 3);

        var cited = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(run.FinalAnswer))
        {
            var answer = run.FinalAnswer.ToLowerInvariant();
            foreach (Match match in KnowledgeRef().Matches(answer))
            {
                if (refToSkill.TryGetValue("K" + match.Groups[1].Value, out var skill))
                {
                    cited.Add(skill);
                }
            }

            foreach (var skill in listedSkills.Where(s => answer.Contains(s, StringComparison.Ordinal)))
            {
                cited.Add(skill);
            }
        }

        return new KnowledgeScore(
            pack,
            listedSkills,
            applicable,
            readSkills,
            served.Count,
            reads.Count - served.Count,
            inAnswerTurn,
            NotExecutedKnowledgeCalls(run),
            Ratio(readApplicable.Count, readSkills.Count),
            Ratio(readApplicable.Count(applicableListed.Contains), applicableListed.Count),
            Ratio(readApplicable.Count, applicable.Count),
            cited.Where(s => !readSkills.Contains(s)).Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>Knowledge calls answered "Not executed" (first call wins): a tool turn of the knowledge function that starts so, each call
    /// counted once although later requests repeat the conversation.</summary>
    private static int NotExecutedKnowledgeCalls(RunCapture run)
    {
        var notExecuted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in run.Requests)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var message in request["messages"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                foreach (var call in message["tool_calls"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    if ((string?)call["id"] is { } id && (string?)call["function"]?["name"] is { } name)
                    {
                        names[id] = name;
                    }
                }

                if ((string?)message["role"] == "tool"
                    && (string?)message["tool_call_id"] is { } callId
                    && names.TryGetValue(callId, out var function)
                    && function.Contains("knowledge_read", StringComparison.Ordinal)
                    && message["content"] is JsonValue content && content.TryGetValue<string>(out var text)
                    && text.StartsWith("Not executed", StringComparison.Ordinal))
                {
                    notExecuted.Add(callId);
                }
            }
        }

        return notExecuted.Count;
    }

    private static CompletionScore Completion(RunCapture run)
    {
        var marker = run.FinalMarker ?? string.Empty;
        var grounding = GroundingSuffixes.FirstOrDefault(g => marker.EndsWith(g.Suffix, StringComparison.Ordinal)).Name ?? "notChecked";
        var disclosure = DisclosurePrefixes.FirstOrDefault(d => marker.StartsWith(d.Prefix, StringComparison.Ordinal)).Name
            ?? (marker.Length > 0 ? "notAttempted" : "none");
        return new CompletionScore(
            run.Status,
            run.TerminalKind,
            run.Status == "Completed" && !string.IsNullOrWhiteSpace(run.FinalAnswer),
            disclosure,
            run.FinalMarker is null ? "none" : grounding,
            run.PlannedSteps,
            run.MaxSteps,
            run.MaxSteps is { } max && run.PlannedSteps > max,
            run.PlanRevisions,
            run.StepsPersisted,
            run.UnexecutedToolCalls,
            run.ModelCalls,
            run.ModelCallFailures);
    }
}
