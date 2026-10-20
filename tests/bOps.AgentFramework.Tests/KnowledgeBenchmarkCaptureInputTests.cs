// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Reflection;
using bOps.Abstractions;
using bOps.Knowledge.Tests;

namespace bOps.AgentFramework.Tests;

[CollectionDefinition("Benchmark capture environment", DisableParallelization = true)]
#pragma warning disable CA1515 // xUnit requires collection definitions to be public.
public sealed class BenchmarkCaptureEnvironmentDefinition;
#pragma warning restore CA1515

[Collection("Benchmark capture environment")]
public sealed class KnowledgeBenchmarkCaptureInputTests
{
    [WindowsOnlyFact]
    public async Task IntegratedInitialization_UsesCaptureHashWithoutImportedInputs()
    {
        using var environment = new CaptureEnvironment();
        using var capture = TrustedEventsCapture.Synthetic(_ =>
            ToolCallResult.Success(KnowledgeBenchmark.Synthetic()["output"]!.GetValue<string>()));
        using var world = new KnowledgeRunnerWorld();
        await capture.CaptureAsync(world);
        var execute = typeof(KnowledgeBenchmarkLiveTests).GetMethod("ExecuteAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var hexInput = typeof(KnowledgeBenchmarkLiveTests).GetMethod("HexInput", BindingFlags.Static | BindingFlags.NonPublic)!;
        // Invoke only initialization: Synthetic provenance must stop execution before metadata, Git or provider setup.
        var expected = Assert.Throws<InvalidOperationException>(() => CanaryEventsCorpus.RequireRealCorpus(capture.Provenance));
        foreach (var externalHash in new[] { null, "invalid", new string('0', 64) })
        {
            Environment.SetEnvironmentVariable("BOPS_EVENTS_CORPUS_SHA256", externalHash);
            Assert.Equal(externalHash?.Length == 64 ? externalHash : null,
                hexInput.Invoke(null, ["BOPS_EVENTS_CORPUS_SHA256", 64]));
            foreach (var knowledge in new[] { false, true })
            {
                var export = new KnowledgeBenchmarkExport();
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    (Task)execute.Invoke(null, [knowledge ? "B" : "A", export,
                        new ChatModelOptions("LlamaCpp", "http://127.0.0.1:1/v1", null, KnowledgeBenchmark.ModelId), capture])!);
                Assert.Equal(expected.Message, error.Message);
                Assert.Equal(capture.Hash, export.CorpusSha256);
                Assert.Null(export.World);
            }
        }
        Assert.Empty(world.Events.OfType<ModelCallAuditEvent>());
    }

    [Fact]
    public async Task IntegratedEntryPoint_RejectsEachExternalImportOrSigningInputBeforeCapture()
    {
        using var environment = new CaptureEnvironment();
        foreach (var input in new[] { "BOPS_RUN_KNOW_BENCH_01D", "BOPS_EVENTS_CORPUS_MODE",
                     "BOPS_EVENTS_CORPUS_PATH", "BOPS_EVENTS_CORPUS_SHA256", "BOPS_KNOW_BENCH_SIGN" })
        {
            Environment.SetEnvironmentVariable(input, input == "BOPS_EVENTS_CORPUS_SHA256" ? new string('0', 64) : "1");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new KnowledgeBenchmarkLiveTests().CaptureThenAB());
            Assert.Equal("Explicit isolated integrated capture opt-in required; no imports or signing.", error.Message);
            Environment.SetEnvironmentVariable(input, null);
        }
    }

    private sealed class CaptureEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> previous = new(StringComparer.Ordinal);

        internal CaptureEnvironment()
        {
            foreach (var name in new[] { "BOPS_RUN_KNOW_BENCH_CAPTURE_AB", "BOPS_EVENTS_CORPUS_ACCEPT_MINIMIZATION",
                         "BOPS_RUN_KNOW_BENCH_01D", "BOPS_EVENTS_CORPUS_MODE", "BOPS_EVENTS_CORPUS_PATH",
                         "BOPS_EVENTS_CORPUS_SHA256", "BOPS_KNOW_BENCH_SIGN", "BOPS_KNOW_BENCH_COMMIT",
                         "BOPS_KNOW_BENCH_MANIFEST_SHA256", "BOPS_KNOW_BENCH_GRANT" })
            {
                previous.Add(name, Environment.GetEnvironmentVariable(name));
                Environment.SetEnvironmentVariable(name,
                    name is "BOPS_RUN_KNOW_BENCH_CAPTURE_AB" or "BOPS_EVENTS_CORPUS_ACCEPT_MINIMIZATION" ? "1" : null);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
