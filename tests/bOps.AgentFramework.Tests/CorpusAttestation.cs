// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// The verified operator approval of one frozen corpus file. It is a harness record only: it never changes a production policy and it is
/// accepted only by <see cref="KnowledgeBenchmark.ReplayFrozen"/>.
/// </summary>
internal sealed record OperatorAttestation(string Sha256, string CorpusSha256, string CaptureId, DateTimeOffset ApprovedUtc);

/// <summary>
/// Trust model for a corpus that was captured by the trusted native path, minimized, saved and is replayed in later processes.
/// <para>
/// A corpus read from disk can never be classified <c>Captured</c>: acquisition happened in another process and the file's own receipt is
/// self-asserted. What the operator can do is <i>attest</i> the file: after the capture-time quality gate passed, the operator repeats the
/// exact corpus SHA-256 they reviewed, and a sidecar (<c>*.bops-events.attestation.json</c>, create-new, never overwritten) binds
/// that hash to the capture receipt (capture id, tool, manifest and schema hashes, environment), the request, the minimization version and
/// the gate figures. Replays pin both hashes out of band (corpus and attestation). The classification is <c>OperatorAttested</c>.
/// </para>
/// <para>
/// This is an operational attestation. It is not a cryptographic proof of provenance: anyone able to write both files and the operator's
/// pins could forge it, and a SHA-256 only proves the bytes did not change since approval.
/// </para>
/// </summary>
internal static class CorpusAttestation
{
    internal const string Format = "bops-events-corpus-attestation-v1";
    internal const string Approval = "operator-explicit-sha256-confirmation";
    // Kept short on purpose: it is part of the corpus provenance fact, which the runtime bounds to 1024 characters.
    internal const string Guarantee = "operator-attested; SHA-256 pinned; acquisition not independently provable; unsigned";
    internal const string CorpusSuffix = ".bops-events.snapshot.json";
    internal const string AttestationSuffix = ".bops-events.attestation.json";
    private const int MaximumBytes = 64 * 1024;

    /// <summary>The only place an attestation may live: next to its corpus, same directory, same stem.</summary>
    internal static string PathFor(string corpusPath)
    {
        if (!corpusPath.EndsWith(CorpusSuffix, StringComparison.Ordinal)) throw new InvalidOperationException("Not a benchmark corpus path.");
        return corpusPath[..^CorpusSuffix.Length] + AttestationSuffix;
    }

