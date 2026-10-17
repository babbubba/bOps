// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Sys.Core;
using bOps.Packages.Sys.Windows;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

// Internal test-host configuration only. No environment binding in a product host, public provider or dispatch route.
internal sealed record CanaryEventsActivation(bool Enabled, bool Capture, string Path, string? ExpectedHash,
    bool AcceptMinimization, string? ExpectedProvenance = null, bool RequireCaptured = false, OperatorAttestation? Attestation = null);

internal sealed class CanaryEventsCorpusTool(WindowsSystemEventsTool live, CanaryEventsActivation activation)
    : IToolAuditSummaryProvider
{
    private int captureStarted;
    public ToolManifest Manifest => live.Manifest;

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        if (!activation.Enabled || !activation.AcceptMinimization)
            return Refuse("Event corpus opt-in and minimization acknowledgement are required.");
        try
        {
            ct.ThrowIfCancellationRequested();
            CanaryEventsCorpus.ValidatePath(activation.Path);
            CanaryEventsCorpus.ValidateArguments(arguments, DateTimeOffset.UnixEpoch);
            JsonObject corpus;
            string hash;
            if (activation.Capture && activation.Attestation is not null)
                return Refuse("A capture cannot carry an operator attestation.");
            if (activation.Capture)
            {
                // Reserve the file before even considering native collection. CreateNew forbids overwrite/races.
                if (Interlocked.Exchange(ref captureStarted, 1) != 0)
                    return Refuse("This capture instance has already been used.");
                using var destination = new FileStream(activation.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var result = await live.ExecuteAsync(arguments, ct);
                if (!result.Succeeded || result.Output is null) return result;
                var captureId = Guid.NewGuid().ToString("D");
                CorpusQuality quality;
                (corpus, hash, quality) = CanaryEventsCorpus.BuildCapture(arguments, result.Output, captureId, DateTimeOffset.UtcNow);
                var bytes = CanaryEventsCorpus.Encode(corpus, hash);
                await destination.WriteAsync(bytes, ct);
#pragma warning disable CA1849 // FlushAsync has no flush-to-disk contract; a captured corpus must be durable before success.
                destination.Flush(flushToDisk: true);
#pragma warning restore CA1849
                // The minimized file is kept for inspection, but an unsuitable capture is a failure and can never be approved.
                if (!quality.Suitable) return Refuse("Event corpus quality gate failed: " + quality.Describe());
            }
            else
            {
                (corpus, hash) = CanaryEventsCorpus.Read(activation.Path, activation.ExpectedHash);
                if (CanaryEventsCorpus.Canonical(arguments.ToJson()) != corpus["arguments"]!.GetValue<string>())
                    return Refuse("The requested arguments do not match the captured request.");
                // An attested replay is only as good as the file it describes: binding and gate are re-checked on every call.
                if (activation.Attestation is { } attestation)
                {
                    if (attestation.CorpusSha256 != hash || attestation.CaptureId != corpus["capture"]?["captureId"]?.GetValue<string>())
                        return Refuse("The operator attestation does not describe this corpus.");
                    CorpusQualityGate.Require(CorpusQualityGate.Evaluate(corpus, hash, KnowledgeBenchmark.Arguments));
                }
            }

            var provenance = CanaryEventsCorpus.Provenance(corpus, hash, activation.Capture, activation.Attestation);
            if ((activation.ExpectedProvenance is not null && activation.ExpectedProvenance != CanaryEventsCorpus.Canonical(provenance))
                || (activation.RequireCaptured && provenance["classification"]!.GetValue<string>() != "Captured"))
                return Refuse("Captured provenance unavailable or changed.");
            var output = JsonNode.Parse(corpus["output"]!.GetValue<string>())!.AsObject();
            output["benchmarkCorpus"]!["origin"] = CanaryEventsCorpus.OriginLabel(provenance);
            // Keep stored evidence/window, with independently resolved provenance visibly attached.
            // Native facts are deliberately not promoted to fresh observations or replay attestations.
            return ToolCallResult.Success(CanaryEventsCorpus.Canonical(output)) with
            {
                Completeness = (ToolResultCompleteness)corpus["completeness"]!.GetValue<int>(),
                Facts = [new EvidenceFact("benchmark.events", "executionMode", ToolParameterType.String,
                    JsonValue.Create(activation.Capture ? "capture" : "replay")!),
                    new EvidenceFact("benchmark.events", "corpusSha256", ToolParameterType.String, JsonValue.Create(hash)!),
                    new EvidenceFact("benchmark.events", "corpusProvenance", ToolParameterType.String,
                        JsonValue.Create(CanaryEventsCorpus.Canonical(provenance))!)],
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                   or InvalidOperationException or ArgumentException or FormatException or NullReferenceException)
        {
            // Never put payloads, file paths or exception messages in diagnostic/audit output.
            return Refuse("Event corpus refused: invalid, missing, incompatible or inaccessible snapshot.");
        }
    }

    public JsonObject CreateAuditSummary(ToolArguments arguments, ToolCallResult result) => new()
    {
        ["executionMode"] = activation.Capture ? "capture" : "replay",
        ["corpusSha256"] = result.Facts.FirstOrDefault(f => f.Key == "corpusSha256")?.Value.DeepClone(),
        ["snapshotOrigin"] = "unattested-local-corpus",
        ["corpusProvenance"] = result.Facts.FirstOrDefault(f => f.Key == "corpusProvenance") is { } fact
            ? JsonNode.Parse(fact.Value.GetValue<string>()) : null,
        ["minimization"] = CanaryEventsCorpus.Minimization,
        ["succeeded"] = result.Succeeded,
    };

    private static ToolCallResult Refuse(string message) => ToolCallResult.Failure(message)
        with { FailureKind = ToolFailureKind.Validation };
}

internal static class CanaryEventsCorpus
{
    // The label the model-visible output carries; identical for every arm that replays the same file.
    internal static string OriginLabel(JsonObject provenance) =>
        provenance["classification"]!.GetValue<string>() is "OperatorAttested"
            ? "operator-attested" : provenance["classification"]!.GetValue<string>().ToLowerInvariant();

    internal const string Minimization = CorpusMinimizer.Version;
    private const int MaximumSnapshotBytes = 2 * 1024 * 1024;

    internal static void ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || !path.EndsWith(".bops-events.snapshot.json", StringComparison.Ordinal)
            || Path.GetDirectoryName(path) is not { } parent
            || Path.GetFileName(parent) != ".bops-event-corpus" || !Directory.Exists(parent))
            throw new InvalidOperationException("An explicit existing local corpus directory is required.");
        if (new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network)
            throw new InvalidOperationException("Network drives are not permitted.");
        // Reject junctions/symlinks rather than silently writing through a tracked or remote target.
        for (var directory = new DirectoryInfo(parent); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Reparse points are not permitted.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Reparse points are not permitted.");
    }

    internal static void ValidateArguments(ToolArguments arguments, DateTimeOffset now)
    {
        var manifest = SystemToolManifests.Events("windows");
        if (ArgumentSchema.Validate(manifest.Parameters, arguments) is not null
            || !SystemEventsArguments.TryRead(arguments, now, out var query, out _, out _, out _, out _)
            || query!.Channel is not (null or "System" or "Application")
            || query.Source is not null || query.Text is not null || query.ExcludeSources is { Count: > 0 }
            || (query.EventId is { } id && (id.Length > 5 || !id.All(char.IsAsciiDigit)
                || !int.TryParse(id, out var number) || number is < 0 or > 65535)))
            throw new InvalidOperationException("Only bounded System/Application requests without free-text filters are supported.");
    }

    internal static JsonObject Build(ToolArguments arguments, string output, string origin, DateTimeOffset capturedUtc, JsonObject? capture = null)
    {
        if (origin is not ("synthetic" or "windows-capture" or "unverified") || capturedUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Invalid corpus metadata.");
        ValidateCapture(capture, origin);
        var root = JsonNode.Parse(output)!.AsObject();
        var to = DateTimeOffset.Parse(root["window"]!["toUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        ValidateArguments(arguments, to);
        SystemEventsArguments.TryRead(arguments, to, out var query, out var mode, out var limit, out var bytes, out _);
        if (root["schemaVersion"]!.GetValue<int>() != SystemEventFormatting.SchemaVersion
            || root["mode"]!.GetValue<string>() != (mode == EvidenceMode.Raw ? "raw" : "aggregate")
            || DateTimeOffset.Parse(root["window"]!["fromUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) != query!.FromUtc)
            throw new InvalidOperationException("Output does not match the request.");

        // Keep allowlisted diagnostics and redact the rest; never substitute invented events, providers, users or machines.
        CorpusMinimizer.Apply(root);
        root["benchmarkCorpus"] = new JsonObject
        {
            ["origin"] = origin, ["minimization"] = Minimization,
            ["evidence"] = "stored-snapshot; not a fresh Event Log observation; execution mode is in audit and facts",
        };
        var sanitized = Canonical(root);
        var corpus = new JsonObject
        {
            ["format"] = "bops-system-events-corpus-v1", ["tool"] = "system.events",
            ["schemaVersion"] = SystemEventFormatting.SchemaVersion,
            ["manifestParameters"] = Canonical(JsonSerializer.SerializeToNode(SystemToolManifests.Events("windows").Parameters)!),
            ["toolAssemblyVersion"] = typeof(WindowsSystemEventsTool).Assembly.GetName().Version!.ToString(),
            ["toolModuleId"] = typeof(WindowsSystemEventsTool).Module.ModuleVersionId.ToString("D"),
            ["origin"] = origin, ["capturedUtc"] = capturedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["arguments"] = Canonical(arguments.ToJson()),
            ["effectiveQuery"] = JsonSerializer.SerializeToNode(query), ["mode"] = (int)mode,
            ["limit"] = limit, ["maxOutputBytes"] = bytes,
            ["minimization"] = Minimization, ["output"] = sanitized,
            ["complete"] = root["complete"]!.GetValue<bool>(), ["truncated"] = root["truncated"]!.GetValue<bool>(),
            ["status"] = root["status"]!.GetValue<string>(),
            ["completeness"] = (int)(root["complete"]!.GetValue<bool>() ? ToolResultCompleteness.Complete
                : root["status"]!.GetValue<string>() is "unavailable" or "unsupported"
                    ? ToolResultCompleteness.Unavailable : ToolResultCompleteness.Partial),
        };
        if (capture is not null) corpus["capture"] = capture.DeepClone();
        return corpus;
    }

    /// <summary>
    /// Builds the corpus of a native capture and judges it in one step, so a capture is never persisted or approved without a verdict.
    /// The gate runs on exactly the bytes that will be stored.
    /// </summary>
    internal static (JsonObject Corpus, string Hash, CorpusQuality Quality) BuildCapture(
        ToolArguments arguments, string output, string captureId, DateTimeOffset capturedUtc)
    {
        var corpus = Build(arguments, output, "windows-capture", capturedUtc, CaptureMetadata(captureId));
        var hash = Hash(corpus);
        return (corpus, hash, CorpusQualityGate.Evaluate(corpus, hash, arguments));
    }

    internal static JsonObject CaptureMetadata(string captureId) => new()
    {
        ["captureId"] = captureId,
        ["auditReference"] = "ToolCallAuditEvent.Summary.corpusProvenance.captureId=" + captureId,
        ["invokedTool"] = typeof(WindowsSystemEventsTool).FullName,
        ["manifestSha256"] = JsonHash(JsonSerializer.SerializeToNode(SystemToolManifests.Events("windows"))!),
        ["schemaSha256"] = JsonHash(JsonSerializer.SerializeToNode(SystemToolManifests.Events("windows").Parameters)!),
        ["environment"] = "Windows/" + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture,
    };

    private static void ValidateCapture(JsonObject? capture, string origin)
    {
        if (capture is null) return; // Legacy snapshots have no acquisition receipt.
        if (origin != "windows-capture" || capture.Count != 6
            || !Guid.TryParseExact(capture["captureId"]!.GetValue<string>(), "D", out _)
            || capture["auditReference"]!.GetValue<string>() != "ToolCallAuditEvent.Summary.corpusProvenance.captureId=" + capture["captureId"]!.GetValue<string>()
            || capture["invokedTool"]!.GetValue<string>() != typeof(WindowsSystemEventsTool).FullName
            || capture["manifestSha256"]!.GetValue<string>() != JsonHash(JsonSerializer.SerializeToNode(SystemToolManifests.Events("windows"))!)
            || capture["schemaSha256"]!.GetValue<string>() != JsonHash(JsonSerializer.SerializeToNode(SystemToolManifests.Events("windows").Parameters)!)
            || capture["environment"]!.GetValue<string>() is not ("Windows/X64" or "Windows/X86" or "Windows/Arm64" or "Windows/Arm"))
            throw new InvalidOperationException("Inconsistent acquisition metadata.");
    }

    // A receipt imported from disk is self-asserted: hashes prove integrity, never acquisition.
    // Captured is available only on the successful native call in this adapter, not by JSON declaration.
    // A file can be OperatorAttested at best, never Captured: that needs a verified attestation of exactly this corpus.
    internal static JsonObject Provenance(JsonObject corpus, string hash, bool capturedHere = false, OperatorAttestation? attestation = null)
    {
        var attested = attestation is not null && !capturedHere;
        if (attested && attestation!.CorpusSha256 != hash) throw new InvalidOperationException("The operator attestation does not describe this corpus.");
        var provenance = new JsonObject
        {
            ["classification"] = capturedHere ? "Captured" : attested ? "OperatorAttested" : corpus["origin"]!.GetValue<string>() == "synthetic" ? "Synthetic" : "Unverified",
            ["guarantee"] = capturedHere ? "observed-local-native-execution; no cryptographic attestation"
                : attested ? CorpusAttestation.Guarantee : "pinned-content-integrity; acquisition-unverified",
            ["corpusSha256"] = hash,
            ["captureId"] = corpus["capture"]?["captureId"]?.DeepClone(),
            ["acquisitionMetadataStatus"] = capturedHere ? "Observed" : attested ? "OperatorAttested" : "SelfAsserted",
            ["tool"] = corpus["tool"]!.DeepClone(),
            ["invokedTool"] = corpus["capture"]?["invokedTool"]?.DeepClone(),
            ["manifestSha256"] = corpus["capture"]?["manifestSha256"]?.DeepClone(),
            ["schemaSha256"] = corpus["capture"]?["schemaSha256"]?.DeepClone(),
            ["recordedUtc"] = corpus["capturedUtc"]!.DeepClone(),
            ["arguments"] = JsonNode.Parse(corpus["arguments"]!.GetValue<string>()),
            ["complete"] = corpus["complete"]!.DeepClone(),
            ["truncated"] = corpus["truncated"]!.DeepClone(),
            ["completeness"] = corpus["completeness"]!.DeepClone(),
            ["environment"] = corpus["capture"]?["environment"]?.DeepClone(),
            ["auditReference"] = corpus["capture"]?["auditReference"]?.DeepClone(),
        };
        if (attested)
        {
            // The capture's audit reference points into another process's audit; the attestation (by hash) is the link that holds here.
            provenance.Remove("auditReference");
            provenance["attestationSha256"] = attestation!.Sha256;
            provenance["approvedUtc"] = attestation.ApprovedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }

        return provenance;
    }

    internal static void RequireRealCorpus(JsonObject provenance)
    {
        if (provenance["classification"]!.GetValue<string>() != "Captured")
            throw new InvalidOperationException("Real Windows benchmark requires independently verified acquisition; imported corpus is Synthetic or Unverified.");
    }

    /// <summary>
    /// The benchmark replay gate: an in-process <c>Captured</c> corpus, or a saved file with a verified operator attestation.
    /// Separate from <see cref="RequireRealCorpus"/> on purpose, which keeps refusing everything not observed in this process.
    /// </summary>
    internal static void RequireBenchmarkProvenance(JsonObject provenance)
    {
        if (provenance["classification"]!.GetValue<string>() is not ("Captured" or "OperatorAttested"))
            throw new InvalidOperationException("Benchmark corpus requires an in-process capture or a verified operator attestation; imported corpus is Synthetic or Unverified.");
    }

    internal static string Canonical(JsonNode node) => Ordered(node).ToJsonString();
    private static JsonNode Ordered(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value is null ? null : Ordered(p.Value)))),
        JsonArray array => new JsonArray(array.Select(item => item is null ? null : Ordered(item)).ToArray()),
        _ => node.DeepClone(),
    };
    internal static string JsonHash(JsonNode node) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(node))));
    internal static string Hash(JsonObject corpus) => JsonHash(corpus);
    internal static byte[] Encode(JsonObject corpus, string hash) => Encoding.UTF8.GetBytes(Canonical(new JsonObject
        { ["corpus"] = corpus.DeepClone(), ["sha256"] = hash }));

    internal static (JsonObject Corpus, string Hash) Read(string path, string? expectedHash)
    {
        ValidatePath(path);
        if (expectedHash is null || expectedHash.Length != 64 || expectedHash.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidOperationException("A separately pinned SHA-256 is required.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumSnapshotBytes) throw new InvalidOperationException("Invalid snapshot size.");
        var data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        var envelope = JsonNode.Parse(data)!.AsObject();
        var corpus = envelope["corpus"]!.AsObject();
        var hash = Hash(corpus);
        if (envelope.Count != 2 || hash != envelope["sha256"]!.GetValue<string>() || hash != expectedHash
            || !data.AsSpan().SequenceEqual(Encode(corpus, hash))
            || corpus["format"]!.GetValue<string>() != "bops-system-events-corpus-v1"
            || corpus["tool"]!.GetValue<string>() != "system.events")
            throw new InvalidOperationException("Snapshot integrity/format check failed.");
        // Rebuild all metadata from the saved sanitized output and parameters: reject unsupported schemas,
        // changed limits/defaults/assembly contract and inconsistent effective windows/completeness.
        var rebuilt = Build(ToolArguments.FromJson(JsonNode.Parse(corpus["arguments"]!.GetValue<string>())!.AsObject()),
            corpus["output"]!.GetValue<string>(), corpus["origin"]!.GetValue<string>(),
            DateTimeOffset.Parse(corpus["capturedUtc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture), corpus["capture"]?.AsObject());
        if (Canonical(rebuilt) != Canonical(corpus)) throw new InvalidOperationException("Incompatible snapshot metadata.");
        return (corpus, hash);
    }
}
