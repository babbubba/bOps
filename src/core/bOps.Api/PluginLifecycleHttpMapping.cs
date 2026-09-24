// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.PluginHost;

namespace bOps.Api;

/// <summary>
/// The single deterministic mapping from the ADR-0037 backend's neutral <see cref="PluginLifecycleResultCategory"/> to an HTTP status and
/// a stable error code (V1.3-M6). Every lifecycle endpoint funnels through here; no endpoint decides a lifecycle or security outcome
/// itself, and no message ever carries backend text, a path, a stack or an idempotency fingerprint.
/// </summary>
/// <remarks>
/// Status choices follow the API's existing conventions where one exists (an unknown id is 404 and a state/identity conflict is 409, as
/// in the settings and approvals endpoints). The API has no earlier <c>If-Match</c> precedent, so a stale precondition is the standard
/// 412 and a missing required one is 428. A package the backend rejects (bad archive, manifest, signature, trust, compatibility, or an
/// archive-limit excess) is a well-formed request about an unacceptable package: 422. The HTTP body-limit rejection is a different
/// category (<see cref="BodyTooLargeCode"/>, 413) so a client can tell a transport bound from a package bound.
/// </remarks>
internal static class PluginLifecycleHttpMapping
{
    internal const string BodyTooLargeCode = "request_body_too_large";
    internal const string UnsupportedMediaTypeCode = "unsupported_media_type";
    internal const string UploadMissingCode = "upload_missing";
    internal const string PreconditionRequiredCode = "precondition_required";
    internal const string InvalidPreconditionCode = "invalid_precondition";
    internal const string InvalidRequestCode = "invalid_request";

    internal readonly record struct Mapped(int Status, string Code, string Message);

    /// <summary>Maps a non-success backend category. <see cref="PluginLifecycleResultCategory.Succeeded"/> is not an error and is never passed here.</summary>
    internal static Mapped Map(PluginLifecycleResultCategory category) => category switch
    {
        PluginLifecycleResultCategory.ArchiveInvalid => new(StatusCodes.Status422UnprocessableEntity, "archive_invalid", "The plugin archive is not acceptable."),
        PluginLifecycleResultCategory.ArchiveLimitExceeded => new(StatusCodes.Status422UnprocessableEntity, "archive_limit_exceeded", "The plugin archive exceeds a configured archive limit."),
        PluginLifecycleResultCategory.ManifestInvalid => new(StatusCodes.Status422UnprocessableEntity, "manifest_invalid", "The plugin manifest is not valid."),
        PluginLifecycleResultCategory.SignatureInvalid => new(StatusCodes.Status422UnprocessableEntity, "signature_invalid", "The plugin package signature is missing or not valid."),
        PluginLifecycleResultCategory.PublisherUntrusted => new(StatusCodes.Status422UnprocessableEntity, "publisher_untrusted", "The plugin publisher is not trusted on this host."),
        PluginLifecycleResultCategory.CompatibilityRejected => new(StatusCodes.Status422UnprocessableEntity, "compatibility_rejected", "The plugin is not compatible with this host."),
        PluginLifecycleResultCategory.IdentityConflict => new(StatusCodes.Status409Conflict, "identity_conflict", "The plugin identity conflicts with an installed plugin."),
        PluginLifecycleResultCategory.VersionConflict => new(StatusCodes.Status409Conflict, "version_conflict", "The plugin version is already installed."),
        PluginLifecycleResultCategory.StaleVersion => new(StatusCodes.Status412PreconditionFailed, "stale_lifecycle_version", "The plugin lifecycle state changed since the supplied ETag was issued."),
        PluginLifecycleResultCategory.ActivationConfirmationRequired => new(StatusCodes.Status422UnprocessableEntity, "activation_confirmation_required", "Explicit confirmation of the exact plugin version is required."),
        PluginLifecycleResultCategory.ActivationFailed => new(StatusCodes.Status422UnprocessableEntity, "activation_failed", "The plugin could not be activated and remains disabled."),
        PluginLifecycleResultCategory.RecoveryRequired => new(StatusCodes.Status409Conflict, "recovery_required", "The plugin needs administrator recovery before this operation."),
        PluginLifecycleResultCategory.NotFound => new(StatusCodes.Status404NotFound, "plugin_not_found", "No installed plugin matches the request."),
        PluginLifecycleResultCategory.StateConflict => new(StatusCodes.Status409Conflict, "state_conflict", "The plugin is not in a state that allows this operation."),
        PluginLifecycleResultCategory.IdempotencyConflict => new(StatusCodes.Status409Conflict, "idempotency_conflict", "The Idempotency-Key was already used for a different request."),
        PluginLifecycleResultCategory.DisableRefused => new(StatusCodes.Status409Conflict, "disable_refused", "The plugin cannot be disabled in this process and remains enabled."),
        _ => new(StatusCodes.Status500InternalServerError, "internal_failure", "The plugin lifecycle operation failed."),
    };

    /// <summary>Converts a backend result to its HTTP response; <paramref name="successStatus"/> is used only for <see cref="PluginLifecycleResultCategory.Succeeded"/>.</summary>
    internal static IResult ToHttp(PluginLifecycleResult result, HttpContext http, int successStatus = StatusCodes.Status200OK)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(http);

        if (result.Replayed)
        {
            http.Response.Headers["Idempotency-Replayed"] = "true";
        }

        var etag = result.LifecycleVersion > 0 ? result.ETag : null;
        if (result.Succeeded)
        {
            if (etag is not null)
            {
                http.Response.Headers.ETag = etag;
            }

            var body = new PluginLifecycleResponse(result.PluginId ?? string.Empty, result.PluginVersion, result.State, etag);
            if (successStatus != StatusCodes.Status201Created)
            {
                return Results.Ok(body);
            }

            http.Response.Headers.Location = $"/api/plugins/{Uri.EscapeDataString(body.PluginId)}";
            return Results.Json(body, statusCode: successStatus);
        }

        var mapped = Map(result.Category);
        return Results.Json(
            new PluginLifecycleError(mapped.Message, mapped.Code, result.Stage, result.PluginId, result.PluginVersion, etag),
            statusCode: mapped.Status);
    }

    /// <summary>A transport-level rejection (before any backend call).</summary>
    internal static IResult Transport(int status, string code, string message) =>
        Results.Json(new PluginLifecycleError(message, code), statusCode: status);
}
