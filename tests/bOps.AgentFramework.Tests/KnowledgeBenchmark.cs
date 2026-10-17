// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
#pragma warning disable CA2000 // World owns processes; caller owns world.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Knowledge.Tests;
using bOps.Packages.Sys.Core;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

/// <summary>Test-project preparation only: same governed operational surface, no product configuration.</summary>
internal static class KnowledgeBenchmark
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true };
    internal const string ModelId = "Qwen3.6-35B";
    // Actual dispatch ceiling with HTTP; also bounds transport-free offline probes.
    internal const int MaximumCalls = 20;
    internal static AgentRunnerOptions RunnerOptions { get; } = new()
    {
        ModelCallAttemptTimeout = TimeSpan.FromSeconds(90),
        ModelCallBudget = TimeSpan.FromSeconds(300),
        MaxAttemptDuration = TimeSpan.FromSeconds(1200)
    };
    internal static CancellationTokenSource ConditionDeadline(TimeProvider? clock = null) =>
        new(RunnerOptions.MaxAttemptDuration!.Value, clock ?? TimeProvider.System);
    internal static PackReference Reference { get; } = new("acme.windows-event-runbooks", "1.0.0");
    internal static string Root
    {
        get
        {
            for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "docs/benchmarks/know-bench-01a/scenario.txt"))) return dir.FullName;
            throw new InvalidOperationException("Benchmark source root missing.");
        }
    }
    internal static string Delivery => Path.Combine(Root, "docs/benchmarks/know-bench-01d");
    internal static string Goal => File.ReadAllText(Path.Combine(Root, "docs/benchmarks/know-bench-01a/scenario.txt"));
    // KNOW-DISC-INV01: C is a separate treatment, not a variant of B. The operator's goal additionally asks for a governed read; the
    // addendum names no skill, pack or ref, and the read still goes through the offered control function, its budget and its audit.
    internal static string ExplicitReadAddendum => File.ReadAllText(Path.Combine(Root, "docs/benchmarks/know-bench-01a/scenario-c-addendum.txt"));
    internal static string GoalFor(string condition) => condition == "C" ? $"{Goal.TrimEnd()}\n\n{ExplicitReadAddendum.Trim()}" : Goal;
    internal static bool UsesKnowledge(string condition) => condition is "B" or "C";
    internal static ToolArguments Arguments => ToolArguments.FromJson(new JsonObject
        { ["mode"] = "raw", ["windowMinutes"] = 1440, ["minSeverity"] = "warning", ["limit"] = 500, ["maxOutputBytes"] = 65536 });
    internal static CanaryEventsActivation Replay(string path, string hash, bool requireCaptured = false)
    {
        var verified = CanaryEventsCorpus.Read(path, hash);
        if (verified.Corpus["arguments"]!.GetValue<string>() != CanaryEventsCorpus.Canonical(Arguments.ToJson()))
            throw new InvalidOperationException("Benchmark corpus request incompatible.");
        var provenance = CanaryEventsCorpus.Provenance(verified.Corpus, verified.Hash);
        if (requireCaptured) CanaryEventsCorpus.RequireRealCorpus(provenance);
        return new(true, false, path, hash, true, CanaryEventsCorpus.Canonical(provenance), requireCaptured);
    }

    /// <summary>A frozen corpus file approved for the benchmark: the replay activation and the provenance it will be exported with.</summary>
    internal sealed record FrozenCorpus(CanaryEventsActivation Activation, JsonObject Provenance);

    /// <summary>
    /// The only replay path for a corpus saved by an earlier capture process (A, B and C all use it). The file must be pinned by SHA-256,
    /// carry an operator attestation pinned by its own SHA-256, still pass the quality gate and match the scenario request. Nothing
    /// here is <c>Captured</c>: the best a file reaches is <c>OperatorAttested</c>, and an unattested file is refused, not downgraded.
    /// </summary>
    internal static FrozenCorpus ReplayFrozen(string path, string? corpusSha256, string? attestationSha256)
    {
        var verified = CanaryEventsCorpus.Read(path, corpusSha256);
        if (verified.Corpus["arguments"]!.GetValue<string>() != CanaryEventsCorpus.Canonical(Arguments.ToJson()))
            throw new InvalidOperationException("Benchmark corpus request incompatible.");
        var attestation = CorpusAttestation.Load(path, verified.Corpus, verified.Hash, attestationSha256);
        var provenance = CanaryEventsCorpus.Provenance(verified.Corpus, verified.Hash, false, attestation);
        CanaryEventsCorpus.RequireBenchmarkProvenance(provenance);
        return new(new(true, false, path, verified.Hash, true, CanaryEventsCorpus.Canonical(provenance), false, attestation), provenance);
    }
    internal static KnowledgeProcess Boot(KnowledgeRunnerWorld world, bool knowledge, RawKnowledgePack? raw = null,
        KnowledgeTrustStore? trust = null, bool grant = true)
    {
        if (!knowledge)
        {
            var baseline = world.Boot([], KnowledgeTrustStore.Empty);
            if (!baseline.Registry.Unregister(baseline.ProviderId)) throw new InvalidOperationException("Baseline provider removal failed.");
            return baseline;
        }
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(trust);
        world.Source.Add(KnowledgeRunnerWorld.TenantA, Reference.PackId, Reference.Version, () => raw);
        return world.Boot(grant ? [Grant] : [], trust, providerTenants: [KnowledgeRunnerWorld.TenantA],
            bindings: new Dictionary<TenantId, PackReference> { [KnowledgeRunnerWorld.TenantA] = Reference });
    }
    internal static PackGrant Grant => new(KnowledgeRunnerWorld.TenantA, Reference, PackKind.Knowledge);
    internal static CanaryRig Compose(KnowledgeProcess process, IChatModel model, bool knowledge, CanaryEventsActivation replay, BenchmarkModelObservation? observation = null) =>
        process.ComposeCanary(new BenchmarkModel(model, observation), withHost: knowledge, withWindowsSystemEvents: true, eventsCorpus: replay,
            runnerOptions: RunnerOptions);

    // Shared by the manual A/B entry points and the offline probes. No live/capture fallback.
    internal static CanaryRig ComposeReplay(KnowledgeRunnerWorld world, IChatModel model, string condition,
        string path, string hash, RawKnowledgePack? raw = null, KnowledgeTrustStore? trust = null,
        string? manifestPin = null, string? authorizedGrant = null, BenchmarkModelObservation? observation = null,
        CanaryEventsActivation? frozen = null)
    {
        ValidateCondition(condition, raw, trust, manifestPin, authorizedGrant);
        // frozen: the already verified, operator-attested activation of the live path; absent only in synthetic offline probes.
        var replay = frozen ?? Replay(path, hash);
        var knowledge = UsesKnowledge(condition);
        var rig = Compose(Boot(world, knowledge, raw, trust), model, knowledge, replay, observation);
        if (observation is not null) observation.Calls = 0;
        return rig;
    }

    internal static void ValidateCondition(string condition, RawKnowledgePack? raw, KnowledgeTrustStore? trust,
        string? manifestPin, string? authorizedGrant)
    {
        if (condition is not ("A" or "B" or "C")) throw new InvalidOperationException("Explicit A, B or C condition required.");
        var knowledge = UsesKnowledge(condition);
        if (!knowledge && (raw is not null || trust is not null || manifestPin is not null || authorizedGrant is not null))
            throw new InvalidOperationException("A must have no Knowledge inputs or grant.");
        if (knowledge && (raw is null || trust is null ||
            manifestPin != KnowledgePackFormat.Sha256Hex(raw.ManifestBytes.Span) || authorizedGrant != GrantAuthorization))
            throw new InvalidOperationException("B and C require the explicit signed manifest pin and exact grant authorization.");
    }

    internal const string GrantAuthorization = "tenant-a/acme.windows-event-runbooks@1.0.0:Knowledge";

    // The existing TestPublisher keeps its RSA private key exclusively in memory. Only public artifacts are exported.
    internal static void SignDelivery()
    {
        var source = Path.Combine(Root, "docs/benchmarks/know-bench-01a/pack-source");
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(source, file).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);
        var pack = new TestPack { PackId = Reference.PackId, Version = Reference.Version, Kind = PackKind.Knowledge, Files = files };
        var destination = Path.Combine(Delivery, "signed-pack", Reference.PackId, Reference.Version);
        if (Directory.Exists(destination)) throw new InvalidOperationException("Signed delivery already exists; no overwrite.");
        pack.WriteTo(destination);
        var json = JsonSerializer.Serialize(new { schemaVersion = KnowledgePackFormat.SchemaVersion,
            keys = new[] { TestPublisher.Acme.Trusted() }, revokedPublishers = Array.Empty<string>() },
            JsonOptions);
        File.WriteAllText(Path.Combine(Delivery, "test-trust.json"), json, new UTF8Encoding(false));
    }
    internal static async Task<(RawKnowledgePack Raw, KnowledgeTrustStore Trust)> LoadAsync()
    {
        var raw = await new DirectoryKnowledgePackSource(Path.Combine(Delivery, "signed-pack"))
            .OpenAsync(Reference, KnowledgeRunnerWorld.TenantA) ?? throw new InvalidOperationException("Signed pack missing.");
        return (raw, KnowledgeTrustStore.FromFile(Path.Combine(Delivery, "test-trust.json")));
    }
    internal static JsonObject Synthetic()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        SystemEventsArguments.TryRead(Arguments, now, out var query, out var mode, out var limit, out var bytes, out _);
        var snapshot = new SystemEventSnapshot(
            [new(now.AddMinutes(-1), SystemEventSeverity.Critical, "synthetic-provider", "41", "System", "synthetic only", null, null)],
            [new("windows.channel.System", InventorySourceStatus.Partial, "synthetic gap")], true);
        return CanaryEventsCorpus.Build(Arguments, SystemEventFormatting.Format(snapshot, query!, mode, limit, bytes), "synthetic", now);
    }
    internal static string WriteSynthetic(TempDirectory temporary, JsonObject corpus)
    {
        var directory = temporary.Sub(".bops-event-corpus");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ab.bops-events.snapshot.json");
        File.WriteAllBytes(path, CanaryEventsCorpus.Encode(corpus, CanaryEventsCorpus.Hash(corpus)));
        return path;
    }
    internal static ModelResponse Plan => new(new JsonObject
    {
        ["rationale"] = "Offline isolation probe",
        ["steps"] = new JsonArray(new JsonObject { ["description"] = "Read events", ["expectedTool"] = "system.events",
            ["expectedArguments"] = Arguments.ToJson() })
    }.ToJsonString(), [], false, null);
    internal static ModelResponse Events => Script.Calls(new ModelToolCall("events", "system.events", Arguments));
    internal static ModelResponse Read => Script.Knowledge("knowledge", "read_resource", "windows-event-triage", "references/interpretation.md");

    internal sealed class BenchmarkModel(IChatModel inner, BenchmarkModelObservation? observation) : IChatModel
    {
        private int calls;
        public ChatModelDescriptor Descriptor => inner.Descriptor;
        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            if (!string.Equals(Descriptor.ModelId, ModelId, StringComparison.Ordinal))
                throw new InvalidOperationException("Benchmark requires exact Qwen3.6-35B model id.");
            if (observation?.Http is null && calls >= MaximumCalls)
            {
                if (observation is not null) observation.CallLimitReached = true;
                throw new InvalidOperationException("Benchmark call budget exhausted.");
            }
            calls++;
            if (observation is not null) observation.Calls = (observation.Calls ?? 0) + 1;
            try
            {
                var response = await inner.CompleteAsync(request, ct);
                if (observation is not null) observation.CompletedCalls++;
                observation?.Discovery.Record(request, response);
                observation?.Diagnosis.Observe(request, response); // D7: what the model produced, before the runtime judges it
                return response;
            }
            catch
            {
                if (observation is not null) observation.FailedCalls++;
                observation?.Discovery.Record(request, null);
                throw;
            }
        }
    }
}

/// <summary>In-memory responses only; records requests after the real MAF/broker projection. No transport exists.</summary>
internal sealed class BenchmarkProbe(IEnumerable<ModelResponse> responses) : IChatModel
{
    private readonly Queue<ModelResponse> queue = new(responses);
    public ChatModelDescriptor Descriptor { get; } = new("offline-probe", KnowledgeBenchmark.ModelId);
    internal List<ModelRequest> Requests { get; } = [];
    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return queue.Count > 0 ? Task.FromResult(queue.Dequeue()) : throw new InvalidOperationException("Probe finished.");
    }
}
