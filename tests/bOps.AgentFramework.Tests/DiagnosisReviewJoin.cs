// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
#pragma warning disable CA1031 // Per-diagnosis problems are reported as closed codes, never thrown past the join.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace bOps.AgentFramework.Tests;

/// <summary>The state of one run after the blind review. <c>Evaluated</c> is the only state that carries a quality score.</summary>
internal enum QualityStatus
{
    Evaluated,

    /// <summary>There is no diagnosis, or it could not be stored/verified: the quality of this run is not measured, not zero.</summary>
    NotMeasurable,

    /// <summary>The diagnosis is reviewable but no evaluation file exists for it.</summary>
    NotEvaluated,

    /// <summary>An evaluation exists but is a draft, malformed, or describes a different text/package.</summary>
    EvaluationRejected,
}

/// <summary>
/// D7: the step that reunites the reviewer's evaluations with the runs <b>after</b> the review, using the private map. Its output holds
/// condition, repetition, run id, availability, the human scores and claim counts, and a consistency check against <c>result.json</c>. It
/// deliberately does not copy any Knowledge-access metric: quality (human judgement) and access (runtime telemetry) stay in separate records and
/// are only ever combined by an analyst, by run id.
/// </summary>
internal static class DiagnosisReviewJoin
{
    internal sealed record Row(DiagnosisMapEntry Entry, QualityStatus Status, string? Problem, DiagnosisEvaluation? Evaluation, string ResultJson);

    internal sealed record JoinResult(JsonObject Document, string OutputPath, IReadOnlyList<Row> Rows);

    /// <summary>
    /// Joins every run in the private map. <paramref name="evaluationsDirectory"/> is the reviewer's filled <c>evaluations</c> folder;
    /// <paramref name="packageId"/> must be the package the reviewer received. <paramref name="resultsRoots"/> are the roots that hold
    /// <c>&lt;condition&gt;/result.json</c> (one per repetition); they are only checked for consistency.
    /// </summary>
    internal static JoinResult Run(DiagnosisArchive archive, string packageId, string evaluationsDirectory, IReadOnlyList<string> resultsRoots,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(resultsRoots);
        var export = archive.ReadExport(packageId);
        var manifestSha = export["manifestSha256"]!.GetValue<string>();
        var included = export["included"]!.AsArray().Select(node => node!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        if (!Directory.Exists(evaluationsDirectory)) throw new ArchiveException("EvaluationsMissing");

        var (entries, unreadable) = archive.ReadMap();
        var rows = new List<Row>();
        foreach (var entry in entries.OrderBy(e => e.Identity.Condition, StringComparer.Ordinal).ThenBy(e => e.Identity.Repetition))
            rows.Add(Evaluate(archive, entry, packageId, included, evaluationsDirectory, resultsRoots));

        var known = entries.Select(e => e.DiagnosisId).ToHashSet(StringComparer.Ordinal);
        var orphans = Directory.EnumerateFiles(evaluationsDirectory, "*.evaluation.json")
            .Select(file => Path.GetFileName(file)[..^".evaluation.json".Length])
            .Where(id => !known.Contains(id)).Order(StringComparer.Ordinal).ToArray();

        var document = new JsonObject
        {
            ["format"] = "bops.know-bench.quality-results",
            ["version"] = 1,
            ["generatedUtc"] = nowUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["packageId"] = packageId,
            ["packageManifestSha256"] = manifestSha,
            ["interpretation"] = "Exploratory human blind review; small n; no causal attribution to Knowledge; no LLM judge",
            ["knowledgeAccessMetrics"] = "Intentionally not merged: Knowledge-access telemetry stays in each result.json and is combined with these scores only by runId, in a separate analysis step",
            ["unreadableMapEntries"] = new JsonArray([.. unreadable.Select(id => (JsonNode)JsonValue.Create(id)!)]),
            ["evaluationsWithoutRun"] = new JsonArray([.. orphans.Select(id => (JsonNode)JsonValue.Create(id)!)]),
            ["diagnoses"] = new JsonArray([.. rows.Select(Project)]),
            ["byCondition"] = ByCondition(rows),
        };
        var directory = Path.Combine(archive.Root, "private", "quality");
        if (!Directory.Exists(directory)) ArchiveAccess.CreateRestrictedDirectory(directory);
        var path = Path.Combine(directory, $"quality-results-{nowUtc.UtcDateTime:yyyyMMddHHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.json");
        DiagnosisArchive.WriteRestrictedNew(path, EvaluationFile.Encode(document));
        return new(document, path, rows);
    }

    private static Row Evaluate(DiagnosisArchive archive, DiagnosisMapEntry entry, string packageId, HashSet<string> included,
        string evaluationsDirectory, IReadOnlyList<string> resultsRoots)
    {
        var resultState = CheckResult(entry, resultsRoots);
        if (entry.Availability == DiagnosisAvailability.None) return new(entry, QualityStatus.NotMeasurable, "NoDiagnosis", null, resultState);
        if (!included.Contains(entry.DiagnosisId)) return new(entry, QualityStatus.NotMeasurable, "NotInPackage", null, resultState);
        OpenedDiagnosis opened;
        try { opened = archive.ReadDiagnosis(entry.DiagnosisId); }
        catch (ArchiveException ex) { return new(entry, QualityStatus.NotMeasurable, "Archive:" + ex.Code, null, resultState); }
        if (opened.TextSha256 != entry.TextSha256) return new(entry, QualityStatus.NotMeasurable, "Archive:IntegrityMismatch", null, resultState);

        var file = Path.Combine(evaluationsDirectory, entry.DiagnosisId + ".evaluation.json");
        if (!File.Exists(file)) return new(entry, QualityStatus.NotEvaluated, null, null, resultState);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(entry, QualityStatus.EvaluationRejected, "Unreadable", null, resultState); }
        var (evaluation, problems) = EvaluationFile.Parse(bytes);
        if (evaluation is null) return new(entry, QualityStatus.EvaluationRejected, problems[0], null, resultState);
        if (evaluation.PackageId != packageId || evaluation.DiagnosisId != entry.DiagnosisId || evaluation.DiagnosisTextSha256 != opened.TextSha256)
            return new(entry, QualityStatus.EvaluationRejected, "DescribesAnotherDiagnosis", null, resultState);
        if (!evaluation.IsFinal) return new(entry, QualityStatus.EvaluationRejected, "NotFinal", null, resultState);
        return new(entry, QualityStatus.Evaluated, null, evaluation, resultState);
    }

