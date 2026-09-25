// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace bOps.PluginHost;

/// <summary>
/// Performs the non-executing ZIP intake boundary from ADR-0037. It deliberately does not know
/// manifests, signatures, or activation: it returns only a backend-owned staging directory whose
/// complete entry set was structurally approved before the first plugin-controlled path is written.
/// </summary>
[SuppressMessage("Performance", "CA1812", Justification = "Constructed by the lifecycle service added in this milestone; it is intentionally internal to prevent callers from receiving a staging path.")]
internal sealed class PluginArchiveStager(string lifecycleRoot, PluginArchiveLimits limits)
{
    private const int BufferSize = 64 * 1024;

    public async Task<string> StageAsync(Stream archive, CancellationToken cancellationToken = default) =>
        (await StageWithDigestAsync(archive, null, cancellationToken)).Directory;

    /// <summary>
    /// Stages an archive and reports the SHA-256 of the received bytes. The digest is request identity
    /// only (ADR-0037); it never substitutes for signature or trust verification. <paramref name="onDigest"/>
    /// runs as soon as the bounded upload is fully received, before any parsing or extraction.
    /// </summary>
    public async Task<StagedArchive> StageWithDigestAsync(Stream archive, Func<string, Task>? onDigest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        limits.Validate();

        var root = Path.GetFullPath(lifecycleRoot);
        var uploadDirectory = Path.Combine(root, "uploads");
        var stagingDirectory = Path.Combine(root, "staging");
        Directory.CreateDirectory(uploadDirectory);
        Directory.CreateDirectory(stagingDirectory);
        var uploadPath = Path.Combine(uploadDirectory, $"{Guid.NewGuid():N}.zip");
        var operationDirectory = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N"));

        try
        {
            var digest = await CopyBoundedAsync(archive, uploadPath, cancellationToken);
            if (onDigest is not null)
            {
                await onDigest(digest);
            }

            using var file = new FileStream(uploadPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            var entries = ValidateEntries(zip);
            Directory.CreateDirectory(operationDirectory);
            await ExtractAsync(entries, operationDirectory, cancellationToken);
            return new StagedArchive(operationDirectory, digest);
        }
        catch (InvalidDataException)
        {
            DeleteIfPresent(operationDirectory);
            throw new PluginArchiveValidationException(PluginLifecycleResultCategory.ArchiveInvalid, "The plugin archive is not a valid ZIP archive.");
        }
        catch
        {
            DeleteIfPresent(operationDirectory);
            throw;
        }
        finally
        {
            DeleteIfPresent(uploadPath);
        }
    }

    private async Task<string> CopyBoundedAsync(Stream source, string target, CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                await destination.FlushAsync(cancellationToken);
                return Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            hash.AppendData(buffer, 0, read);

            total = checked(total + read);
            if (total > limits.MaximumCompressedBytes)
            {
                throw new PluginArchiveValidationException(PluginLifecycleResultCategory.ArchiveLimitExceeded, "The plugin archive exceeds the compressed-size limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private List<ApprovedEntry> ValidateEntries(ZipArchive zip)
    {
        if (zip.Entries.Count > limits.MaximumEntries)
        {
            throw Limit("entry-count");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var approved = new List<ApprovedEntry>(zip.Entries.Count);
        long declaredTotal = 0;
        foreach (var entry in zip.Entries)
        {
            var isDirectory = entry.FullName.EndsWith('/');
            var relative = Canonicalize(entry.FullName, isDirectory);
            if (!paths.Add(relative))
            {
                throw Invalid("The archive contains colliding entries.");
            }

            RejectSpecialEntry(entry);
            if (!isDirectory)
            {
                if (entry.Length < 0 || entry.Length > limits.MaximumEntryUncompressedBytes)
                {
                    throw Limit("single-entry size");
                }

                declaredTotal = checked(declaredTotal + entry.Length);
                if (declaredTotal > limits.MaximumUncompressedBytes || IsBomb(entry))
                {
                    throw Limit("uncompressed-size or compression-ratio");
                }
            }

            foreach (var parent in Parents(relative))
            {
                if (types.TryGetValue(parent, out var parentIsDirectory) && !parentIsDirectory)
                {
                    throw Invalid("The archive contains a file/directory collision.");
                }
            }

            if (types.TryGetValue(relative, out var prior) && prior != isDirectory)
            {
                throw Invalid("The archive contains a file/directory collision.");
            }

            types[relative] = isDirectory;
            approved.Add(new ApprovedEntry(entry, relative, isDirectory));
        }

        return approved;
    }

    private async Task ExtractAsync(List<ApprovedEntry> entries, string stagingRoot, CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var approved in entries)
        {
            var target = Path.GetFullPath(Path.Combine(stagingRoot, approved.RelativePath));
            if (!target.StartsWith(stagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("The archive contains a path outside the staging root.");
            }

            if (approved.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var parent = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(parent);
            EnsureOrdinaryParents(stagingRoot, parent);
            await using var source = await approved.Entry.OpenAsync(cancellationToken);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            var buffer = new byte[BufferSize];
            long perEntry = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                perEntry = checked(perEntry + read);
                total = checked(total + read);
                if (perEntry > limits.MaximumEntryUncompressedBytes || total > limits.MaximumUncompressedBytes)
                {
                    throw Limit("actual extracted bytes");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
    }

    private string Canonicalize(string entryName, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(entryName) || Path.IsPathRooted(entryName) || entryName.StartsWith("\\\\", StringComparison.Ordinal) ||
            entryName.Length >= 2 && char.IsLetter(entryName[0]) && entryName[1] == ':')
        {
            throw Invalid("The archive contains a rooted path.");
        }

        var parts = entryName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > limits.MaximumDepth || parts.Any(part => part is "." or ".."))
        {
            throw Invalid("The archive contains an invalid relative path.");
        }

        var canonical = string.Join('/', parts).Normalize(NormalizationForm.FormC);
        if (canonical.Length > limits.MaximumPathLength || canonical.StartsWith(".lifecycle/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(canonical, ".lifecycle", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(canonical, "plugins.json", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("The archive path is reserved or exceeds the path limit.");
        }

        return isDirectory ? canonical.TrimEnd('/') : canonical;
    }

    private static IEnumerable<string> Parents(string path)
    {
        var index = path.LastIndexOf('/');
        while (index > 0)
        {
            yield return path[..index];
            index = path.LastIndexOf('/', index - 1);
        }
    }

    private static bool IsBomb(ZipArchiveEntry entry) => entry.CompressedLength <= 0 ? entry.Length > 0 : entry.Length > entry.CompressedLength * 100;

    private static void RejectSpecialEntry(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixMode is 0xA000 or 0x6000 or 0x2000)
        {
            throw Invalid("The archive contains an unsupported special entry.");
        }
    }

    private static void EnsureOrdinaryParents(string root, string parent)
    {
        for (var current = parent; !string.Equals(current, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw Invalid("The staging path contains a reparse point.");
            }
        }
    }

    private static PluginArchiveValidationException Limit(string limit) => new(PluginLifecycleResultCategory.ArchiveLimitExceeded, $"The plugin archive exceeds the {limit} limit.");
    private static PluginArchiveValidationException Invalid(string message) => new(PluginLifecycleResultCategory.ArchiveInvalid, message);
    private static void DeleteIfPresent(string path) { if (File.Exists(path)) File.Delete(path); else if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    internal sealed record StagedArchive(string Directory, string ArchiveDigestSha256);
    private sealed record ApprovedEntry(ZipArchiveEntry Entry, string RelativePath, bool IsDirectory);
}
