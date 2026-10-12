// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using bOps.Abstractions;

namespace bOps.Knowledge.Tests;

/// <summary>KNOW-01A: provider admission is by registered instance, the tenant scope is host authority, the trust store holds public keys only.</summary>
public sealed class ProviderAndScopeTests
{
    private static readonly KnowledgeTaskScope LocalScope = KnowledgeTaskScope.ForLocalAgent(Guid.NewGuid());

    private static string Register(KnowledgeProviderRegistry registry, IKnowledgePackSource source, string label = "src", KnowledgeProviderTrust trust = KnowledgeProviderTrust.Trusted) =>
        registry.Register(source, label, trust, [PackKind.Knowledge, PackKind.Skill], [TenantId.Local]);

    // ---- provider admission ----

    [Fact]
    public void AProviderThatWasNeverRegistered_IsRefusedBeforeItIsInvoked_WhateverItClaims()
    {
        var registry = new KnowledgeProviderRegistry();
        Register(registry, new InMemoryPackSource());
        var impostor = new InMemoryPackSource { ClaimedName = "kp.src.1", ClaimsTrusted = true };

        var admission = registry.Admit(impostor, LocalScope);

        Assert.Equal(KnowledgeAdmissionCode.ProviderNotAdmitted, admission.Code);
        Assert.Null(admission.ProviderId);
        Assert.Null(registry.Admit(null, LocalScope).ProviderId);
        Assert.Equal(0, impostor.Opens);
    }

    [Fact]
    public void TwoInstancesOfOneType_GetTwoIds_AndEachIsKnownByReferenceOnly()
    {
        var registry = new KnowledgeProviderRegistry();
        var first = new InMemoryPackSource();
        var second = new InMemoryPackSource();

        var firstId = Register(registry, first);
        var secondId = Register(registry, second);

        Assert.NotEqual(firstId, secondId);
        Assert.Equal(firstId, registry.Admit(first, LocalScope).ProviderId);
        Assert.Equal(secondId, registry.Admit(second, LocalScope).ProviderId);
        Assert.Throws<InvalidOperationException>(() => Register(registry, first, "again"));
    }

    [Fact]
    public void TheIdentityOfAProvider_IsAssignedByTheRegistry_NeverRequestedByTheProvider()
    {
        var registry = new KnowledgeProviderRegistry();
        var source = new InMemoryPackSource { ClaimedName = "bops.context/runtime" };

        var id = Register(registry, source, "files");

        Assert.Equal("kp.files.1", id);
        Assert.DoesNotContain("runtime", id, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => registry.Register(new InMemoryPackSource(), "Bad Label", KnowledgeProviderTrust.Trusted, [PackKind.Skill], [TenantId.Local]));
        Assert.Throws<ArgumentException>(() => registry.Register(new InMemoryPackSource(), "ok", KnowledgeProviderTrust.Trusted, [], [TenantId.Local]));
        Assert.Throws<ArgumentException>(() => registry.Register(new InMemoryPackSource(), "ok", KnowledgeProviderTrust.Trusted, [PackKind.Skill], []));
    }

    [Fact]
    public void ARegistrationOnlyCoversTheTenantsItListed_AndCanBeWithdrawn()
    {
        var registry = new KnowledgeProviderRegistry();
        var source = new InMemoryPackSource();
        var id = Register(registry, source);
        var other = KnowledgeTaskScope.ForLocalAgent(Guid.NewGuid());
        var tenantB = KnowledgeTaskScope.FromAuthority(new ActorIdTenantAuthority(), ActorIdentity.FromOperatingSystemUser("tenant-b"), Guid.NewGuid())!;

        Assert.Equal(KnowledgeAdmissionCode.Admitted, registry.Admit(source, other).Code);
        Assert.Equal(KnowledgeAdmissionCode.TenantNotAllowed, registry.Admit(source, tenantB).Code);

        Assert.True(registry.Unregister(id));
        Assert.False(registry.Unregister(id));
        Assert.Equal(KnowledgeAdmissionCode.ProviderNotAdmitted, registry.Admit(source, other).Code);
    }

    [Fact]
    public void AnUntrustedProvider_NeverServesASkillPack_ButAKnowledgePackIsFine()
    {
        var registry = new KnowledgeProviderRegistry();
        var source = new InMemoryPackSource();
        Register(registry, source, trust: KnowledgeProviderTrust.Untrusted);

        var admission = registry.Admit(source, LocalScope);

        Assert.True(admission.MayServe(PackKind.Knowledge));
        Assert.False(admission.MayServe(PackKind.Skill));
        Assert.False(admission.MayServe(PackKind.Experience)); // not listed by the registration
    }

