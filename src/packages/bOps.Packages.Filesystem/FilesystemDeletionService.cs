// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Runtime.CompilerServices;
using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

public sealed class FilesystemDeletionException : Exception
{
    public FilesystemDeletionException()
    {
    }

    public FilesystemDeletionException(string message)
        : base(message)
    {
    }

    public FilesystemDeletionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class FilesystemDeletionService
{
    private const int StoreBufferSize = 128;
    private static readonly SearchValues<char> PatternCharacters = SearchValues.Create("*?[]{}");
    private readonly FilesystemPathPolicy _pathPolicy;
    private readonly FilesystemInventoryOptions _options;
    private readonly FilesystemInventoryService _inventory;
    private readonly SqliteFilesystemManifestStore _inventoryStore;
    private readonly SqliteDeletionManifestStore _deletionStore;
    private readonly TimeProvider _timeProvider;
    private readonly Func<int, CancellationToken, Task>? _beforeDelete;

    public FilesystemDeletionService(
        FilesystemPathPolicy pathPolicy,
        FilesystemInventoryOptions options,
        FilesystemInventoryService inventory,
        SqliteFilesystemManifestStore inventoryStore,
        SqliteDeletionManifestStore deletionStore,
        TimeProvider? timeProvider = null)
        : this(pathPolicy, options, inventory, inventoryStore, deletionStore, timeProvider, beforeDelete: null)
    {
    }

    internal FilesystemDeletionService(
        FilesystemPathPolicy pathPolicy,
        FilesystemInventoryOptions options,
        FilesystemInventoryService inventory,
        SqliteFilesystemManifestStore inventoryStore,
        SqliteDeletionManifestStore deletionStore,
        TimeProvider? timeProvider,
        Func<int, CancellationToken, Task>? beforeDelete)
    {
        _pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _inventoryStore = inventoryStore ?? throw new ArgumentNullException(nameof(inventoryStore));
        _deletionStore = deletionStore ?? throw new ArgumentNullException(nameof(deletionStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _beforeDelete = beforeDelete;
    }

    public async Task<DeletionManifestSummary> PrepareAsync(
        DeletionManifestRequest request,
        ToolExecutionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ValidateRequest(request);
        var warnings = new List<string>();
        var roots = NormalizeRoots(request.Roots, warnings);
        var scope = FilesystemManifestScope.FromExecutionContext(context);
        var createdAtUtc = _timeProvider.GetUtcNow();
        var expiresAtUtc = createdAtUtc + _options.DeletionApprovalWindow;
        var retainUntilUtc = createdAtUtc + _options.DeletionResultRetention;
        await _deletionStore.CleanupAsync(createdAtUtc, ct);
        var manifestId = await _deletionStore.StartAsync(
            scope, roots, warnings, createdAtUtc, expiresAtUtc, retainUntilUtc, ct);

        var started = _timeProvider.GetTimestamp();
        var ordinal = 0;
        var fileCount = 0;
        var directoryCount = 0;
        var linkCount = 0;
        long totalBytes = 0;
        var buffer = new List<DeletionManifestEntry>(StoreBufferSize);

        try
        {
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                var remainingDuration = request.MaxDuration - _timeProvider.GetElapsedTime(started);
                var remainingEntries = request.MaxEntries - ordinal;
                if (remainingDuration <= TimeSpan.Zero || remainingEntries <= 0)
                {
                    throw new FilesystemDeletionException("The deletion preflight exceeded its hard work ceiling.");
                }

                var inventoryRequest = new FilesystemInventoryRequest(
                    root,
                    request.MaxDepth,
                    remainingEntries,
                    TopEntries: 0,
                    remainingDuration,
                    Exact: true);
                var result = await _inventory.CreateAsync(inventoryRequest, context, ct);
                if (!result.Complete || result.Manifest is null)
                {
                    throw new FilesystemDeletionException(
                        $"The exact inventory for '{root}' was incomplete; no deletion manifest was created.");
                }

                fileCount = checked(fileCount + result.FileCount);
                directoryCount = checked(directoryCount + result.DirectoryCount);
                linkCount = checked(linkCount + result.LinkCount);
                totalBytes = checked(totalBytes + result.TotalBytes);

                await foreach (var source in _inventoryStore.ReadEntriesAsync(result.Manifest.Id, scope, ct))
                {
                    var absolutePath = source.RelativePath == "."
                        ? root
                        : Path.Combine(root, source.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    buffer.Add(new DeletionManifestEntry(
                        ordinal++,
                        Path.GetFullPath(absolutePath),
                        root,
                        source.RelativePath,
                        source.Type,
                        source.SizeBytes,
                        source.CreationTimeUtcTicks,
                        source.LastWriteTimeUtcTicks,
                        source.Attributes,
                        source.LinkTarget,
                        RelativeDepth(source.RelativePath)));
                    if (buffer.Count == StoreBufferSize)
                    {
                        await _deletionStore.AppendEntriesAsync(manifestId, buffer, ct);
                        buffer.Clear();
                    }
                }
            }

            if (buffer.Count > 0)
            {
                await _deletionStore.AppendEntriesAsync(manifestId, buffer, ct);
            }

            return await _deletionStore.CompleteAsync(
                manifestId,
                scope,
                ordinal,
                fileCount,
                directoryCount,
                linkCount,
                totalBytes,
                ct);
        }
        catch (OperationCanceledException)
        {
            await TryRejectAsync(manifestId, scope);
            throw;
        }
        catch (Exception ex) when (ex is FilesystemInventoryException or IOException or UnauthorizedAccessException or OverflowException)
        {
            await _deletionStore.MarkRejectedAsync(manifestId, scope, CancellationToken.None);
            throw ex is FilesystemDeletionException
                ? ex
                : new FilesystemDeletionException("Could not build a complete deletion manifest.", ex);
        }
        catch (FilesystemDeletionException)
        {
            await _deletionStore.MarkRejectedAsync(manifestId, scope, CancellationToken.None);
            throw;
        }
    }

    public Task<DeletionManifestSummary?> TryGetSummaryAsync(
        string manifestId,
        ToolExecutionContext context,
        CancellationToken ct = default) =>
        _deletionStore.TryGetSummaryAsync(
            manifestId,
            FilesystemManifestScope.FromExecutionContext(context),
            _timeProvider.GetUtcNow(),
            ct);

    public Task<DeletionManifestPage?> TryGetPageAsync(
        string manifestId,
        ToolExecutionContext context,
        string? cursor,
        int? limit,
        string? search,
        CancellationToken ct = default)
    {
        var pageSize = limit ?? _options.DefaultManifestPageSize;
        if (pageSize < 1 || pageSize > _options.MaximumManifestPageSize)
        {
            throw new FilesystemDeletionException(
                $"limit must be between 1 and {_options.MaximumManifestPageSize}.");
        }

        return _deletionStore.TryGetPageAsync(
            manifestId,
            FilesystemManifestScope.FromExecutionContext(context),
            _timeProvider.GetUtcNow(),
            cursor,
            pageSize,
            string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            ct);
    }

    public async IAsyncEnumerable<DeletionManifestEntry> ReadEntriesAsync(
        string manifestId,
        ToolExecutionContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var scope = FilesystemManifestScope.FromExecutionContext(context);
        if (await _deletionStore.TryGetSummaryAsync(manifestId, scope, _timeProvider.GetUtcNow(), ct) is null)
        {
            yield break;
        }

        await foreach (var entry in _deletionStore.ReadEntriesAsync(manifestId, scope, childrenFirst: false, ct))
        {
            yield return entry;
        }
    }

    internal async Task<DeletionApprovalBindingResult> BindDecisionAsync(
        string manifestId,
        string approvalHash,
        ToolExecutionContext context,
        bool approved,
        CancellationToken ct) =>
        await _deletionStore.BindDecisionAsync(
            manifestId,
            approvalHash,
            FilesystemManifestScope.FromExecutionContext(context),
            approved,
            _timeProvider.GetUtcNow(),
            ct);

    public async Task<DeletionExecutionResult> ExecuteAsync(
        string manifestId,
        string approvalHash,
        ToolExecutionContext context,
        CancellationToken ct = default)
    {
        var scope = FilesystemManifestScope.FromExecutionContext(context);
        var summary = await _deletionStore.TryGetSummaryAsync(manifestId, scope, _timeProvider.GetUtcNow(), ct)
            ?? throw new FilesystemDeletionException("The deletion manifest does not exist in this execution scope.");
        if (summary.Status != DeletionManifestStatus.Approved
            || !string.Equals(summary.ApprovalHash, approvalHash, StringComparison.Ordinal))
        {
            throw new FilesystemDeletionException(
                $"The deletion manifest is not approved and fresh for this exact hash (status: {summary.Status}).");
        }

        if (!await _deletionStore.TryBeginExecutionAsync(
            manifestId, approvalHash, scope, _timeProvider.GetUtcNow(), ct))
        {
            throw new FilesystemDeletionException("The deletion manifest was already consumed, expired or changed.");
        }

        var entries = new List<DeletionManifestEntry>(summary.EntryCount);
        await foreach (var entry in _deletionStore.ReadEntriesAsync(manifestId, scope, childrenFirst: false, ct))
        {
            entries.Add(entry);
        }

        try
        {
            if (ValidateCompleteSet(entries) is { } drift)
            {
                await _deletionStore.RecordResultAsync(
                    manifestId, scope, drift.Ordinal, "drift", drift.Error, _timeProvider.GetUtcNow(), ct);
                await _deletionStore.MarkPartiallyCompletedAsync(manifestId, scope, ct);
                return new DeletionExecutionResult(
                    manifestId,
                    approvalHash,
                    DeletionManifestStatus.PartiallyCompleted,
                    summary.EntryCount,
                    summary.TotalBytes,
                    0,
                    1);
            }

            var attempt = 0;
            foreach (var entry in entries
                .OrderByDescending(item => item.Depth)
                .ThenByDescending(item => item.AbsolutePath, PathComparer))
            {
                ct.ThrowIfCancellationRequested();
                var immediate = ValidateEntry(entry, exactMetadata: entry.Type != "directory");
                if (immediate is not null)
                {
                    await _deletionStore.RecordResultAsync(
                        manifestId, scope, entry.Ordinal, "drift", immediate, _timeProvider.GetUtcNow(), ct);
                    await _deletionStore.MarkPartiallyCompletedAsync(manifestId, scope, ct);
                    break;
                }

                try
                {
                    if (_beforeDelete is not null)
                    {
                        await _beforeDelete(attempt, ct);
                    }

                    attempt++;
                    DeleteEntry(entry);
                    await _deletionStore.RecordResultAsync(
                        manifestId, scope, entry.Ordinal, "deleted", null, _timeProvider.GetUtcNow(), ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await _deletionStore.RecordResultAsync(
                        manifestId, scope, entry.Ordinal, "failed", ex.Message, _timeProvider.GetUtcNow(), ct);
                    await _deletionStore.MarkPartiallyCompletedAsync(manifestId, scope, ct);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            await _deletionStore.MarkPartiallyCompletedAsync(manifestId, scope, CancellationToken.None);
            throw;
        }

        var completed = await _deletionStore.TryGetSummaryAsync(manifestId, scope, _timeProvider.GetUtcNow(), ct)
            ?? throw new InvalidOperationException("Deletion manifest disappeared after execution.");
        return new DeletionExecutionResult(
            manifestId,
            approvalHash,
            completed.Status,
            completed.EntryCount,
            completed.TotalBytes,
            completed.DeletedCount,
            completed.FailureCount);
    }

    public async Task<DeletionVerificationResult> VerifyAsync(
        string manifestId,
        string approvalHash,
        ToolExecutionContext context,
        CancellationToken ct = default)
    {
        var scope = FilesystemManifestScope.FromExecutionContext(context);
        var summary = await _deletionStore.TryGetSummaryAsync(manifestId, scope, _timeProvider.GetUtcNow(), ct)
            ?? throw new FilesystemDeletionException("The deletion manifest does not exist in this execution scope.");
        if (!string.Equals(summary.ApprovalHash, approvalHash, StringComparison.Ordinal))
        {
            throw new FilesystemDeletionException("The verification hash does not match the deletion manifest.");
        }

        var absent = 0;
        var remaining = 0;
        var changed = 0;
        var inaccessible = 0;
        await foreach (var entry in _deletionStore.ReadEntriesAsync(manifestId, scope, childrenFirst: false, ct))
        {
            try
            {
                if (!EntryExists(entry.AbsolutePath))
                {
                    absent++;
                    continue;
                }

                var actual = ReadEntry(entry.AbsolutePath, entry.RootPath, entry.RelativePath, entry.Ordinal);
                if (Matches(entry, actual, exactMetadata: true))
                {
                    remaining++;
                }
                else
                {
                    changed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                inaccessible++;
            }
        }

        var status = remaining > 0 || changed > 0
            ? VerificationStatus.Refuted
            : inaccessible > 0
                ? VerificationStatus.Inconclusive
                : VerificationStatus.Confirmed;
        await _deletionStore.SetVerificationStatusAsync(manifestId, scope, status, ct);
        return new DeletionVerificationResult(
            manifestId, approvalHash, status, absent, remaining, changed, inaccessible);
    }

    private List<string> NormalizeRoots(IReadOnlyList<string> requested, List<string> warnings)
    {
        var normalized = new List<string>(requested.Count);
        foreach (var root in requested)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new FilesystemDeletionException("Deletion roots cannot be empty.");
            }

            if (root.AsSpan().IndexOfAny(PatternCharacters) >= 0)
            {
                throw new FilesystemDeletionException($"Deletion root '{root}' contains ambiguous glob metacharacters.");
            }

            var fullPath = Path.GetFullPath(root);
            if (!EntryExists(fullPath))
            {
                throw new FilesystemDeletionException($"Deletion root '{fullPath}' does not exist.");
            }

            var resolved = FilesystemPathPolicy.Resolve(fullPath);
            if (!_pathPolicy.AllowsRead(resolved) || !_pathPolicy.AllowsWrite(resolved))
            {
                throw new FilesystemDeletionException(
                    $"Deletion root '{root}' is not permitted for both read and write access.");
            }

            normalized.Add(fullPath);
        }

        normalized = normalized.Distinct(PathComparer).OrderBy(path => path, PathComparer).ToList();
        var reduced = new List<string>(normalized.Count);
        foreach (var candidate in normalized)
        {
            var coveringRoot = reduced.FirstOrDefault(root => IsSameOrDescendant(candidate, root));
            if (coveringRoot is not null)
            {
                warnings.Add($"Removed overlapping root '{candidate}' because '{coveringRoot}' already covers it.");
                continue;
            }

            reduced.Add(candidate);
        }

        return reduced;
    }

    private (int Ordinal, string Error)? ValidateCompleteSet(IReadOnlyList<DeletionManifestEntry> entries)
    {
        var approvedPaths = entries.Select(entry => entry.AbsolutePath).ToHashSet(PathComparer);
        foreach (var entry in entries)
        {
            var error = ValidateEntry(entry, exactMetadata: true);
            if (error is not null)
            {
                return (entry.Ordinal, error);
            }

            if (entry.Type != "directory")
            {
                continue;
            }

            try
            {
                var resolved = FilesystemPathPolicy.Resolve(entry.AbsolutePath);
                if (!_pathPolicy.AllowsRead(resolved) || !_pathPolicy.AllowsWrite(resolved))
                {
                    return (entry.Ordinal, $"Directory escaped policy before enumeration: {entry.AbsolutePath}");
                }

                foreach (var child in Directory.EnumerateFileSystemEntries(entry.AbsolutePath))
                {
                    if (!approvedPaths.Contains(Path.GetFullPath(child)))
                    {
                        return (entry.Ordinal, $"Unapproved entry was added after preflight: {child}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (entry.Ordinal, $"Could not reconcile '{entry.AbsolutePath}': {ex.Message}");
            }
        }

        return null;
    }

    private string? ValidateEntry(DeletionManifestEntry expected, bool exactMetadata)
    {
        try
        {
            if (!EntryExists(expected.AbsolutePath))
            {
                return $"Approved entry is missing: {expected.AbsolutePath}";
            }

            var resolved = FilesystemPathPolicy.Resolve(expected.AbsolutePath);
            if (!_pathPolicy.AllowsWrite(resolved))
            {
                return $"Approved entry escaped write policy: {expected.AbsolutePath}";
            }

            var actual = ReadEntry(expected.AbsolutePath, expected.RootPath, expected.RelativePath, expected.Ordinal);
            return Matches(expected, actual, exactMetadata)
                ? null
                : $"Approved entry changed after preflight: {expected.AbsolutePath}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not revalidate '{expected.AbsolutePath}': {ex.Message}";
        }
    }

    private DeletionManifestEntry ReadEntry(string path, string root, string relative, int ordinal)
    {
        var resolved = FilesystemPathPolicy.Resolve(path);
        if (!_pathPolicy.AllowsRead(resolved) || !_pathPolicy.AllowsWrite(resolved))
        {
            throw new UnauthorizedAccessException("Path is outside the filesystem policy.");
        }

        var attributes = File.GetAttributes(path);
        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        info.Refresh();
        var isLink = attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
        var type = isLink ? "link" : isDirectory ? "directory" : "file";
        return new DeletionManifestEntry(
            ordinal,
            Path.GetFullPath(path),
            root,
            relative,
            type,
            type == "file" ? ((FileInfo)info).Length : null,
            info.CreationTimeUtc.Ticks,
            info.LastWriteTimeUtc.Ticks,
            (int)attributes,
            isLink ? info.LinkTarget : null,
            RelativeDepth(relative));
    }

    private static bool Matches(
        DeletionManifestEntry expected,
        DeletionManifestEntry actual,
        bool exactMetadata)
    {
        // Linux exposes inode-change time through FileSystemInfo.CreationTimeUtc on filesystems
        // without a birth-time value. Removing an approved child legitimately changes that value
        // on its parent directory. The complete pre-delete reconciliation still compares it; the
        // later parent check cannot do so without treating our own child deletion as hostile drift.
        var creationMatches = expected.CreationTimeUtcTicks == actual.CreationTimeUtcTicks
            || (!exactMetadata && expected.Type == "directory" && OperatingSystem.IsLinux());
        return string.Equals(expected.AbsolutePath, actual.AbsolutePath, PathComparison)
        && string.Equals(expected.Type, actual.Type, StringComparison.Ordinal)
        && creationMatches
        && expected.Attributes == actual.Attributes
        && string.Equals(expected.LinkTarget, actual.LinkTarget, StringComparison.Ordinal)
        && (!exactMetadata
            || (expected.SizeBytes == actual.SizeBytes
                && expected.LastWriteTimeUtcTicks == actual.LastWriteTimeUtcTicks));
    }

    private static void DeleteEntry(DeletionManifestEntry entry)
    {
        if (entry.Type == "directory"
            || (entry.Type == "link" && ((FileAttributes)entry.Attributes).HasFlag(FileAttributes.Directory)))
        {
            Directory.Delete(entry.AbsolutePath, recursive: false);
        }
        else
        {
            File.Delete(entry.AbsolutePath);
        }
    }

    private void ValidateRequest(DeletionManifestRequest request)
    {
        if (request.Roots.Count < 1 || request.Roots.Count > _options.MaximumDeletionRoots)
        {
            throw new FilesystemDeletionException(
                $"roots must contain between 1 and {_options.MaximumDeletionRoots} paths.");
        }

        if (request.MaxDepth is < 0 || request.MaxDepth > _options.MaximumDepth)
        {
            throw new FilesystemDeletionException($"maxDepth must be between 0 and {_options.MaximumDepth}.");
        }

        if (request.MaxEntries is < 1 || request.MaxEntries > _options.MaximumEntries)
        {
            throw new FilesystemDeletionException($"maxEntries must be between 1 and {_options.MaximumEntries}.");
        }

        if (request.MaxDuration <= TimeSpan.Zero || request.MaxDuration > _options.MaximumDuration)
        {
            throw new FilesystemDeletionException(
                $"maxDuration must be positive and no greater than {_options.MaximumDuration}.");
        }
    }

    private async Task TryRejectAsync(string manifestId, FilesystemManifestScope scope)
    {
        try
        {
            await _deletionStore.MarkRejectedAsync(manifestId, scope, CancellationToken.None);
        }
        catch (IOException)
        {
            // The original cancellation remains authoritative; retention cleanup removes the row.
        }
    }

    private static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        if (string.Equals(candidate, root, PathComparison))
        {
            return true;
        }

        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", PathComparison)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison);
    }

    private static int RelativeDepth(string relativePath) =>
        relativePath == "."
            ? 0
            : relativePath.Count(character => character == '/' || character == '\\') + 1;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
