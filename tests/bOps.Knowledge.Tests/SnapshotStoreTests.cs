// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using bOps.Abstractions;

namespace bOps.Knowledge.Tests;

/// <summary>KNOW-01A: content-addressed snapshots, tampering after the pin, and the per-tenant partition of the store.</summary>
public sealed class SnapshotStoreTests : IDisposable
{
    private const string Checklist = "nginx-triage/references/checklist.md";

    private readonly TempDirectory temp = new();
    private readonly KnowledgePackVerifier verifier = new(TestPack.TrustStore(TestPublisher.Acme.Trusted()), TestEngine.Version);

    public void Dispose() => temp.Dispose();

    private VerifiedKnowledgePack Verified(TestPack? pack = null) =>
        verifier.Verify((pack ?? new TestPack()).Build()).Pack ?? throw new InvalidOperationException("The test pack must verify.");

    [Fact]
    public void ASnapshot_IsContentAddressed_PinsEveryFile_AndIsIntact()
    {
        var store = new KnowledgeSnapshotStore(temp.Sub("store"));
        var pack = Verified();

        var snapshot = store.Acquire(TenantId.Local, pack)!;

        Assert.EndsWith(pack.Identity.ContentHash, snapshot.RootPath, StringComparison.Ordinal);
        Assert.Equal(pack.Manifest.Files.Select(file => file.Path).Order(StringComparer.Ordinal), snapshot.Pin.Files.Keys);
        Assert.All(snapshot.Pin.Files, file => Assert.Equal(file.Value, KnowledgePackFormat.Sha256Hex(File.ReadAllBytes(Path.Combine(snapshot.ContentPath, file.Key)))));
        Assert.Null(snapshot.VerifyIntegrity());
        Assert.Equal("local", snapshot.Pin.TenantId);
    }

    [Fact]
    public void AResourceAlteredAfterTheSnapshot_EvenOneNeverRead_IsDetected()
    {
        var store = new KnowledgeSnapshotStore(temp.Sub("store"));
        var snapshot = store.Acquire(TenantId.Local, Verified())!;
        var path = Path.Combine(snapshot.ContentPath, Checklist);

        File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllText(path, "rm -rf /etc/nginx");

        Assert.Equal(KnowledgeAdmissionCode.HashMismatch, snapshot.VerifyIntegrity());
    }

    [Fact]
    public void ADeletedFile_AnAddedFile_AndAnAlteredManifest_AreDetected()
    {
        var store = new KnowledgeSnapshotStore(temp.Sub("store"));
        var snapshot = store.Acquire(TenantId.Local, Verified())!;

        var added = Path.Combine(snapshot.ContentPath, "nginx-triage", "references", "extra.md");
        File.WriteAllText(added, "x");
        Assert.Equal(KnowledgeAdmissionCode.UnexpectedContent, snapshot.VerifyIntegrity());
        File.Delete(added);
        Assert.Null(snapshot.VerifyIntegrity());

        var checklist = Path.Combine(snapshot.ContentPath, Checklist);
        File.SetAttributes(checklist, FileAttributes.Normal);
        File.Delete(checklist);
        Assert.Equal(KnowledgeAdmissionCode.MissingResource, snapshot.VerifyIntegrity());

        var manifest = Path.Combine(snapshot.RootPath, KnowledgeSnapshot.ManifestFileName);
        File.SetAttributes(manifest, FileAttributes.Normal);
        File.AppendAllText(manifest, " ");
        Assert.Equal(KnowledgeAdmissionCode.SnapshotTampered, snapshot.VerifyIntegrity());
    }

