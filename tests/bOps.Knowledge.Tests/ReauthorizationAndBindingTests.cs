// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Knowledge.Tests;

/// <summary>
/// KNOW-01B (ADR-0056): a pin is not an authorisation. <see cref="KnowledgeAdmissionService.Reauthorize"/> answers from the authority the
/// host holds now, never from the pin; and the pin a task persists comes back as a pin only if it is exactly well formed.
/// </summary>
public sealed class ReauthorizationAndBindingTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("tenant-a");

    private static KnowledgeTaskScope ScopeOf(TenantId tenant) =>
        KnowledgeTaskScope.FromAuthority(new ActorIdTenantAuthority(), ActorIdentity.FromOperatingSystemUser(tenant.Value), Guid.NewGuid())!;

    private static PackReference ReferenceOf(TestPack pack) => new(pack.PackId, pack.Version);

    private static async Task<(KnowledgeTestHost Host, AdmittedKnowledgePack Admitted, KnowledgeTaskScope Scope, TestPack Pack)> AdmitAsync(
        PackKind kind = PackKind.Skill)
    {
        var pack = new TestPack { Kind = kind };
        var host = new KnowledgeTestHost([new PackGrant(TenantA, ReferenceOf(pack), kind)], tenants: [TenantA]);
        host.Source.Add(TenantA, pack.PackId, pack.Version, () => pack.Build());
        var scope = ScopeOf(TenantA);
        var result = await host.AdmitAsync(host.Request(scope, ReferenceOf(pack)));
        Assert.Equal(KnowledgeAdmissionCode.Admitted, result.Code);
        return (host, result.Pack!, scope, pack);
    }

    private static KnowledgeAdmissionService ServiceWith(KnowledgeTestHost host, IEnumerable<PackGrant> grants, KnowledgeTrustStore trust) =>
        new(host.Registry, new KnowledgeGrants(grants), new KnowledgePackVerifier(trust, TestEngine.Version), host.Store);

    [Fact]
    public async Task WhileNothingIsWithdrawn_TheAuthorityStillCoversThePin()
    {
        var (host, admitted, scope, _) = await AdmitAsync();
        using (host)
        {
            Assert.Equal(KnowledgeAdmissionCode.Admitted, host.Service.Reauthorize(scope, host.Source, admitted.Pin));
        }
    }

    [Fact]
    public async Task AWithdrawnGrant_ARevokedKeyOrPublisher_AnUnknownKey_AndAnUnregisteredProvider_EachRefuse_WithTheirOwnCause()
    {
        var (host, admitted, scope, pack) = await AdmitAsync();
        using (host)
        {
            var pin = admitted.Pin;
            var grant = new PackGrant(TenantA, ReferenceOf(pack), PackKind.Skill);
            var trusted = TestPublisher.Acme.Trusted();

            Assert.Equal(KnowledgeAdmissionCode.NotEntitled, ServiceWith(host, [], new KnowledgeTrustStore([trusted])).Reauthorize(scope, host.Source, pin));
            Assert.Equal(
                KnowledgeAdmissionCode.NotEntitled,
                ServiceWith(host, [new PackGrant(TenantA, ReferenceOf(pack), PackKind.Knowledge)], new KnowledgeTrustStore([trusted])).Reauthorize(scope, host.Source, pin));
            Assert.Equal(KnowledgeAdmissionCode.PublisherRevoked, ServiceWith(host, [grant], new KnowledgeTrustStore([trusted], ["acme"])).Reauthorize(scope, host.Source, pin));
            Assert.Equal(KnowledgeAdmissionCode.KeyRevoked, ServiceWith(host, [grant], new KnowledgeTrustStore([TestPublisher.Acme.Trusted(revoked: true)])).Reauthorize(scope, host.Source, pin));
            Assert.Equal(KnowledgeAdmissionCode.UnknownKey, ServiceWith(host, [grant], KnowledgeTrustStore.Empty).Reauthorize(scope, host.Source, pin));
            Assert.Equal(KnowledgeAdmissionCode.Admitted, ServiceWith(host, [grant], new KnowledgeTrustStore([trusted])).Reauthorize(scope, host.Source, pin));

            Assert.True(host.Registry.Unregister(host.ProviderId));
            Assert.Equal(KnowledgeAdmissionCode.ProviderNotAdmitted, host.Service.Reauthorize(scope, host.Source, pin));
        }
    }

    [Fact]
    public async Task APinOfOneTenant_IsNeverAuthorisedForAnotherTenantsScope_AndAnUnregisteredSourceIsNeverAuthorised()
    {
        var (host, admitted, _, _) = await AdmitAsync();
        using (host)
        {
            Assert.Equal(KnowledgeAdmissionCode.TenantNotAllowed, host.Service.Reauthorize(ScopeOf(TenantB), host.Source, admitted.Pin));
            Assert.Equal(KnowledgeAdmissionCode.ProviderNotAdmitted, host.Service.Reauthorize(ScopeOf(TenantA), new InMemoryPackSource(), admitted.Pin));
        }
    }

    [Fact]
    public async Task AnUntrustedProvider_StopsBeingAuthorisedForASkillPack_ButNotForAKnowledgePack()
    {
        var skillPack = new TestPack { Kind = PackKind.Skill };
        var knowledgePack = new TestPack { PackId = "acme.reference", Kind = PackKind.Knowledge };
        using var host = new KnowledgeTestHost(
            [new PackGrant(TenantA, ReferenceOf(skillPack), PackKind.Skill), new PackGrant(TenantA, ReferenceOf(knowledgePack), PackKind.Knowledge)],
            trust: KnowledgeProviderTrust.Untrusted, tenants: [TenantA]);
        var scope = ScopeOf(TenantA);
        host.Source.Add(TenantA, knowledgePack.PackId, knowledgePack.Version, () => knowledgePack.Build());
        var admitted = (await host.AdmitAsync(host.Request(scope, ReferenceOf(knowledgePack)))).Pack!;

        Assert.Equal(KnowledgeAdmissionCode.Admitted, host.Service.Reauthorize(scope, host.Source, admitted.Pin));
        var skillPin = admitted.Pin with { PackId = skillPack.PackId, Kind = PackKind.Skill };
        Assert.Equal(KnowledgeAdmissionCode.KindNotAllowed, host.Service.Reauthorize(scope, host.Source, skillPin));
    }

    [Fact]
    public async Task ThePinTheTaskPersists_ComesBackAsExactlyTheSamePin_AndNamesTheSnapshotItWasServedFrom()
    {
        var (host, admitted, _, _) = await AdmitAsync();
        using (host)
        {
            var binding = admitted.Pin.ToBinding(Actor, admitted.Snapshot.Id);

            Assert.True(KnowledgePin.TryFromBinding(binding, out var pin));
            Assert.Equal(admitted.Pin.ToJson(), pin.ToJson());
            Assert.Equal(("tenant-a", "Skill", admitted.Pin.ContentHash), (binding.TenantId, binding.Kind, binding.ManifestHash));
            Assert.Equal(Actor, binding.AuthorizedBy);

            // The id is the tenant's partition and the content hash, relative to the store root: where the verified bytes live, not a path.
            var relative = Path.GetRelativePath(host.Store is { } ? Path.Combine(host.Root, "snapshots") : host.Root, admitted.Snapshot.RootPath).Replace('\\', '/');
            Assert.Equal(relative, admitted.Snapshot.Id);
            Assert.EndsWith("/" + admitted.Pin.ContentHash, admitted.Snapshot.Id, StringComparison.Ordinal);
            Assert.DoesNotContain(host.Root.Replace('\\', '/'), admitted.Snapshot.Id, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task APersistedBindingThatIsNotExactlyWellFormed_IsNotAPin()
    {
        var (host, admitted, _, _) = await AdmitAsync();
        using (host)
        {
            var good = admitted.Pin.ToBinding(Actor, admitted.Snapshot.Id);
            Assert.True(KnowledgePin.TryFromBinding(good, out _));

            var hash = new string('a', 64);
            TaskKnowledgeBinding[] bad =
            [
                good with { SchemaVersion = 2 },
                good with { Kind = "skill" },
                good with { Kind = "Everything" },
                good with { Kind = "7" },
                good with { PackId = "../other" },
                good with { PackId = "Acme.Upper" },
                good with { Version = "latest" },
                good with { Version = "1.0" },
                good with { Publisher = "a publisher" },
                good with { KeyId = "key/../id" },
                good with { TenantId = "Tenant-A" },
                good with { TenantId = "" },
                good with { ManifestHash = hash[..63] },
                good with { ManifestHash = hash.ToUpperInvariant() },
                good with { ManifestHash = new string('g', 64) },
                good with { ResourceHashes = new Dictionary<string, string>() },
                good with { ResourceHashes = new Dictionary<string, string> { ["../escape/SKILL.md"] = hash } },
                good with { ResourceHashes = new Dictionary<string, string> { ["nginx-triage/SKILL.md"] = "not-a-hash" } },
                good with { ResourceHashes = new Dictionary<string, string> { ["nginx-triage/scripts/run.sh"] = hash } },
            ];
            foreach (var binding in bad)
            {
                Assert.False(KnowledgePin.TryFromBinding(binding, out _), $"{binding.Kind} {binding.PackId} {binding.Version} {binding.TenantId}");
            }

            Assert.False(KnowledgePin.TryFromBinding(null, out _));
        }
    }
}
