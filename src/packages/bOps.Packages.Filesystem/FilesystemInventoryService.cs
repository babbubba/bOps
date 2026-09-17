// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Data.Sqlite;

namespace bOps.Packages.Filesystem;

/// <summary>Expected root or policy failure while creating a filesystem inventory.</summary>
public sealed class FilesystemInventoryException : Exception
{
    public FilesystemInventoryException()
    {
    }

    public FilesystemInventoryException(string message)
        : base(message)
    {
    }

    public FilesystemInventoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Streams bounded filesystem summaries and optional exact durable manifests.</summary>
public sealed class FilesystemInventoryService
{
    private const int StoreBufferSize = 128;
    private readonly FilesystemPathPolicy _pathPolicy;
    private readonly FilesystemInventoryOptions _options;
    private readonly SqliteFilesystemManifestStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly Action<int>? _entryObserved;

    public FilesystemInventoryService(
        FilesystemPathPolicy pathPolicy,
        FilesystemInventoryOptions options,
        SqliteFilesystemManifestStore store,
        TimeProvider? timeProvider = null)
        : this(pathPolicy, options, store, timeProvider ?? TimeProvider.System, entryObserved: null)
    {
    }

    internal FilesystemInventoryService(
        FilesystemPathPolicy pathPolicy,
        FilesystemInventoryOptions options,
        SqliteFilesystemManifestStore store,
        TimeProvider timeProvider,
        Action<int>? entryObserved)
    {
        _pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _entryObserved = entryObserved;
    }

