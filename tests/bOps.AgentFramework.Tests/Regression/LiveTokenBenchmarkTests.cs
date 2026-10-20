// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.AgentFramework.Tests.Regression;

/// <summary>
/// AF-05H token comparison by call purpose for the LIVE-1/2/3 fixtures (DETERMINISTIC FIXTURE; synthetic token proxy). It uses only
/// APIs that predate AF-05H, so the same file runs on the baseline commit to give the "before" rows. Set <c>BOPS_BENCHMARK_OUT</c>
/// to receive the table and <c>BOPS_BENCHMARK_LABEL</c> to name the build; the test itself asserts only that every fixture ran.
/// </summary>
[Collection("MAF summarization telemetry")]
public sealed class LiveTokenBenchmarkTests
{
    [Fact]
    public async Task TokensByCallPurpose_ForTheLiveFixtures()
    {
        var label = Environment.GetEnvironmentVariable("BOPS_BENCHMARK_LABEL") ?? "current";
        var rows = new List<string>();
        foreach (var script in new[] { LiveRegression.ShortDiagnostic(), LiveRegression.LongStabilityDiagnostic(), LiveRegression.FocusedCrashDiagnosis() })
        {
            foreach (var window in new int?[] { null, 32768, 65536 })
            {
                var run = await LiveRegression.RunAsync(script, window is null ? "unknown" : $"MAF {window}", RegressionRunner.BaseOptions(window));
                rows.Add(LiveRegression.Row(label, run));
            }
        }

        BenchmarkOutput.Append($"AF-05H live fixtures — tokens by call purpose ({label})", LiveRegression.Header + "\n" + string.Join('\n', rows));
        Assert.Equal(9, rows.Count);
    }
}
