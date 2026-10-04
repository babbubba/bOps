// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace bOps.Api;

/// <summary>Body of <c>POST /api/session</c> (ADR-0043 §10.1). Strict: unknown and duplicate members, and trailing content, are refused.</summary>
internal sealed class BrowserSessionLoginRequest
{
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("keepSignedIn")]
    public bool KeepSignedIn { get; init; }
}

/// <summary>Response of <c>POST /api/session</c>: the same shape as <c>GET /api/session/me</c>. Never the key, token, digest or binding.</summary>
internal sealed record BrowserSessionIdentity(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("roles")] string[] Roles)
{
    public static BrowserSessionIdentity From(ClaimsPrincipal principal) => new(
        principal.FindFirstValue(ClaimTypes.NameIdentifier),
        principal.Identity?.Name,
        principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray());
}

[JsonSourceGenerationOptions(
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    PropertyNameCaseInsensitive = false,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    AllowTrailingCommas = false,
    NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(BrowserSessionLoginRequest))]
internal sealed partial class BrowserSessionJsonContext : JsonSerializerContext;

/// <summary>
/// <c>POST /api/session</c> (login) and <c>DELETE /api/session</c> (logout), ADR-0043 §10. <c>GET /api/session/me</c> stays in
/// <see cref="IdentityEndpoints"/>. Every response carries <c>Cache-Control: no-store</c> and fixed error texts.
/// </summary>
internal static class BrowserSessionEndpoints
{
    public const string LoginRateLimitPolicy = "bops.session-login";
    public const int MaximumLoginBodyBytes = 4096;

    internal static void MapBrowserSessionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/session", LoginAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimitPolicy);
        app.MapDelete("/api/session", LogoutAsync).RequireAuthorization();
    }

    private static async Task<IResult> LoginAsync(
        HttpContext http, BrowserSessionService sessions, ApiCredentialAuthority authority)
    {
        var request = http.Request;
        BrowserSessionCookie.NoStore(http.Response);

        // 1. Origin gate, whatever the scheme: the body is not read and no key is validated for a foreign origin.
        if (!BrowserSessionCsrf.Passes(request, sessions.Settings.Origins))
        {
            sessions.LoginRejected(BrowserSessionCsrf.RejectedCode);
            return BrowserSessionCsrf.Rejected(http.Response);
        }

        // 2. JSON only: a plain form or text/plain request never reaches the key.
        if (!IsJsonUtf8(request.ContentType))
        {
            return Error(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", "The request body must be application/json.");
        }

        // 3. At most 4 KiB, counted on the bytes actually delivered.
        if (request.ContentLength > MaximumLoginBodyBytes)
        {
            return TooLarge();
        }

        byte[] body;
        try
        {
            using var buffer = new MemoryStream();
            using var bounded = new BoundedRequestStream(request.Body, MaximumLoginBodyBytes);
            await bounded.CopyToAsync(buffer, http.RequestAborted);
            body = buffer.ToArray();
        }
        catch (RequestBodyTooLargeException)
        {
            return TooLarge();
        }

        // 4. Strict shape. Neither the payload nor the parser's message is ever logged or returned.
        BrowserSessionLoginRequest? login;
        try
        {
            login = JsonSerializer.Deserialize(body, BrowserSessionJsonContext.Default.BrowserSessionLoginRequest);
        }
        catch (JsonException)
        {
            login = null;
        }
        finally
        {
            Array.Clear(body);
        }

        var apiKey = login?.ApiKey?.Trim();
        if (login is null || string.IsNullOrEmpty(apiKey))
        {
            sessions.LoginRejected("invalid_request");
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "The request body must be {\"apiKey\": string, \"keepSignedIn\": boolean}.");
        }

        // 5. The key, exactly as Bearer resolves it. A failure here changes nothing: a presented session stays as it was.
        if (authority.FindByPresentedKey(apiKey) is not { } match)
        {
            sessions.LoginRejected("invalid_credential");
            return Error(StatusCodes.Status401Unauthorized, "invalid_credential", "The API key is not valid.");
        }

        // 6. The UI's floor: the same role check the viewer policy makes on the same principal.
        var principal = ApiCredentialAuthority.CreatePrincipal(match, BrowserSessionService.SchemeName);
        if (!principal.IsInRole(ApiAuthorization.ViewerRole))
        {
            sessions.LoginRejected("viewer_role_required");
            return Error(StatusCodes.Status403Forbidden, "viewer_role_required", "This API key does not hold the viewer role required by the web UI.");
        }

        // 7. A fresh token; a presented (canonical) session is superseded in the same transaction.
        var (token, maxAge) = await sessions.CreateAsync(
            match, login.KeepSignedIn, BrowserSessionCookie.PresentedDigest(request), http.RequestAborted);

        // 8. Bounded cleanup, failures logged and ignored.
        await sessions.OpportunisticCleanupAsync(CancellationToken.None);

        BrowserSessionCookie.Append(http.Response, token, maxAge);
        return Results.Ok(BrowserSessionIdentity.From(principal));
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, BrowserSessionService sessions)
    {
        BrowserSessionCookie.NoStore(http.Response);

        // Bearer precedence: a Bearer request is a no-op and never revokes a browser session, even if it also carried the cookie.
        if (http.Features.Get<BrowserSessionFeature>() is { } feature)
        {
            await sessions.LogoutAsync(feature, http.User.FindFirstValue(ClaimTypes.NameIdentifier), http.RequestAborted);
            BrowserSessionCookie.Delete(http.Response);
        }

        return Results.NoContent();
    }

    private static bool IsJsonUtf8(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            || !string.Equals(parsed.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !parsed.Charset.HasValue || string.Equals(parsed.Charset.Value, "utf-8", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult TooLarge() =>
        Error(StatusCodes.Status413PayloadTooLarge, "payload_too_large", "The request body exceeds 4096 bytes.");

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new TaskErrorResponse(code, message), statusCode: status);
}

/// <summary>
/// <c>bops.session-login</c> (ADR-0043 §11): a fixed window of 10 requests per minute per remote address, no queue, applied to
/// <c>POST /api/session</c> after the global limiter. Never partitions by a credential or body content.
/// </summary>
internal sealed class BrowserSessionLoginRateLimitPolicy : IRateLimiterPolicy<string>
{
    public const int PermitLimit = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = async (context, ct) =>
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        BrowserSessionCookie.NoStore(response);
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            response.Headers[HeaderNames.RetryAfter] =
                ((long)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await response.WriteAsJsonAsync(
            new TaskErrorResponse("rate_limited", "Too many sign-in attempts. Try again later."), ct);
    };

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var address = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PermitLimit,
            Window = Window,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }
}