    [Fact]
    public void ALinkPlacedInASnapshot_IsDetected()
    {
        var store = new KnowledgeSnapshotStore(temp.Sub("store"));
        var snapshot = store.Acquire(TenantId.Local, Verified())!;
        var outside = temp.Sub("outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(snapshot.ContentPath, "nginx-triage", "references", "elsewhere");

        CreateDirectoryLink(link, outside);

        Assert.Equal(KnowledgeAdmissionCode.LinkNotAllowed, snapshot.VerifyIntegrity());
        Directory.Delete(link); // removes the link, never the target
    }

    [Fact]
    public async Task ChangingTheSourceAfterAdmission_DoesNotChangeTheSnapshot()
    {
        var root = temp.Sub("source");
        var pack = new TestPack();
        var packDirectory = Path.Combine(root, pack.PackId, pack.Version);
        pack.WriteTo(packDirectory);
        var raw = await new DirectoryKnowledgePackSource(root).OpenAsync(new PackReference(pack.PackId, pack.Version), TenantId.Local);
        var snapshot = new KnowledgeSnapshotStore(temp.Sub("store")).Acquire(TenantId.Local, verifier.Verify(raw!).Pack!)!;

        await File.WriteAllTextAsync(Path.Combine(packDirectory, Checklist), "changed at the source");

        Assert.Equal("nginx -t; ss -ltnp :80", await File.ReadAllTextAsync(Path.Combine(snapshot.ContentPath, Checklist)));
        Assert.Null(snapshot.VerifyIntegrity());
    }

    [Fact]
    public void AStoredSnapshotThatNoLongerMatches_IsNotReused_AndIsNotQuietlyRepaired()
    {
        var store = new KnowledgeSnapshotStore(temp.Sub("store"));
        var first = store.Acquire(TenantId.Local, Verified())!;
        var path = Path.Combine(first.ContentPath, Checklist);
        File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllText(path, "tampered");

        Assert.Null(store.Acquire(TenantId.Local, Verified()));
        Assert.Equal("tampered", File.ReadAllText(path)); // evidence left in place for the operator
    }

    [Fact]
    public void TheSameContentForTwoTenants_IsTwoSeparateSnapshots_AndNeitherCanReachTheOther()
    {
        var store = new KnowledgeSnapshotStore(temp.Sub("store"));
        var a = new TenantId("tenant-a");
        var b = new TenantId("tenant-b");

        var forA = store.Acquire(a, Verified())!;
        var forB = store.Acquire(b, Verified())!;

        Assert.NotEqual(forA.RootPath, forB.RootPath);
        Assert.NotEqual(Path.GetDirectoryName(forA.RootPath), Path.GetDirectoryName(forB.RootPath));
        Assert.DoesNotContain("tenant-a", forB.RootPath, StringComparison.Ordinal);
        Assert.Equal("tenant-a", forA.Pin.TenantId);
        Assert.Equal("tenant-b", forB.Pin.TenantId);

        // Damage to one tenant's copy is not visible in, and does not spread to, the other's.
        var path = Path.Combine(forA.ContentPath, Checklist);
        File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllText(path, "tampered");
        Assert.NotNull(forA.VerifyIntegrity());
        Assert.Null(forB.VerifyIntegrity());
    }

    [Fact]
    public async Task ConcurrentAdmissionsOfOneTenantsPack_ProduceOneIntactSnapshot_AndNoLeftovers()
    {
        var store = new KnowledgeSnapshotStore(temp.Sub("store"));
        var pack = Verified();

        var snapshots = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => store.Acquire(TenantId.Local, pack))));

        Assert.All(snapshots, snapshot => Assert.Null(snapshot!.VerifyIntegrity()));
        Assert.Single(snapshots.Select(snapshot => snapshot!.RootPath).Distinct());
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(snapshots[0]!.RootPath)!, ".tmp-*"));
    }

    [Fact]
    public void ThePin_RoundTripsThroughJson_ForAFutureResume()
    {
        var pin = new KnowledgeSnapshotStore(temp.Sub("store")).Acquire(new TenantId("tenant-a"), Verified())!.Pin;

        var restored = KnowledgePin.FromJson(pin.ToJson());

        Assert.Equal(pin.Identity, restored.Identity);
        Assert.Equal(pin.TenantId, restored.TenantId);
        Assert.Equal(pin.Files, restored.Files);
        Assert.Throws<InvalidDataException>(() => KnowledgePin.FromJson("{ \"tenantId\": 4 }"));
        Assert.Throws<InvalidDataException>(() => KnowledgePin.FromJson("not json"));
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        // A junction needs no privilege, and is a reparse point exactly like a symbolic link for the purpose of this check.
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "The test needs to create a junction.");
    }

    [Fact]
    public void TheStoreWritesOnlyWhatTheVerifierHeld_NotWhatTheSourceHolds()
    {
        var (manifest, signature, files) = new TestPack().Parts();
        var raw = TestPack.Raw(manifest, signature, files);
        var verified = verifier.Verify(raw).Pack!;
        files[Checklist] = Encoding.UTF8.GetBytes("mutated after verification, before the snapshot");

        var snapshot = new KnowledgeSnapshotStore(temp.Sub("store")).Acquire(TenantId.Local, verified)!;

        Assert.Equal("nginx -t; ss -ltnp :80", File.ReadAllText(Path.Combine(snapshot.ContentPath, Checklist)));
    }
}
