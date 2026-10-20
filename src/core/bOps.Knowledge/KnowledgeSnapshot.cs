// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Knowledge;

/// <summary>
/// An immutable, content-addressed copy of a verified pack, written by bOps from the bytes it verified and served from nowhere else.
/// Immutability here is by verification, not by operating-system permission: anything with the host's own privileges can still
/// rewrite a file, so <see cref="VerifyIntegrity"/> re-reads and re-hashes the whole snapshot and the reader compares every item it
/// serves with the pin. An alteration is therefore refused, not prevented.
/// </summary>
public sealed class KnowledgeSnapshot
{
    internal const string ContentDirectoryName = "content";
    internal const string ManifestFileName = "pack.json";
    internal const string SignatureFileName = "pack.sig.json";

    internal KnowledgeSnapshot(string rootPath, KnowledgePin pin)
    {
        RootPath = rootPath;
        Pin = pin;
        ContentPath = Path.Combine(rootPath, ContentDirectoryName);
    }

    /// <summary>The directory the pack's skills are read from: one subdirectory per skill, each with a <c>SKILL.md</c>.</summary>
    public string ContentPath { get; }

    /// <summary>The pin this snapshot satisfies.</summary>
    public KnowledgePin Pin { get; }

    /// <summary>
    /// The identifier of this snapshot: the tenant's partition key and the content hash, which is how the store addresses it. It
    /// names where the verified bytes are kept for that tenant; it reveals no path and is not a credential.
    /// </summary>
    public string Id => $"{Path.GetFileName(Path.GetDirectoryName(RootPath))}/{Path.GetFileName(RootPath)}";

    internal string RootPath { get; }

    /// <summary>
    /// Checks the snapshot against its pin: the manifest is the verified manifest, every pinned file exists with its pinned hash, and
    /// nothing else exists (no extra file, no link, no reparse point). Returns <c>null</c> when it is intact, otherwise the code.
    /// </summary>
    public KnowledgeAdmissionCode? VerifyIntegrity()
    {
        try
        {
            var manifest = Path.Combine(RootPath, ManifestFileName);
            if (!File.Exists(manifest)
                || !File.Exists(Path.Combine(RootPath, SignatureFileName))
                || !string.Equals(KnowledgePackFormat.Sha256Hex(File.ReadAllBytes(manifest)), Pin.ContentHash, StringComparison.Ordinal))
            {
                return KnowledgeAdmissionCode.SnapshotTampered;
            }

            if (!Directory.Exists(ContentPath))
            {
                return KnowledgeAdmissionCode.SnapshotTampered;
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false,
            };
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (var entry in new DirectoryInfo(ContentPath).EnumerateFileSystemInfos("*", options))
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null)
                {
                    return KnowledgeAdmissionCode.LinkNotAllowed;
                }

                if (entry is not FileInfo file)
                {
                    continue;
                }

                var relative = Path.GetRelativePath(ContentPath, file.FullName).Replace('\\', '/');
                if (!Pin.Files.TryGetValue(relative, out var expected))
                {
                    return KnowledgeAdmissionCode.UnexpectedContent;
                }

                if (!string.Equals(KnowledgePackFormat.Sha256Hex(File.ReadAllBytes(file.FullName)), expected, StringComparison.Ordinal))
                {
                    return KnowledgeAdmissionCode.HashMismatch;
                }

                seen.Add(relative);
            }

            return seen.Count == Pin.Files.Count ? null : KnowledgeAdmissionCode.MissingResource;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return KnowledgeAdmissionCode.SnapshotTampered;
        }
    }
}
