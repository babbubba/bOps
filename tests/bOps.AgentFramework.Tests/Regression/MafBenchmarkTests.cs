// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Runtime;

namespace bOps.AgentFramework.Tests.Regression;

/// <summary>
/// Generates the AF-05 measurement matrix (DETERMINISTIC FIXTURE; token counts are a synthetic proxy, wall-clock is not model
/// latency). Set <c>BOPS_BENCHMARK_OUT</c> to a file path to receive the tables; the tests themselves only assert sanity.
/// </summary>
// Shares a collection with GovernedSummarizationTests: its telemetry test listens globally for summary activities.
[Collection("MAF summarization telemetry")]
public sealed class MafBenchmarkTests
{
    private static readonly int[] Reserves = [0, 512, 1024, 2048, 4096];
    private static readonly int[] Groups = [0, 1, 2, 3, 4, 6];
    private static readonly int[] Turns = [0, 1, 2, 4];
    private static readonly int[] UnknownGroups = [0, 2, 3, 4, 6, 8, 12];
    private static readonly double[] Ratios = [0.5, 0.75, 0.9, 1.0];
    private static readonly int[] Recents = [2, 4, 8];

    private const string Unknown = "unknown profile (structural MAF, the shipped default for an unprofiled model)";
    private const string NoProjector = "no projector (unprojected full history)";

    private static SummarizationOptions TaskSummarizer(double ratio = 0.75, int recent = 4) =>
        new() { Enabled = true, TriggerRatio = ratio, RecentGroupsToPreserve = recent };

    private static readonly ModelContextProfile DedicatedProfile =
        new() { Provider = "Sum", Model = "small", MaxContextTokens = 8192, ReservedOutputTokens = 1024 };

    private static RegressionConfiguration Config(
        string name, int? window, SummarizationOptions? summary = null, bool dedicated = false,
        Func<ModelContextOptions, ModelContextOptions>? context = null, bool projector = true)
    {
        var options = RegressionRunner.BaseOptions(
            window,
            summarization: dedicated ? new SummarizationOptions { Enabled = true, Provider = "Sum", Model = "small" } : summary,
            extraProfiles: dedicated ? [DedicatedProfile] : null,
            context: context);
        return new RegressionConfiguration(name, options, UseProjector: projector, DedicatedSummarizer: dedicated);
    }

    private static async Task<List<BenchmarkRow>> RunAll(RegressionScenario scenario, IEnumerable<RegressionConfiguration> configurations)
    {
        var rows = new List<BenchmarkRow>();
        foreach (var configuration in configurations)
        {
            rows.Add(BenchmarkRow.From(await RegressionRunner.RunAsync(scenario, configuration)));
        }

        return rows;
    }

    private static IEnumerable<RegressionConfiguration> ProviderMatrix()
    {
        yield return Config(NoProjector, null, projector: false);
        yield return Config(Unknown, null);
        foreach (var window in new[] { 32768, 16384, 12288, 10240, 9216 })
        {
            yield return Config($"MAF {window}, summarization off", window);
            yield return Config($"MAF {window}, summarization on (task model)", window, TaskSummarizer());
            if (window is 32768 or 12288 or 10240)
            {
                yield return Config($"MAF {window}, summarization on (dedicated Sum/small)", window, dedicated: true);
            }
        }

        yield return Config("MAF 8192, summarization off", 8192);
    }

