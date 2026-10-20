// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>
/// What a source hands over: bytes and nothing else, unverified. It is data in transit; nothing in it is believed, and the verifier
/// copies the bytes before it checks them so that a source that keeps a reference cannot change them afterwards.
/// </summary>
public sealed class RawKnowledgePack
{
    /// <summary>The bytes of <c>bops-pack.json</c>; empty when the source found none.</summary>
    public ReadOnlyMemory<byte> ManifestBytes { get; init; }

    /// <summary>The bytes of <c>bops-pack.sig.json</c>; empty when the source found none.</summary>
    public ReadOnlyMemory<byte> SignatureBytes { get; init; }

    /// <summary>Every other file the source found, by relative path with forward slashes.</summary>
    public required IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files { get; init; }

    /// <summary>The relative paths of any symbolic link or reparse point the source found and did not follow.</summary>
    public IReadOnlyList<string> LinkedEntries { get; init; } = [];

    /// <summary>
    /// <c>true</c> when the source stopped reading because the pack exceeded a bound of <see cref="KnowledgePackLimits"/>; the
    /// bytes it holds are then incomplete and the pack is refused.
    /// </summary>
    public bool ExceededLimits { get; init; }
}
