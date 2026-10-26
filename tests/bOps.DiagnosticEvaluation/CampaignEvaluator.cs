// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;

namespace bOps.DiagnosticEvaluation;

/// <summary>
/// Scores every executed run of a campaign and writes <c>auto-evaluation/&lt;blindCode&gt;.json</c> plus <c>auto-evaluation/manifest.json</c>.
/// The campaign is only read; nothing else in it is written. The output is keyed by blind code and never names the variant; it holds
/// counts, fact keys, sentence indexes and check reasons, never evidence or answer text. It carries no timestamp, so evaluating the same
/// campaign twice with the same keys gives the same bytes.
/// </summary>
internal static class CampaignEvaluator
{
    public const string OutputDirectoryName = "auto-evaluation";
    public const string ResultSchema = "bops.diagnostic-evaluation/1";
    public const string EvaluatorVersion = "1.0.0-p1";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly ScenarioKey EmptyKey = new(ScenarioKey.SchemaId, [], [], [], [], [], [], [], new KnowledgeKey([]));

    public static async Task<IReadOnlyList<string>> RunAsync(string campaignDirectory, string keysDirectory, CancellationToken cancellationToken)
    {
        var keys = ScenarioKey.LoadDirectory(keysDirectory);
        var runs = new List<(RunCapture Run, RunScores Scores, ScenarioKey? Key)>();
        foreach (var planned in RunCapture.ReadManifest(campaignDirectory))
        {
            var capture = RunCapture.Read(planned);
            if (!capture.Executed)
            {
                continue;
            }

            var key = keys.GetValueOrDefault(planned.Scenario);
            runs.Add((capture, DiagnosticScorer.Score(capture, key ?? EmptyKey), key));
        }

        var checks = await MafEvaluation.EvaluateAsync(runs.Select(r => (r.Run, r.Scores)).ToList(), cancellationToken).ConfigureAwait(false);

        var output = Path.Combine(campaignDirectory, OutputDirectoryName);
        Directory.CreateDirectory(output);
        var summary = new List<string>();
        foreach (var (run, scores, key) in runs.OrderBy(r => r.Run.Planned.BlindCode, StringComparer.Ordinal))
        {
            var runChecks = checks[run.Planned.BlindCode];
            var document = new
            {
                schema = ResultSchema,
                blindCode = run.Planned.BlindCode,
                scenario = run.Planned.Scenario,
                evaluator = Evaluator(),
                key = key is null ? null : new { sha256 = key.Sha256, scenarios = key.Scenarios },
                inputs = new { stateSha256 = run.StateSha256, auditSha256 = run.AuditSha256 },
                notApplicable = scores.NotApplicableReason,
                m1Classification = scores.Classification,
                m2Counts = scores.Counts,
                m3Support = scores.Support,
                m4Knowledge = scores.Knowledge,
                m5Completion = scores.Completion,
                checks = runChecks,
                checksFailed = runChecks.Count(c => c.Passed == false),
            };
            await File.WriteAllTextAsync(Path.Combine(output, run.Planned.BlindCode + ".json"),
                JsonSerializer.Serialize(document, Json) + "\n", cancellationToken).ConfigureAwait(false);
            summary.Add(SummaryLine(run, scores, runChecks));
        }

        var manifest = new
        {
            schema = ResultSchema + "#manifest",
            evaluator = Evaluator(),
            keys = keys.Values.Distinct().OrderBy(k => k.Scenarios[0], StringComparer.Ordinal)
                .Select(k => new { sha256 = k.Sha256, scenarios = k.Scenarios }),
            runs = runs.Select(r => r.Run.Planned.BlindCode).Order(StringComparer.Ordinal),
            unscoredScenarios = runs.Where(r => r.Key is null).Select(r => r.Run.Planned.Scenario).Distinct().Order(StringComparer.Ordinal),
        };
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, Json) + "\n", cancellationToken)
            .ConfigureAwait(false);
        return summary;
    }

    private static object Evaluator() => new
    {
        name = "bOps.DiagnosticEvaluation",
        version = EvaluatorVersion,
        phase = "P1 deterministic",
        maf = "Microsoft.Agents.AI " + (typeof(LocalEvaluator).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?.Split('+')[0] ?? "unknown"),
    };

    private static string SummaryLine(RunCapture run, RunScores scores, IReadOnlyList<CheckOutcome> checks)
    {
        var failed = checks.Where(c => c.Passed == false).Select(c => c.Name).ToList();
        var counts = scores.Counts is { } c
            ? string.Create(CultureInfo.InvariantCulture, $"counts exact={c.Exact} wrongFamily={c.WrongFamily} unverified={c.Unverified}")
            : "counts n/a";
        var knowledge = scores.Knowledge is { } k
            ? string.Create(CultureInfo.InvariantCulture, $"knowledge read={k.Read.Count}/{k.Applicable.Count} citedNotLoaded={k.CitedNotLoaded.Count}")
            : "knowledge off";
        return $"{run.Planned.BlindCode} {run.Planned.Scenario} {scores.Completion.Status} | {counts} | {knowledge} | failed: "
            + (failed.Count == 0 ? "none" : string.Join(",", failed));
    }
}
