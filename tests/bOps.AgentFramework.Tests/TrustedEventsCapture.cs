// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
#pragma warning disable CA2000 // The caller owns and disposes each world.
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Knowledge;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Windows;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

// One process-local capability, no import constructor, JSON binding or persistent attestation.
internal sealed class TrustedEventsCapture : IDisposable
{
    internal enum RefusalCode { IncompatibleArguments, IdentityMismatch, AlreadyConsumed, IntegrityOrProvenance, QualityGate }

    internal static ToolManifest ReplayManifest(ToolManifest native) => native with
    {
        Description = "Replay the single governed benchmark snapshot using the exact canonical request; one accepted read per condition.",
        Parameters = [
            new("mode", ToolParameterType.Enum, "Canonical raw mode.", AllowedValues: ["raw"]),
            new("windowMinutes", ToolParameterType.Integer, "Canonical 1440 minutes.") { Minimum = 1440, Maximum = 1440 },
            new("minSeverity", ToolParameterType.Enum, "Canonical warning severity.", AllowedValues: ["warning"]),
            new("limit", ToolParameterType.Integer, "Canonical 500 events.") { Minimum = 500, Maximum = 500 },
            new("maxOutputBytes", ToolParameterType.Integer, "Canonical 65536 bytes.") { Minimum = 65536, Maximum = 65536 },
        ],
    };
    private readonly ITool source;
    private readonly Guid captureTask = Guid.NewGuid();
    private readonly Guid captureId = Guid.NewGuid();
    private string? snapshot;
    private string? hash;
    private string? provenance;
    private int started;
    private bool audited;
    private bool disposed;
    private readonly HashSet<string> conditions = [];

