// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
#pragma warning disable CA1416 // Windows-only branches are guarded by OperatingSystem.IsWindows().
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Knowledge.Tests;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// D7: the protected local archive. Offline and deterministic: every diagnosis here is invented test text; nothing contacts a provider, reads
/// an Event Log or touches a real archive. Confidentiality (AES-GCM + key wrapping), access restriction (ACL / mode bits) and integrity
/// (SHA-256) are asserted separately because they are separate guarantees.
/// </summary>
public sealed class KnowledgeDiagnosisArchiveTests
{
    internal const string Sentinel = "SENTINEL-DIAGNOSIS-7f3k The system rebooted at 10:12 UTC (Kernel-Power 41), then WHEA 18.";
    internal static readonly OperatorSecretKeyProtector Protector = new("offline-test-secret-0123456789");
    private static readonly string[] RelabeledCodes = ["RecordCorrupt", "IntegrityMismatch"];

    internal static DiagnosisRunIdentity Identity(string condition = "A", int repetition = 1, string? corpus = null, string? attestation = null) =>
        new(condition, repetition, Guid.NewGuid(), corpus, attestation);

    internal static CapturedDiagnosis Complete(string text = Sentinel) =>
        new(text, DiagnosisAvailability.Complete, DiagnosisSource.PersistedFinalStep, DiagnosisReason.None);

    internal static CapturedDiagnosis Partial(string text = Sentinel) =>
        new(text, DiagnosisAvailability.Partial, DiagnosisSource.ObservedModelReply, DiagnosisReason.TruncatedAtOutputLimit);

