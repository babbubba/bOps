// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests.Regression;

/// <summary>One measured run, flattened for the benchmark table (AF-05 §Measurement matrix).</summary>
internal sealed record BenchmarkRow(
    string Scenario,
    string Configuration,
    string Profile,
    int PrimaryCalls,
    int PrimaryPrompt,
    int PrimaryCompletion,
    int SummaryCalls,
    int SummaryPrompt,
    int SummaryCompletion,
    int AllIn,
    string GroupsBeforeAfter,
    int ToolGroupsCompacted,
    int ReadsServed,
    int ReadsExhausted,
    int ReadsDenied,
    int OperationalSteps,
    int Replans,
    string Status,
    string Facts,
    string Limitations,
    string Availability,
    double ProjectionMs)
{
    internal static BenchmarkRow From(RegressionRun run)
    {
        var stats = run.Projector?.Projections ?? [];
        (string Provider, string Model, ModelContextProjectionStats Stats, TimeSpan Elapsed)? worst = stats.Count == 0
            ? null
            : stats.MaxBy(p => p.Stats.HistoricalGroupsBefore - p.Stats.HistoricalGroupsAfter + p.Stats.CompactedToolGroups);
        var profile = run.Projector is null
            ? "none (unprojected)"
            : worst is null
                ? "n/a"
                : worst.Value.Stats.MaxContextTokens is { } max
                    ? $"{max}/{worst.Value.Stats.ReservedOutputTokens} ({worst.Value.Stats.ProfileSource})"
                    : $"unknown ({worst.Value.Stats.ProfileSource})";
        var availability = run.Model.FinalRequest is null || run.Scenario.ExpectedFacts.Count == 0
            ? null
            : EvidenceAvailability.Of(run);
        var facts = run.Model.FinalFactVisibility;
        var limits = run.Model.FinalLimitationVisibility;
        return new BenchmarkRow(
            run.Scenario.Name,
            run.Configuration.Name,
            profile,
            run.PrimaryCalls.Count(),
            run.PrimaryPromptTokens,
            run.PrimaryCompletionTokens,
            run.SummaryCalls.Count(),
            run.SummaryPromptTokens,
            run.SummaryCompletionTokens,
            run.AllInTokens,
            worst is null
                ? $"{run.Model.Log.Max(c => c.ToolGroupsVisible)} max verbatim"
                : $"{worst.Value.Stats.HistoricalGroupsBefore}->{worst.Value.Stats.HistoricalGroupsAfter}",
            stats.Count == 0 ? 0 : stats.Max(p => p.Stats.CompactedToolGroups),
            run.EvidenceReadServed,
            run.EvidenceReadExhausted,
            run.Model.ReadsDeniedByExhaustion,
            run.OperationalSteps,
            run.Replans,
            run.State.Status.ToString(),
            facts.Count == 0 ? "n/a" : $"{facts.Count(kv => kv.Value)}/{run.Scenario.ExpectedFacts.Count}",
            limits.Count == 0 && run.Scenario.ExpectedLimitations.Count == 0
                ? "n/a"
                : $"{limits.Count(kv => kv.Value)}/{run.Scenario.ExpectedLimitations.Count}",
            availability is null
                ? "n/a"
                : $"{availability.Count(item => item.Direct)} / {availability.Count(item => item.Grounded)} / {availability.Count(item => item.Addressable)} / {availability.Count(item => item.Available)} of {availability.Count}",
            stats.Sum(p => p.Elapsed.TotalMilliseconds));
    }

    internal static string Header =>
        "| Scenario | Config | Profile (max/reserved, source) | Primary calls | Primary prompt | Primary compl. | Summary calls | Summary prompt | Summary compl. | ALL-IN | Hist. groups before->after (worst call) | Tool groups compacted | ER served | ER exhausted | ER denied (model wanted, not offered) | Op. steps | Replans | Status | Facts retained | Limitations retained | Evidence visible / grounded / exact-addressable / available | Projection ms (real) |\n"
        + "|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---|---|---|---|---:|";

    internal string ToMarkdown() => string.Create(CultureInfo.InvariantCulture,
        $"| {Scenario} | {Configuration} | {Profile} | {PrimaryCalls} | {PrimaryPrompt} | {PrimaryCompletion} | {SummaryCalls} | {SummaryPrompt} | {SummaryCompletion} | **{AllIn}** | {GroupsBeforeAfter} | {ToolGroupsCompacted} | {ReadsServed} | {ReadsExhausted} | {ReadsDenied} | {OperationalSteps} | {Replans} | {Status} | {Facts} | {Limitations} | {Availability} | {ProjectionMs:0.0} |");

    internal static string Table(IEnumerable<BenchmarkRow> rows)
    {
        var builder = new StringBuilder(Header);
        foreach (var row in rows)
        {
            builder.Append('\n').Append(row.ToMarkdown());
        }

        return builder.ToString();
    }
}

/// <summary>Writes the generated benchmark tables when <c>BOPS_BENCHMARK_OUT</c> names a file; otherwise a no-op.</summary>
internal static class BenchmarkOutput
{
    private static readonly object Gate = new();

    internal static void Append(string section, string markdown)
    {
        var path = Environment.GetEnvironmentVariable("BOPS_BENCHMARK_OUT");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (Gate)
        {
            File.AppendAllText(path, $"\n### {section}\n\n{markdown}\n");
        }
    }
}
