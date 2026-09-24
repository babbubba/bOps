// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.PluginHost;

/// <summary>Persistent generation and transaction roles. These types are internal because API projection belongs to M6.</summary>
internal sealed record PluginGeneration(
    string GenerationId,
    string RelativePath,
    string PackageDigestSha256,
    DateTimeOffset CreatedAtUtc);

internal enum PluginTransactionPhase
{
    None,
    CandidatePrepared,
    RollbackCaptured,
    PromotionStarted,
    CandidatePromoted,
    MetadataCommitStarted,
    Committed,
}

/// <summary>
/// The sole durable lifecycle authority for one plugin. Filesystem directories only prove bytes
/// exist; they never assign one of these roles during recovery (ADR-0037).
/// </summary>
internal sealed record PluginLifecycleMetadata(
    long LifecycleVersion,
    PluginLifecycleState State,
    string CurrentGenerationId,
    string? ActivationLkgGenerationId,
    string? TransactionRollbackGenerationId,
    string? CandidateGenerationId,
    PluginTransactionPhase TransactionPhase,
    IReadOnlyList<PluginGeneration> Generations,
    string? SanitizedFailure = null);

internal sealed record PluginLifecycleJournal(
    string OperationId,
    string PluginId,
    PluginTransactionPhase Phase,
    string? CurrentGenerationId,
    string? ActivationLkgGenerationId,
    string? RollbackGenerationId,
    string? CandidateGenerationId);

internal enum PluginIdempotencyStatus { InProgress, Completed, Failed }

internal sealed record PluginIdempotencyOperation(
    string NodeId,
    string ActorKind,
    string ActorId,
    string Key,
    PluginIdempotencyStatus Status,
    string? IntentFingerprint,
    string? ResultCategory,
    DateTimeOffset ExpiresAtUtc);

/// <summary>The authoritative state of a committed plugin generation (ADR-0037).</summary>
public enum PluginLifecycleState
{
    InstalledDisabled,
    Enabled,
    ActivationFailed,
    RecoveryRequired,
}

/// <summary>Neutral result category for lifecycle admission and mutation.</summary>
public enum PluginLifecycleResultCategory
{
    Succeeded,
    ArchiveInvalid,
    ArchiveLimitExceeded,
    ManifestInvalid,
    SignatureInvalid,
    PublisherUntrusted,
    CompatibilityRejected,
    IdentityConflict,
    VersionConflict,
    StaleVersion,
    ActivationConfirmationRequired,
    ActivationFailed,
    RecoveryRequired,
    InternalFailure,
}

/// <summary>Configured ZIP intake limits. Every value is bounded by the ADR-0037 hard ceiling.</summary>
public sealed record PluginArchiveLimits(
    long MaximumCompressedBytes = 64L * 1024 * 1024,
    int MaximumEntries = 2_048,
    long MaximumUncompressedBytes = 256L * 1024 * 1024,
    long MaximumEntryUncompressedBytes = 64L * 1024 * 1024,
    int MaximumDepth = 16,
    int MaximumPathLength = 240,
    int MaximumCompressionRatio = 100)
{
    internal const long HardMaximumCompressedBytes = 256L * 1024 * 1024;
    internal const int HardMaximumEntries = 10_000;
    internal const long HardMaximumUncompressedBytes = 1024L * 1024 * 1024;
    internal const long HardMaximumEntryUncompressedBytes = 256L * 1024 * 1024;
    internal const int HardMaximumDepth = 32;

    internal void Validate()
    {
        if (MaximumCompressedBytes <= 0 || MaximumCompressedBytes > HardMaximumCompressedBytes ||
            MaximumEntries <= 0 || MaximumEntries > HardMaximumEntries ||
            MaximumUncompressedBytes <= 0 || MaximumUncompressedBytes > HardMaximumUncompressedBytes ||
            MaximumEntryUncompressedBytes <= 0 || MaximumEntryUncompressedBytes > HardMaximumEntryUncompressedBytes ||
            MaximumEntryUncompressedBytes > MaximumUncompressedBytes ||
            MaximumDepth <= 0 || MaximumDepth > HardMaximumDepth ||
            MaximumPathLength <= 0 || MaximumPathLength > 240 ||
            MaximumCompressionRatio <= 0 || MaximumCompressionRatio > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(PluginArchiveLimits), "Plugin archive limits are missing, inconsistent, or exceed ADR-0037 hard ceilings.");
        }
    }
}

/// <summary>A sanitized archive intake error; absolute paths and archive contents are never included.</summary>
public sealed class PluginArchiveValidationException : Exception
{
    public PluginArchiveValidationException() : this(PluginLifecycleResultCategory.ArchiveInvalid, "Plugin archive validation failed.") { }
    public PluginArchiveValidationException(string message) : this(PluginLifecycleResultCategory.ArchiveInvalid, message) { }
    public PluginArchiveValidationException(string message, Exception innerException) : base(message, innerException) { Category = PluginLifecycleResultCategory.ArchiveInvalid; }
    public PluginArchiveValidationException(PluginLifecycleResultCategory category, string message) : base(message) { Category = category; }
    public PluginLifecycleResultCategory Category { get; }
}
