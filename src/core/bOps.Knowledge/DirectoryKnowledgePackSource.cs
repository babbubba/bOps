// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>
/// Reads packs from a directory the operator controls: <c>root/pack id/version</c>, or <c>root/tenant/pack id/version</c> when
/// partitioned by tenant (the layout for Experience). It reads every file once, never follows a link or reparse point (it reports
/// them), stops at the size and count bounds, and never executes or interprets anything it reads. The pack id, the version and the
/// tenant are shape-checked values, so no part of the path can come from pack content or from a model.
/// </summary>
public sealed class DirectoryKnowledgePackSource : IKnowledgePackSource
{
    private readonly string root;
    private readonly bool partitionByTenant;
    private readonly KnowledgePackLimits limits;

    /// <summary>Creates a source over an operator-controlled directory.</summary>
    public DirectoryKnowledgePackSource(string rootDirectory, bool partitionByTenant = false, KnowledgePackLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        root = Path.GetFullPath(rootDirectory);
        this.partitionByTenant = partitionByTenant;
        this.limits = limits ?? KnowledgePackLimits.Default;
    }

    /// <inheritdoc />
    public async Task<RawKnowledgePack?> OpenAsync(PackReference reference, TenantId tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(tenant);

        var directory = partitionByTenant
            ? Path.Combine(root, tenant.Value, reference.PackId, reference.Version)
            : Path.Combine(root, reference.PackId, reference.Version);
        if (!Directory.Exists(directory) || new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return null;
        }

        // One directory level at a time: a link or reparse point is reported and never entered, so nothing outside the pack is read.
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false };
        Dictionary<string, ReadOnlyMemory<byte>> files = new(StringComparer.Ordinal);
        List<string> links = [];
        ReadOnlyMemory<byte> manifest = default;
        ReadOnlyMemory<byte> signature = default;
        long total = 0;
        var exceeded = false;
        Stack<DirectoryInfo> pending = new([new DirectoryInfo(directory)]);

        while (pending.Count > 0 && !exceeded)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos("*", options))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(directory, entry.FullName).Replace('\\', '/');
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null)
                {
                    links.Add(relative);
                    continue;
                }

                if (entry is DirectoryInfo subdirectory)
                {
                    pending.Push(subdirectory);
                    continue;
                }

                var file = (FileInfo)entry;
                var isManifest = string.Equals(relative, KnowledgePackFormat.ManifestFileName, StringComparison.Ordinal);
                var isSignature = string.Equals(relative, KnowledgePackFormat.SignatureFileName, StringComparison.Ordinal);
                var bound = isManifest ? limits.MaxManifestBytes : isSignature ? limits.MaxSignatureBytes : limits.MaxFileBytes;
                if (file.Length > bound
                    || (!isManifest && !isSignature && (files.Count >= limits.MaxFiles || (total += file.Length) > limits.MaxTotalBytes)))
                {
                    exceeded = true;
                    break;
                }

                var bytes = await File.ReadAllBytesAsync(file.FullName, ct);
                if (isManifest)
                {
                    manifest = bytes;
                }
                else if (isSignature)
                {
                    signature = bytes;
                }
                else
                {
                    files.Add(relative, bytes);
                }
            }
        }

        return new RawKnowledgePack
        {
            ManifestBytes = manifest,
            SignatureBytes = signature,
            Files = files,
            LinkedEntries = links,
            ExceededLimits = exceeded,
        };
    }
}
