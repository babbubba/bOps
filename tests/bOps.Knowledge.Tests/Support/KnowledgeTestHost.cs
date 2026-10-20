// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Knowledge.Tests;

/// <summary>
/// A host composed the way a real one will be: a trust store, a provider registry with one registered in-memory source, host
/// grants, a snapshot store in a scratch directory and the admission service over all of them.
/// </summary>
internal sealed class KnowledgeTestHost : IDisposable
{
    private readonly TempDirectory directory = new();

    internal KnowledgeTestHost(
        IEnumerable<PackGrant> grants,
        KnowledgeProviderTrust trust = KnowledgeProviderTrust.Trusted,
        IEnumerable<PublisherKey>? keys = null,
        IEnumerable<TenantId>? tenants = null,
        KnowledgePackLimits? limits = null,
        IEnumerable<string>? revokedPublishers = null)
    {
        TrustStore = new KnowledgeTrustStore(keys ?? [TestPublisher.Acme.Trusted()], revokedPublishers);
        Registry = new KnowledgeProviderRegistry();
        ProviderId = Registry.Register(
            Source, "test-source", trust, [PackKind.Knowledge, PackKind.Experience, PackKind.Skill], [.. tenants ?? [TenantId.Local]]);
        Store = new KnowledgeSnapshotStore(directory.Sub("snapshots"));
        Service = new KnowledgeAdmissionService(
            Registry, new KnowledgeGrants(grants), new KnowledgePackVerifier(TrustStore, TestEngine.Version, limits), Store);
    }

    internal KnowledgeTrustStore TrustStore { get; }

    internal KnowledgeProviderRegistry Registry { get; }

    internal InMemoryPackSource Source { get; } = new();

    internal string ProviderId { get; }

    internal KnowledgeSnapshotStore Store { get; }

    internal KnowledgeAdmissionService Service { get; }

    internal ConcurrentAuditSink Audit { get; } = new();

    internal string Root => directory.Path;

    internal static ActorIdentity Actor { get; } = ActorIdentity.FromOperatingSystemUser("know-test");

    internal KnowledgeAdmissionRequest Request(
        KnowledgeTaskScope scope, PackReference reference, KnowledgePin? expectedPin = null, IKnowledgePackSource? source = null) => new()
    {
        Scope = scope,
        Actor = Actor,
        Source = source ?? Source,
        Reference = reference,
        ExpectedPin = expectedPin,
    };

    /// <summary>Admits and audits, as a real consumer does.</summary>
    internal async Task<KnowledgeAdmissionResult> AdmitAsync(KnowledgeAdmissionRequest request)
    {
        var result = await Service.AdmitAsync(request);
        await Service.WriteAuditAsync(Audit, request, result);
        return result;
    }

    public void Dispose() => directory.Dispose();
}
