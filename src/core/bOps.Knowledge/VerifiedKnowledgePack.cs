// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>
/// A pack that passed every check: signed by a trusted key, compatible, hash-complete and free of forbidden content. Only
/// <see cref="KnowledgePackVerifier"/> creates one, and it holds the verifier's own copy of the bytes, so nothing the source does
/// afterwards can change it. The snapshot store accepts nothing else.
/// </summary>
public sealed class VerifiedKnowledgePack
{
    internal VerifiedKnowledgePack(
        PackIdentity identity,
        PackManifest manifest,
        byte[] manifestBytes,
        byte[] signatureBytes,
        IReadOnlyDictionary<string, byte[]> files)
    {
        Identity = identity;
        Manifest = manifest;
        ManifestBytes = manifestBytes;
        SignatureBytes = signatureBytes;
        Files = files;
    }

    /// <summary>The complete identity.</summary>
    public PackIdentity Identity { get; }

    /// <summary>The signed manifest.</summary>
    public PackManifest Manifest { get; }

    internal byte[] ManifestBytes { get; }

    internal byte[] SignatureBytes { get; }

    internal IReadOnlyDictionary<string, byte[]> Files { get; }
}