    private TrustedEventsCapture(ITool source) => this.source = source;
    internal static TrustedEventsCapture Native()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows native capture required.");
        return new(new WindowsSystemToolProvider().GetTools().OfType<WindowsSystemEventsTool>().Single());
    }
    // A controlled source is always Synthetic, even if it returns JSON claiming Captured.
    internal static TrustedEventsCapture Synthetic(Func<ToolArguments, ToolCallResult> collect) => new(new SyntheticSource(collect));
    internal string Hash => audited && !disposed ? hash! : throw new InvalidOperationException("Capture not available.");
    internal JsonObject Provenance => audited && !disposed ? JsonNode.Parse(provenance!)!.AsObject() : throw new InvalidOperationException("Capture not available.");

    internal async Task CaptureAsync(KnowledgeRunnerWorld world)
    {
        if (disposed || Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Single capture only.");
        var rig = KnowledgeBenchmark.Boot(world, false).ComposeCanary(new BenchmarkProbe([]), withHost: false,
            withWindowsSystemEvents: true, governedEvents: new SessionTool(this, captureTask, true));
        await rig.Runner.ExecuteExecutionPlanAsync(captureTask, KnowledgeRunnerWorld.ActorA,
            new ExecutionPlan("trusted.capture", "1", "Governed single capture",
                [new ExecutionPlanStep(0, "system.events", KnowledgeBenchmark.Arguments, "capture")]), null);
        var call = world.Events.OfType<ToolCallAuditEvent>().Single(e => e.TaskId == captureTask);
        if (call.Outcome != ToolOutcome.Success || snapshot is null ||
            call.Summary?["corpusSha256"]?.GetValue<string>() != hash ||
            CanaryEventsCorpus.Canonical(call.Summary!["corpusProvenance"]!) != provenance)
            throw new InvalidOperationException("Governed capture/audit failed.");
        audited = true;
    }

    internal CanaryRig Compose(KnowledgeRunnerWorld world, IChatModel model, string condition, Guid taskId,
        RawKnowledgePack? raw = null, KnowledgeTrustStore? trust = null, string? pin = null, string? grant = null,
        BenchmarkModelObservation? observation = null)
    {
        _ = Hash;
        // The in-process capture pair is A/B only; C runs from a pinned replay file (KNOW-DISC-INV01).
        if (condition is not ("A" or "B")) throw new InvalidOperationException("Integrated capture supports only A and B.");
        KnowledgeBenchmark.ValidateCondition(condition, raw, trust, pin, grant);
        if ((condition == "B" && !conditions.Contains("A")) || !conditions.Add(condition))
            throw new InvalidOperationException("Only one ordered A/B execution is permitted.");
        var rig = KnowledgeBenchmark.Boot(world, condition == "B", raw, trust).ComposeCanary(
            new KnowledgeBenchmark.BenchmarkModel(model, observation), withHost: condition == "B",
            withWindowsSystemEvents: true, governedEvents: new SessionTool(this, taskId, false),
            runnerOptions: KnowledgeBenchmark.RunnerOptions);
        if (observation is not null) observation.Calls = 0;
        return rig;
    }

    public void Dispose() { disposed = true; snapshot = hash = provenance = null; }

    private sealed class SessionTool(TrustedEventsCapture owner, Guid taskId, bool capture) : IContextualTool, IToolAuditSummaryProvider
    {
        private int used;
        public ToolManifest Manifest { get; } = capture ? owner.source.Manifest : ReplayManifest(owner.source.Manifest);
        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) =>
            Task.FromResult(Refuse(RefusalCode.IdentityMismatch)); // No host identity: fail closed.
        public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, ToolExecutionContext context, CancellationToken ct = default)
        {
            if (context.TaskId != taskId || context.Actor != KnowledgeRunnerWorld.ActorA) return Refuse(RefusalCode.IdentityMismatch);
            if (owner.disposed) return Refuse(RefusalCode.IntegrityOrProvenance);
            if (Volatile.Read(ref used) != 0) return Refuse(RefusalCode.AlreadyConsumed);
            try
            {
                if (ArgumentSchema.Validate(Manifest.Parameters, arguments) is not null) return Refuse(RefusalCode.IncompatibleArguments);
                CanaryEventsCorpus.ValidateArguments(arguments, DateTimeOffset.UnixEpoch);
                if (CanaryEventsCorpus.Canonical(arguments.ToJson()) != CanaryEventsCorpus.Canonical(KnowledgeBenchmark.Arguments.ToJson()))
                    return Refuse(RefusalCode.IncompatibleArguments);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
            { return Refuse(RefusalCode.IncompatibleArguments); }
            try
            {
                ct.ThrowIfCancellationRequested();
                if (capture)
                {
                    if (Interlocked.CompareExchange(ref used, 1, 0) != 0) return Refuse(RefusalCode.AlreadyConsumed);
                    var result = await owner.source.ExecuteAsync(arguments, ct);
                    if (!result.Succeeded || result.Output is null) return result;
                    var native = owner.source is WindowsSystemEventsTool;
                    var corpus = CanaryEventsCorpus.Build(arguments, result.Output, native ? "windows-capture" : "synthetic",
                        DateTimeOffset.UtcNow, native ? CanaryEventsCorpus.CaptureMetadata(owner.captureId.ToString("D")) : null);
                    // A native capture that would not pass the corpus quality gate is never retained (a controlled source is Synthetic and exempt).
                    if (native && !CorpusQualityGate.Evaluate(corpus, CanaryEventsCorpus.Hash(corpus), arguments).Suitable)
                        return Refuse(RefusalCode.QualityGate);
                    owner.snapshot = CanaryEventsCorpus.Canonical(corpus); // Immutable strings; no mutable corpus escapes.
                    owner.hash = CanaryEventsCorpus.Hash(corpus);
                    var origin = CanaryEventsCorpus.Provenance(corpus, owner.hash, capturedHere: native);
                    origin["controlledCaptureId"] = owner.captureId.ToString("D");
                    owner.provenance = CanaryEventsCorpus.Canonical(origin);
                }
                else if (!owner.audited) return Refuse(RefusalCode.IntegrityOrProvenance);
                var saved = JsonNode.Parse(owner.snapshot!)!.AsObject();
                if (CanaryEventsCorpus.Hash(saved) != owner.hash || saved["arguments"]!.GetValue<string>() != CanaryEventsCorpus.Canonical(arguments.ToJson())) return Refuse(RefusalCode.IntegrityOrProvenance);
                var expectedOrigin = CanaryEventsCorpus.Provenance(saved, owner.hash!, capturedHere: owner.source is WindowsSystemEventsTool);
                expectedOrigin["controlledCaptureId"] = owner.captureId.ToString("D");
                if (CanaryEventsCorpus.Canonical(expectedOrigin) != owner.provenance) return Refuse(RefusalCode.IntegrityOrProvenance);
                var output = JsonNode.Parse(saved["output"]!.GetValue<string>())!.AsObject();
                output["benchmarkCorpus"]!["origin"] = JsonNode.Parse(owner.provenance!)!["classification"]!.GetValue<string>().ToLowerInvariant();
                var accepted = ToolCallResult.Success(CanaryEventsCorpus.Canonical(output)) with
                {
                    Completeness = (ToolResultCompleteness)saved["completeness"]!.GetValue<int>(),
                    Facts = [new EvidenceFact("benchmark.events", "corpusSha256", ToolParameterType.String, JsonValue.Create(owner.hash)!),
                        new EvidenceFact("benchmark.events", "corpusProvenance", ToolParameterType.String, JsonValue.Create(owner.provenance)!)],
                };
                ct.ThrowIfCancellationRequested();
                // Atomic admission only after validation, provenance and output preparation. Losing callers get no corpus.
                if (!capture && Interlocked.CompareExchange(ref used, 1, 0) != 0) return Refuse(RefusalCode.AlreadyConsumed);
                return accepted;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Text.Json.JsonException or NullReferenceException or FormatException)
            { return Refuse(RefusalCode.IntegrityOrProvenance); }
        }
        public JsonObject CreateAuditSummary(ToolArguments arguments, ToolCallResult result) => new()
        {
            ["executionMode"] = capture ? "capture" : "replay",
            ["snapshotOrigin"] = "single-run-in-process; exports-have-no-persistent-attestation",
            ["corpusSha256"] = result.Facts.FirstOrDefault(f => f.Key == "corpusSha256")?.Value.DeepClone(),
            ["corpusProvenance"] = result.Facts.FirstOrDefault(f => f.Key == "corpusProvenance") is { } fact ? JsonNode.Parse(fact.Value.GetValue<string>()) : null,
            ["succeeded"] = result.Succeeded,
            ["replayRefusalCode"] = result.Facts.FirstOrDefault(f => f.Key == "replayRefusalCode")?.Value.DeepClone(),
        };
        private static ToolCallResult Refuse(RefusalCode code) => ToolCallResult.Failure("Controlled event snapshot refused.") with
        {
            FailureKind = ToolFailureKind.Validation,
            Facts = [new EvidenceFact("benchmark.events", "replayRefusalCode", ToolParameterType.String, JsonValue.Create(code.ToString())!)],
        };
    }

    private sealed class SyntheticSource(Func<ToolArguments, ToolCallResult> collect) : ITool
    {
        public ToolManifest Manifest => SystemToolManifests.Events("windows");
        public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default) => Task.FromResult(collect(arguments));
    }
}
