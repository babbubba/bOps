// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0
// The archive boundary converts every I/O or crypto failure into a closed code; messages, paths and diagnosis text never leave it.
#pragma warning disable CA1031
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bOps.Runtime;

namespace bOps.AgentFramework.Tests;

/// <summary>
/// Opaque identifiers: 26 characters drawn uniformly (OS CSPRNG) from a 28-symbol alphabet, about 125 random bits. Nothing in them is derived
/// from a condition, a run or a time. The alphabet leaves out the letters a, b and c (and the look-alikes i, l, o), so no identifier or file
/// name can even contain the bare condition labels.
/// </summary>
internal static partial class OpaqueIds
{
    internal const string DiagnosisPrefix = "dx-";
    internal const string PackagePrefix = "pk-";
    internal const string Alphabet = "23456789defghjkmnpqrstuvwxyz";
    private const int Length = 26;

    internal static string NewDiagnosis() => DiagnosisPrefix + Random();

    internal static string NewPackage() => PackagePrefix + Random();

    internal static bool IsDiagnosis(string? value) => value is not null && DiagnosisPattern().IsMatch(value);

    internal static bool IsPackage(string? value) => value is not null && PackagePattern().IsMatch(value);

    private static string Random() => new(RandomNumberGenerator.GetItems<char>(Alphabet, Length));

