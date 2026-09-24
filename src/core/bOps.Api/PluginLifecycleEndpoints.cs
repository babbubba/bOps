// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;
using bOps.Abstractions;
using bOps.PluginHost;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace bOps.Api;

/// <summary>
/// Maps the administrator plugin lifecycle mutations under <c>/api/plugins</c> (ADR-0037, V1.3-M6): archive install/replace, enable,
/// disable and recovery. This is transport only. Authorization is the existing server-side administrator policy; the request context
/// (node, actor, idempotency key, correlation) is built from trusted server state; <c>If-Match</c>/<c>If-None-Match</c> are parsed
/// and handed to the backend, which alone decides stale/current under the per-plugin lock; the idempotency key is handed to the
/// backend, which alone decides replay/conflict; and every backend result goes through the single
/// <see cref="PluginLifecycleHttpMapping"/>. There is no API-side lock, cache, retry or idempotency store, and no delete, remote-URL
/// or marketplace operation.
/// </summary>
/// <remarks>
/// <b>CSRF.</b> The API authenticates with an explicit <c>Authorization: Bearer</c> credential only (<see cref="ApiKeyAuthenticationHandler"/>);
/// there is no cookie or other ambient credential a browser could attach to a forged cross-site request, so the API has — and needs — no
/// antiforgery mechanism, and none is invented here. The upload is a raw <c>application/zip</c> body (never a form), so it also never
/// enters form/antiforgery binding, which would materialise the archive; every JSON body requires <c>application/json</c>.
/// </remarks>
internal static class PluginLifecycleEndpoints
{
    internal const string ZipMediaType = "application/zip";
    private const int MaximumIdempotencyKeyLength = 128;
    private const int MaximumPluginIdLength = 128;

    internal static void MapPluginLifecycleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/plugins").RequireAuthorization(ApiAuthorization.AdministratorPolicy);

        // Install by manifest identity: the client cannot know an unseen archive's id, so this route can only ever create.
        group.MapPost("/archives", InstallNewAsync);