    /// <summary>
    /// Records the operator's approval of <paramref name="corpusPath"/>. The operator must repeat the corpus hash they reviewed
    /// (<paramref name="approvedSha256"/>); the corpus must pass integrity, provenance receipt and the quality gate against the scenario request.
    /// Returns the attestation SHA-256 to pin for replays.
    /// </summary>
    internal static string Approve(string corpusPath, string pinnedCorpusSha256, string approvedSha256, DateTimeOffset nowUtc)
    {
        if (!string.Equals(approvedSha256, pinnedCorpusSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Operator approval must repeat the exact corpus SHA-256.");
        var (corpus, hash) = CanaryEventsCorpus.Read(corpusPath, pinnedCorpusSha256);
        var quality = CorpusQualityGate.Require(CorpusQualityGate.Evaluate(corpus, hash, KnowledgeBenchmark.Arguments));
        var capture = corpus["capture"]!.AsObject();
        var capturedUtc = ParseUtc(corpus["capturedUtc"]!.GetValue<string>());
        if (nowUtc.Offset != TimeSpan.Zero || nowUtc < capturedUtc) throw new InvalidOperationException("Approval time precedes the capture.");
        var body = new JsonObject
        {
            ["format"] = Format,
            ["approval"] = Approval,
            ["guarantee"] = Guarantee,
            ["corpusSha256"] = hash,
            ["captureId"] = capture["captureId"]!.DeepClone(),
            ["capturedUtc"] = corpus["capturedUtc"]!.DeepClone(),
            ["arguments"] = corpus["arguments"]!.DeepClone(),
            ["minimization"] = corpus["minimization"]!.DeepClone(),
            ["invokedTool"] = capture["invokedTool"]!.DeepClone(),
            ["manifestSha256"] = capture["manifestSha256"]!.DeepClone(),
            ["schemaSha256"] = capture["schemaSha256"]!.DeepClone(),
            ["environment"] = capture["environment"]!.DeepClone(),
            ["quality"] = Quality(quality),
            ["approvedUtc"] = nowUtc.ToString("O", CultureInfo.InvariantCulture),
        };
        var sha = CanaryEventsCorpus.JsonHash(body);
        var bytes = Encode(body, sha);
        using var file = new FileStream(PathFor(corpusPath), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
#pragma warning disable CA1849 // The approval must be durable before the operator relies on it.
        file.Flush(flushToDisk: true);
#pragma warning restore CA1849
        return sha;
    }

    /// <summary>
    /// Verifies the attestation pinned by <paramref name="attestationSha256"/> against the already verified corpus. Any difference,
    /// a missing sidecar, a changed byte, a stale gate figure or a corpus that is no longer suitable refuses the replay.
    /// </summary>
    internal static OperatorAttestation Load(string corpusPath, JsonObject corpus, string corpusSha256, string? attestationSha256)
    {
        if (attestationSha256 is not { Length: 64 } || !attestationSha256.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("A separately pinned attestation SHA-256 is required.");
        CanaryEventsCorpus.ValidatePath(corpusPath);
        var path = PathFor(corpusPath);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The corpus has no operator attestation.");
        byte[] data;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length is <= 0 or > MaximumBytes) throw new InvalidOperationException("Invalid attestation size.");
            data = new byte[(int)stream.Length];
            stream.ReadExactly(data);
        }

        try
        {
            var envelope = JsonNode.Parse(data)!.AsObject();
            var body = envelope["attestation"]!.AsObject();
            var sha = CanaryEventsCorpus.JsonHash(body);
            if (envelope.Count != 2 || sha != envelope["sha256"]!.GetValue<string>() || sha != attestationSha256
                || !data.AsSpan().SequenceEqual(Encode(body, sha)))
                throw new InvalidOperationException("Attestation integrity check failed.");
            var capture = corpus["capture"]?.AsObject() ?? throw new InvalidOperationException("Corpus has no capture receipt.");
            var quality = CorpusQualityGate.Require(CorpusQualityGate.Evaluate(corpus, corpusSha256, KnowledgeBenchmark.Arguments));
            var approvedUtc = ParseUtc(body["approvedUtc"]!.GetValue<string>());
            if (body["format"]!.GetValue<string>() != Format || body["approval"]!.GetValue<string>() != Approval
                || body["guarantee"]!.GetValue<string>() != Guarantee
                || body["corpusSha256"]!.GetValue<string>() != corpusSha256
                || body["captureId"]!.GetValue<string>() != capture["captureId"]!.GetValue<string>()
                || body["capturedUtc"]!.GetValue<string>() != corpus["capturedUtc"]!.GetValue<string>()
                || body["arguments"]!.GetValue<string>() != corpus["arguments"]!.GetValue<string>()
                || body["arguments"]!.GetValue<string>() != CanaryEventsCorpus.Canonical(KnowledgeBenchmark.Arguments.ToJson())
                || body["minimization"]!.GetValue<string>() != corpus["minimization"]!.GetValue<string>()
                || body["invokedTool"]!.GetValue<string>() != capture["invokedTool"]!.GetValue<string>()
                || body["manifestSha256"]!.GetValue<string>() != capture["manifestSha256"]!.GetValue<string>()
                || body["schemaSha256"]!.GetValue<string>() != capture["schemaSha256"]!.GetValue<string>()
                || body["environment"]!.GetValue<string>() != capture["environment"]!.GetValue<string>()
                || CanaryEventsCorpus.Canonical(body["quality"]!) != CanaryEventsCorpus.Canonical(Quality(quality))
                || approvedUtc < ParseUtc(corpus["capturedUtc"]!.GetValue<string>()))
                throw new InvalidOperationException("Attestation does not describe this corpus.");
            return new OperatorAttestation(sha, corpusSha256, capture["captureId"]!.GetValue<string>(), approvedUtc);
        }
        catch (Exception ex) when (ex is JsonException or NullReferenceException or FormatException or InvalidCastException or KeyNotFoundException)
        {
            throw new InvalidOperationException("Attestation is invalid.");
        }
    }

    private static JsonObject Quality(CorpusQuality quality) => new()
    {
        ["events"] = quality.Events,
        ["usableEvents"] = quality.UsableEvents,
        ["requiredChannels"] = new JsonArray([.. quality.Channels.Select(channel => (JsonNode)JsonValue.Create(channel)!)]),
        ["verdict"] = "suitable",
    };

    private static byte[] Encode(JsonObject body, string sha) =>
        Encoding.UTF8.GetBytes(CanaryEventsCorpus.Canonical(new JsonObject { ["attestation"] = body.DeepClone(), ["sha256"] = sha }));

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