    [GeneratedRegex("^dx-[2-9defghjkmnpqrstuvwxyz]{26}$", RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosisPattern();

    [GeneratedRegex("^pk-[2-9defghjkmnpqrstuvwxyz]{26}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackagePattern();
}

/// <summary>The identity the private map stores for a diagnosis: the information a reviewer must never see.</summary>
internal sealed record DiagnosisRunIdentity(string Condition, int Repetition, Guid RunId, string? CorpusSha256, string? AttestationSha256);

/// <summary>One line of the private map: <c>diagnosisId → condition, repetition, runId</c> plus what was stored for it.</summary>
internal sealed record DiagnosisMapEntry(string DiagnosisId, DiagnosisRunIdentity Identity, DiagnosisAvailability Availability,
    DiagnosisSource Source, DiagnosisReason Reason, string? TextSha256, int? TextBytes, string? SealedSha256);

/// <summary>A decrypted, integrity-verified diagnosis. <see cref="Text"/> is sensitive.</summary>
internal sealed record OpenedDiagnosis(string DiagnosisId, string Text, DiagnosisAvailability Availability, string TextSha256)
{
    public override string ToString() => $"OpenedDiagnosis({DiagnosisId}, {Availability})";
}

/// <summary>What happened to the diagnosis of one run. This (and only this) is projected into <c>result.json</c>.</summary>
internal sealed record DiagnosisArchiveOutcome(string DiagnosisId, int Repetition, string Status, string? FailureCode,
    DiagnosisAvailability Availability, DiagnosisSource Source, DiagnosisReason Reason, string? TextSha256, int? TextBytes, string? SealedSha256)
{
    internal const string Stored = "Stored";
    internal const string NotConfigured = "NotConfigured";
    internal const string NotAuthorized = "NotAuthorized";
    internal const string Failed = "Failed";

    /// <summary>A quality judgement is possible only for a diagnosis that exists and was stored verifiably.</summary>
    internal bool QualityMeasurable => Status == Stored && Availability != DiagnosisAvailability.None;

    internal string? NotMeasurableReason => QualityMeasurable ? null
        : Status != Stored ? $"Archive{Status}{(FailureCode is null ? "" : ":" + FailureCode)}"
        : "NoDiagnosis";

    /// <summary>Ids, hashes, availability and storage state only: never text, never a path.</summary>
    internal JsonObject ToResult() => new()
    {
        ["schemaVersion"] = 1,
        ["diagnosisId"] = DiagnosisId,
        ["repetition"] = Repetition,
        ["availability"] = Availability.ToString(),
        ["source"] = Source.ToString(),
        ["reason"] = Reason == DiagnosisReason.None ? null : Reason.ToString(),
        ["textSha256"] = TextSha256,
        ["textBytes"] = TextBytes,
        ["sealedSha256"] = SealedSha256,
        ["archiveStatus"] = Status,
        ["archiveFailureCode"] = FailureCode,
        ["qualityMeasurable"] = QualityMeasurable,
        ["qualityStatus"] = QualityMeasurable ? "PendingBlindReview" : "NotMeasurable",
        ["qualityNotMeasurableReason"] = NotMeasurableReason,
        ["representation"] = "Opaque id, SHA-256 and storage state only; the diagnosis text lives in the protected local archive, never here",
    };

    /// <summary>The outcome of a run whose archive is missing or not authorized: availability is observed, nothing is stored or hashed.</summary>
    internal static DiagnosisArchiveOutcome Unarchived(string id, int repetition, string status, CapturedDiagnosis? diagnosis) =>
        new(id, repetition, status, null, diagnosis?.Availability ?? DiagnosisAvailability.None, diagnosis?.Source ?? DiagnosisSource.None,
            diagnosis?.Reason ?? DiagnosisReason.None, null, null, null);
}

/// <summary>
/// D7: the protected local archive of agent diagnoses and of the private map that links an opaque diagnosis id to its condition.
/// <para>Guarantees, each with its own mechanism (they are not interchangeable):</para>
/// <list type="bullet">
/// <item><b>Confidentiality at rest</b>: every record is AES-256-GCM (<see cref="VaultCipher"/>) under a random data key; the key file is
/// wrapped by DPAPI (current user) or by an operator secret (<see cref="IArchiveKeyProtector"/>). The GCM tag also authenticates the id.</item>
/// <item><b>Access restriction</b>: restricted directories and files (<see cref="ArchiveAccess"/>), verified on every open and read.</item>
/// <item><b>Integrity</b>: SHA-256 of the exact diagnosis text bytes and of the sealed record, checked after writing and on every read.</item>
/// <item><b>No overwrite</b>: every file is created new (temp file, flush, no-overwrite move); an existing record is never replaced.</item>
/// </list>
/// Layout: <c>records/&lt;diagnosisId&gt;.sealed</c> (names carry no condition), <c>private/key.json</c>, <c>private/map/&lt;diagnosisId&gt;.sealed</c>,
/// <c>private/exports/&lt;packageId&gt;.sealed</c>.
/// </summary>
internal sealed class DiagnosisArchive : IDisposable
{
    private const string SealedFormat = "bops.know-bench.sealed";
    private const string KeyFormat = "bops.know-bench.key";
    private readonly byte[] dataKey;

    private DiagnosisArchive(string root, byte[] dataKey)
    {
        Root = root;
        this.dataKey = dataKey;
    }

    internal string Root { get; }

    private string RecordsDirectory => Path.Combine(Root, "records");

    private string PrivateDirectory => Path.Combine(Root, "private");

    private string MapDirectory => Path.Combine(PrivateDirectory, "map");

    private string ExportsDirectory => Path.Combine(PrivateDirectory, "exports");

    private string KeyFile => Path.Combine(PrivateDirectory, "key.json");

    /// <summary>Opens the archive at <paramref name="root"/>, creating it (restricted) when it does not exist yet. Fails closed.</summary>
    internal static DiagnosisArchive OpenOrCreate(string root, IArchiveKeyProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (!Path.IsPathFullyQualified(root)) throw new ArchiveException("RootNotAbsolute");
        var full = Path.GetFullPath(root);
        try
        {
            if (!Directory.Exists(full))
            {
                ArchiveAccess.CreateRestrictedDirectory(full);
                foreach (var name in new[] { "records", "private" })
                    ArchiveAccess.CreateRestrictedDirectory(Path.Combine(full, name));
                foreach (var name in new[] { "map", "exports" })
                    ArchiveAccess.CreateRestrictedDirectory(Path.Combine(full, "private", name));
            }

            var archive = new DiagnosisArchive(full, ResolveKey(full, protector));
            archive.RequireRestrictedLayout();
            return archive;
        }
        catch (ArchiveException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException
            or InvalidOperationException or NotSupportedException)
        {
            throw new ArchiveException("ArchiveUnavailable");
        }
    }

    /// <summary>Opens an existing archive only (the review and join steps never create one).</summary>
    internal static DiagnosisArchive Open(string root, IArchiveKeyProtector protector)
    {
        if (!Path.IsPathFullyQualified(root) || !File.Exists(Path.Combine(root, "private", "key.json")))
            throw new ArchiveException("ArchiveMissing");
        return OpenOrCreate(root, protector);
    }

    /// <summary>
    /// Seals and stores the diagnosis of one run, then its map entry, and verifies the stored record by reading it back. Never throws and
    /// never reports a state it did not verify: any failure is a closed code with nothing partial left behind.
    /// </summary>
    internal DiagnosisArchiveOutcome Store(string diagnosisId, DiagnosisRunIdentity identity, CapturedDiagnosis? diagnosis)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var availability = diagnosis?.Availability ?? DiagnosisAvailability.None;
        var source = diagnosis?.Source ?? DiagnosisSource.None;
        var reason = diagnosis?.Reason ?? DiagnosisReason.None;
        string? textHash = null, sealedHash = null;
        int? textBytes = null;
        string? recordPath = null, mapPath = null;
        var mapWritten = false;
        try
        {
            if (!OpaqueIds.IsDiagnosis(diagnosisId)) throw new ArchiveException("InvalidDiagnosisId");
            RequireRestrictedLayout();
            byte[]? sealedBytes = null;
            if (diagnosis is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(diagnosis.Text);
                var text = Encoding.UTF8.GetString(bytes); // invalid UTF-16 becomes U+FFFD once, so hash and stored text agree
                textHash = Sha256(bytes);
                textBytes = bytes.Length;
                var payload = new JsonObject
                {
                    ["diagnosisId"] = diagnosisId, ["text"] = text, ["availability"] = availability.ToString(),
                    ["source"] = source.ToString(), ["reason"] = reason.ToString(), ["textSha256"] = textHash, ["textBytes"] = textBytes,
                };
                sealedBytes = Seal("diagnosis", diagnosisId, payload.ToJsonString());
                sealedHash = Sha256(sealedBytes);
            }

            var entry = new DiagnosisMapEntry(diagnosisId, identity, availability, source, reason, textHash, textBytes, sealedHash);
            mapPath = Path.Combine(MapDirectory, diagnosisId + ".sealed");
            WriteNew(mapPath, Seal("map", diagnosisId, MapJson(entry).ToJsonString()));
            mapWritten = true;
            if (sealedBytes is not null)
            {
                recordPath = Path.Combine(RecordsDirectory, diagnosisId + ".sealed");
                WriteNew(recordPath, sealedBytes);
                var verified = ReadDiagnosis(diagnosisId);
                if (verified.TextSha256 != textHash || Sha256(File.ReadAllBytes(recordPath)) != sealedHash)
                    throw new ArchiveException("VerificationFailed");
            }

            return new(diagnosisId, identity.Repetition, DiagnosisArchiveOutcome.Stored, null, availability, source, reason, textHash, textBytes, sealedHash);
        }
        catch (Exception ex) when (ex is ArchiveException or IOException or UnauthorizedAccessException or CryptographicException
            or JsonException or InvalidOperationException or NotSupportedException)
        {
            // Only files this call created are removed; an existing record is never touched.
            if (ex is not ArchiveException { Code: "AlreadyExists" })
            {
                TryDelete(recordPath);
                if (mapWritten) TryDelete(mapPath);
            }

            var code = ex is ArchiveException archive ? archive.Code : "WriteFailed";
            return new(diagnosisId, identity.Repetition, DiagnosisArchiveOutcome.Failed, code, availability, source, reason, null, null, null);
        }
    }

    /// <summary>Decrypts and verifies one diagnosis. Fails with a closed code if it is missing, loosened, altered or relabeled.</summary>
    internal OpenedDiagnosis ReadDiagnosis(string diagnosisId)
    {
        if (!OpaqueIds.IsDiagnosis(diagnosisId)) throw new ArchiveException("InvalidDiagnosisId");
        var path = Path.Combine(RecordsDirectory, diagnosisId + ".sealed");
        var node = Unseal("diagnosis", diagnosisId, path);
        try
        {
            var text = node["text"]!.GetValue<string>();
            var expected = node["textSha256"]!.GetValue<string>();
            var bytes = Encoding.UTF8.GetBytes(text);
            if (node["diagnosisId"]!.GetValue<string>() != diagnosisId || Sha256(bytes) != expected || node["textBytes"]!.GetValue<int>() != bytes.Length)
                throw new ArchiveException("IntegrityMismatch");
            return new(diagnosisId, text, Enum.Parse<DiagnosisAvailability>(node["availability"]!.GetValue<string>()), expected);
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new ArchiveException("RecordCorrupt");
        }
    }

    /// <summary>
    /// Every map entry. An entry that cannot be read or verified is returned in <c>Unreadable</c> by id, never silently dropped.
    /// </summary>
    internal (IReadOnlyList<DiagnosisMapEntry> Entries, IReadOnlyList<string> Unreadable) ReadMap()
    {
        RequireRestrictedLayout();
        var entries = new List<DiagnosisMapEntry>();
        var unreadable = new List<string>();
        foreach (var file in Directory.EnumerateFiles(MapDirectory, "*.sealed").Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            try
            {
                if (!OpaqueIds.IsDiagnosis(id)) throw new ArchiveException("InvalidDiagnosisId");
                entries.Add(MapEntry(id, Unseal("map", id, file)));
            }
            catch (Exception ex) when (ex is ArchiveException or NullReferenceException or InvalidOperationException or FormatException
                or ArgumentException)
            {
                unreadable.Add(OpaqueIds.IsDiagnosis(id) ? id : "invalid-name");
            }
        }

        return (entries, unreadable);
    }

    internal void RecordExport(string packageId, JsonObject record)
    {
        if (!OpaqueIds.IsPackage(packageId)) throw new ArchiveException("InvalidPackageId");
        WriteNew(Path.Combine(ExportsDirectory, packageId + ".sealed"), Seal("export", packageId, record.ToJsonString()));
    }

    internal JsonObject ReadExport(string packageId)
    {
        if (!OpaqueIds.IsPackage(packageId)) throw new ArchiveException("InvalidPackageId");
        return Unseal("export", packageId, Path.Combine(ExportsDirectory, packageId + ".sealed"));
    }

    /// <summary>Writes the owner-only review/join output next to the archive's rules: create-new, atomic, restricted.</summary>
    internal static void WriteRestrictedNew(string path, byte[] bytes) => AtomicWrite(path, bytes);

    internal static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Dispose() => CryptographicOperations.ZeroMemory(dataKey);

    private void RequireRestrictedLayout()
    {
        foreach (var directory in new[] { Root, RecordsDirectory, PrivateDirectory, MapDirectory, ExportsDirectory })
        {
            if (!Directory.Exists(directory)) throw new ArchiveException("ArchiveMissing");
            ArchiveAccess.RequireRestricted(directory);
        }
    }

    private static byte[] ResolveKey(string root, IArchiveKeyProtector protector)
    {
        var keyFile = Path.Combine(root, "private", "key.json");
        if (!File.Exists(keyFile))
        {
            var fresh = RandomNumberGenerator.GetBytes(VaultCipher.KeySizeBytes);
            var envelope = new JsonObject
            {
                ["format"] = KeyFormat, ["version"] = 1, ["protection"] = protector.Name, ["wrapped"] = protector.Protect(fresh),
            };
            try { AtomicWrite(keyFile, Encoding.UTF8.GetBytes(envelope.ToJsonString())); }
            catch (ArchiveException ex) when (ex.Code == "AlreadyExists") { } // another opener won; its key is the one read below
            finally { CryptographicOperations.ZeroMemory(fresh); }
            if (!File.Exists(keyFile)) throw new ArchiveException("KeyUnavailable");
        }

        ArchiveAccess.RequireRestricted(keyFile);
        var node = JsonNode.Parse(File.ReadAllText(keyFile))?.AsObject() ?? throw new ArchiveException("KeyUnavailable");
        if (node["format"]?.GetValue<string>() != KeyFormat || node["version"]?.GetValue<int>() != 1)
            throw new ArchiveException("KeyUnavailable");
        if (node["protection"]?.GetValue<string>() != protector.Name) throw new ArchiveException("ProtectionMismatch");
        var key = protector.Unprotect(node["wrapped"]!.AsObject());
        if (key.Length != VaultCipher.KeySizeBytes) throw new ArchiveException("KeyUnavailable");
        return key;
    }

    private byte[] Seal(string kind, string id, string plaintext)
    {
        var sealedValue = VaultCipher.Encrypt(dataKey, kind + ":" + id, 1, plaintext);
        return Encoding.UTF8.GetBytes(new JsonObject
        {
            ["format"] = SealedFormat, ["version"] = 1, ["kind"] = kind, ["id"] = id, ["cipher"] = "AES-256-GCM",
            ["nonce"] = sealedValue.NonceBase64, ["ciphertext"] = sealedValue.CiphertextBase64, ["tag"] = sealedValue.TagBase64,
        }.ToJsonString());
    }

    private JsonObject Unseal(string kind, string id, string path)
    {
        if (!File.Exists(path)) throw new ArchiveException("RecordMissing");
        ArchiveAccess.RequireRestricted(path);
        try
        {
            var envelope = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
            if (envelope["format"]!.GetValue<string>() != SealedFormat || envelope["version"]!.GetValue<int>() != 1
                || envelope["kind"]!.GetValue<string>() != kind || envelope["id"]!.GetValue<string>() != id
                || envelope["cipher"]!.GetValue<string>() != "AES-256-GCM")
                throw new ArchiveException("RecordCorrupt");
            var plaintext = VaultCipher.Decrypt(dataKey, kind + ":" + id, 1, new VaultSealedValue(
                envelope["nonce"]!.GetValue<string>(), envelope["ciphertext"]!.GetValue<string>(), envelope["tag"]!.GetValue<string>()));
            return JsonNode.Parse(plaintext)!.AsObject();
        }
        catch (VaultCorruptedException) { throw new ArchiveException("IntegrityMismatch"); }
        catch (Exception ex) when (ex is JsonException or NullReferenceException or InvalidOperationException or FormatException)
        {
            throw new ArchiveException("RecordCorrupt");
        }
    }

    private static void WriteNew(string path, byte[] bytes) => AtomicWrite(path, bytes);

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) + ".partial");
        try
        {
            using (var file = ArchiveAccess.CreateNewFile(temp))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: false); // never replaces, including against a concurrent writer
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new ArchiveException(File.Exists(path) ? "AlreadyExists" : "WriteFailed");
        }
    }

    private static void TryDelete(string? path)
    {
        try { if (path is not null && File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static JsonObject MapJson(DiagnosisMapEntry entry) => new()
    {
        ["diagnosisId"] = entry.DiagnosisId,
        ["condition"] = entry.Identity.Condition,
        ["repetition"] = entry.Identity.Repetition,
        ["runId"] = entry.Identity.RunId.ToString("D"),
        ["corpusSha256"] = entry.Identity.CorpusSha256,
        ["attestationSha256"] = entry.Identity.AttestationSha256,
        ["availability"] = entry.Availability.ToString(),
        ["source"] = entry.Source.ToString(),
        ["reason"] = entry.Reason.ToString(),
        ["textSha256"] = entry.TextSha256,
        ["textBytes"] = entry.TextBytes,
        ["sealedSha256"] = entry.SealedSha256,
    };

    private static DiagnosisMapEntry MapEntry(string id, JsonObject node)
    {
        if (node["diagnosisId"]!.GetValue<string>() != id) throw new ArchiveException("IntegrityMismatch");
        var identity = new DiagnosisRunIdentity(node["condition"]!.GetValue<string>(), node["repetition"]!.GetValue<int>(),
            Guid.Parse(node["runId"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            node["corpusSha256"]?.GetValue<string>(), node["attestationSha256"]?.GetValue<string>());
        return new(id, identity, Enum.Parse<DiagnosisAvailability>(node["availability"]!.GetValue<string>()),
            Enum.Parse<DiagnosisSource>(node["source"]!.GetValue<string>()), Enum.Parse<DiagnosisReason>(node["reason"]!.GetValue<string>()),
            node["textSha256"]?.GetValue<string>(), node["textBytes"]?.GetValue<int>(), node["sealedSha256"]?.GetValue<string>());
    }
}
