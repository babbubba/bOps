// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using bOps.Abstractions;

namespace bOps.Knowledge.Tests;

/// <summary>KNOW-01A: the admission order, the tenant partitions, the resume pin, and the audit of every outcome.</summary>
public sealed class AdmissionServiceTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");

    private static KnowledgeTaskScope ScopeOf(TenantId tenant) =>
        KnowledgeTaskScope.FromAuthority(new ActorIdTenantAuthority(), ActorIdentity.FromOperatingSystemUser(tenant.Value), Guid.NewGuid())!;

    private static PackReference ReferenceOf(TestPack pack) => new(pack.PackId, pack.Version);

    [Fact]
    public async Task AGrantedSignedPack_FromARegisteredProvider_IsAdmitted_PinnedAndAudited()
    {
        var pack = new TestPack();
        using var host = new KnowledgeTestHost([new PackGrant(TenantA, ReferenceOf(pack), PackKind.Skill)], tenants: [TenantA]);
        host.Source.Add(TenantA, pack.PackId, pack.Version, () => pack.Build());

        var result = await host.AdmitAsync(host.Request(ScopeOf(TenantA), ReferenceOf(pack)));

        Assert.Equal(KnowledgeAdmissionCode.Admitted, result.Code);
        var admitted = result.Pack!;
        Assert.Equal(host.ProviderId, admitted.ProviderId);
        Assert.Equal("tenant-a", admitted.Pin.TenantId);
        Assert.Equal(PackKind.Skill, admitted.Identity.Kind);
        Assert.Null(admitted.Snapshot.VerifyIntegrity());

        var audit = Assert.IsType<KnowledgePackAdmissionAuditEvent>(Assert.Single(host.Audit.Events));
        Assert.Equal((KnowledgeAdmissionCode.Admitted, "tenant-a", host.ProviderId, "Skill", "acme", "acme-2026"), (audit.Code, audit.TenantId, audit.ProviderId, audit.PackKind, audit.Publisher, audit.KeyId));
        Assert.Equal(admitted.Identity.ContentHash, audit.ContentHash);
        var persisted = System.Text.Json.JsonSerializer.Serialize<AuditEvent>(audit);
        Assert.DoesNotContain("ss -ltnp", persisted, StringComparison.Ordinal); // never content
        Assert.DoesNotContain("PRIVATE", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAGrant_NothingIsReadFromTheSource_AndTheRefusalIsAudited()
    {
        var pack = new TestPack();
        using var host = new KnowledgeTestHost([new PackGrant(TenantB, ReferenceOf(pack), PackKind.Skill)], tenants: [TenantA, TenantB]);
        host.Source.Add(TenantA, pack.PackId, pack.Version, () => pack.Build());

        var result = await host.AdmitAsync(host.Request(ScopeOf(TenantA), ReferenceOf(pack)));

        Assert.Equal(KnowledgeAdmissionCode.NotEntitled, result.Code);
        Assert.Equal(0, host.Source.Opens);
        Assert.Equal(KnowledgeAdmissionCode.NotEntitled, ((KnowledgePackAdmissionAuditEvent)Assert.Single(host.Audit.Events)).Code);
    }

    [Fact]
    public async Task AnUnregisteredSource_IsRefusedBeforeAnyByteIsRead_WhateverItClaims()
    {
        var pack = new TestPack();
        using var host = new KnowledgeTestHost([new PackGrant(TenantA, ReferenceOf(pack), PackKind.Skill)], tenants: [TenantA]);
        var forged = new InMemoryPackSource { ClaimedName = host.ProviderId, ClaimsTrusted = true };
        forged.Add(TenantA, pack.PackId, pack.Version, () => pack.Build());

        var result = await host.AdmitAsync(host.Request(ScopeOf(TenantA), ReferenceOf(pack), source: forged));

        Assert.Equal(KnowledgeAdmissionCode.ProviderNotAdmitted, result.Code);
        Assert.Equal(0, forged.Opens);
        Assert.Null(((KnowledgePackAdmissionAuditEvent)Assert.Single(host.Audit.Events)).ProviderId); // the forged id is not echoed
    }

    [Fact]
    public async Task AnUntrustedProvider_CannotSupplyASkillPack_ButCanSupplyAKnowledgePack()
    {
        var skill = new TestPack();
        var knowledge = new TestPack { PackId = "acme.reference", Kind = PackKind.Knowledge };
        using var host = new KnowledgeTestHost(
            [new PackGrant(TenantA, ReferenceOf(skill), PackKind.Skill), new PackGrant(TenantA, ReferenceOf(knowledge), PackKind.Knowledge)],
            trust: KnowledgeProviderTrust.Untrusted, tenants: [TenantA]);
        host.Source.Add(TenantA, skill.PackId, skill.Version, () => skill.Build()).Add(TenantA, knowledge.PackId, knowledge.Version, () => knowledge.Build());

        var refused = await host.AdmitAsync(host.Request(ScopeOf(TenantA), ReferenceOf(skill)));
        var admitted = await host.AdmitAsync(host.Request(ScopeOf(TenantA), ReferenceOf(knowledge)));

        Assert.Equal(KnowledgeAdmissionCode.KindNotAllowed, refused.Code);
        Assert.Equal(KnowledgeAdmissionCode.Admitted, admitted.Code);
        Assert.Equal(1, host.Source.Opens); // the refused skill pack was never even read
    }

    [Fact]
    public async Task AKindThatDiffersFromTheGrant_IsNotEntitled_AndAWrongIdentityIsRefused()
    {
        var pack = new TestPack { Kind = PackKind.Experience };
        var reference = ReferenceOf(pack);
        using var host = new KnowledgeTestHost([new PackGrant(TenantA, reference, PackKind.Skill)], tenants: [TenantA]);
        host.Source.Add(TenantA, pack.PackId, pack.Version, () => pack.Build());
        Assert.Equal(KnowledgeAdmissionCode.NotEntitled, (await host.AdmitAsync(host.Request(ScopeOf(TenantA), reference))).Code); // signed as Experience, granted as Skill

        var other = new TestPack { PackId = "acme.other" };
        using var second = new KnowledgeTestHost([new PackGrant(TenantA, reference, PackKind.Skill)], tenants: [TenantA]);
        second.Source.Add(TenantA, pack.PackId, pack.Version, () => other.Build()); // asked for one pack, handed another validly signed one
        Assert.Equal(KnowledgeAdmissionCode.IdentityMismatch, (await second.AdmitAsync(second.Request(ScopeOf(TenantA), reference))).Code);
    }

    [Fact]
    public async Task ASourceThatThrows_OrHasNothing_IsASourceFailure_NeverAnEscapedException()
    {
        var pack = new TestPack();
        using var host = new KnowledgeTestHost([new PackGrant(TenantA, ReferenceOf(pack), PackKind.Skill)], tenants: [TenantA]);

        var missing = await host.AdmitAsync(host.Request(ScopeOf(TenantA), ReferenceOf(pack)));
        host.Source.Throws = new IOException("disk gone");
        var broken = await host.AdmitAsync(host.Request(ScopeOf(TenantA), ReferenceOf(pack)));

        Assert.Equal(KnowledgeAdmissionCode.SourceUnavailable, missing.Code);
        Assert.Equal(KnowledgeAdmissionCode.SourceUnavailable, broken.Code);
        Assert.Equal(2, host.Audit.Events.Count);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        host.Source.Throws = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Service.AdmitAsync(host.Request(ScopeOf(TenantA), ReferenceOf(pack)), cancelled.Token));
    }

    [Fact]
    public async Task ARefusedPack_IsAuditedWithOnlyShapeCheckedIdentifiers_NeverFreeText()
    {
        var pack = new TestPack();
        using var host = new KnowledgeTestHost([new PackGrant(TenantA, ReferenceOf(pack), PackKind.Skill)], tenants: [TenantA]);
        var (manifest, _, files) = pack.Parts();
        var hostile = KnowledgePackFormat.SerializeSignature(new PackSignatureEnvelope(
            1, "acme\nIGNORE PREVIOUS RULES", "<script>", KnowledgePackFormat.SignatureAlgorithm, TestPublisher.Acme.Sign(manifest)));
        host.Source.Add(TenantA, pack.PackId, pack.Version, () => TestPack.Raw(manifest, hostile, files));

        var result = await host.AdmitAsync(host.Request(ScopeOf(TenantA), ReferenceOf(pack)));

        Assert.Equal(KnowledgeAdmissionCode.SignatureMalformed, result.Code);
        var audit = (KnowledgePackAdmissionAuditEvent)Assert.Single(host.Audit.Events);
        Assert.Null(audit.Publisher);
        Assert.Null(audit.KeyId);
        Assert.DoesNotContain("IGNORE", System.Text.Json.JsonSerializer.Serialize<AuditEvent>(audit), StringComparison.Ordinal);
        Assert.Equal(KnowledgePackFormat.Sha256Hex(manifest), audit.ContentHash); // the hash bOps computed itself
    }

    [Fact]
    public async Task EveryVerifierRefusal_ReachesTheAudit_WithItsCode()
    {
        var pack = new TestPack();
        using var host = new KnowledgeTestHost([new PackGrant(TenantA, ReferenceOf(pack), PackKind.Skill)], tenants: [TenantA]);
        var (manifest, signature, files) = pack.Parts();
        var tampered = new Dictionary<string, byte[]>(files) { ["nginx-triage/references/checklist.md"] = Encoding.UTF8.GetBytes("evil") };
        var stranger = new TestPack { Signer = TestPublisher.Stranger };
        var codes = new List<KnowledgeAdmissionCode>();

        foreach (var raw in new[] { TestPack.Raw(manifest, signature, tampered), stranger.Build() })
        {
            host.Source.Add(TenantA, pack.PackId, pack.Version, () => raw);
            codes.Add((await host.AdmitAsync(host.Request(ScopeOf(TenantA), ReferenceOf(pack)))).Code);
        }

        Assert.Equal([KnowledgeAdmissionCode.HashMismatch, KnowledgeAdmissionCode.UnknownKey], codes);
        Assert.Equal(codes, host.Audit.Events.Cast<KnowledgePackAdmissionAuditEvent>().Select(audit => audit.Code));
    }

    // ---- resume: a pin names exact content ----

    [Fact]
    public async Task APinSerializedAtTaskStart_ReadmitsExactlyThatContent_AndRefusesARepublishedOne()
    {
        var pack = new TestPack();
        var reference = ReferenceOf(pack);
        using var host = new KnowledgeTestHost([new PackGrant(TenantA, reference, PackKind.Skill)], tenants: [TenantA]);
        host.Source.Add(TenantA, pack.PackId, pack.Version, () => pack.Build());
        var first = (await host.AdmitAsync(host.Request(ScopeOf(TenantA), reference))).Pack!;
        var stored = first.Pin.ToJson(); // what a task store would hold

        var same = await host.AdmitAsync(host.Request(ScopeOf(TenantA), reference, KnowledgePin.FromJson(stored)));
        Assert.Equal(KnowledgeAdmissionCode.Admitted, same.Code);
        Assert.Equal(first.Identity.ContentHash, same.Pack!.Identity.ContentHash);

        var republished = new TestPack();
        republished.Files["nginx-triage/references/checklist.md"] = "a different checklist under the same version";
        host.Source.Add(TenantA, pack.PackId, pack.Version, () => republished.Build());
        var changed = await host.AdmitAsync(host.Request(ScopeOf(TenantA), reference, KnowledgePin.FromJson(stored)));
        Assert.Equal(KnowledgeAdmissionCode.PinMismatch, changed.Code);

        // A pin of another tenant names nothing for this one.
        var foreign = KnowledgePin.FromJson(stored.Replace("tenant-a", "tenant-b", StringComparison.Ordinal));
        host.Source.Add(TenantA, pack.PackId, pack.Version, () => pack.Build());
        Assert.Equal(KnowledgeAdmissionCode.PinMismatch, (await host.AdmitAsync(host.Request(ScopeOf(TenantA), reference, foreign))).Code);
    }

    [Fact]
    public async Task ARevokedKey_StopsNewAdmissionsAndResumes_ButNotAnAlreadyHeldSnapshot()
    {
        var pack = new TestPack();
        var reference = ReferenceOf(pack);
        var grants = new[] { new PackGrant(TenantA, reference, PackKind.Skill) };
        using var trusting = new KnowledgeTestHost(grants, tenants: [TenantA]);
        trusting.Source.Add(TenantA, pack.PackId, pack.Version, () => pack.Build());
        var held = (await trusting.AdmitAsync(trusting.Request(ScopeOf(TenantA), reference))).Pack!;

        using var afterRevocation = new KnowledgeTestHost(grants, keys: [TestPublisher.Acme.Trusted(revoked: true)], tenants: [TenantA]);
        afterRevocation.Source.Add(TenantA, pack.PackId, pack.Version, () => pack.Build());
        var resumed = await afterRevocation.AdmitAsync(afterRevocation.Request(ScopeOf(TenantA), reference, held.Pin));

        Assert.Equal(KnowledgeAdmissionCode.KeyRevoked, resumed.Code);
        Assert.Null(held.Snapshot.VerifyIntegrity()); // the running task keeps serving what it verified
    }

    // ---- tenant isolation ----

    [Fact]
    public async Task ExperienceOfOneTenant_IsNotReachableByAnother_EvenWithItsPinAndTheSameSource()
    {
        var experience = new TestPack { PackId = "acme.incident-history", Kind = PackKind.Experience };
        var reference = ReferenceOf(experience);
        using var host = new KnowledgeTestHost([new PackGrant(TenantA, reference, PackKind.Experience)], tenants: [TenantA, TenantB]);
        host.Source.Add(TenantA, experience.PackId, experience.Version, () => experience.Build());
        host.Source.Add(TenantB, experience.PackId, experience.Version, () => experience.Build()); // even if the source would serve it
        var forA = (await host.AdmitAsync(host.Request(ScopeOf(TenantA), reference))).Pack!;
        var opensBefore = host.Source.Opens;

        var forB = await host.AdmitAsync(host.Request(ScopeOf(TenantB), reference));
        var forBWithAsPin = await host.AdmitAsync(host.Request(ScopeOf(TenantB), reference, forA.Pin));

        Assert.Equal(KnowledgeAdmissionCode.NotEntitled, forB.Code);
        Assert.Equal(KnowledgeAdmissionCode.NotEntitled, forBWithAsPin.Code);
        Assert.Equal(opensBefore, host.Source.Opens);
        Assert.Null(forB.Pack);
    }

    [Fact]
    public async Task TwoTenantsAdmittingConcurrently_ThePackOfTheSameIdAndVersionWithDifferentContent_NeverMix()
    {
        var packA = new TestPack();
        packA.Files["nginx-triage/references/checklist.md"] = "tenant A private checklist";
        var packB = new TestPack();
        packB.Files["nginx-triage/references/checklist.md"] = "tenant B private checklist";
        var reference = ReferenceOf(packA);
        using var host = new KnowledgeTestHost(
            [new PackGrant(TenantA, reference, PackKind.Skill), new PackGrant(TenantB, reference, PackKind.Skill)], tenants: [TenantA, TenantB]);
        var rawA = packA.Build(); // signed once: the signing key is not shared between threads
        var rawB = packB.Build();
        host.Source.Add(TenantA, packA.PackId, packA.Version, () => rawA).Add(TenantB, packB.PackId, packB.Version, () => rawB);

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(index => Task.Run(async () =>
        {
            var tenant = index % 2 == 0 ? TenantA : TenantB;
            return (Tenant: tenant, Pack: (await host.AdmitAsync(host.Request(ScopeOf(tenant), reference))).Pack!);
        })));

        Assert.All(results, entry =>
        {
            var text = File.ReadAllText(Path.Combine(entry.Pack.Snapshot.ContentPath, "nginx-triage", "references", "checklist.md"));
            Assert.Equal(entry.Tenant == TenantA ? "tenant A private checklist" : "tenant B private checklist", text);
            Assert.Equal(entry.Tenant.Value, entry.Pack.Pin.TenantId);
        });
        Assert.Equal(2, results.Select(entry => entry.Pack.Snapshot.RootPath).Distinct().Count());
        Assert.NotEqual(
            results.First(entry => entry.Tenant == TenantA).Pack.Identity.ContentHash,
            results.First(entry => entry.Tenant == TenantB).Pack.Identity.ContentHash);
        var audits = host.Audit.Events.Cast<KnowledgePackAdmissionAuditEvent>().ToArray();
        Assert.Equal(40, audits.Length);
        Assert.Equal(20, audits.Count(audit => audit.TenantId == "tenant-a"));
    }

    [Fact]
    public async Task TheLocalAgentIsATenantLikeAnyOther_AndSeesNothingNotGrantedToIt()
    {
        var pack = new TestPack();
        using var host = new KnowledgeTestHost([new PackGrant(TenantA, ReferenceOf(pack), PackKind.Skill)], tenants: [TenantA, TenantId.Local]);
        host.Source.Add(TenantA, pack.PackId, pack.Version, () => pack.Build());
        host.Source.Add(TenantId.Local, pack.PackId, pack.Version, () => pack.Build());

        var local = await host.AdmitAsync(host.Request(KnowledgeTaskScope.ForLocalAgent(Guid.NewGuid()), ReferenceOf(pack)));

        Assert.Equal(KnowledgeAdmissionCode.NotEntitled, local.Code);
        Assert.Equal(0, host.Source.Opens);
    }

    // ---- directory source ----

    [Fact]
    public async Task APackOnDisk_ThroughTheDirectorySource_IsAdmittedLikeAnyOther_AndATenantPartitionIsSeparate()
    {
        using var temp = new TempDirectory();
        var pack = new TestPack();
        pack.WriteTo(Path.Combine(temp.Path, "shared", pack.PackId, pack.Version));
        pack.WriteTo(Path.Combine(temp.Path, "tenanted", "tenant-a", pack.PackId, pack.Version));
        var reference = ReferenceOf(pack);
        var verifier = new KnowledgePackVerifier(TestPack.TrustStore(TestPublisher.Acme.Trusted()), TestEngine.Version);

        var shared = await new DirectoryKnowledgePackSource(Path.Combine(temp.Path, "shared")).OpenAsync(reference, TenantA);
        var partitionedA = await new DirectoryKnowledgePackSource(Path.Combine(temp.Path, "tenanted"), partitionByTenant: true).OpenAsync(reference, TenantA);
        var partitionedB = await new DirectoryKnowledgePackSource(Path.Combine(temp.Path, "tenanted"), partitionByTenant: true).OpenAsync(reference, TenantB);

        Assert.Equal(KnowledgeAdmissionCode.Admitted, verifier.Verify(shared!).Code);
        Assert.Equal(KnowledgeAdmissionCode.Admitted, verifier.Verify(partitionedA!).Code);
        Assert.Null(partitionedB); // a tenant has its own folder; there is nothing to fall back to
    }

    [Fact]
    public async Task TheDirectorySource_ReportsLinks_StopsAtTheBounds_AndRefusesAnUnsafeReference()
    {
        using var temp = new TempDirectory();
        var pack = new TestPack();
        var directory = Path.Combine(temp.Path, pack.PackId, pack.Version);
        pack.WriteTo(directory);
        var outside = temp.Sub("outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.md"), "outside the pack");
        LinkDirectory(Path.Combine(directory, "nginx-triage", "linked"), outside);
        var reference = ReferenceOf(pack);
        var verifier = new KnowledgePackVerifier(TestPack.TrustStore(TestPublisher.Acme.Trusted()), TestEngine.Version);

        var linked = await new DirectoryKnowledgePackSource(temp.Path).OpenAsync(reference, TenantA);
        var bounded = await new DirectoryKnowledgePackSource(temp.Path, limits: new KnowledgePackLimits(MaxFiles: 1)).OpenAsync(reference, TenantA);

        Assert.NotEmpty(linked!.LinkedEntries);
        Assert.DoesNotContain(linked.Files.Keys, path => path.Contains("secret", StringComparison.Ordinal)); // the link was reported, never followed
        Assert.Equal(KnowledgeAdmissionCode.LinkNotAllowed, verifier.Verify(linked).Code);
        Assert.True(bounded!.ExceededLimits);
        Assert.Equal(KnowledgeAdmissionCode.LimitExceeded, verifier.Verify(bounded).Code);
        Assert.Throws<ArgumentException>(() => new PackReference("..", "1.0.0"));
        Assert.Throws<ArgumentException>(() => new PackReference("acme.pack", "../1.0.0"));
        Directory.Delete(Path.Combine(directory, "nginx-triage", "linked")); // removes the link, never its target
    }

    private static void LinkDirectory(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "The test needs to create a junction.");
    }
}