        // Identity-bound install or replacement: the manifest id must equal the route id (never the filename).
        group.MapPut("/{id}/archive", InstallForIdAsync);
        group.MapPost("/{id}/enable", EnableAsync);
        group.MapPost("/{id}/disable", DisableAsync);
        group.MapPost("/{id}/recover", RecoverAsync);
    }

    private static Task<IResult> InstallNewAsync(
        HttpContext http, ClaimsPrincipal principal, PluginLifecycleService lifecycle, PluginUploadOptions upload, ILoggerFactory loggers)
    {
        if (http.Request.Headers.ContainsKey(HeaderNames.IfMatch))
        {
            return Task.FromResult(PluginLifecycleHttpMapping.Transport(
                StatusCodes.Status400BadRequest, PluginLifecycleHttpMapping.InvalidPreconditionCode,
                "If-Match is not valid here: this route only creates a plugin; use PUT /api/plugins/{id}/archive to replace one."));
        }

        var (createOnly, createError) = ReadCreateOnly(http.Request);
        if (createError is not null)
        {
            return Task.FromResult(createError);
        }

        if (!createOnly)
        {
            return Task.FromResult(RequiredPrecondition("If-None-Match: * is required to create a plugin."));
        }

        return InstallAsync(http, principal, lifecycle, upload, loggers, routeId: null, expected: null);
    }

    private static Task<IResult> InstallForIdAsync(
        string id, HttpContext http, ClaimsPrincipal principal, PluginLifecycleService lifecycle, PluginUploadOptions upload, ILoggerFactory loggers)
    {
        if (!IsAcceptablePluginId(id))
        {
            return Task.FromResult(InvalidRequest("The plugin id is not valid."));
        }

        var (createOnly, createError) = ReadCreateOnly(http.Request);
        if (createError is not null)
        {
            return Task.FromResult(createError);
        }

        var hasIfMatch = http.Request.Headers.ContainsKey(HeaderNames.IfMatch);
        if (createOnly && hasIfMatch)
        {
            return Task.FromResult(PluginLifecycleHttpMapping.Transport(
                StatusCodes.Status400BadRequest, PluginLifecycleHttpMapping.InvalidPreconditionCode,
                "If-Match and If-None-Match cannot both be supplied."));
        }

        long? expected = null;
        if (hasIfMatch)
        {
            var (version, error) = ReadIfMatch(http.Request);
            if (error is not null)
            {
                return Task.FromResult(error);
            }

            expected = version;
        }
        else if (!createOnly)
        {
            // A replacement must state its precondition; the API never substitutes the latest revision on the caller's behalf.
            return Task.FromResult(RequiredPrecondition("If-Match with the current lifecycle ETag (replace) or If-None-Match: * (create) is required."));
        }

        return InstallAsync(http, principal, lifecycle, upload, loggers, id, expected);
    }

    private static async Task<IResult> InstallAsync(
        HttpContext http, ClaimsPrincipal principal, PluginLifecycleService lifecycle, PluginUploadOptions upload, ILoggerFactory loggers,
        string? routeId, long? expected)
    {
        var (key, keyError) = ReadIdempotencyKey(http.Request);
        if (keyError is not null)
        {
            return keyError;
        }

        if (!IsZip(http.Request.ContentType))
        {
            return PluginLifecycleHttpMapping.Transport(
                StatusCodes.Status415UnsupportedMediaType, PluginLifecycleHttpMapping.UnsupportedMediaTypeCode,
                $"The upload must be sent as a raw '{ZipMediaType}' body.");
        }

        // Content-Length is only a claim: it may refuse early, but the bound that matters is enforced on the bytes actually read below.
        var declared = http.Request.ContentLength;
        if (declared == 0)
        {
            return PluginLifecycleHttpMapping.Transport(
                StatusCodes.Status400BadRequest, PluginLifecycleHttpMapping.UploadMissingCode, "The request has no plugin archive.");
        }

        if (declared > upload.MaximumRequestBodyBytes)
        {
            return BodyTooLarge();
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = upload.MaximumRequestBodyBytes;
        }

        var context = ContextFor(http, principal, key);
        return await RunAsync(http, loggers, async () =>
        {
            await using var body = new BoundedRequestStream(http.Request.Body, upload.MaximumRequestBodyBytes);
            var result = await lifecycle.InstallArchiveAsync(context, body, expected, routeId, http.RequestAborted);
            // A create (If-None-Match: *) answers 201; a replacement answers 200. Both carry the new lifecycle ETag.
            return (result, expected is null ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });
    }

    private static async Task<IResult> EnableAsync(
        string id, EnablePluginRequest? request, HttpContext http, ClaimsPrincipal principal, PluginLifecycleService lifecycle, ILoggerFactory loggers)
    {
        var prepared = Prepare(id, http, principal);
        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        // A missing or wrong confirmation is passed through as-is; only the backend decides, and it loads nothing without it.
        return await RunAsync(http, loggers, async () =>
            (await lifecycle.EnableAsync(prepared.Context!, id, prepared.Expected, request?.ConfirmedVersion, http.RequestAborted), StatusCodes.Status200OK));
    }

    private static async Task<IResult> DisableAsync(
        string id, HttpContext http, ClaimsPrincipal principal, PluginLifecycleService lifecycle, ILoggerFactory loggers)
    {
        var prepared = Prepare(id, http, principal);
        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        return await RunAsync(http, loggers, async () =>
            (await lifecycle.DisableAsync(prepared.Context!, id, prepared.Expected, http.RequestAborted), StatusCodes.Status200OK));
    }

    private static async Task<IResult> RecoverAsync(
        string id, RecoverPluginRequest? request, HttpContext http, ClaimsPrincipal principal, PluginLifecycleService lifecycle, ILoggerFactory loggers)
    {
        var prepared = Prepare(id, http, principal);
        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        // Recovery restores the activation LKG as InstalledDisabled. It never enables, activates or runs plugin code.
        return await RunAsync(http, loggers, async () =>
            (await lifecycle.RecoverAsync(prepared.Context!, id, prepared.Expected, request?.Confirmed ?? false, http.RequestAborted), StatusCodes.Status200OK));
    }

    // ---- Transport helpers -------------------------------------------------------------------------------

    private readonly record struct Prepared(PluginLifecycleRequestContext? Context, long? Expected, IResult? Error);

    /// <summary>Validates the transport properties shared by enable/disable/recover: route id, <c>If-Match</c> (required) and <c>Idempotency-Key</c> (optional, bounded).</summary>
    private static Prepared Prepare(string id, HttpContext http, ClaimsPrincipal principal)
    {
        if (!IsAcceptablePluginId(id))
        {
            return new Prepared(null, null, InvalidRequest("The plugin id is not valid."));
        }

        if (!http.Request.Headers.ContainsKey(HeaderNames.IfMatch))
        {
            return new Prepared(null, null, RequiredPrecondition("If-Match with the current lifecycle ETag is required."));
        }

        var (version, ifMatchError) = ReadIfMatch(http.Request);
        if (ifMatchError is not null)
        {
            return new Prepared(null, null, ifMatchError);
        }

        var (key, keyError) = ReadIdempotencyKey(http.Request);
        return keyError is not null
            ? new Prepared(null, null, keyError)
            : new Prepared(ContextFor(http, principal, key), version, null);
    }

    /// <summary>
    /// The backend request context, built only from trusted server state: the canonical local node, the authenticated principal's
    /// audit actor, the accepted idempotency header and the server-assigned trace id. Nothing here is read from a body or a caller-chosen header.
    /// </summary>
    private static PluginLifecycleRequestContext ContextFor(HttpContext http, ClaimsPrincipal principal, string? key) =>
        new(NodeId.Local, AgentsEndpoints.ApiActor(principal), key, http.TraceIdentifier);

    private static async Task<IResult> RunAsync(HttpContext http, ILoggerFactory loggers, Func<Task<(PluginLifecycleResult Result, int SuccessStatus)>> operation)
    {
        try
        {
            var (result, successStatus) = await operation();
            return PluginLifecycleHttpMapping.ToHttp(result, http, successStatus);
        }
#pragma warning disable CA1031 // Any failure of a request whose caller already left (a reset connection surfaces as an arbitrary IOException) is the same non-event.
        catch (Exception) when (http.RequestAborted.IsCancellationRequested)
#pragma warning restore CA1031
        {
            // The caller went away mid-request (cancellation or a reset connection while the body was still being read). The backend has
            // already committed nothing — it releases its reservation and deletes staging on any owner failure — and there is no one left
            // to answer, so this is neither an error to log nor a response to build.
            return Results.Empty;
        }
        catch (RequestBodyTooLargeException)
        {
            return BodyTooLarge();
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return BodyTooLarge();
        }
        catch (BadHttpRequestException)
        {
            return InvalidRequest("The request could not be read.");
        }
#pragma warning disable CA1031 // The API boundary must turn any unexpected backend failure into a sanitized 500 rather than a raw exception response.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Server-side log only; the response never carries the exception, its message or any path.
            loggers.CreateLogger("bOps.Api.PluginLifecycle").LogError(ex, "A plugin lifecycle operation failed unexpectedly.");
            var mapped = PluginLifecycleHttpMapping.Map(PluginLifecycleResultCategory.InternalFailure);
            return PluginLifecycleHttpMapping.Transport(mapped.Status, mapped.Code, mapped.Message);
        }
    }

    private static (string? Key, IResult? Error) ReadIdempotencyKey(HttpRequest request)
    {
        var values = request.Headers["Idempotency-Key"];
        if (values.Count == 0)
        {
            return (null, null);
        }

        if (values.Count > 1)
        {
            return (null, InvalidRequest("Idempotency-Key must be supplied at most once."));
        }

        var key = values[0];
        if (string.IsNullOrWhiteSpace(key))
        {
            return (null, null);
        }

        return key.Length > MaximumIdempotencyKeyLength
            ? (null, InvalidRequest($"Idempotency-Key must not exceed {MaximumIdempotencyKeyLength} characters."))
            : (key, null);
    }

    /// <summary>Parses a single strong lifecycle ETag. It is only normalized to the expected revision here; whether it is stale is the backend's decision.</summary>
    private static (long? Version, IResult? Error) ReadIfMatch(HttpRequest request)
    {
        var values = request.Headers.IfMatch;
        if (values.Count != 1 || !PluginLifecycleETag.TryParse(values[0], out var version))
        {
            return (null, PluginLifecycleHttpMapping.Transport(
                StatusCodes.Status400BadRequest, PluginLifecycleHttpMapping.InvalidPreconditionCode,
                "If-Match must carry exactly one lifecycle ETag as issued by this API."));
        }

        return (version, null);
    }

    /// <summary>Recognises the create-only precondition, <c>If-None-Match: *</c>; any other <c>If-None-Match</c> value is refused.</summary>
    private static (bool CreateOnly, IResult? Error) ReadCreateOnly(HttpRequest request)
    {
        var values = request.Headers.IfNoneMatch;
        if (values.Count == 0)
        {
            return (false, null);
        }

        return values.Count == 1 && string.Equals(values[0], "*", StringComparison.Ordinal)
            ? (true, null)
            : (false, PluginLifecycleHttpMapping.Transport(
                StatusCodes.Status400BadRequest, PluginLifecycleHttpMapping.InvalidPreconditionCode,
                "If-None-Match must be '*' (create only)."));
    }

    private static bool IsZip(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed) &&
        string.Equals(parsed.MediaType.Value, ZipMediaType, StringComparison.OrdinalIgnoreCase);

    private static bool IsAcceptablePluginId(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= MaximumPluginIdLength;

    private static IResult BodyTooLarge() => PluginLifecycleHttpMapping.Transport(
        StatusCodes.Status413PayloadTooLarge, PluginLifecycleHttpMapping.BodyTooLargeCode, "The request body exceeds the upload limit.");

    private static IResult InvalidRequest(string message) => PluginLifecycleHttpMapping.Transport(
        StatusCodes.Status400BadRequest, PluginLifecycleHttpMapping.InvalidRequestCode, message);

    private static IResult RequiredPrecondition(string message) => PluginLifecycleHttpMapping.Transport(
        StatusCodes.Status428PreconditionRequired, PluginLifecycleHttpMapping.PreconditionRequiredCode, message);
}
