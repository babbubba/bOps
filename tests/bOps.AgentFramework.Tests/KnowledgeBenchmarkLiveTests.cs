// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using bOps.Abstractions;
using bOps.Knowledge.Tests;
using bOps.Packages.Providers.LlamaCpp;

namespace bOps.AgentFramework.Tests;

/// <summary>Future manual entry points, visibly skipped unless their individual condition is explicitly selected.</summary>
public sealed class KnowledgeBenchmarkLiveTests
{
    [BenchmarkFact("A")]
    public Task WithoutKnowledge() => RunAsync(false);
    [BenchmarkFact("B")]
    public Task WithKnowledge() => RunAsync(true);

    private static async Task RunAsync(bool knowledge)
    {
        var condition = knowledge ? "B" : "A";
        if (Environment.GetEnvironmentVariable("BOPS_RUN_KNOW_BENCH_01D") != condition)
            throw new InvalidOperationException("Explicit individual benchmark condition required.");
        var path = Required("BOPS_EVENTS_CORPUS_PATH");
        var hash = Required("BOPS_EVENTS_CORPUS_SHA256");
        if (Environment.GetEnvironmentVariable("BOPS_EVENTS_CORPUS_ACCEPT_MINIMIZATION") != "1")
            throw new InvalidOperationException("Minimization acknowledgement required.");
        var replay = KnowledgeBenchmark.Replay(path, hash); // No capture branch or synthetic fallback.
        var metadataPath = Required("BOPS_KNOW_BENCH_METADATA");
        var metadataBytes = await File.ReadAllBytesAsync(metadataPath);
        if (bOps.Knowledge.KnowledgePackFormat.Sha256Hex(metadataBytes) != Required("BOPS_KNOW_BENCH_METADATA_SHA256"))
            throw new InvalidOperationException("Shared execution metadata pin mismatch.");
        using var metadata = JsonDocument.Parse(metadataBytes);
        var root = metadata.RootElement;
        foreach (var name in new[] { "baseline", "llamaCppVersion", "model", "modelFileSha256", "quantization", "ctxSize",
                     "nPredict", "reasoning", "sampling", "bopsCommit", "corpusSha256", "providerProfileSha256", "runtimeLimits", "retryPolicy" })
            if (!root.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new InvalidOperationException("Execution metadata incomplete.");
        if (root.GetProperty("baseline").GetString() != "Qwen8192" || root.GetProperty("model").GetString() != KnowledgeBenchmark.ModelId ||
            root.GetProperty("nPredict").GetInt32() != 8192 || !root.GetProperty("reasoning").GetBoolean() ||
            root.GetProperty("corpusSha256").GetString() != hash || root.GetProperty("bopsCommit").GetString() != Required("BOPS_KNOW_BENCH_COMMIT"))
            throw new InvalidOperationException("Execution metadata incompatible.");
        // This is an operator attestation of effective server settings, not remote server introspection.
        if (Environment.GetEnvironmentVariable("BOPS_KNOW_BENCH_SETTINGS_VERIFIED") != "1")
            throw new InvalidOperationException("Effective server settings must be independently verified for both conditions.");
        if (root.GetProperty("bopsCommit").GetString() != await GitAsync("rev-parse", "HEAD") ||
            !string.IsNullOrEmpty(await GitAsync("status", "--porcelain", "--untracked-files=no")))
            throw new InvalidOperationException("Benchmark requires the pinned commit and a clean tracked checkout.");
        var options = KnowledgeHostCanaryLiveTests.LocalQwenConfiguration.Read();
        if (options.Model != KnowledgeBenchmark.ModelId) throw new InvalidOperationException("Exact model required.");
        var profile = JsonSerializer.SerializeToUtf8Bytes(new { options.Provider, options.BaseUrl, options.Model,
            options.SupportsNativeToolCalling, options.RequestTimeout });
        if (root.GetProperty("providerProfileSha256").GetString() != bOps.Knowledge.KnowledgePackFormat.Sha256Hex(profile))
            throw new InvalidOperationException("Shared non-secret provider profile mismatch.");
        var resultRoot = Path.GetFullPath(Required("BOPS_KNOW_BENCH_RESULTS"));
        if (!Directory.Exists(resultRoot)) throw new InvalidOperationException("Explicit existing artifact directory required.");
        var resultDirectory = Path.Combine(resultRoot, condition);
        if (Directory.Exists(resultDirectory)) throw new InvalidOperationException("Condition artifacts already exist; no overwrite.");
        Directory.CreateDirectory(resultDirectory);
        await File.WriteAllBytesAsync(Path.Combine(resultDirectory, "execution-metadata.json"), metadataBytes);
        using var world = new KnowledgeRunnerWorld();
        var pack = knowledge ? await KnowledgeBenchmark.LoadAsync() : default;
        var process = KnowledgeBenchmark.Boot(world, knowledge, pack.Raw, pack.Trust);
        using var client = new HttpClient();
        var provider = new LlamaCppProviderPackage(new ClientFactory(client));
        var rig = KnowledgeBenchmark.Compose(process, provider.Create(options), knowledge, replay);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        TaskState? state = null;
        try
        {
            state = await rig.Runner.RunAsync(KnowledgeBenchmark.Goal, KnowledgeRunnerWorld.ActorA);
        }
        finally
        {
            timer.Stop();
            // Aggregate broker duration includes server loading/retries; never mislabel it as generation time.
            var result = new { condition, baseline = "Qwen8192", corpusSha256 = hash,
                totalBrokerLoopMs = timer.ElapsedMilliseconds, modelLoadMs = (long?)null, generationMs = (long?)null,
                timingsRequireServerTelemetry = true, state?.Status, state?.TerminalReason, state?.Accounting,
                finalObservation = state is { Steps.Count: > 0 } ? state.Steps[^1].Observation : null,
                modelAttempts = world.Events.OfType<ModelCallAuditEvent>().Select(call => new
                { call.DurationMs, call.ModelAttempt, call.ProviderAttempt, call.Outcome, call.FailureKind }),
                knowledgeUsed = world.Events.OfType<KnowledgeReadAuditEvent>().Any(read => read.ResultCode == KnowledgeReadResultCode.Success) };
            await File.WriteAllTextAsync(Path.Combine(resultDirectory, "result.json"), JsonSerializer.Serialize(result, KnowledgeBenchmark.JsonOptions));
            File.Copy(world.AuditFile, Path.Combine(resultDirectory, "audit.jsonl"));
        }
        Assert.NotNull(state);
        Assert.Equal(AgentTaskStatus.Completed, state.Status);
        Assert.All(world.Events.OfType<ToolCallAuditEvent>(), call => Assert.Equal("system.events", call.Tool));
        Assert.Contains(world.Events.OfType<ToolCallAuditEvent>(), call => call.Outcome == ToolOutcome.Success &&
            call.Summary?["corpusSha256"]?.GetValue<string>() == hash);
        if (knowledge) Assert.Contains(world.Events.OfType<KnowledgeReadAuditEvent>(), read => read.ResultCode == KnowledgeReadResultCode.Success);
        else
        {
            Assert.Null(state.KnowledgeBinding);
            Assert.Empty(world.Events.OfType<KnowledgePackAdmissionAuditEvent>());
            Assert.Empty(world.Events.OfType<KnowledgeReadAuditEvent>());
        }
    }
    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException("Explicit benchmark input required: " + name);
    private static async Task<string> GitAsync(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        { WorkingDirectory = KnowledgeBenchmark.Root, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Git unavailable.");
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("Revision verification failed.");
        return output.Trim();
    }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class BenchmarkFactAttribute : FactAttribute
    {
        public BenchmarkFactAttribute(string condition)
        {
            if (Environment.GetEnvironmentVariable("BOPS_RUN_KNOW_BENCH_01D") != condition)
                Skip = "Manual benchmark only: select this condition explicitly.";
        }
    }
}
