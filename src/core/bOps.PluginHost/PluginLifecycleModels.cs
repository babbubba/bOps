// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.PluginHost;

/// <summary>Persistent generation and transaction roles. These types are internal because API projection belongs to M6.</summary>
internal sealed record PluginGeneration(
    string GenerationId,
    string RelativePath,
    string PackageDigestSha256,
    DateTimeOffset CreatedAtUtc,
    bool WasActivationLkg = false,
    DateTimeOffset? RetiredAtUtc = null);

internal enum PluginTransactionPhase
{
    None,
    CandidatePrepared,
    RollbackCaptured,
    PromotionStarted,
    CandidatePromoted,
    MetadataCommitStarted,
    Committed,

    /// <summary>Recovery has begun restoring the transaction-rollback generation; a rerun treats "already restored" as done.</summary>
    Restoring,
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
    string? CandidateGenerationId,
    bool CandidateFromRetained = false);

internal static class PluginLifecycleTransitions
{
    /// <summary>
    /// A successful activation: the current generation becomes activation LKG. The previous
    /// activation LKG, when distinct, starts its bounded retention clock (ADR-0037).
    /// </summary>
    public static PluginLifecycleMetadata WithActivationLkg(PluginLifecycleMetadata lifecycle, DateTimeOffset now)
    {
        var previous = lifecycle.ActivationLkgGenerationId;
        var generations = lifecycle.Generations.Select(generation =>
            generation.GenerationId == lifecycle.CurrentGenerationId
                ? generation with { WasActivationLkg = true, RetiredAtUtc = null }
                : previous is not null && generation.GenerationId == previous
                    ? generation with { RetiredAtUtc = now }
                    : generation).ToList();
        return lifecycle with { ActivationLkgGenerationId = lifecycle.CurrentGenerationId, Generations = generations };
    }
}

internal enum PluginIdempotencyStatus { InProgress, Completed, Failed }

internal sealed record PluginIdempotencyOperation(
    string NodeId,
    string ActorKind,
    string ActorId,
    string Key,
    PluginIdempotencyStatus Status,
    string? IntentFingerprint,
    string? ResultCategory,
    DateTimeOffset ExpiresAtUtc,
    string? OperationKind = null,
    string? PluginId = null,
    string? PluginVersion = null,
    string? ResultState = null,
    long ResultLifecycleVersion = 0);

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
    NotFound,
    StateConflict,
    IdempotencyConflict,
    DisableRefused,
    InternalFailure,
}

/// <summary>
/// Who is asking and under which idempotency scope. The uniqueness scope of a lifecycle mutation is
/// exactly (<see cref="Node"/>, <see cref="Actor"/>, <see cref="IdempotencyKey"/>) — never plugin id or
/// operation kind (ADR-0037). A null/blank key opts out of idempotency, like the existing task API.
/// </summary>
public sealed record PluginLifecycleRequestContext(
    NodeId Node,
    ActorIdentity Actor,
    string? IdempotencyKey = null,
    string? CorrelationId = null);

/// <summary>Neutral outcome of one lifecycle operation. Contains no paths, stacks, or archive data.</summary>
/// <param name="Category">The deterministic result category.</param>
/// <param name="PluginId">The validated manifest plugin id, when known.</param>
/// <param name="PluginVersion">The validated manifest version, when known.</param>
/// <param name="State">The persisted lifecycle state after the operation, when the plugin exists.</param>
/// <param name="LifecycleVersion">The authoritative lifecycle revision after the operation (0 when the plugin does not exist).</param>
/// <param name="Stage">The validation/transaction stage that produced a non-success category.</param>
/// <param name="Replayed">Whether this result replays an earlier logical operation for the same idempotency scope.</param>
/// <param name="LifecycleFailure">Status reads only: the persisted, already-sanitized lifecycle failure, or <c>null</c> when none is current.</param>
/// <param name="RecoveryAvailable">Status reads only: advisory flag that Recover is currently an applicable operator action. Never authority; <c>RecoverAsync</c> revalidates everything.</param>
public sealed record PluginLifecycleResult(
    PluginLifecycleResultCategory Category,
    string? PluginId,
    string? PluginVersion,
    PluginLifecycleState? State,
    long LifecycleVersion,
    string? Stage = null,
    bool Replayed = false,
    string? LifecycleFailure = null,
    bool RecoveryAvailable = false)
{
    /// <summary>Opaque strong ETag projection of <see cref="LifecycleVersion"/> for M6.</summary>
    public string ETag => PluginLifecycleETag.Format(LifecycleVersion);

    /// <summary>Whether the operation completed its requested transition (or an idempotent no-op).</summary>
    public bool Succeeded => Category == PluginLifecycleResultCategory.Succeeded;
}

/// <summary>Opaque ETag encoding for the monotonic lifecycle revision. The revision is never wall-clock based.</summary>
public static class PluginLifecycleETag
{
    /// <summary>Formats a lifecycle revision as a strong ETag.</summary>
    public static string Format(long lifecycleVersion) => $"\"plv-{lifecycleVersion}\"";

    /// <summary>Parses an ETag produced by <see cref="Format"/>; any other value is rejected.</summary>
    public static bool TryParse(string? etag, out long lifecycleVersion)
    {
        lifecycleVersion = 0;
        return etag is { Length: > 6 } && etag.StartsWith("\"plv-", StringComparison.Ordinal) && etag.EndsWith('"') &&
            long.TryParse(etag.AsSpan(5, etag.Length - 6), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out lifecycleVersion);
    }
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