    [Fact]
    public void CompleteDiagnosis_IsSealed_Verified_AndRoundTripsExactly()
    {
        using var temporary = new TempDirectory();
        using var archive = DiagnosisArchive.OpenOrCreate(temporary.Sub("archive"), Protector);
        var id = OpaqueIds.NewDiagnosis();

        var outcome = archive.Store(id, Identity("B"), Complete());

        Assert.Equal(DiagnosisArchiveOutcome.Stored, outcome.Status);
        Assert.Null(outcome.FailureCode);
        Assert.True(outcome.QualityMeasurable);
        Assert.Equal(DiagnosisAvailability.Complete, outcome.Availability);
        var bytes = Encoding.UTF8.GetBytes(Sentinel);
        Assert.Equal(DiagnosisArchive.Sha256(bytes), outcome.TextSha256);
        Assert.Equal(bytes.Length, outcome.TextBytes);
        var record = Path.Combine(archive.Root, "records", id + ".sealed");
        Assert.Equal(DiagnosisArchive.Sha256(File.ReadAllBytes(record)), outcome.SealedSha256);

        var opened = archive.ReadDiagnosis(id);
        Assert.Equal(Sentinel, opened.Text);
        Assert.Equal(outcome.TextSha256, opened.TextSha256);

        // Confidentiality at rest: the text is nowhere on disk in the clear, in any file of the archive.
        foreach (var file in Directory.EnumerateFiles(archive.Root, "*", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain("SENTINEL", content, StringComparison.Ordinal);
            Assert.DoesNotContain("Kernel-Power", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PartialDiagnosis_KeepsItsAvailabilityAndReason()
    {
        using var temporary = new TempDirectory();
        using var archive = DiagnosisArchive.OpenOrCreate(temporary.Sub("archive"), Protector);
        var id = OpaqueIds.NewDiagnosis();

        var outcome = archive.Store(id, Identity("C"), Partial("cut off mid-sentence because the provider hit its out"));

        Assert.Equal(DiagnosisArchiveOutcome.Stored, outcome.Status);
        Assert.Equal(DiagnosisAvailability.Partial, outcome.Availability);
        Assert.Equal(DiagnosisReason.TruncatedAtOutputLimit, outcome.Reason);
        Assert.True(outcome.QualityMeasurable);
        Assert.Equal(DiagnosisAvailability.Partial, archive.ReadDiagnosis(id).Availability);
        var entry = Assert.Single(archive.ReadMap().Entries);
        Assert.Equal(DiagnosisAvailability.Partial, entry.Availability);
        Assert.Equal("C", entry.Identity.Condition);
    }

    [Fact]
    public void NoDiagnosis_IsRecordedAsSuch_AndIsNeverAZeroQuality()
    {
        using var temporary = new TempDirectory();
        using var archive = DiagnosisArchive.OpenOrCreate(temporary.Sub("archive"), Protector);
        var id = OpaqueIds.NewDiagnosis();

        var outcome = archive.Store(id, Identity("A"), null);

        Assert.Equal(DiagnosisArchiveOutcome.Stored, outcome.Status); // the map line is stored; there is nothing to seal
        Assert.Equal(DiagnosisAvailability.None, outcome.Availability);
        Assert.Null(outcome.TextSha256);
        Assert.Null(outcome.SealedSha256);
        Assert.False(outcome.QualityMeasurable);
        Assert.Equal("NoDiagnosis", outcome.NotMeasurableReason);
        Assert.Empty(Directory.GetFiles(Path.Combine(archive.Root, "records")));
        var result = outcome.ToResult();
        Assert.False(result["qualityMeasurable"]!.GetValue<bool>());
        Assert.Equal("NotMeasurable", result["qualityStatus"]!.GetValue<string>());
        Assert.Equal("None", result["availability"]!.GetValue<string>());
        Assert.Equal("RecordMissing", ArchiveCode(() => archive.ReadDiagnosis(id)));
    }

    [Fact]
    public void ARecordIsNeverOverwritten_AndTheOriginalSurvivesAnAttempt()
    {
        using var temporary = new TempDirectory();
        using var archive = DiagnosisArchive.OpenOrCreate(temporary.Sub("archive"), Protector);
        var id = OpaqueIds.NewDiagnosis();
        var first = archive.Store(id, Identity("A"), Complete());
        var record = Path.Combine(archive.Root, "records", id + ".sealed");
        var before = File.ReadAllBytes(record);
        var mapBefore = File.ReadAllBytes(Path.Combine(archive.Root, "private", "map", id + ".sealed"));

        var second = archive.Store(id, Identity("B"), Complete("a different, later diagnosis"));

        Assert.Equal(DiagnosisArchiveOutcome.Failed, second.Status);
        Assert.Equal("AlreadyExists", second.FailureCode);
        Assert.False(second.QualityMeasurable);
        Assert.Equal(before, File.ReadAllBytes(record));
        Assert.Equal(mapBefore, File.ReadAllBytes(Path.Combine(archive.Root, "private", "map", id + ".sealed")));
        Assert.Equal(Sentinel, archive.ReadDiagnosis(id).Text);
        Assert.Equal(first.TextSha256, archive.ReadDiagnosis(id).TextSha256);
        Assert.Empty(PartialFiles(archive.Root));
    }

    [Fact]
    public void AFailedWrite_LeavesNothingPartialBehind_AndRollsBackTheMapLine()
    {
        using var temporary = new TempDirectory();
        using var archive = DiagnosisArchive.OpenOrCreate(temporary.Sub("archive"), Protector);
        var id = OpaqueIds.NewDiagnosis();
        // A directory squatting on the record's name makes the final no-overwrite move fail after the temp file was written.
        Directory.CreateDirectory(Path.Combine(archive.Root, "records", id + ".sealed"));

        var outcome = archive.Store(id, Identity("B"), Complete());

        Assert.Equal(DiagnosisArchiveOutcome.Failed, outcome.Status);
        Assert.Equal("WriteFailed", outcome.FailureCode);
        Assert.False(outcome.QualityMeasurable);
        Assert.Null(outcome.TextSha256);
        Assert.Null(outcome.SealedSha256);
        Assert.Empty(PartialFiles(archive.Root));
        Assert.Empty(archive.ReadMap().Entries); // no map line that points at a record that does not exist
        Assert.Equal("Failed", outcome.ToResult()["archiveStatus"]!.GetValue<string>());
        Assert.StartsWith("ArchiveFailed:WriteFailed", outcome.NotMeasurableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TamperedOrRelabeledRecords_AreRejected_NotReturned()
    {
        using var temporary = new TempDirectory();
        using var archive = DiagnosisArchive.OpenOrCreate(temporary.Sub("archive"), Protector);
        var first = OpaqueIds.NewDiagnosis();
        var second = OpaqueIds.NewDiagnosis();
        archive.Store(first, Identity("A"), Complete());
        archive.Store(second, Identity("B"), Complete("another diagnosis entirely"));
        var firstFile = Path.Combine(archive.Root, "records", first + ".sealed");
        var secondFile = Path.Combine(archive.Root, "records", second + ".sealed");

        // Altered ciphertext: the GCM tag no longer verifies.
        var node = JsonNode.Parse(File.ReadAllText(firstFile))!.AsObject();
        var ciphertext = node["ciphertext"]!.GetValue<string>();
        node["ciphertext"] = (ciphertext[0] == 'A' ? 'B' : 'A') + ciphertext[1..];
        File.WriteAllText(firstFile, node.ToJsonString());
        Assert.Equal("IntegrityMismatch", ArchiveCode(() => archive.ReadDiagnosis(first)));

        // A genuine record moved under another id: the id is authenticated, so it is refused.
        File.Copy(secondFile, firstFile, overwrite: true);
        Assert.Contains(ArchiveCode(() => archive.ReadDiagnosis(first)), RelabeledCodes);
        Assert.Equal("another diagnosis entirely", archive.ReadDiagnosis(second).Text);

        // Truncation and garbage.
        File.WriteAllBytes(firstFile, [1, 2, 3]);
        Assert.Equal("RecordCorrupt", ArchiveCode(() => archive.ReadDiagnosis(first)));
    }

    [Fact]
    public void AWrongSecret_OrAnotherProtection_CannotOpenTheArchive()
    {
        using var temporary = new TempDirectory();
        var root = temporary.Sub("archive");
        using (var archive = DiagnosisArchive.OpenOrCreate(root, Protector)) archive.Store(OpaqueIds.NewDiagnosis(), Identity(), Complete());

        Assert.Equal("KeyUnavailable", ArchiveCode(() => DiagnosisArchive.OpenOrCreate(root, new OperatorSecretKeyProtector("another-secret-0123456789"))));
        Assert.Equal("ProtectionMismatch", ArchiveCode(() => DiagnosisArchive.OpenOrCreate(root, new RenamedProtector())));
        Assert.Equal("OperatorSecretTooShort", ArchiveCode(() => new OperatorSecretKeyProtector("short")));
        Assert.Equal("ArchiveMissing", ArchiveCode(() => DiagnosisArchive.Open(temporary.Sub("does-not-exist"), Protector)));
        using var reopened = DiagnosisArchive.Open(root, Protector);
        Assert.Single(reopened.ReadMap().Entries);
    }

    [Fact]
    public void ArchiveAndRecordsAreOwnerOnly_AndLoosenedPermissionsFailClosed()
    {
        using var temporary = new TempDirectory();
        var root = temporary.Sub("archive");
        var id = OpaqueIds.NewDiagnosis();
        using (var archive = DiagnosisArchive.OpenOrCreate(root, Protector))
            Assert.Equal(DiagnosisArchiveOutcome.Stored, archive.Store(id, Identity(), Complete()).Status);

        var locations = new[] { root, Path.Combine(root, "records"), Path.Combine(root, "private"), Path.Combine(root, "private", "map"),
            Path.Combine(root, "private", "exports"), Path.Combine(root, "private", "key.json"),
            Path.Combine(root, "records", id + ".sealed"), Path.Combine(root, "private", "map", id + ".sealed") };
        foreach (var location in locations) AssertOwnerOnly(location);

        // Someone (or a bad copy) widens the records directory: the next open and the next read refuse to go on.
        Loosen(Path.Combine(root, "records"));
        Assert.Equal("PermissionsTooBroad", ArchiveCode(() => DiagnosisArchive.OpenOrCreate(root, Protector)));
    }

    [Fact]
    public void IdsAndFileNames_AreOpaque_AndNeverCarryAConditionOrAKnowledgeName()
    {
        using var temporary = new TempDirectory();
        using var archive = DiagnosisArchive.OpenOrCreate(temporary.Sub("archive"), Protector);
        var ids = Enumerable.Range(0, 500).Select(_ => OpaqueIds.NewDiagnosis()).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id =>
        {
            Assert.True(OpaqueIds.IsDiagnosis(id));
            // None of the bare condition letters can occur in the random part, in either case.
            Assert.DoesNotContain(id[OpaqueIds.DiagnosisPrefix.Length..], c => "abcABC".Contains(c, StringComparison.Ordinal));
        });
        Assert.False(OpaqueIds.IsDiagnosis("dx-" + new string('a', 26)));
        Assert.False(OpaqueIds.IsDiagnosis("../" + ids[0]));
        Assert.False(OpaqueIds.IsDiagnosis(ids[0] + "x"));

        foreach (var (condition, id) in new[] { ("A", ids[0]), ("B", ids[1]), ("C", ids[2]) })
            archive.Store(id, Identity(condition), Complete(condition + " arm diagnosis"));
        var names = Directory.EnumerateFiles(archive.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("key.json", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToArray();
        Assert.Equal(6, names.Length);
        Assert.All(names, name =>
        {
            Assert.Matches("^dx-[2-9defghjkmnpqrstuvwxyz]{26}\\.sealed$", name!);
            Assert.DoesNotContain("skill", name!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("knowledge", name!, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ThePrivateMap_IsSeparate_Sealed_AndNotReadableWithoutTheKey()
    {
        using var temporary = new TempDirectory();
        var root = temporary.Sub("archive");
        var run = Guid.NewGuid();
        var id = OpaqueIds.NewDiagnosis();
        using (var archive = DiagnosisArchive.OpenOrCreate(root, Protector))
        {
            archive.Store(id, new DiagnosisRunIdentity("C", 3, run, new string('1', 64), new string('2', 64)), Complete());
            var entry = Assert.Single(archive.ReadMap().Entries);
            Assert.Equal(("C", 3, run), (entry.Identity.Condition, entry.Identity.Repetition, entry.Identity.RunId));
        }

        // The map lives under private/ only; the records directory carries ids and ciphertext and nothing else.
        Assert.Single(Directory.GetFiles(Path.Combine(root, "private", "map")));
        var recordText = File.ReadAllText(Path.Combine(root, "records", id + ".sealed"));
        Assert.DoesNotContain(run.ToString("D"), recordText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"condition\"", recordText, StringComparison.Ordinal);
        // The map entry is itself sealed: copying the file out reveals neither condition nor run id.
        var mapText = File.ReadAllText(Path.Combine(root, "private", "map", id + ".sealed"));
        Assert.DoesNotContain(run.ToString("D"), mapText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("condition", mapText, StringComparison.OrdinalIgnoreCase);
        // No one without the secret reads it.
        Assert.Equal("KeyUnavailable", ArchiveCode(() => DiagnosisArchive.Open(root, new OperatorSecretKeyProtector("not-the-right-secret-0123"))));
    }

    [Fact]
    public void TheResultProjection_AndAllErrors_NeverCarryTextOrPaths()
    {
        using var temporary = new TempDirectory();
        var root = temporary.Sub("archive");
        using var archive = DiagnosisArchive.OpenOrCreate(root, Protector);
        var id = OpaqueIds.NewDiagnosis();
        var outcome = archive.Store(id, Identity("B"), Complete());

        var projected = outcome.ToResult().ToJsonString() + outcome + outcome.NotMeasurableReason;
        Assert.DoesNotContain("SENTINEL", projected, StringComparison.Ordinal);
        Assert.DoesNotContain("Kernel-Power", projected, StringComparison.Ordinal);
        Assert.DoesNotContain(root, projected, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(temporary.Path, projected, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"condition\"", projected, StringComparison.Ordinal);

        var captured = Complete();
        Assert.DoesNotContain("SENTINEL", captured.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", archive.ReadDiagnosis(id).ToString(), StringComparison.Ordinal);

        var failure = Assert.Throws<ArchiveException>(() => archive.ReadDiagnosis(OpaqueIds.NewDiagnosis()));
        Assert.Equal(failure.Code, failure.Message);
        Assert.DoesNotContain(root, failure.ToString(), StringComparison.OrdinalIgnoreCase);

        // An invalid id or a relative root is refused before anything touches the disk.
        Assert.Equal("InvalidDiagnosisId", archive.Store("../escape", Identity(), Complete()).FailureCode);
        Assert.Equal("RootNotAbsolute", ArchiveCode(() => DiagnosisArchive.OpenOrCreate("relative\\archive", Protector)));
    }

    [Fact]
    public void TheArchiveEnvironment_RequiresAnExplicitLocalWriteAuthorization()
    {
        string? Lookup(Dictionary<string, string> values, string name) => values.GetValueOrDefault(name);
        Assert.Equal(DiagnosisArchiveOutcome.NotConfigured, DiagnosisArchiveEnvironment.Read(n => Lookup([], n)).Status);

        var root = Path.Combine(Path.GetTempPath(), "bops-never-created");
        var named = new Dictionary<string, string> { [DiagnosisArchiveEnvironment.Root] = root };
        Assert.Equal(DiagnosisArchiveOutcome.NotAuthorized, DiagnosisArchiveEnvironment.Read(n => Lookup(named, n)).Status);
        named[DiagnosisArchiveEnvironment.Accept] = "yes";
        Assert.Equal(DiagnosisArchiveOutcome.NotAuthorized, DiagnosisArchiveEnvironment.Read(n => Lookup(named, n)).Status);
        Assert.Throws<InvalidOperationException>(() => DiagnosisArchiveEnvironment.RequireAuthorized(n => Lookup(named, n)));

        named[DiagnosisArchiveEnvironment.Accept] = "1";
        named[DiagnosisArchiveEnvironment.Protection] = "operator-secret";
        named[DiagnosisArchiveEnvironment.Secret] = "offline-test-secret-0123456789";
        var settings = DiagnosisArchiveEnvironment.Read(n => Lookup(named, n));
        Assert.True(settings.IsAuthorized);
        Assert.Equal("operator-secret", settings.Protector!.Name);
        Assert.False(Directory.Exists(root)); // reading the switches writes nothing

        named[DiagnosisArchiveEnvironment.Protection] = "plaintext";
        Assert.Throws<InvalidOperationException>(() => DiagnosisArchiveEnvironment.Read(n => Lookup(named, n)));
        named[DiagnosisArchiveEnvironment.Protection] = "operator-secret";
        named[DiagnosisArchiveEnvironment.Root] = "relative";
        Assert.Throws<InvalidOperationException>(() => DiagnosisArchiveEnvironment.Read(n => Lookup(named, n)));

        Assert.Equal(1, DiagnosisArchiveEnvironment.ReadRepetition(_ => null));
        Assert.Equal(3, DiagnosisArchiveEnvironment.ReadRepetition(_ => "3"));
        Assert.Throws<InvalidOperationException>(() => DiagnosisArchiveEnvironment.ReadRepetition(_ => "0"));
        Assert.Throws<InvalidOperationException>(() => DiagnosisArchiveEnvironment.ReadRepetition(_ => "-1"));
    }

    [Fact]
    public void WindowsDpapi_WrapsTheKeyForTheCurrentUserOnly_AndTheArchiveWorksWithIt()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI exists on Windows only; the operator-secret path above covers the rest.
        var protector = new WindowsDpapiKeyProtector();
        var key = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 };
        var wrapped = protector.Protect(key);
        Assert.NotEqual(Convert.ToBase64String(key), wrapped["blob"]!.GetValue<string>());
        Assert.Equal(key, protector.Unprotect(wrapped));
        var damaged = Convert.FromBase64String(wrapped["blob"]!.GetValue<string>());
        damaged[^1] ^= 0xFF;
        Assert.Equal("KeyUnavailable", ArchiveCode(() => protector.Unprotect(new JsonObject { ["blob"] = Convert.ToBase64String(damaged) })));
        Assert.Equal("KeyUnavailable", ArchiveCode(() => protector.Unprotect(new JsonObject { ["blob"] = "not base64 !!" })));

        using var temporary = new TempDirectory();
        var root = temporary.Sub("archive");
        var id = OpaqueIds.NewDiagnosis();
        using (var archive = DiagnosisArchive.OpenOrCreate(root, protector)) archive.Store(id, Identity(), Complete());
        using (var reopened = DiagnosisArchive.Open(root, new WindowsDpapiKeyProtector())) Assert.Equal(Sentinel, reopened.ReadDiagnosis(id).Text);
        var keyFile = File.ReadAllText(Path.Combine(root, "private", "key.json"));
        Assert.Contains("windows-dpapi-current-user", keyFile, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(key), keyFile, StringComparison.Ordinal);
    }

    internal static string ArchiveCode(Func<object?> action) => Assert.Throws<ArchiveException>(action).Code;

    private static string[] PartialFiles(string root) =>
        [.. Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories)];

    internal static void AssertOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var self = WindowsIdentity.GetCurrent().User!;
            var security = Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            if (Directory.Exists(path)) Assert.True(security.AreAccessRulesProtected, "inheritance from the parent must be cut");
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Where(rule => rule.AccessControlType == AccessControlType.Allow).ToArray();
            Assert.NotEmpty(rules);
            Assert.All(rules, rule => Assert.Equal(self, rule.IdentityReference));
        }
        else
        {
            var mode = File.GetUnixFileMode(path);
            Assert.Equal(Directory.Exists(path) ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    internal static void Loosen(string directory)
    {
        if (OperatingSystem.IsWindows()) LoosenWindows(directory);
        else File.SetUnixFileMode(directory, (UnixFileMode)0b111_101_101);
    }

    [SupportedOSPlatform("windows")]
    private static void LoosenWindows(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    private sealed class RenamedProtector : IArchiveKeyProtector
    {
        public string Name => "some-other-protection";

        public JsonObject Protect(byte[] dataKey) => new();

        public byte[] Unprotect(JsonObject wrapped) => throw new ArchiveException("KeyUnavailable");
    }
}
