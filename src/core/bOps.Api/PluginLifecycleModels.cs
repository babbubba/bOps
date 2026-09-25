// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.PluginHost;

namespace bOps.Api;

/// <summary>
/// The neutral outcome of a successful lifecycle mutation (ADR-0037, V1.3-M6). Carries the persisted lifecycle state and the
/// opaque lifecycle ETag only — never an install path, generation identifier, digest, journal detail or archive content. The same
/// ETag is also returned in the <c>ETag</c> response header.
/// </summary>
internal sealed record PluginLifecycleResponse(
    string PluginId,
    string? Version,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<PluginLifecycleState>))]
    PluginLifecycleState? State,
    string? ETag);

/// <summary>
/// The sanitized failure shape of every lifecycle operation. <see cref="Category"/> is a stable machine-readable code (see
/// <see cref="PluginLifecycleHttpMapping"/>); <see cref="Message"/> is a fixed neutral sentence, never backend text; <see cref="Stage"/>
/// is the neutral validation/transaction stage the backend reported. <see cref="ETag"/> is the plugin's current lifecycle ETag when the
/// plugin exists, so a client that lost a stale-precondition race can refresh without another round trip.
/// </summary>
internal sealed record PluginLifecycleError(
    string Message,
    string Category,
    string? Stage = null,
    string? PluginId = null,
    string? Version = null,
    string? ETag = null);

/// <summary>Body of <c>POST /api/plugins/{id}/enable</c>: the exact manifest version the administrator confirms will run in-process with host privileges.</summary>
internal sealed record EnablePluginRequest(string? ConfirmedVersion);

/// <summary>Body of <c>POST /api/plugins/{id}/recover</c>: explicit confirmation of activation-LKG recovery (which itself never enables anything).</summary>
internal sealed record RecoverPluginRequest(bool Confirmed);

/// <summary>
/// The HTTP request-body bound for archive upload. The body IS the archive, so the transport bound is exactly the backend's
/// compressed-archive limit (ADR-0037: the API request-body limit and the backend streaming limit are both applied, from one
/// configured value): a byte beyond it is refused by the transport as a body-limit rejection before the backend ever counts it, and
/// the backend's own archive limits (entry count, uncompressed size, ratio, ...) remain a distinct rejection category.
/// </summary>
internal sealed record PluginUploadOptions(long MaximumRequestBodyBytes)
{
    internal static PluginUploadOptions For(PluginArchiveLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return new PluginUploadOptions(limits.MaximumCompressedBytes);
    }
}