    [Fact]
    public async Task Matrix_C508Equivalent()
    {
        var rows = await RunAll(RegressionScenarios.C508Equivalent(), ProviderMatrix());
        BenchmarkOutput.Append("c508-equivalent — configuration matrix", BenchmarkRow.Table(rows));
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task Matrix_C508ExtendedPressure()
    {
        var rows = await RunAll(RegressionScenarios.C508Extended(10),
        [
            Config(NoProjector, null, projector: false),
            Config(Unknown, null),
            Config("MAF 32768, summarization off", 32768),
            Config("MAF 32768, summarization on (task model)", 32768, TaskSummarizer()),
            Config("MAF 16384, summarization off", 16384),
            Config("MAF 16384, summarization on (task model)", 16384, TaskSummarizer()),
            Config("MAF 16384, summarization on (dedicated Sum/small)", 16384, dedicated: true),
            Config("MAF 12288, summarization off", 12288),
            Config("MAF 12288, summarization on (task model)", 12288, TaskSummarizer()),
        ]);
        BenchmarkOutput.Append("c508-extended-10 (compaction pressure at the final call) — configuration matrix", BenchmarkRow.Table(rows));
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task Matrix_ShortDiagnostic()
    {
        var rows = await RunAll(RegressionScenarios.ShortDiagnostic(),
        [
            Config(NoProjector, null, projector: false),
            Config(Unknown, null),
            Config("MAF 32768, summarization off", 32768),
            Config("MAF 32768, summarization on (task model)", 32768, TaskSummarizer()),
            Config("MAF 16384, summarization off", 16384),
            Config("MAF 16384, summarization on (task model)", 16384, TaskSummarizer()),
        ]);
        BenchmarkOutput.Append("short-diagnostic — configuration matrix", BenchmarkRow.Table(rows));
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task Sweeps_Tuning()
    {
        var c508 = RegressionScenarios.C508Equivalent();
        var extended = RegressionScenarios.C508Extended(10);

        BenchmarkOutput.Append("sweep SafetyReserveTokens — c508-equivalent @ MAF 10240, summarization off",
            BenchmarkRow.Table(await RunAll(c508, Reserves.Select(reserve =>
                Config($"SafetyReserveTokens={reserve}", 10240, context: c => c with { SafetyReserveTokens = reserve })))));
        BenchmarkOutput.Append("sweep SafetyReserveTokens — c508-extended-10 @ MAF 16384, summarization off",
            BenchmarkRow.Table(await RunAll(extended, Reserves.Select(reserve =>
                Config($"SafetyReserveTokens={reserve}", 16384, context: c => c with { SafetyReserveTokens = reserve })))));

        BenchmarkOutput.Append("sweep MinimumPreservedToolGroups — c508-extended-10 @ MAF 16384, summarization off",
            BenchmarkRow.Table(await RunAll(extended, Groups.Select(groups =>
                Config($"MinimumPreservedToolGroups={groups}", 16384, context: c => c with { MinimumPreservedToolGroups = groups })))));
        BenchmarkOutput.Append("sweep MinimumPreservedToolGroups — c508-equivalent @ MAF 10240, summarization off",
            BenchmarkRow.Table(await RunAll(c508, Groups.Select(groups =>
                Config($"MinimumPreservedToolGroups={groups}", 10240, context: c => c with { MinimumPreservedToolGroups = groups })))));

        BenchmarkOutput.Append("sweep MinimumPreservedTurns — c508-extended-10 @ MAF 16384, summarization off",
            BenchmarkRow.Table(await RunAll(extended, Turns.Select(turns =>
                Config($"MinimumPreservedTurns={turns}", 16384, context: c => c with { MinimumPreservedTurns = turns })))));

        foreach (var (scenario, window) in new[] { (extended, 16384), (c508, 10240) })
        {
            var configurations = new List<RegressionConfiguration>();
            foreach (var ratio in Ratios)
            {
                foreach (var recent in Recents)
                {
                    configurations.Add(Config($"TriggerRatio={ratio}, RecentGroupsToPreserve={recent}", window, TaskSummarizer(ratio, recent)));
                }
            }

            BenchmarkOutput.Append(
                $"sweep summarization TriggerRatio x RecentGroupsToPreserve — {scenario.Name} @ MAF {window} (task model)",
                BenchmarkRow.Table(await RunAll(scenario, configurations)));
        }

        BenchmarkOutput.Append("sweep MinimumPreservedToolGroups — c508-equivalent, UNKNOWN profile (the only structural bound)",
            BenchmarkRow.Table(await RunAll(c508, UnknownGroups.Select(groups =>
                Config($"unknown profile, MinimumPreservedToolGroups={groups}", null, context: c => c with { MinimumPreservedToolGroups = groups })))));
        BenchmarkOutput.Append("sweep MinimumPreservedToolGroups — c508-extended-10, UNKNOWN profile (the only structural bound)",
            BenchmarkRow.Table(await RunAll(extended, UnknownGroups.Select(groups =>
                Config($"unknown profile, MinimumPreservedToolGroups={groups}", null, context: c => c with { MinimumPreservedToolGroups = groups })))));
        Assert.True(true);
    }
}
