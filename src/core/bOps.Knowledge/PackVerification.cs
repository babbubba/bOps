// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Knowledge;

/// <summary>
/// The verifier's verdict. Besides the verified pack or the refusal code it carries only what is safe to audit for a refused pack:
/// the manifest hash bOps computed itself, and the publisher and key id the signature named if they have the shape of identifiers.
/// The kind is present only once the signature verified, because only then is the manifest believed.
/// </summary>
public sealed class PackVerification
{
    internal PackVerification(
        KnowledgeAdmissionCode code, VerifiedKnowledgePack? pack, string? contentHash, string? publisher, string? keyId, PackKind? kind)
    {
        Code = code;
        Pack = pack;
        ContentHash = contentHash;
        Publisher = publisher;
        KeyId = keyId;
        Kind = kind;
    }

    /// <summary>How verification ended; <see cref="KnowledgeAdmissionCode.Admitted"/> when the pack was verified.</summary>
    public KnowledgeAdmissionCode Code { get; }

    /// <summary>The verified pack, when <see cref="Code"/> is <see cref="KnowledgeAdmissionCode.Admitted"/>.</summary>
    public VerifiedKnowledgePack? Pack { get; }

    /// <summary>The SHA-256 of the manifest bytes, when they were within bounds.</summary>
    public string? ContentHash { get; }

    /// <summary>The publisher the signature named, when it has the shape of an identifier.</summary>
    public string? Publisher { get; }

    /// <summary>The key id the signature named, when it has the shape of an identifier.</summary>
    public string? KeyId { get; }

    /// <summary>The kind the manifest declares, once the signature verified.</summary>
    public PackKind? Kind { get; }
}
