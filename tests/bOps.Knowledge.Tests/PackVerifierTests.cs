// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using bOps.Abstractions;

namespace bOps.Knowledge.Tests;

/// <summary>KNOW-01A: signature, manifest and byte-level verification. Keys are throw-away test keys held in memory.</summary>
public sealed class PackVerifierTests
{
    private static KnowledgePackVerifier Verifier(params PublisherKey[] keys) =>
        new(TestPack.TrustStore(keys.Length == 0 ? [TestPublisher.Acme.Trusted()] : keys), TestEngine.Version);

    // ---- signature: valid, altered, unknown, revoked ----

    [Fact]
    public void AValidSignedPack_IsAdmitted_WithItsCompleteIdentity()
    {
        var raw = new TestPack().Build();

        var verification = Verifier().Verify(raw);

        Assert.Equal(KnowledgeAdmissionCode.Admitted, verification.Code);
        var identity = verification.Pack!.Identity;
        Assert.Equal(("acme.nginx-runbooks", "1.0.0", PackKind.Skill, "acme", "acme-2026"), (identity.PackId, identity.Version, identity.Kind, identity.Publisher, identity.KeyId));
        // The content identity is the hash of the signed manifest, computed by bOps, not a value the pack declares.
        Assert.Equal(KnowledgePackFormat.Sha256Hex(raw.ManifestBytes.Span), identity.ContentHash);
    }

    [Fact]
    public void AManifestAlteredAfterSigning_IsRefusedAsAnInvalidSignature()
    {
        var (manifest, signature, files) = new TestPack().Parts();
        var altered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace("\"1.0.0\"", "\"1.0.1\"", StringComparison.Ordinal));

        var verification = Verifier().Verify(TestPack.Raw(altered, signature, files));

