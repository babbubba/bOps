// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Knowledge;

/// <summary>The registry's answer for one candidate source and one task scope.</summary>
public sealed class ProviderAdmission
{
    internal ProviderAdmission(
        KnowledgeAdmissionCode code,
        string? providerId,
        KnowledgeProviderTrust trust,
        IReadOnlySet<PackKind> kinds,
        IKnowledgePackSource? source)
    {
        Code = code;
        ProviderId = providerId;
        Trust = trust;
        Kinds = kinds;
        Source = source;
    }

    /// <summary><see cref="KnowledgeAdmissionCode.Admitted"/> when the source may be used for the scope.</summary>
    public KnowledgeAdmissionCode Code { get; }

    /// <summary>The id the registry assigned to the registered instance; <c>null</c> when the candidate is not registered.</summary>
    public string? ProviderId { get; }

    /// <summary>The trust level of the registration.</summary>
    public KnowledgeProviderTrust Trust { get; }

    /// <summary>The kinds the registration allows.</summary>
    public IReadOnlySet<PackKind> Kinds { get; }

    internal IKnowledgePackSource? Source { get; }

    /// <summary>Whether this provider may serve a pack of the given kind: the registration lists it, and an untrusted provider never serves a skill pack.</summary>
    public bool MayServe(PackKind kind) =>
        Kinds.Contains(kind) && (Trust == KnowledgeProviderTrust.Trusted || kind != PackKind.Skill);
}