    public async Task<FilesystemInventoryResult> CreateAsync(
        FilesystemInventoryRequest request,
        ToolExecutionContext? executionContext = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        if (request.Exact && executionContext is null)
        {
            throw new FilesystemInventoryException("An exact inventory requires host-owned tool execution context.");
        }

        ct.ThrowIfCancellationRequested();
        var requestedRoot = Path.GetFullPath(request.Path);
        string resolvedRoot;
        try
        {
            resolvedRoot = FilesystemPathPolicy.Resolve(requestedRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FilesystemInventoryException($"Could not resolve '{requestedRoot}'.", ex);
        }

        if (!_pathPolicy.AllowsRead(resolvedRoot))
        {
            throw new FilesystemInventoryException($"Path not permitted for read access by filesystem policy: {request.Path}");
        }

        if (!File.Exists(requestedRoot) && !Directory.Exists(requestedRoot))
        {
            throw new FilesystemInventoryException($"'{requestedRoot}' does not exist.");
        }

        var startedTimestamp = _timeProvider.GetTimestamp();
        var createdAtUtc = _timeProvider.GetUtcNow();
        var expiresAtUtc = createdAtUtc + _options.ManifestRetention;
        var scope = executionContext is null ? null : FilesystemManifestScope.FromExecutionContext(executionContext);
        string? manifestId = null;
        if (request.Exact)
        {
            await _store.CleanupExpiredAsync(createdAtUtc, ct);
            manifestId = await _store.StartAsync(scope!, requestedRoot, request, createdAtUtc, expiresAtUtc, ct);
        }

        var totals = new InventoryTotals(request.TopEntries);
        var warnings = new BoundedWarnings(_options.MaximumWarnings);
        var storeBuffer = request.Exact ? new List<FilesystemInventoryEntry>(StoreBufferSize) : null;
        var complete = true;

        try
        {
            var rootEntry = ReadEntry(requestedRoot, ".", out var rootIsDirectory, out var rootIsLink);
            if (rootEntry is null)
            {
                throw new FilesystemInventoryException($"Could not read metadata for '{requestedRoot}'.");
            }

            if (!totals.TryAdd(rootEntry))
            {
                complete = false;
                warnings.Add("Total byte count overflowed Int64; the result is incomplete.");
            }

            await BufferAsync(manifestId, rootEntry, storeBuffer, ct);
            _entryObserved?.Invoke(totals.EntryCount);

            if (rootIsDirectory && !rootIsLink && complete)
            {
                var frames = new Stack<TraversalFrame>();
                if (request.MaxDepth == 0)
                {
                    if (HasAnyEntry(requestedRoot, warnings))
                    {
                        complete = false;
                        warnings.Add("Maximum depth was reached before the tree was fully enumerated.");
                    }
                }
                // Ownership transfers to the stack; every pop disposes, and the final sweep
                // disposes frames left when a bound stops traversal. CA2000 cannot model that
                // collection ownership transfer.
#pragma warning disable CA2000
                else if (OpenFrame(requestedRoot, ".", depth: 0, warnings) is { } rootFrame)
#pragma warning restore CA2000
                {
                    frames.Push(rootFrame);
                }
                else
                {
                    complete = false;
                }

                while (frames.Count > 0 && complete)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_timeProvider.GetElapsedTime(startedTimestamp) >= request.MaxDuration)
                    {
                        complete = false;
                        warnings.Add("Maximum collection duration was reached before enumeration completed.");
                        break;
                    }

                    var frame = frames.Peek();
                    // Directory.EnumerateFileSystemEntries opens one native enumeration handle
                    // on the first MoveNext. Revalidate immediately before that operation; the
                    // handle then remains bound to the opened directory until exhaustion. The
                    // second check below, at exhaustion, detects mutations during the walk.
                    if (!frame.Started && !DirectoryStampMatches(frame))
                    {
                        complete = false;
                        warnings.Add($"Directory changed or escaped policy during enumeration: {frame.RelativePath}");
                        break;
                    }

                    frame.Started = true;

                    string childPath;
                    try
                    {
                        if (!frame.Enumerator.MoveNext())
                        {
                            frame.Dispose();
                            frames.Pop();
                            if (!DirectoryStampMatches(frame))
                            {
                                complete = false;
                                warnings.Add($"Directory changed during enumeration: {frame.RelativePath}");
                            }

                            continue;
                        }

                        childPath = frame.Enumerator.Current;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        frame.Dispose();
                        frames.Pop();
                        complete = false;
                        warnings.Add($"Could not enumerate '{frame.RelativePath}': {ex.Message}");
                        continue;
                    }

                    if (totals.EntryCount >= request.MaxEntries)
                    {
                        complete = false;
                        warnings.Add("Maximum entry count was reached before enumeration completed.");
                        break;
                    }

                    var relativePath = NormalizeRelativePath(Path.GetRelativePath(requestedRoot, childPath));
                    FilesystemInventoryEntry? entry;
                    bool isDirectory;
                    bool isLink;
                    try
                    {
                        entry = ReadEntry(childPath, relativePath, out isDirectory, out isLink);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        complete = false;
                        warnings.Add($"Could not inspect '{relativePath}': {ex.Message}");
                        continue;
                    }

                    if (entry is null)
                    {
                        complete = false;
                        warnings.Add($"Path is outside the read policy after resolution: {relativePath}");
                        continue;
                    }

                    if (!totals.TryAdd(entry))
                    {
                        complete = false;
                        warnings.Add("Total byte count overflowed Int64; the result is incomplete.");
                        break;
                    }

                    await BufferAsync(manifestId, entry, storeBuffer, ct);
                    _entryObserved?.Invoke(totals.EntryCount);

                    if (!isDirectory || isLink)
                    {
                        continue;
                    }

                    var childDepth = frame.Depth + 1;
                    if (childDepth >= request.MaxDepth)
                    {
                        if (HasAnyEntry(childPath, warnings))
                        {
                            complete = false;
                            warnings.Add($"Maximum depth was reached at '{relativePath}'.");
                        }

                        continue;
                    }

                    // Same stack-owned lifetime as the root frame above.
#pragma warning disable CA2000
                    if (OpenFrame(childPath, relativePath, childDepth, warnings) is { } childFrame)
#pragma warning restore CA2000
                    {
                        frames.Push(childFrame);
                    }
                    else
                    {
                        complete = false;
                    }
                }

                foreach (var frame in frames)
                {
                    frame.Dispose();
                }
            }

            if (storeBuffer is { Count: > 0 })
            {
                await _store.AppendAsync(manifestId!, storeBuffer, ct);
                storeBuffer.Clear();
            }

            if (complete && manifestId is not null)
            {
                complete = await ValidateStoredEntriesAsync(manifestId, scope!, requestedRoot, warnings, ct);
            }

            FilesystemManifestReference? manifest = null;
            if (manifestId is not null)
            {
                if (complete)
                {
                    manifest = await _store.CompleteAsync(
                        manifestId, scope!, requestedRoot, request, expiresAtUtc, totals.EntryCount, ct);
                }
                else
                {
                    await _store.MarkIncompleteAsync(manifestId, ct);
                }
            }

            return new FilesystemInventoryResult(
                requestedRoot,
                complete,
                totals.FileCount,
                totals.DirectoryCount,
                totals.LinkCount,
                totals.TotalBytes,
                totals.TopEntries,
                warnings.Items,
                _timeProvider.GetElapsedTime(startedTimestamp),
                request,
                manifest);
        }
        catch (OperationCanceledException)
        {
            if (manifestId is not null)
            {
                await TryMarkIncompleteAfterCancellationAsync(manifestId);
            }

            throw;
        }
        catch
        {
            if (manifestId is not null)
            {
                await _store.MarkIncompleteAsync(manifestId, CancellationToken.None);
            }

            throw;
        }
    }

