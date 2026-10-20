// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>
/// The manifest of a pack (<c>bops-pack.json</c>). Its exact bytes are what the publisher signs, so every field here is covered by
/// the signature; none of them is believed before the signature verified against a key in the host's trust store.
/// </summary>
public sealed record PackManifest
{
    /// <summary>The manifest schema version; <see cref="KnowledgePackFormat.SchemaVersion"/> is the only one defined.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>The pack id.</summary>
    public required string PackId { get; init; }

    /// <summary>The pack version (<c>major.minor.patch</c> with an optional pre-release suffix).</summary>
    public required string Version { get; init; }

    /// <summary>What the pack carries.</summary>
    public required PackKind Kind { get; init; }

    /// <summary>The engine versions the pack is compatible with.</summary>
    public required PackEngineRange Engine { get; init; }

    /// <summary>The publisher. It must equal the publisher of the signature; trust is decided by the trust store, not by this field.</summary>
    public required string Publisher { get; init; }

    /// <summary>The id of the key that signed. It must equal the key id of the signature.</summary>
    public required string KeyId { get; init; }

    /// <summary>The SHA-256 over the sorted <c>path, newline, file hash, newline</c> listing of <see cref="Files"/> (see <see cref="KnowledgePackFormat.ComputeContentHash"/>).</summary>
    public required string ContentSha256 { get; init; }

    /// <summary>Every payload file of the pack. A file not listed here is refused.</summary>
    public required IReadOnlyList<PackFileEntry> Files { get; init; }
}
