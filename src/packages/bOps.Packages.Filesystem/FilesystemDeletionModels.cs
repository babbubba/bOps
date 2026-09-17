// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Filesystem;

public enum DeletionManifestStatus
{
    Building,
    Ready,
    Approved,
    Executing,
    PartiallyCompleted,
    Verified,
    Refuted,
    Inconclusive,
    Expired,
    Rejected,
}

public sealed record DeletionManifestRequest(
    IReadOnlyList<string> Roots,
    int MaxDepth,
    int MaxEntries,
    TimeSpan MaxDuration);

public sealed record DeletionManifestSummary(
    string Id,
    DeletionManifestStatus Status,
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> Warnings,
    string ApprovalHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int EntryCount,
    int FileCount,
    int DirectoryCount,
    int LinkCount,
    long TotalBytes,
    int DeletedCount,
    int FailureCount);

public sealed record DeletionManifestEntry(
    int Ordinal,
    string AbsolutePath,
    string RootPath,
    string RelativePath,
    string Type,
    long? SizeBytes,
    long CreationTimeUtcTicks,
    long LastWriteTimeUtcTicks,
    int Attributes,
    string? LinkTarget,
    int Depth,
    string? Outcome = null,
    string? Error = null);

public sealed record DeletionManifestPage(
    IReadOnlyList<DeletionManifestEntry> Entries,
    string? NextCursor);

public sealed record DeletionExecutionResult(
    string ManifestId,
    string ApprovalHash,
    DeletionManifestStatus Status,
    int EntryCount,
    long TotalBytes,
    int DeletedCount,
    int FailureCount);

public sealed record DeletionVerificationResult(
    string ManifestId,
    string ApprovalHash,
    VerificationStatus Status,
    int AbsentCount,
    int RemainingCount,
    int ChangedCount,
    int InaccessibleCount);

internal enum DeletionApprovalBindingResult
{
    Bound,
    Rejected,
    NotFound,
    HashMismatch,
    Expired,
    InvalidState,
}
