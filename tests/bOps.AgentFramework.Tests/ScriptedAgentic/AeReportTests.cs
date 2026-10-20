// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace bOps.AgentFramework.Tests.ScriptedAgentic;

/// <summary>
/// AE-1: with <c>BOPS_AE1_REPORT_DIR</c> set, writes the observed sequences, counts and verdicts of every case as Markdown (the source of the
/// tables in <c>docs/benchmarks/ae-1-scripted-runtime-baseline.md</c>). Without it the test only proves that every case runs.
/// </summary>
[Collection("MAF summarization telemetry")]
public sealed class AeReportTests
{
    private const string ReportVariable = "BOPS_AE1_REPORT_DIR";

    [Fact]
    public async Task EveryCaseRuns_AndWhenAskedTheObservedSequencesAreWrittenForTheReport()
    {
        var directory = Environment.GetEnvironmentVariable(ReportVariable);
        var sequences = new StringBuilder();
        var metrics = new StringBuilder().AppendLine(AeObserver.MetricsHeader);
        foreach (var item in AeCases.All)
        {
            var run = await item.Run();
            var verdict = item.Evaluate(run);
            metrics.AppendLine(AeObserver.MetricsRow(run, verdict));
            sequences.AppendLine(CultureInfo.InvariantCulture, $"## {item.Id} — {item.Title}\n");
            sequences.AppendLine(CultureInfo.InvariantCulture, $"Origin: {item.Origin}. Final status: `{run.Metrics.FinalStatus}`{(run.Metrics.Terminal is { } t ? $" / `{t}`" : string.Empty)}. Final: {run.Metrics.FinalClass}.\n");
            sequences.AppendLine("Provider requests:\n").AppendLine(AeObserver.RenderCalls(run));
            sequences.AppendLine("Persisted steps:\n").AppendLine(AeObserver.RenderSteps(run));
            foreach (var diff in run.PlanDiffs)
            {
                sequences.AppendLine(CultureInfo.InvariantCulture,
                    $"- Plan revision {diff.FromRevision} → {diff.ToRevision} (\"{diff.Rationale}\"): owed [{string.Join(", ", diff.Owed)}], added [{string.Join(", ", diff.Added)}], dropped [{string.Join(", ", diff.Dropped)}].");
            }

            sequences.AppendLine(CultureInfo.InvariantCulture, $"\nVerdict: **{verdict.Label}** (objective met: {verdict.ObjectiveMet}; attribution: {(verdict.Attributions.Count == 0 ? "none" : string.Join("; ", verdict.Attributions))}) — {verdict.Reason}\n");
            Assert.Equal(item.Today, verdict.Outcome);
        }

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "ae1-metrics.md"), metrics.ToString(), new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(directory, "ae1-sequences.md"), sequences.ToString(), new UTF8Encoding(false));
        }
    }
}
