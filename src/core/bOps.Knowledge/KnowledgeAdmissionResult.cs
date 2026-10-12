// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Knowledge;

/// <summary>
/// The outcome of an admission attempt: the admitted pack, or a refusal code, with the facts that are safe to audit. Refusals are
/// results, not exceptions.
/// </summary>
public sealed class KnowledgeAdmissionResult
{
    internal KnowledgeAdmissionResult(
        KnowledgeAdmissionCode code,
        AdmittedKnowledgePack? pack,
        string? providerId,
        PackKind? kind,
        string? publisher,
        string? keyId,
        string? contentHash)
    {
        Code = code;
        Pack = pack;
        ProviderId = providerId;
        Kind = kind;
        Publisher = publisher;
        KeyId = keyId;
        ContentHash = contentHash;
    }

    /// <summary><see cref="KnowledgeAdmissionCode.Admitted"/> when <see cref="Pack"/> is set.</summary>
    public KnowledgeAdmissionCode Code { get; }

    /// <summary>The admitted pack.</summary>
    public AdmittedKnowledgePack? Pack { get; }

    internal string? ProviderId { get; }

    internal PackKind? Kind { get; }

    internal string? Publisher { get; }

    internal string? KeyId { get; }

    internal string? ContentHash { get; }

    /// <summary>
    /// The same attempt, refused for a reason found after admission by a consumer of the snapshot (for example an invalid skill
    /// found when the content engine parses it). The facts are kept, the pack is dropped, and the result is audited as a refusal.
    /// </summary>
    public KnowledgeAdmissionResult Refuse(KnowledgeAdmissionCode code)
    {
        if (code == KnowledgeAdmissionCode.Admitted)
        {
            throw new ArgumentException("A refusal needs a refusal code.", nameof(code));
        }

        return new KnowledgeAdmissionResult(code, null, ProviderId, Kind, Publisher, KeyId, ContentHash);
    }
}
