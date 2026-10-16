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
    [ConfigurationPreflightFact]
    public void LocalConfigurationPreflight()
    {
        var options = ReadReadyOptions();
        using var client = new HttpClient();
        _ = new LlamaCppProviderPackage(new ClientFactory(client)).Create(options);
    }

    private static ChatModelOptions ReadReadyOptions()
    {
        var options = KnowledgeHostCanaryLiveTests.LocalQwenConfiguration.Read();
        KnowledgeBenchmark.RunnerOptions.Validate(options.EffectiveRequestTimeout);
        if (options.Model != KnowledgeBenchmark.ModelId) throw new InvalidOperationException("Exact model required.");
        return options;
    }

    [BenchmarkFact("A")]
    public Task WithoutKnowledge() => RunAsync("A");
    [BenchmarkFact("B")]
    public Task WithKnowledge() => RunAsync("B");
    // KNOW-DISC-INV01: a separate experiment (different goal), never pooled with B.
    [BenchmarkFact("C")]
    public Task WithKnowledgeAndExplicitReadRequest() => RunAsync("C");

    [IntegratedCaptureFact]
    public async Task CaptureThenAB()
    {
        if (Environment.GetEnvironmentVariable("BOPS_RUN_KNOW_BENCH_CAPTURE_AB") != "1" ||
            Environment.GetEnvironmentVariable("BOPS_EVENTS_CORPUS_ACCEPT_MINIMIZATION") != "1" ||
            Environment.GetEnvironmentVariable("BOPS_RUN_KNOW_BENCH_01D") is not null ||
            Environment.GetEnvironmentVariable("BOPS_EVENTS_CORPUS_MODE") is not null ||
            Environment.GetEnvironmentVariable("BOPS_EVENTS_CORPUS_PATH") is not null ||
            Environment.GetEnvironmentVariable("BOPS_EVENTS_CORPUS_SHA256") is not null ||
            Environment.GetEnvironmentVariable("BOPS_KNOW_BENCH_SIGN") is not null)
            throw new InvalidOperationException("Explicit isolated integrated capture opt-in required; no imports or signing.");
        var options = ReadReadyOptions(); // Fail before native capture or model composition.
        using var capture = TrustedEventsCapture.Native();
        using var captureWorld = new KnowledgeRunnerWorld();
        await capture.CaptureAsync(captureWorld);
        await KnowledgeBenchmarkComparison.RunAsync(Required("BOPS_KNOW_BENCH_RESULTS"),
            (knowledge, export) => ExecuteAsync(knowledge ? "B" : "A", export, options, capture), Validate);
    }

    private static async Task RunAsync(string condition)
    {
        var knowledge = KnowledgeBenchmark.UsesKnowledge(condition);
        if (Environment.GetEnvironmentVariable("BOPS_RUN_KNOW_BENCH_01D") != condition)
            throw new InvalidOperationException("Explicit individual benchmark condition required.");
        var options = ReadReadyOptions();
        KnowledgeBenchmarkExport? observed = null;
        await KnowledgeBenchmarkExport.RunAsync(Required("BOPS_KNOW_BENCH_RESULTS"), condition, async export =>
        {
            observed = export;
            await ExecuteAsync(condition, export, options);
        });
        Validate(knowledge, observed!);
    }

    private static async Task ExecuteAsync(string condition, KnowledgeBenchmarkExport export, ChatModelOptions options, TrustedEventsCapture? capture = null)
    {
        var knowledge = KnowledgeBenchmark.UsesKnowledge(condition);
        export.RequestedCommit = HexInput("BOPS_KNOW_BENCH_COMMIT", 40);
        export.CorpusSha256 = HexInput("BOPS_EVENTS_CORPUS_SHA256", 64);
        if (Environment.GetEnvironmentVariable("BOPS_EVENTS_CORPUS_MODE") is not null ||
            Environment.GetEnvironmentVariable("BOPS_KNOW_BENCH_SIGN") is not null)
            throw new InvalidOperationException("A/B replay must be separate from corpus preparation and signing.");
        var manifestPin = Environment.GetEnvironmentVariable("BOPS_KNOW_BENCH_MANIFEST_SHA256");
        var authorizedGrant = Environment.GetEnvironmentVariable("BOPS_KNOW_BENCH_GRANT");
        if (!knowledge && capture is null && (manifestPin is not null || authorizedGrant is not null))
            throw new InvalidOperationException("A must have no Knowledge pin or grant.");
        if (!knowledge && capture is not null) { manifestPin = null; authorizedGrant = null; }
        var path = capture is null ? Required("BOPS_EVENTS_CORPUS_PATH") : null;
        var hash = capture?.Hash ?? Required("BOPS_EVENTS_CORPUS_SHA256");
        export.CorpusSha256 = hash;
        if (Environment.GetEnvironmentVariable("BOPS_EVENTS_CORPUS_ACCEPT_MINIMIZATION") != "1")
            throw new InvalidOperationException("Minimization acknowledgement required.");
        if (capture is null)
        {
            var verified = CanaryEventsCorpus.Read(path!, hash);
            export.CorpusProvenance = CanaryEventsCorpus.Provenance(verified.Corpus, verified.Hash);
            _ = KnowledgeBenchmark.Replay(path!, hash, requireCaptured: true);
        }
        else
        {
            export.CorpusProvenance = capture.Provenance;
            CanaryEventsCorpus.RequireRealCorpus(export.CorpusProvenance);
        }
        var metadataPath = Required("BOPS_KNOW_BENCH_METADATA");
        var metadataBytes = await File.ReadAllBytesAsync(metadataPath);
        if (bOps.Knowledge.KnowledgePackFormat.Sha256Hex(metadataBytes) != Required("BOPS_KNOW_BENCH_METADATA_SHA256"))
            throw new InvalidOperationException("Shared execution metadata pin mismatch.");
        using var metadata = JsonDocument.Parse(metadataBytes);
        var root = metadata.RootElement;
        export.Configuration = KnowledgeBenchmarkExport.SafeConfiguration(root);
        foreach (var name in new[] { "baseline", "llamaCppVersion", "model", "modelFileSha256", "quantization", "ctxSize",
                     "nPredict", "reasoning", "sampling", "bopsCommit", "corpusSha256", "providerProfileSha256", "runtimeLimits", "retryPolicy" })
            if (!root.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new InvalidOperationException("Execution metadata incomplete.");
        if (root.GetProperty("baseline").GetString() != "Qwen8192" || root.GetProperty("model").GetString() != KnowledgeBenchmark.ModelId ||
            root.GetProperty("nPredict").GetInt32() != 8192 || !root.GetProperty("reasoning").GetBoolean() ||
            root.GetProperty("corpusSha256").GetString() != (capture is null ? hash : "single-run-in-process") || root.GetProperty("bopsCommit").GetString() != Required("BOPS_KNOW_BENCH_COMMIT"))
            throw new InvalidOperationException("Execution metadata incompatible.");
        // This is an operator attestation of effective server settings, not remote server introspection.
        if (Environment.GetEnvironmentVariable("BOPS_KNOW_BENCH_SETTINGS_VERIFIED") != "1")
            throw new InvalidOperationException("Effective server settings must be independently verified for both conditions.");
        export.Commit = await GitAsync("rev-parse", "HEAD");
        if (root.GetProperty("bopsCommit").GetString() != export.Commit ||
            !string.IsNullOrEmpty(await GitAsync("status", "--porcelain", "--untracked-files=no")))
            throw new InvalidOperationException("Benchmark requires the pinned commit and a clean tracked checkout.");
        export.ProviderRequestTimeout = options.RequestTimeout;
        KnowledgeBenchmark.RunnerOptions.Validate(options.EffectiveRequestTimeout);
        if (options.Model != KnowledgeBenchmark.ModelId) throw new InvalidOperationException("Exact model required.");
        var profile = JsonSerializer.SerializeToUtf8Bytes(new { options.Provider, options.BaseUrl, options.Model,
            options.SupportsNativeToolCalling, options.RequestTimeout });
        if (root.GetProperty("providerProfileSha256").GetString() != bOps.Knowledge.KnowledgePackFormat.Sha256Hex(profile))
            throw new InvalidOperationException("Shared non-secret provider profile mismatch.");
        var world = new KnowledgeRunnerWorld();
        export.World = world;
        var pack = knowledge ? await KnowledgeBenchmark.LoadAsync() : default;
        using var transport = BenchmarkHttpBudget.CreateTransport();
        using var httpBudget = new BenchmarkHttpBudget(transport);
        export.ModelObservation.Http = httpBudget;
        using var client = new HttpClient(httpBudget, disposeHandler: false);
        var provider = new LlamaCppProviderPackage(new ClientFactory(client));
        var rig = capture is null
            ? KnowledgeBenchmark.ComposeReplay(world, provider.Create(options), condition, path!, hash,
                pack.Raw, pack.Trust, manifestPin, authorizedGrant, export.ModelObservation)
            : capture.Compose(world, provider.Create(options), condition, export.RunId,
                pack.Raw, pack.Trust, manifestPin, authorizedGrant, export.ModelObservation);
        export.Store = rig.Store;
        using var deadline = KnowledgeBenchmark.ConditionDeadline();
        try
        {
            await export.ExecuteTaskAsync(() => rig.Runner.RunAsync(KnowledgeBenchmark.GoalFor(condition), KnowledgeRunnerWorld.ActorA, export.RunId, deadline.Token));
        }
        finally { export.ModelObservation.ConditionTimedOut = deadline.IsCancellationRequested; }
    }

    private static void Validate(bool knowledge, KnowledgeBenchmarkExport export)
    {
        var state = export.State;
        var world = export.World!;
        var hash = export.CorpusSha256;
        Assert.NotNull(state);
        Assert.True(state.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed);
        Assert.All(world.Events.OfType<ToolCallAuditEvent>(), call => Assert.Equal("system.events", call.Tool));
        Assert.All(world.Events.OfType<ToolCallAuditEvent>().Where(call => call.Outcome == ToolOutcome.Success),
            call => Assert.Equal(hash, call.Summary?["corpusSha256"]?.GetValue<string>()));
        if (knowledge) Assert.NotNull(state.KnowledgeBinding);
        else
        {
            Assert.Null(state.KnowledgeBinding);
            Assert.Empty(world.Events.OfType<KnowledgePackAdmissionAuditEvent>());
            Assert.Empty(world.Events.OfType<KnowledgeReadAuditEvent>());
        }
    }
    private static string? HexInput(string name, int length)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value?.Length == length && value.All(Uri.IsHexDigit) ? value : null;
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
    private sealed class ConfigurationPreflightFactAttribute : FactAttribute
    {
        public ConfigurationPreflightFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("BOPS_RUN_KNOW_BENCH_PREFLIGHT") != "1")
                Skip = "Manual local configuration/Vault preflight only; no capture or LLM calls.";
        }
    }
    private sealed class IntegratedCaptureFactAttribute : FactAttribute
    {
        public IntegratedCaptureFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("BOPS_RUN_KNOW_BENCH_CAPTURE_AB") != "1")
                Skip = "Manual integrated native capture/A/B only; never enabled by offline tests.";
        }
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