        Assert.Equal(KnowledgeAdmissionCode.InvalidSignature, verification.Code);
        Assert.Null(verification.Pack);
        Assert.Null(verification.Kind); // an unverified manifest is not believed, not even for the audit
    }

    [Fact]
    public void ASignatureWithFlippedBits_IsRefused()
    {
        var pack = new TestPack();
        var (manifest, _, files) = pack.Parts();
        var good = Convert.FromBase64String(TestPublisher.Acme.Sign(manifest));
        good[10] ^= 0x01;
        var envelope = KnowledgePackFormat.SerializeSignature(new PackSignatureEnvelope(
            1, "acme", "acme-2026", KnowledgePackFormat.SignatureAlgorithm, Convert.ToBase64String(good)));

        Assert.Equal(KnowledgeAdmissionCode.InvalidSignature, Verifier().Verify(TestPack.Raw(manifest, envelope, files)).Code);
    }

    [Theory]
    [InlineData("not base64 !!")]
    [InlineData("")]
    public void ASignatureThatIsNotBase64OrIsEmpty_IsMalformed(string signature)
    {
        var (manifest, _, files) = new TestPack().Parts();
        var envelope = KnowledgePackFormat.SerializeSignature(new PackSignatureEnvelope(
            1, "acme", "acme-2026", KnowledgePackFormat.SignatureAlgorithm, signature));

        Assert.Equal(KnowledgeAdmissionCode.SignatureMalformed, Verifier().Verify(TestPack.Raw(manifest, envelope, files)).Code);
    }

    [Fact]
    public void AnUnsignedPack_IsRefused()
    {
        var raw = new RawKnowledgePack
        {
            ManifestBytes = new TestPack().Parts().Manifest,
            Files = new Dictionary<string, ReadOnlyMemory<byte>>(),
        };

        Assert.Equal(KnowledgeAdmissionCode.SignatureMalformed, Verifier().Verify(raw).Code);
    }

    [Theory]
    [InlineData("rsa-md5")]
    [InlineData("none")]
    public void AnUnsupportedAlgorithm_IsRefused_NotDowngraded(string algorithm)
    {
        var (manifest, _, files) = new TestPack().Parts();
        var envelope = KnowledgePackFormat.SerializeSignature(new PackSignatureEnvelope(1, "acme", "acme-2026", algorithm, TestPublisher.Acme.Sign(manifest)));

        Assert.Equal(KnowledgeAdmissionCode.SignatureMalformed, Verifier().Verify(TestPack.Raw(manifest, envelope, files)).Code);
    }

    [Fact]
    public void AKeyThatIsNotInTheTrustStore_IsRefused_EvenWhenTheSignatureIsMathematicallyValid()
    {
        var pack = new TestPack { Signer = TestPublisher.Stranger };

        var verification = Verifier().Verify(pack.Build());

        Assert.Equal(KnowledgeAdmissionCode.UnknownKey, verification.Code);
        Assert.Equal(("stranger", "stranger-1"), (verification.Publisher, verification.KeyId)); // identifiers that passed a shape check, for the audit
    }

    [Fact]
    public void AnImpostorUsingATrustedKeyId_ButAnotherKey_IsRefused()
    {
        var pack = new TestPack { Signer = TestPublisher.AcmeImpostor };

        Assert.Equal(KnowledgeAdmissionCode.InvalidSignature, Verifier().Verify(pack.Build()).Code);
    }

    [Fact]
    public void ARevokedKey_AndARevokedPublisher_AreRefused_WhileTheOtherPublishersKeyStillVerifies()
    {
        var acme = new TestPack();
        var globex = new TestPack { Signer = TestPublisher.Globex, PackId = "globex.pack" };
        var keys = new[] { TestPublisher.Acme.Trusted(), TestPublisher.Globex.Trusted() };

        var keyRevoked = new KnowledgePackVerifier(
            TestPack.TrustStore(TestPublisher.Acme.Trusted(revoked: true), TestPublisher.Globex.Trusted()), TestEngine.Version);
        var publisherRevoked = new KnowledgePackVerifier(new KnowledgeTrustStore(keys, ["acme"]), TestEngine.Version);

        Assert.Equal(KnowledgeAdmissionCode.KeyRevoked, keyRevoked.Verify(acme.Build()).Code);
        Assert.Equal(KnowledgeAdmissionCode.PublisherRevoked, publisherRevoked.Verify(acme.Build()).Code);
        Assert.Equal(KnowledgeAdmissionCode.Admitted, publisherRevoked.Verify(globex.Build()).Code);
    }

    // ---- the signed manifest is the truth: resources are checked byte for byte ----

    [Fact]
    public void AResourceAlteredAfterSigning_IsRefused_EvenWhenItWasNeverRead()
    {
        var (manifest, signature, files) = new TestPack().Parts();
        files["nginx-triage/references/checklist.md"] = Encoding.UTF8.GetBytes("rm -rf /etc/nginx");

        Assert.Equal(KnowledgeAdmissionCode.HashMismatch, Verifier().Verify(TestPack.Raw(manifest, signature, files)).Code);
    }

    [Fact]
    public void AMissingFile_AndAnExtraFile_AreRefused()
    {
        var (manifest, signature, files) = new TestPack().Parts();
        var missing = new Dictionary<string, byte[]>(files);
        missing.Remove("nginx-triage/references/checklist.md");
        var extra = new Dictionary<string, byte[]>(files) { ["nginx-triage/references/unlisted.md"] = [65] };

        Assert.Equal(KnowledgeAdmissionCode.MissingResource, Verifier().Verify(TestPack.Raw(manifest, signature, missing)).Code);
        Assert.Equal(KnowledgeAdmissionCode.UnexpectedContent, Verifier().Verify(TestPack.Raw(manifest, signature, extra)).Code);
    }

    [Theory]
    [InlineData("../evil.md")]
    [InlineData("nginx-triage/../../evil.md")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/evil.md")]
    [InlineData("nginx-triage\\references\\checklist.md")]
    [InlineData("nginx-triage//references/checklist.md")]
    [InlineData("nginx-triage/./references/checklist.md")]
    [InlineData("nginx-triage/references/check list.md")]
    [InlineData("nginx-triage/references/checklist.md.")]
    [InlineData("nginx-triage/references/NUL.md")]
    [InlineData("nginx-triage/references/checklist.md:stream")]
    [InlineData("Nginx-Triage/SKILL.md")]
    public void APathThatIsNotAPlainRelativeFilePath_IsRefused_EvenWhenItIsSigned(string path)
    {
        var pack = new TestPack();
        pack.Files[path] = "x";

        var verification = Verifier().Verify(pack.Build());

        Assert.Equal(KnowledgeAdmissionCode.UnsafePath, verification.Code);
    }

    [Fact]
    public void AnUnlistedFileWithATraversalPath_IsRefusedAsUnsafe_NotJustAsUnexpected()
    {
        var (manifest, signature, files) = new TestPack().Parts();
        var traversal = new Dictionary<string, byte[]>(files) { ["../outside.md"] = [65] };

        Assert.Equal(KnowledgeAdmissionCode.UnsafePath, Verifier().Verify(TestPack.Raw(manifest, signature, traversal)).Code);
    }

    [Fact]
    public void TwoPathsDifferingOnlyByCase_AreRefused_BecauseTheyAliasOnWindowsAndMacOs()
    {
        var pack = new TestPack();
        pack.Files["nginx-triage/references/a.md"] = "1";
        pack.Files["nginx-triage/references/A.md"] = "2";

        Assert.Equal(KnowledgeAdmissionCode.UnsafePath, Verifier().Verify(pack.Build()).Code);
    }

    // ---- a pack is data: no scripts, no executables, no key material ----

    [Theory]
    [InlineData("nginx-triage/scripts/purge.ps1")]
    [InlineData("nginx-triage/scripts/purge.md")]
    [InlineData("nginx-triage/purge.sh")]
    [InlineData("nginx-triage/tool.exe")]
    [InlineData("nginx-triage/lib.dll")]
    [InlineData("nginx-triage/run.py")]
    [InlineData("nginx-triage/task.cmd")]
    [InlineData("nginx-triage/noextension")]
    [InlineData("nginx-triage/references/SKILL.md")]
    [InlineData("nginx-triage/skill.MD.exe")]
    public void AScriptOrExecutableOrUnlistedFileType_RejectsTheWholePack(string path)
    {
        var pack = new TestPack();
        pack.Files[path] = "echo pwned";

        Assert.Equal(KnowledgeAdmissionCode.ForbiddenContent, Verifier().Verify(pack.Build()).Code);
    }

    [Fact]
    public void APackCarryingPrivateKeyMaterial_IsRefused()
    {
        var pack = new TestPack();
        pack.Files["nginx-triage/references/key.txt"] = TestPublisher.Acme.PrivateKeyPem();

        Assert.Equal(KnowledgeAdmissionCode.ForbiddenContent, Verifier().Verify(pack.Build()).Code);
    }

    [Fact]
    public void ABinaryOrNonUtf8File_IsRefused()
    {
        var latin1 = new TestPack();
        latin1.RawFiles["nginx-triage/references/blob.txt"] = [0xFF, 0xFE, 0x41, 0x80]; // not valid UTF-8
        var nul = new TestPack();
        nul.Files["nginx-triage/references/blob.txt"] = "text\0with nul";

        Assert.Equal(KnowledgeAdmissionCode.InvalidContent, Verifier().Verify(latin1.Build()).Code);
        Assert.Equal(KnowledgeAdmissionCode.InvalidContent, Verifier().Verify(nul.Build()).Code);
    }

    [Fact]
    public void ALinkFoundByTheSource_IsRefused()
    {
        var raw = new TestPack().Build();
        var linked = new RawKnowledgePack
        {
            ManifestBytes = raw.ManifestBytes,
            SignatureBytes = raw.SignatureBytes,
            Files = raw.Files,
            LinkedEntries = ["nginx-triage/references/shortcut.md"],
        };

        Assert.Equal(KnowledgeAdmissionCode.LinkNotAllowed, Verifier().Verify(linked).Code);
    }

    // ---- manifest rules ----

    [Fact]
    public void AManifestThatContradictsItsSignatureEnvelope_IsRefused()
    {
        var pack = new TestPack();
        var verification = Verifier(TestPublisher.Acme.Trusted(), TestPublisher.Globex.Trusted())
            .Verify(pack.Build(m => m with { Publisher = "globex" })); // signed by acme, claims to be globex

        Assert.Equal(KnowledgeAdmissionCode.ManifestInvalid, verification.Code);
    }

    [Fact]
    public void AnEngineOutsideTheDeclaredRange_IsRefused()
    {
        Assert.Equal(KnowledgeAdmissionCode.EngineIncompatible, Verifier().Verify(new TestPack { EngineMin = "1.4.0" }.Build()).Code);
        Assert.Equal(KnowledgeAdmissionCode.EngineIncompatible, Verifier().Verify(new TestPack { EngineMax = "1.2.9" }.Build()).Code);
        Assert.Equal(KnowledgeAdmissionCode.Admitted, Verifier().Verify(new TestPack { EngineMin = "1.3.0", EngineMax = "1.3.0" }.Build()).Code);
        Assert.Equal(KnowledgeAdmissionCode.ManifestInvalid, Verifier().Verify(new TestPack { EngineMin = "2.0.0", EngineMax = "1.0.0" }.Build()).Code);
        Assert.Equal(KnowledgeAdmissionCode.ManifestInvalid, Verifier().Verify(new TestPack { EngineMin = "latest" }.Build()).Code);
    }

    [Fact]
    public void AnUnknownManifestField_AndAnUnsupportedSchema_AreRefused()
    {
        var pack = new TestPack();
        var (manifest, _, files) = pack.Parts();
        var withExtra = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace("\"packId\"", "\"trusted\": true, \"packId\"", StringComparison.Ordinal));

        Assert.Equal(KnowledgeAdmissionCode.ManifestInvalid, Verifier().Verify(TestPack.Raw(withExtra, TestPack.Sign(TestPublisher.Acme, withExtra), files)).Code);
        Assert.Equal(KnowledgeAdmissionCode.ManifestInvalid, Verifier().Verify(pack.Build(m => m with { SchemaVersion = 2 })).Code);
    }

    [Fact]
    public void AManifestWhoseDeclaredContentHashDoesNotMatchItsFileList_IsRefused()
    {
        Assert.Equal(KnowledgeAdmissionCode.ManifestInvalid, Verifier().Verify(new TestPack().Build(m => m with { ContentSha256 = new string('a', 64) })).Code);
    }

    [Theory]
    [InlineData("Bad Id")]
    [InlineData("../x")]
    [InlineData("UPPER")]
    public void APackIdOrVersionThatIsNotAnIdentifier_IsRefused(string packId)
    {
        Assert.Equal(KnowledgeAdmissionCode.ManifestInvalid, Verifier().Verify(new TestPack().Build(m => m with { PackId = packId })).Code);
        Assert.Equal(KnowledgeAdmissionCode.ManifestInvalid, Verifier().Verify(new TestPack().Build(m => m with { Version = "latest" })).Code);
    }

    [Fact]
    public void ASkillDirectoryWithoutAnEntryDocument_AndAnEmptyPack_AreRefused()
    {
        var headless = new TestPack { Files = new Dictionary<string, string>(StringComparer.Ordinal) { ["orphan/notes.md"] = "no entry document" } };
        var empty = new TestPack { Files = [] };

        Assert.Equal(KnowledgeAdmissionCode.ManifestInvalid, Verifier().Verify(headless.Build()).Code);
        Assert.Equal(KnowledgeAdmissionCode.ManifestInvalid, Verifier().Verify(empty.Build()).Code);
    }

    // ---- bounds ----

    [Fact]
    public void ASizeOrCountBeyondTheLimits_IsRefused()
    {
        var limits = new KnowledgePackLimits(MaxFiles: 3, MaxFileBytes: 200, MaxTotalBytes: 300);
        var verifier = new KnowledgePackVerifier(TestPack.TrustStore(TestPublisher.Acme.Trusted()), TestEngine.Version, limits);
        var big = new TestPack();
        big.Files["nginx-triage/references/checklist.md"] = new string('x', 201);
        var many = new TestPack();
        many.Files["nginx-triage/references/b.md"] = "b";
        var total = new TestPack();
        total.Files["nginx-triage/references/checklist.md"] = new string('x', 190);

        Assert.Equal(KnowledgeAdmissionCode.Admitted, verifier.Verify(new TestPack().Build()).Code); // the same pack fits, so the limits are what refused the others
        Assert.Equal(KnowledgeAdmissionCode.LimitExceeded, verifier.Verify(big.Build()).Code);
        Assert.Equal(KnowledgeAdmissionCode.LimitExceeded, verifier.Verify(many.Build()).Code);
        Assert.Equal(KnowledgeAdmissionCode.LimitExceeded, verifier.Verify(total.Build()).Code);
        var truncated = new RawKnowledgePack { Files = new Dictionary<string, ReadOnlyMemory<byte>>(), ExceededLimits = true };
        Assert.Equal(KnowledgeAdmissionCode.LimitExceeded, verifier.Verify(truncated).Code);
    }

    // ---- what the verifier trusts: its own copy of the bytes ----

    [Fact]
    public void ASourceThatKeepsAReferenceAndChangesTheBytesAfterwards_CannotChangeTheVerifiedPack()
    {
        var (manifest, signature, files) = new TestPack().Parts();
        var raw = TestPack.Raw(manifest, signature, files);

        var verification = Verifier().Verify(raw);
        files["nginx-triage/references/checklist.md"][0] = (byte)'X'; // the source mutates its array after verification

        var kept = Encoding.UTF8.GetString(verification.Pack!.Files["nginx-triage/references/checklist.md"]);
        Assert.Equal("nginx -t; ss -ltnp :80", kept);
    }

    [Fact]
    public void TheManifestSerialization_RoundTripsAndIsDeterministic()
    {
        var manifest = new TestPack().Manifest();

        var first = KnowledgePackFormat.SerializeManifest(manifest);
        var second = KnowledgePackFormat.SerializeManifest(manifest);

        Assert.Equal(first, second);
        Assert.Equal(KnowledgeAdmissionCode.Admitted, Verifier().Verify(new TestPack().Build()).Code);
    }
}