    [Fact]
    public async Task ConcurrentRegistrationsAndAdmissions_KeepEveryIdUnique()
    {
        var registry = new KnowledgeProviderRegistry();
        var sources = Enumerable.Range(0, 64).Select(_ => new InMemoryPackSource()).ToArray();

        var ids = await Task.WhenAll(sources.Select(source => Task.Run(() => Register(registry, source))));

        Assert.Equal(64, ids.Distinct().Count());
        Assert.All(sources.Zip(ids), pair => Assert.Equal(pair.Second, registry.Admit(pair.First, LocalScope).ProviderId));
    }

    // ---- tenant scope is host authority ----

    [Fact]
    public void AScope_CannotBeConstructedOrCopiedWithAnotherTenant()
    {
        Assert.All(typeof(KnowledgeTaskScope).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), constructor => Assert.True(constructor.IsPrivate));
        Assert.Null(typeof(KnowledgeTaskScope).GetMethod("<Clone>$")); // a class, not a record: there is no `with`
        Assert.All(typeof(KnowledgeTaskScope).GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void ATenantComesFromTheHostAuthority_AnUnknownActorGetsNoScope()
    {
        var authority = new ActorIdTenantAuthority();

        var known = KnowledgeTaskScope.FromAuthority(authority, ActorIdentity.FromOperatingSystemUser("tenant-a"), Guid.NewGuid());
        var unknown = KnowledgeTaskScope.FromAuthority(authority, ActorIdentity.FromOperatingSystemUser("someone"), Guid.NewGuid());

        Assert.Equal(new TenantId("tenant-a"), known!.Tenant);
        Assert.Null(unknown);
        Assert.Equal(TenantId.Local, KnowledgeTaskScope.ForLocalAgent(Guid.NewGuid()).Tenant);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Tenant-A")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("a b")]
    public void ATenantIdThatIsNotAnIdentifier_IsRefused(string value)
    {
        Assert.Throws<ArgumentException>(() => new TenantId(value));
    }

    [Fact]
    public void TheModelFacingTool_HasNoTenantArgument()
    {
        // The knowledge-read declaration lives in bOps.AgentFramework; the contract that matters here is that no pack API takes a tenant
        // from anything but a KnowledgeTaskScope, which only the host's authority can issue.
        var publicMembersTakingATenantString = typeof(KnowledgeAdmissionService).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string) && parameter.Name!.Contains("tenant", StringComparison.OrdinalIgnoreCase)));
        Assert.Empty(publicMembersTakingATenantString);
    }

    // ---- the trust store holds public keys only ----

    [Fact]
    public void ATrustStore_RefusesAPrivateKey_AWeakKey_AndADuplicate()
    {
        var acme = TestPublisher.Acme.Trusted();

        Assert.Throws<ArgumentException>(() => new KnowledgeTrustStore([acme with { PublicKeyPem = TestPublisher.Acme.PrivateKeyPem() }]));
        Assert.Throws<ArgumentException>(() => new KnowledgeTrustStore([TestPublisher.WeakKey("weak", "weak-1")]));
        Assert.Throws<ArgumentException>(() => new KnowledgeTrustStore([acme, acme]));
        Assert.Throws<ArgumentException>(() => new KnowledgeTrustStore([acme with { PublicKeyPem = "not a key" }]));
        Assert.Throws<ArgumentException>(() => new KnowledgeTrustStore([acme with { Publisher = "bad publisher" }]));
    }

    [Fact]
    public void ATrustStore_ReadFromJson_KeepsKeysAndRevocations_AndRefusesAnythingMalformed()
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "keys": [ { "publisher": "acme", "keyId": "acme-2026", "publicKeyPem": {{System.Text.Json.JsonSerializer.Serialize(TestPublisher.Acme.Trusted().PublicKeyPem)}}, "revoked": false } ],
              "revokedPublishers": [ "globex" ]
            }
            """;

        var store = KnowledgeTrustStore.FromJson(json);

        Assert.Equal(KeyStatus.Trusted, store.Find("acme", "acme-2026").Status);
        Assert.Equal(KeyStatus.PublisherRevoked, store.Find("globex", "globex-2026").Status);
        Assert.Equal(KeyStatus.Unknown, store.Find("acme", "other").Status);
        Assert.Throws<InvalidDataException>(() => KnowledgeTrustStore.FromJson("{ \"schemaVersion\": 2, \"keys\": [], \"revokedPublishers\": [] }"));
        Assert.Throws<InvalidDataException>(() => KnowledgeTrustStore.FromJson("{ \"schemaVersion\": 1, \"keys\": [], \"revokedPublishers\": [], \"trustEveryone\": true }"));
        Assert.Throws<InvalidDataException>(() => KnowledgeTrustStore.FromJson("nonsense"));
        Assert.Equal(KeyStatus.Unknown, KnowledgeTrustStore.Empty.Find("acme", "acme-2026").Status);
    }
}
