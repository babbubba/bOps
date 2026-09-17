// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

/// <summary>Host defaults and hard ceilings for bounded filesystem inventories.</summary>
public sealed class FilesystemInventoryOptions
{
    public int DefaultMaxDepth { get; set; } = 32;

    public int MaximumDepth { get; set; } = 128;

    public int DefaultMaxEntries { get; set; } = 10_000;

    public int MaximumEntries { get; set; } = 100_000;

    public int DefaultTopEntries { get; set; } = 10;

    public int MaximumTopEntries { get; set; } = 100;

    public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromSeconds(20);

    public TimeSpan MaximumDuration { get; set; } = TimeSpan.FromMinutes(2);

    public int MaximumWarnings { get; set; } = 32;

    public int MaximumOutputBytes { get; set; } = 32 * 1024;

    public string ManifestStorePath { get; set; } = "filesystem-inventory.db";

    public TimeSpan ManifestRetention { get; set; } = TimeSpan.FromHours(24);

    public int DefaultDeletionMaxRoots { get; set; } = 32;

    public int MaximumDeletionRoots { get; set; } = 64;

    public TimeSpan DeletionApprovalWindow { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan DeletionResultRetention { get; set; } = TimeSpan.FromDays(7);

    public int DefaultManifestPageSize { get; set; } = 200;

    public int MaximumManifestPageSize { get; set; } = 1_000;

    internal void Validate()
    {
        if (DefaultMaxDepth < 0 || MaximumDepth < DefaultMaxDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultMaxDepth), "Depth defaults and ceilings are inconsistent.");
        }

        if (DefaultMaxEntries < 1 || MaximumEntries < DefaultMaxEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultMaxEntries), "Entry defaults and ceilings are inconsistent.");
        }

        if (DefaultTopEntries < 0 || MaximumTopEntries < DefaultTopEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultTopEntries), "Top-entry defaults and ceilings are inconsistent.");
        }

        if (DefaultDuration <= TimeSpan.Zero || MaximumDuration < DefaultDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultDuration), "Duration defaults and ceilings are inconsistent.");
        }

        if (MaximumWarnings < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumWarnings));
        }

        if (MaximumOutputBytes < 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumOutputBytes));
        }

        if (string.IsNullOrWhiteSpace(ManifestStorePath))
        {
            throw new ArgumentException("A manifest store path is required.", nameof(ManifestStorePath));
        }

        if (ManifestRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ManifestRetention));
        }

        if (DefaultDeletionMaxRoots < 1 || MaximumDeletionRoots < DefaultDeletionMaxRoots)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultDeletionMaxRoots));
        }

        if (DeletionApprovalWindow <= TimeSpan.Zero || DeletionApprovalWindow > ManifestRetention)
        {
            throw new ArgumentOutOfRangeException(nameof(DeletionApprovalWindow));
        }

        if (DeletionResultRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DeletionResultRetention));
        }

        if (DefaultManifestPageSize < 1 || MaximumManifestPageSize < DefaultManifestPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultManifestPageSize));
        }
    }
}

/// <summary>The normalized limits for one inventory.</summary>
public sealed record FilesystemInventoryRequest(
    string Path,
    int MaxDepth,
    int MaxEntries,
    int TopEntries,
    TimeSpan MaxDuration,
    bool Exact);

/// <summary>Host-owned authorization scope persisted with an exact manifest.</summary>
public sealed record FilesystemManifestScope(NodeId Node, Guid TaskId, string ActorKind, string ActorId)
{
    public static FilesystemManifestScope FromExecutionContext(ToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new FilesystemManifestScope(context.Node, context.TaskId, context.Actor.Kind, context.Actor.Id);
    }
}

/// <summary>A bounded top-entry observation returned by <c>fs.size</c>.</summary>
public sealed record FilesystemTopEntry(string RelativePath, long SizeBytes);

/// <summary>Reference to a complete immutable inventory held outside tool output.</summary>
public sealed record FilesystemManifestReference(
    string Id,
    string ContentHash,
    DateTimeOffset ExpiresAtUtc,
    int EntryCount);

/// <summary>The bounded inventory result used by the tool and future filesystem capabilities.</summary>
public sealed record FilesystemInventoryResult(
    string Path,
    bool Complete,
    int FileCount,
    int DirectoryCount,
    int LinkCount,
    long TotalBytes,
    IReadOnlyList<FilesystemTopEntry> TopEntries,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed,
    FilesystemInventoryRequest Limits,
    FilesystemManifestReference? Manifest);

internal sealed record FilesystemInventoryEntry(
    string RelativePath,
    string Type,
    long? SizeBytes,
    long CreationTimeUtcTicks,
    long LastWriteTimeUtcTicks,
    int Attributes,
    string? LinkTarget);

internal sealed class InventoryTotals(int topCount)
{
    private readonly int _topCount = topCount;
    private readonly List<FilesystemTopEntry> _topEntries = new(topCount);

    public int EntryCount { get; private set; }

    public int FileCount { get; private set; }

    public int DirectoryCount { get; private set; }

    public int LinkCount { get; private set; }

    public long TotalBytes { get; private set; }

    public IReadOnlyList<FilesystemTopEntry> TopEntries => _topEntries;

    public bool TryAdd(FilesystemInventoryEntry entry)
    {
        EntryCount++;
        switch (entry.Type)
        {
            case "file":
                FileCount++;
                if (entry.SizeBytes is not { } size || !TryAddBytes(TotalBytes, size, out var total))
                {
                    return false;
                }

                TotalBytes = total;
                AddTopEntry(new FilesystemTopEntry(entry.RelativePath, size));
                return true;
            case "directory":
                DirectoryCount++;
                return true;
            case "link":
                LinkCount++;
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(entry), entry.Type, "Unknown filesystem entry type.");
        }
    }

    internal static bool TryAddBytes(long current, long next, out long total)
    {
        try
        {
            total = checked(current + next);
            return true;
        }
        catch (OverflowException)
        {
            total = current;
            return false;
        }
    }

    private void AddTopEntry(FilesystemTopEntry candidate)
    {
        if (_topCount == 0)
        {
            return;
        }

        _topEntries.Add(candidate);
        _topEntries.Sort(static (left, right) =>
        {
            var bySize = right.SizeBytes.CompareTo(left.SizeBytes);
            return bySize != 0 ? bySize : StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath);
        });
        if (_topEntries.Count > _topCount)
        {
            _topEntries.RemoveAt(_topEntries.Count - 1);
        }
    }
}
