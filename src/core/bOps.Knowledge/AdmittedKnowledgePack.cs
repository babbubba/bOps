// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>
/// A pack that is signed by a trusted key, hash-complete, granted to the task's tenant, supplied by a registered provider and held
/// in a verified snapshot. It is created only by <see cref="KnowledgeAdmissionService"/>; content is read from its snapshot and
/// from nowhere else.
/// </summary>
public sealed class AdmittedKnowledgePack
{
    internal AdmittedKnowledgePack(
        KnowledgeTaskScope scope, string providerId, KnowledgeProviderTrust providerTrust, KnowledgeSnapshot snapshot)
    {
        Scope = scope;
        ProviderId = providerId;
        ProviderTrust = providerTrust;
        Snapshot = snapshot;
    }

    /// <summary>The scope the pack was admitted for.</summary>
    public KnowledgeTaskScope Scope { get; }

    /// <summary>The id the host registry assigned to the provider that supplied the pack.</summary>
    public string ProviderId { get; }

    /// <summary>The trust level the host assigned to that provider.</summary>
    public KnowledgeProviderTrust ProviderTrust { get; }

    /// <summary>The verified snapshot.</summary>
    public KnowledgeSnapshot Snapshot { get; }

    /// <summary>The pin: tenant, complete identity and per-file hashes. Serializable.</summary>
    public KnowledgePin Pin => Snapshot.Pin;

    /// <summary>The complete identity of the pack.</summary>
    public PackIdentity Identity => Snapshot.Pin.Identity;
}