    private FilesystemInventoryEntry? ReadEntry(
        string path,
        string relativePath,
        out bool isDirectory,
        out bool isLink)
    {
        var resolved = FilesystemPathPolicy.Resolve(path);
        if (!_pathPolicy.AllowsRead(resolved))
        {
            isDirectory = false;
            isLink = false;
            return null;
        }

        var attributes = File.GetAttributes(path);
        isDirectory = attributes.HasFlag(FileAttributes.Directory);
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        info.Refresh();
        isLink = attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
        var type = isLink ? "link" : isDirectory ? "directory" : "file";
        var size = type == "file" ? ((FileInfo)info).Length : (long?)null;
        return new FilesystemInventoryEntry(
            relativePath,
            type,
            size,
            info.CreationTimeUtc.Ticks,
            info.LastWriteTimeUtc.Ticks,
            (int)attributes,
            isLink ? info.LinkTarget : null);
    }

    private async Task<bool> ValidateStoredEntriesAsync(
        string manifestId,
        FilesystemManifestScope scope,
        string requestedRoot,
        BoundedWarnings warnings,
        CancellationToken ct)
    {
        await foreach (var expected in _store.ReadEntriesAsync(manifestId, scope, ct))
        {
            var path = expected.RelativePath == "."
                ? requestedRoot
                : Path.Combine(requestedRoot, expected.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var actual = ReadEntry(path, expected.RelativePath, out _, out _);
                if (actual is null || actual != expected)
                {
                    warnings.Add($"Path changed while the exact inventory was being created: {expected.RelativePath}");
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not revalidate '{expected.RelativePath}': {ex.Message}");
                return false;
            }
        }

        return true;
    }

    private async Task BufferAsync(
        string? manifestId,
        FilesystemInventoryEntry entry,
        List<FilesystemInventoryEntry>? buffer,
        CancellationToken ct)
    {
        if (buffer is null)
        {
            return;
        }

        buffer.Add(entry);
        if (buffer.Count == StoreBufferSize)
        {
            await _store.AppendAsync(manifestId!, buffer, ct);
            buffer.Clear();
        }
    }

    private TraversalFrame? OpenFrame(
        string directory,
        string relativePath,
        int depth,
        BoundedWarnings warnings)
    {
        try
        {
            var resolved = FilesystemPathPolicy.Resolve(directory);
            if (!_pathPolicy.AllowsRead(resolved))
            {
                warnings.Add($"Directory is outside the read policy after resolution: {relativePath}");
                return null;
            }

            var info = new DirectoryInfo(directory);
            info.Refresh();
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
            {
                warnings.Add($"Directory became a link before enumeration: {relativePath}");
                return null;
            }

            return new TraversalFrame(
                directory,
                relativePath,
                depth,
                info.CreationTimeUtc.Ticks,
                info.LastWriteTimeUtc.Ticks,
                info.Attributes,
                Directory.EnumerateFileSystemEntries(directory).GetEnumerator());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not enumerate '{relativePath}': {ex.Message}");
            return null;
        }
    }

    private bool DirectoryStampMatches(TraversalFrame frame)
    {
        try
        {
            var resolved = FilesystemPathPolicy.Resolve(frame.DirectoryPath);
            if (!_pathPolicy.AllowsRead(resolved))
            {
                return false;
            }

            var info = new DirectoryInfo(frame.DirectoryPath);
            info.Refresh();
            return info.Exists
                && !info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                && info.LinkTarget is null
                && info.CreationTimeUtc.Ticks == frame.CreationTimeUtcTicks
                && info.LastWriteTimeUtc.Ticks == frame.LastWriteTimeUtcTicks
                && info.Attributes == frame.Attributes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool HasAnyEntry(string directory, BoundedWarnings warnings)
    {
        try
        {
            var resolved = FilesystemPathPolicy.Resolve(directory);
            if (!_pathPolicy.AllowsRead(resolved))
            {
                warnings.Add($"Directory is outside the read policy after resolution: {directory}");
                return true;
            }

            var info = new DirectoryInfo(directory);
            info.Refresh();
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
            {
                warnings.Add($"Directory became a link before depth probing: {directory}");
                return true;
            }

            using var enumerator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            return enumerator.MoveNext();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not inspect whether '{directory}' contains deeper entries: {ex.Message}");
            return true;
        }
    }

    private async Task TryMarkIncompleteAfterCancellationAsync(string manifestId)
    {
        try
        {
            await _store.MarkIncompleteAsync(manifestId, CancellationToken.None);
        }
        catch (SqliteException)
        {
            // The original cancellation remains authoritative. Expiry cleanup removes a building
            // row if the best-effort terminal marker cannot be written.
        }
    }

    private void ValidateRequest(FilesystemInventoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            throw new FilesystemInventoryException("path is required.");
        }

        if (request.MaxDepth is < 0 || request.MaxDepth > _options.MaximumDepth)
        {
            throw new FilesystemInventoryException($"maxDepth must be between 0 and {_options.MaximumDepth}.");
        }

        if (request.MaxEntries is < 1 || request.MaxEntries > _options.MaximumEntries)
        {
            throw new FilesystemInventoryException($"maxEntries must be between 1 and {_options.MaximumEntries}.");
        }

        if (request.TopEntries is < 0 || request.TopEntries > _options.MaximumTopEntries)
        {
            throw new FilesystemInventoryException($"topEntries must be between 0 and {_options.MaximumTopEntries}.");
        }

        if (request.MaxDuration <= TimeSpan.Zero || request.MaxDuration > _options.MaximumDuration)
        {
            throw new FilesystemInventoryException(
                $"maxDuration must be positive and no greater than {_options.MaximumDuration}.");
        }
    }

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private sealed class TraversalFrame(
        string directoryPath,
        string relativePath,
        int depth,
        long creationTimeUtcTicks,
        long lastWriteTimeUtcTicks,
        FileAttributes attributes,
        IEnumerator<string> enumerator) : IDisposable
    {
        public string DirectoryPath { get; } = directoryPath;

        public string RelativePath { get; } = relativePath;

        public int Depth { get; } = depth;

        public long CreationTimeUtcTicks { get; } = creationTimeUtcTicks;

        public long LastWriteTimeUtcTicks { get; } = lastWriteTimeUtcTicks;

        public FileAttributes Attributes { get; } = attributes;

        public IEnumerator<string> Enumerator { get; } = enumerator;

        public bool Started { get; set; }

        public void Dispose() => Enumerator.Dispose();
    }

    private sealed class BoundedWarnings(int maximum)
    {
        private readonly List<string> _items = [];

        public IReadOnlyList<string> Items => _items;

        public void Add(string warning)
        {
            if (_items.Count < maximum)
            {
                _items.Add(warning);
            }
        }
    }
}
