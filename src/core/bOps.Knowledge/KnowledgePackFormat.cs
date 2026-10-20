// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace bOps.Knowledge;

/// <summary>
/// The on-disk and on-the-wire format of a pack, version 1: file names, the one signature algorithm, the closed list of payload
/// extensions, and the serialization publisher tooling and tests use to produce a manifest. It contains no signing code: private keys
/// exist only in publisher tooling, never in the runtime.
/// </summary>
public static class KnowledgePackFormat
{
    /// <summary>The only manifest and envelope schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The manifest file at the pack root.</summary>
    public const string ManifestFileName = "bops-pack.json";

    /// <summary>The detached signature file at the pack root.</summary>
    public const string SignatureFileName = "bops-pack.sig.json";

    /// <summary>RSA with PSS padding over SHA-256, the standard .NET primitive also used for plugin signatures.</summary>
    public const string SignatureAlgorithm = "RSA-PSS-SHA256";

    /// <summary>The entry document of a skill, at <c>skill-name/SKILL.md</c>.</summary>
    public const string SkillFileName = "SKILL.md";

    /// <summary>The smallest RSA key the trust store accepts, in bits.</summary>
    public const int MinimumKeyBits = 2048;

    /// <summary>The only payload extensions: plain text formats. Every other extension, executable or not, refuses the pack.</summary>
    public static IReadOnlyList<string> AllowedExtensions { get; } = [".md", ".txt", ".json", ".yaml", ".yml", ".csv", ".xml"];

    /// <summary>The lower-case hexadecimal SHA-256 of some bytes.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The content hash a manifest must declare for its file list: SHA-256 over the paths and file hashes in ordinal path order.</summary>
    public static string ComputeContentHash(IEnumerable<PackFileEntry> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        StringBuilder listing = new();
        foreach (var file in files.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            listing.Append(file.Path).Append('\n').Append(file.Sha256).Append('\n');
        }

        return Sha256Hex(Encoding.UTF8.GetBytes(listing.ToString()));
    }

    /// <summary>Serializes a manifest. The bytes returned are the bytes a publisher signs and the bytes a source must hand over unchanged.</summary>
    public static byte[] SerializeManifest(PackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.SerializeToUtf8Bytes(manifest, KnowledgeJsonContext.Default.PackManifest);
    }

    /// <summary>Serializes a signature envelope.</summary>
    public static byte[] SerializeSignature(PackSignatureEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, KnowledgeJsonContext.Default.PackSignatureEnvelope);
    }
}