    // result.json is only consulted for consistency (same run, same diagnosis id and hash); none of its metrics is copied.
    private static string CheckResult(DiagnosisMapEntry entry, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            var path = Path.Combine(root, entry.Identity.Condition, "result.json");
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                var top = document.RootElement;
                if (!top.TryGetProperty("runId", out var run) || run.GetGuid() != entry.Identity.RunId) continue;
                var block = top.GetProperty("diagnosis");
                return block.GetProperty("diagnosisId").GetString() == entry.DiagnosisId
                    && block.GetProperty("textSha256").GetString() == entry.TextSha256
                    && block.GetProperty("availability").GetString() == entry.Availability.ToString()
                    ? "Consistent" : "Inconsistent";
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IOException)
            { return "Unreadable"; }
        }

        return "NotFound";
    }

    private static JsonNode Project(Row row) => new JsonObject
    {
        ["diagnosisId"] = row.Entry.DiagnosisId,
        ["condition"] = row.Entry.Identity.Condition,
        ["repetition"] = row.Entry.Identity.Repetition,
        ["runId"] = row.Entry.Identity.RunId.ToString("D"),
        ["availability"] = row.Entry.Availability.ToString(),
        ["qualityStatus"] = row.Status.ToString(),
        ["problem"] = row.Problem,
        ["resultJson"] = row.ResultJson,
        ["reviewer"] = row.Evaluation?.Reviewer,
        ["scores"] = row.Evaluation is null ? null
            : new JsonObject([.. row.Evaluation.Scores.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)JsonValue.Create(s.Value)))]),
        ["total"] = row.Evaluation?.Total,
        ["claimCounts"] = row.Evaluation is null ? null
            : new JsonObject([.. ClaimCategories.All.Select(c => KeyValuePair.Create(c.Key,
                (JsonNode?)JsonValue.Create(row.Evaluation.Claims.Count(claim => claim.Category == c.Key))))]),
    };

    private static JsonObject ByCondition(IReadOnlyList<Row> rows)
    {
        var result = new JsonObject();
        foreach (var group in rows.GroupBy(r => r.Entry.Identity.Condition).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var evaluated = group.Where(r => r.Status == QualityStatus.Evaluated).ToArray();
            var summary = new JsonObject
            {
                ["runs"] = group.Count(),
                ["evaluated"] = evaluated.Length,
                ["notMeasurable"] = group.Count(r => r.Status == QualityStatus.NotMeasurable),
                ["notEvaluated"] = group.Count(r => r.Status == QualityStatus.NotEvaluated),
                ["evaluationRejected"] = group.Count(r => r.Status == QualityStatus.EvaluationRejected),
                ["meanTotal"] = evaluated.Length == 0 ? null : Math.Round(evaluated.Average(r => (double)r.Evaluation!.Total!.Value), 3),
                ["meanPerCriterion"] = evaluated.Length == 0 ? null : new JsonObject([.. ReviewRubric.Criteria.Select(c => KeyValuePair.Create(c.Key,
                    (JsonNode?)JsonValue.Create(Math.Round(evaluated.Average(r => (double)r.Evaluation!.Scores[c.Key]!.Value), 3))))]),
                ["claimTotals"] = new JsonObject([.. ClaimCategories.All.Select(c => KeyValuePair.Create(c.Key,
                    (JsonNode?)JsonValue.Create(evaluated.Sum(r => r.Evaluation!.Claims.Count(claim => claim.Category == c.Key)))))]),
            };
            result[group.Key] = summary;
        }

        return result;
    }
}
