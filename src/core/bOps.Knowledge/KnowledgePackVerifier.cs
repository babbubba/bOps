// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using bOps.Abstractions;

namespace bOps.Knowledge;

/// <summary>
/// Decides whether the bytes a source handed over are a pack bOps will use (ADR-0055 section 2). Nothing is believed before the
/// signature verified against a key in the host's <see cref="KnowledgeTrustStore"/>; after that the signed manifest is the truth and
/// every byte must match it exactly: no missing file, no extra file, no different hash, no link, no script, no unsafe path. It works
/// on its own copy of the bytes, so a source that keeps a reference cannot change what was verified. A valid signature says who
/// published the pack, not that its text is safe: served content stays attributed, delimited data.
/// </summary>
public sealed partial class KnowledgePackVerifier
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly KnowledgeTrustStore trustStore;
    private readonly Version engineVersion;
    private readonly KnowledgePackLimits limits;

    /// <summary>Creates a verifier.</summary>
    /// <param name="trustStore">The keys the host trusts.</param>
    /// <param name="engineVersion">The engine version packs are checked against; the host supplies it.</param>
    /// <param name="limits">The size and count bounds; <see cref="KnowledgePackLimits.Default"/> when omitted.</param>
    public KnowledgePackVerifier(KnowledgeTrustStore trustStore, Version engineVersion, KnowledgePackLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(trustStore);
        ArgumentNullException.ThrowIfNull(engineVersion);
        this.trustStore = trustStore;
        this.engineVersion = engineVersion;
        this.limits = limits ?? KnowledgePackLimits.Default;
    }

    /// <summary>Verifies a raw pack. Refusals are results, never exceptions.</summary>
    public PackVerification Verify(RawKnowledgePack raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.ExceededLimits
            || raw.ManifestBytes.Length > limits.MaxManifestBytes
            || raw.SignatureBytes.Length > limits.MaxSignatureBytes)
        {
            return Refuse(KnowledgeAdmissionCode.LimitExceeded);
        }

        if (raw.ManifestBytes.IsEmpty)
        {
            return Refuse(KnowledgeAdmissionCode.ManifestInvalid);
        }

        var manifestBytes = raw.ManifestBytes.ToArray();
        var contentHash = KnowledgePackFormat.Sha256Hex(manifestBytes);
        var signatureBytes = raw.SignatureBytes.ToArray();

        if (!TryReadEnvelope(signatureBytes, out var envelope))
        {
            return Refuse(KnowledgeAdmissionCode.SignatureMalformed, contentHash);
        }

        var lookup = trustStore.Find(envelope.Publisher, envelope.KeyId);
        if (lookup.Status != KeyStatus.Trusted)
        {
            var code = lookup.Status switch
            {
                KeyStatus.PublisherRevoked => KnowledgeAdmissionCode.PublisherRevoked,
                KeyStatus.KeyRevoked => KnowledgeAdmissionCode.KeyRevoked,
                _ => KnowledgeAdmissionCode.UnknownKey,
            };
            return Refuse(code, contentHash, envelope.Publisher, envelope.KeyId);
        }

        if (!SignatureVerifies(lookup.Key!, SHA256.HashData(manifestBytes), envelope.Signature, out var malformed))
        {
            return Refuse(
                malformed ? KnowledgeAdmissionCode.SignatureMalformed : KnowledgeAdmissionCode.InvalidSignature,
                contentHash, envelope.Publisher, envelope.KeyId);
        }

        // From here the manifest is the signed truth of a trusted publisher.
        if (!TryReadManifest(manifestBytes, out var manifest)
            || !string.Equals(manifest.Publisher, envelope.Publisher, StringComparison.Ordinal)
            || !string.Equals(manifest.KeyId, envelope.KeyId, StringComparison.Ordinal))
        {
            return Refuse(KnowledgeAdmissionCode.ManifestInvalid, contentHash, envelope.Publisher, envelope.KeyId);
        }

        PackVerification Reject(KnowledgeAdmissionCode code) =>
            Refuse(code, contentHash, envelope.Publisher, envelope.KeyId, manifest.Kind);

        var declared = CheckManifest(manifest);
        if (declared is { } manifestCode)
        {
            return Reject(manifestCode);
        }

        if (raw.LinkedEntries.Count > 0)
        {
            return Reject(KnowledgeAdmissionCode.LinkNotAllowed);
        }

        var files = CopyAndCheckFiles(raw, manifest, out var fileCode);
        if (fileCode is { } rejected)
        {
            return Reject(rejected);
        }

        var identity = new PackIdentity(
            manifest.PackId, manifest.Version, manifest.Kind, manifest.Publisher, manifest.KeyId, contentHash);
        return new PackVerification(
            KnowledgeAdmissionCode.Admitted,
            new VerifiedKnowledgePack(identity, manifest, manifestBytes, signatureBytes, files!),
            contentHash, envelope.Publisher, envelope.KeyId, manifest.Kind);
    }

    private static PackVerification Refuse(
        KnowledgeAdmissionCode code,
        string? contentHash = null,
        string? publisher = null,
        string? keyId = null,
        PackKind? kind = null) =>
        new(code, null, contentHash, KnowledgeTrustStore.IsIdentifier(publisher) ? publisher : null,
            KnowledgeTrustStore.IsIdentifier(keyId) ? keyId : null, kind);

    private static bool TryReadEnvelope(byte[] bytes, out PackSignatureEnvelope envelope)
    {
        envelope = null!;
        if (bytes.Length == 0)
        {
            return false;
        }

        try
        {
            var read = JsonSerializer.Deserialize(bytes, KnowledgeJsonContext.Default.PackSignatureEnvelope);
            if (read is null
                || read.SchemaVersion != KnowledgePackFormat.SchemaVersion
                || !string.Equals(read.Algorithm, KnowledgePackFormat.SignatureAlgorithm, StringComparison.Ordinal)
                || !KnowledgeTrustStore.IsIdentifier(read.Publisher)
                || !KnowledgeTrustStore.IsIdentifier(read.KeyId)
                || string.IsNullOrEmpty(read.Signature))
            {
                return false;
            }

            envelope = read;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool SignatureVerifies(PublisherKey key, byte[] digest, string signature, out bool malformed)
    {
        malformed = false;
        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            malformed = true;
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(key.PublicKeyPem);
            return rsa.VerifyHash(digest, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool TryReadManifest(byte[] bytes, out PackManifest manifest)
    {
        manifest = null!;
        try
        {
            var read = JsonSerializer.Deserialize(bytes, KnowledgeJsonContext.Default.PackManifest);
            if (read is null)
            {
                return false;
            }

            manifest = read;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Judges the signed manifest on its own: shape, engine compatibility, file list, layout and the declared content hash.</summary>
    private KnowledgeAdmissionCode? CheckManifest(PackManifest manifest)
    {
        if (manifest.SchemaVersion != KnowledgePackFormat.SchemaVersion
            || !PackReference.IsPackId(manifest.PackId)
            || !PackReference.IsVersion(manifest.Version)
            || !Enum.IsDefined(manifest.Kind)
            || manifest.Engine is null
            || manifest.Files is null
            || !Sha256Shape().IsMatch(manifest.ContentSha256 ?? string.Empty))
        {
            return KnowledgeAdmissionCode.ManifestInvalid;
        }

        if (!TryVersion(manifest.Engine.Min, out var min) || !TryVersion(manifest.Engine.Max, out var max) || min > max)
        {
            return KnowledgeAdmissionCode.ManifestInvalid;
        }

        if (engineVersion < min || engineVersion > max)
        {
            return KnowledgeAdmissionCode.EngineIncompatible;
        }

        if (manifest.Files.Count == 0)
        {
            return KnowledgeAdmissionCode.ManifestInvalid;
        }

        if (manifest.Files.Count > limits.MaxFiles)
        {
            return KnowledgeAdmissionCode.LimitExceeded;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || file.Path is null || !Sha256Shape().IsMatch(file.Sha256 ?? string.Empty) || file.Size < 0)
            {
                return KnowledgeAdmissionCode.ManifestInvalid;
            }

            if (PackPathPolicy.Check(file.Path, limits) is { } pathCode)
            {
                return pathCode;
            }

            if (!seen.Add(file.Path))
            {
                return KnowledgeAdmissionCode.UnsafePath;
            }

            if (file.Size > limits.MaxFileBytes || (total += file.Size) > limits.MaxTotalBytes)
            {
                return KnowledgeAdmissionCode.LimitExceeded;
            }
        }

        var skills = manifest.Files.Select(file => file.Path.Split('/')[0]).ToHashSet(StringComparer.Ordinal);
        var entries = manifest.Files.Where(file => file.Path.EndsWith("/" + KnowledgePackFormat.SkillFileName, StringComparison.Ordinal))
            .Select(file => file.Path.Split('/')[0]).ToHashSet(StringComparer.Ordinal);
        if (!skills.SetEquals(entries))
        {
            return KnowledgeAdmissionCode.ManifestInvalid;
        }

        return string.Equals(KnowledgePackFormat.ComputeContentHash(manifest.Files), manifest.ContentSha256, StringComparison.Ordinal)
            ? null
            : KnowledgeAdmissionCode.ManifestInvalid;
    }

    /// <summary>Takes a private copy of every file and requires the set, the sizes, the hashes and the text to be exactly the signed ones.</summary>
    private Dictionary<string, byte[]>? CopyAndCheckFiles(RawKnowledgePack raw, PackManifest manifest, out KnowledgeAdmissionCode? code)
    {
        code = null;
        var expected = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        if (raw.Files.Count > limits.MaxFiles)
        {
            code = KnowledgeAdmissionCode.LimitExceeded;
            return null;
        }

        foreach (var path in raw.Files.Keys)
        {
            if (!expected.ContainsKey(path))
            {
                code = PackPathPolicy.Check(path, limits) is not null ? KnowledgeAdmissionCode.UnsafePath : KnowledgeAdmissionCode.UnexpectedContent;
                return null;
            }
        }

        Dictionary<string, byte[]> copies = new(StringComparer.Ordinal);
        foreach (var (path, entry) in expected)
        {
            if (!raw.Files.TryGetValue(path, out var content))
            {
                code = KnowledgeAdmissionCode.MissingResource;
                return null;
            }

            if (content.Length > limits.MaxFileBytes)
            {
                code = KnowledgeAdmissionCode.LimitExceeded;
                return null;
            }

            var bytes = content.ToArray();
            if (bytes.Length != entry.Size
                || !string.Equals(KnowledgePackFormat.Sha256Hex(bytes), entry.Sha256, StringComparison.Ordinal))
            {
                code = KnowledgeAdmissionCode.HashMismatch;
                return null;
            }

            if (!IsPlainText(bytes, out var text))
            {
                code = KnowledgeAdmissionCode.InvalidContent;
                return null;
            }

            if (text.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
            {
                code = KnowledgeAdmissionCode.ForbiddenContent;
                return null;
            }

            copies.Add(path, bytes);
        }

        return copies;
    }

    private static bool IsPlainText(byte[] bytes, out string text)
    {
        text = string.Empty;
        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return false;
        }

        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryVersion(string? text, out Version version)
    {
        version = new Version();
        return text is not null && ThreeComponents().IsMatch(text) && Version.TryParse(text, out version!);
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Shape();

    [GeneratedRegex("^[0-9]{1,9}\\.[0-9]{1,9}\\.[0-9]{1,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex ThreeComponents();
}
