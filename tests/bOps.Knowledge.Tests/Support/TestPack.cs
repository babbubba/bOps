// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace bOps.Knowledge.Tests;

/// <summary>The engine version every test host verifies against; test packs declare a range that contains it.</summary>
internal static class TestEngine
{
    internal static Version Version { get; } = new(1, 3, 0);
}

/// <summary>
/// Builds a test pack and signs it with a <see cref="TestPublisher"/>. The negative tests start from <see cref="Parts"/> and change
/// one byte, one file or one field, so each failure is caused by exactly one defect.
/// </summary>
internal sealed class TestPack
{
    internal string PackId { get; init; } = "acme.nginx-runbooks";

    internal string Version { get; init; } = "1.0.0";

    internal PackKind Kind { get; init; } = PackKind.Skill;

    internal string EngineMin { get; init; } = "1.0.0";

    internal string EngineMax { get; init; } = "1.9.9";

    internal TestPublisher Signer { get; init; } = TestPublisher.Acme;

    internal Dictionary<string, string> Files { get; init; } = new(StringComparer.Ordinal)
    {
        ["nginx-triage/SKILL.md"] = Skill("nginx-triage", "nginx 502 triage", "1. read resource references/checklist.md"),
        ["nginx-triage/references/checklist.md"] = "nginx -t; ss -ltnp :80",
        ["disk-cleanup/SKILL.md"] = Skill("disk-cleanup", "free space safely", "1. list the biggest folders"),
    };

    /// <summary>Files given as bytes (for content that is not valid text), listed in the manifest like the others.</summary>
    internal Dictionary<string, byte[]> RawFiles { get; } = new(StringComparer.Ordinal);

    private IEnumerable<KeyValuePair<string, byte[]>> AllFiles() =>
        Files.Select(file => new KeyValuePair<string, byte[]>(file.Key, Encoding.UTF8.GetBytes(file.Value))).Concat(RawFiles);

    /// <summary>A SKILL.md with the frontmatter the framework's parser expects.</summary>
    internal static string Skill(string name, string description, string body, string? extraFrontmatter = null) =>
        $"---\nname: {name}\ndescription: {description}\n{extraFrontmatter}---\n{body}\n";

    /// <summary>The manifest the publisher would sign for the files, with <paramref name="mutate"/> applied before it is serialized.</summary>
    internal PackManifest Manifest(Func<PackManifest, PackManifest>? mutate = null)
    {
        var entries = AllFiles()
            .OrderBy(file => file.Key, StringComparer.Ordinal)
            .Select(file => new PackFileEntry(file.Key, KnowledgePackFormat.Sha256Hex(file.Value), file.Value.Length))
            .ToList();
        var manifest = new PackManifest
        {
            SchemaVersion = KnowledgePackFormat.SchemaVersion,
            PackId = PackId,
            Version = Version,
            Kind = Kind,
            Engine = new PackEngineRange(EngineMin, EngineMax),
            Publisher = Signer.Publisher,
            KeyId = Signer.KeyId,
            ContentSha256 = KnowledgePackFormat.ComputeContentHash(entries),
            Files = entries,
        };
        return mutate is null ? manifest : mutate(manifest);
    }

    /// <summary>The three parts of the pack, correctly signed: manifest bytes, signature bytes, file bytes.</summary>
    internal (byte[] Manifest, byte[] Signature, Dictionary<string, byte[]> Files) Parts(Func<PackManifest, PackManifest>? mutate = null)
    {
        // The indented serializer writes Environment.NewLine, so the same manifest has different bytes (and a different hash) on
        // Windows and Linux. The fixture pins the CRLF form the checked-in PA-1 golden was captured with, so every host signs and
        // pins identical bytes; the verifier still checks exactly the bytes it is handed.
        var manifestBytes = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(KnowledgePackFormat.SerializeManifest(Manifest(mutate))).ReplaceLineEndings("\r\n"));
        var signature = Sign(Signer, manifestBytes);
        var files = AllFiles().ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        return (manifestBytes, signature, files);
    }

    internal RawKnowledgePack Build(Func<PackManifest, PackManifest>? mutate = null)
    {
        var (manifest, signature, files) = Parts(mutate);
        return Raw(manifest, signature, files);
    }

    internal static byte[] Sign(TestPublisher signer, byte[] manifestBytes) =>
        KnowledgePackFormat.SerializeSignature(new PackSignatureEnvelope(
            KnowledgePackFormat.SchemaVersion, signer.Publisher, signer.KeyId, KnowledgePackFormat.SignatureAlgorithm, signer.Sign(manifestBytes)));

    internal static RawKnowledgePack Raw(byte[] manifest, byte[] signature, Dictionary<string, byte[]> files) => new()
    {
        ManifestBytes = manifest,
        SignatureBytes = signature,
        Files = files.ToDictionary(file => file.Key, file => (ReadOnlyMemory<byte>)file.Value, StringComparer.Ordinal),
    };

    /// <summary>Writes the pack to a directory exactly as a publisher would ship it.</summary>
    internal void WriteTo(string directory, Func<PackManifest, PackManifest>? mutate = null)
    {
        var (manifest, signature, files) = Parts(mutate);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, KnowledgePackFormat.ManifestFileName), manifest);
        File.WriteAllBytes(Path.Combine(directory, KnowledgePackFormat.SignatureFileName), signature);
        foreach (var (path, bytes) in files)
        {
            var target = Path.Combine(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
    }

    internal static KnowledgeTrustStore TrustStore(params PublisherKey[] keys) => new(keys);
}
