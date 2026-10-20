// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
namespace bOps.AgentFramework.Tests;

/// <summary>
/// Manual operator entry points of the D7 blind review (driven by <c>scripts/Invoke-KnowBenchReview.ps1</c>). Both are skipped unless
/// <c>BOPS_KNOW_BENCH_REVIEW_OP</c> names them; neither contacts a provider, reads an Event Log or modifies a result. They write only into the
/// new locations the operator names.
/// </summary>
public sealed class KnowledgeDiagnosisReviewTools
{
    internal const string Operation = "BOPS_KNOW_BENCH_REVIEW_OP";
    internal const string PackageDirectory = "BOPS_KNOW_BENCH_REVIEW_PACKAGE_DIR";
    internal const string EvaluationsDirectory = "BOPS_KNOW_BENCH_REVIEW_EVALUATIONS_DIR";
    internal const string PackageIdVariable = "BOPS_KNOW_BENCH_REVIEW_PACKAGE_ID";
    internal const string ResultsRoots = "BOPS_KNOW_BENCH_REVIEW_RESULTS_ROOTS";

    [ReviewToolFact("export-package")]
    public void ExportBlindPackage()
    {
        var settings = DiagnosisArchiveEnvironment.Read();
        if (!settings.IsAuthorized) throw new InvalidOperationException("Archive access must be explicitly authorized.");
        using var archive = DiagnosisArchive.Open(settings.Root!, settings.Protector!);
        var result = BlindReviewPackage.Export(archive, Required(PackageDirectory), Required("BOPS_EVENTS_CORPUS_PATH"),
            Required("BOPS_EVENTS_CORPUS_SHA256"), Required("BOPS_EVENTS_CORPUS_ATTESTATION_SHA256"), DateTimeOffset.UtcNow);
        // Ids and counts only: never a condition, a path inside the archive or any diagnosis text.
        Console.WriteLine($"blind-package: {result.PackageId} cards={result.IncludedDiagnosisIds.Count} excluded={result.Excluded.Count}");
    }

    [ReviewToolFact("join")]
    public void JoinEvaluations()
    {
        var settings = DiagnosisArchiveEnvironment.Read();
        if (!settings.IsAuthorized) throw new InvalidOperationException("Archive access must be explicitly authorized.");
        using var archive = DiagnosisArchive.Open(settings.Root!, settings.Protector!);
        var roots = (Environment.GetEnvironmentVariable(ResultsRoots) ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var joined = DiagnosisReviewJoin.Run(archive, Required(PackageIdVariable), Required(EvaluationsDirectory), roots, DateTimeOffset.UtcNow);
        var evaluated = joined.Rows.Count(r => r.Status == QualityStatus.Evaluated);
        Console.WriteLine($"quality-results: runs={joined.Rows.Count} evaluated={evaluated} file={Path.GetFileName(joined.OutputPath)}");
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException("Explicit review input required: " + name);

    private sealed class ReviewToolFactAttribute : FactAttribute
    {
        public ReviewToolFactAttribute(string operation)
        {
            if (Environment.GetEnvironmentVariable(Operation) != operation)
                Skip = "Manual blind-review step: select it with BOPS_KNOW_BENCH_REVIEW_OP.";
        }
    }
}
