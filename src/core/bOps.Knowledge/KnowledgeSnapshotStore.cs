// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Knowledge;

/// <summary>
/// The content-addressed store of snapshots, partitioned by tenant: <c>root/tenant key/content hash</c>. There is no shared area and
/// no lookup that crosses a tenant, so the same pack admitted for two tenants is two separate copies, and a hash known to one tenant
/// finds nothing in another. A snapshot that already exists is never trusted: it is re-verified before it is reused, and a mismatch
/// refuses the admission instead of being repaired quietly.
/// </summary>
public sealed class KnowledgeSnapshotStore
{
    private readonly string root;

    /// <summary>Creates a store over a host-chosen, operator-protected directory.</summary>
    public KnowledgeSnapshotStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        root = Path.GetFullPath(rootDirectory);
    }

    /// <summary>
    /// Returns the snapshot of a verified pack for a tenant, writing it from the verifier's bytes if the tenant has none yet.
    /// <c>null</c> when a snapshot already stored for this tenant and content no longer matches the verified pack.
    /// </summary>
    public KnowledgeSnapshot? Acquire(TenantId tenant, VerifiedKnowledgePack pack)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(pack);

        var pin = KnowledgePin.For(tenant, pack);
        var tenantDirectory = Path.Combine(root, tenant.PartitionKey);
        var finalPath = Path.Combine(tenantDirectory, pack.Identity.ContentHash);
        if (Directory.Exists(finalPath))
        {
            var existing = new KnowledgeSnapshot(finalPath, pin);
            return existing.VerifyIntegrity() is null ? existing : null;
        }

        Directory.CreateDirectory(tenantDirectory);
        var temporary = Path.Combine(tenantDirectory, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            Write(temporary, pack);
            var staged = new KnowledgeSnapshot(temporary, pin);
            if (staged.VerifyIntegrity() is not null)
            {
                return null;
            }

            try
            {
                Directory.Move(temporary, finalPath);
            }
            catch (IOException) when (Directory.Exists(finalPath))
            {
                // A concurrent admission of the same content won the rename; its copy is checked like any stored one.
                var winner = new KnowledgeSnapshot(finalPath, pin);
                return winner.VerifyIntegrity() is null ? winner : null;
            }

            return new KnowledgeSnapshot(finalPath, pin);
        }
        finally
        {
            DeleteTree(temporary);
        }
    }

    private static void Write(string directory, VerifiedKnowledgePack pack)
    {
        var content = Path.Combine(directory, KnowledgeSnapshot.ContentDirectoryName);
        Directory.CreateDirectory(content);
        var contentRoot = Path.GetFullPath(content) + Path.DirectorySeparatorChar;
        foreach (var (relative, bytes) in pack.Files)
        {
            var target = Path.GetFullPath(Path.Combine(content, relative));
            if (!target.StartsWith(contentRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A verified pack path left the snapshot; the path policy should have refused it.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            WriteReadOnly(target, bytes);
        }

        WriteReadOnly(Path.Combine(directory, KnowledgeSnapshot.ManifestFileName), pack.ManifestBytes);
        WriteReadOnly(Path.Combine(directory, KnowledgeSnapshot.SignatureFileName), pack.SignatureBytes);
    }

    private static void WriteReadOnly(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
        File.SetAttributes(path, FileAttributes.ReadOnly);
    }

    private static void DeleteTree(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover staging directory holds only bytes bOps itself verified, in a tenant-private location; the next admission uses a new name.
        }
    }
}
