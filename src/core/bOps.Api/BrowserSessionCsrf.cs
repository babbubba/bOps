// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Net.Http.Headers;

namespace bOps.Api;

/// <summary>
/// The browser-session CSRF gate (ADR-0043 §9): exactly one <c>X-bOps-Request: 1</c> header field <b>and</b> an <c>Origin</c> (or, only
/// when no <c>Origin</c> field is present, a <c>Referer</c>) whose <c>(scheme, host, port)</c> equals a configured
/// <c>BrowserSession:Origins</c> tuple. <c>Host</c> and forwarded headers are never read. Also the login's origin gate (§10.1 step 1).
/// </summary>
internal static class BrowserSessionCsrf
{
    public const string HeaderName = "X-bOps-Request";
    public const string RejectedCode = "csrf_rejected";
    public const string RejectedMessage = "The request was refused by the browser-session origin check.";

    /// <summary>Unsafe = every method other than GET, HEAD, OPTIONS and TRACE (extension methods included).</summary>
    public static bool IsUnsafe(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));

    public static bool Passes(HttpRequest request, IReadOnlySet<BrowserOrigin> trustedOrigins)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(trustedOrigins);
        return HasCustomHeader(request) && HasTrustedOrigin(request, trustedOrigins);
    }

    public static IResult Rejected(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        BrowserSessionCookie.NoStore(response);
        return Results.Json(new TaskErrorResponse(RejectedCode, RejectedMessage), statusCode: StatusCodes.Status403Forbidden);
    }

    private static bool HasCustomHeader(HttpRequest request)
    {
        var values = request.Headers[HeaderName];
        return values.Count == 1 && string.Equals(values[0], "1", StringComparison.Ordinal);
    }

    private static bool HasTrustedOrigin(HttpRequest request, IReadOnlySet<BrowserOrigin> trustedOrigins)
    {
        var origins = request.Headers[HeaderNames.Origin];
        if (origins.Count > 0)
        {
            // An Origin field decides on its own: Referer is never consulted, even when Origin fails.
            if (origins.Count != 1 || origins[0] is not { } origin || origin.Contains(',', StringComparison.Ordinal)
                || string.Equals(origin.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return BrowserOrigin.TryParseOrigin(origin, out var parsed) && trustedOrigins.Contains(parsed);
        }

        var referers = request.Headers[HeaderNames.Referer];
        return referers.Count == 1
            && BrowserOrigin.TryParseUrlOrigin(referers[0], out var refererOrigin)
            && trustedOrigins.Contains(refererOrigin);
    }
}

/// <summary>The gate and the idle touch as pipeline steps (ADR-0043 §8 pipeline order).</summary>
internal static class BrowserSessionMiddleware
{
    /// <summary>After authentication, before rate limiting, authorization, binding and any body read: only cookie-authenticated unsafe requests.</summary>
    public static IApplicationBuilder UseBrowserSessionCsrf(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Features.Get<BrowserSessionFeature>() is not null && BrowserSessionCsrf.IsUnsafe(context.Request.Method))
            {
                var settings = context.RequestServices.GetRequiredService<BrowserSessionSettings>();
                if (!BrowserSessionCsrf.Passes(context.Request, settings.Origins))
                {
                    await BrowserSessionCsrf.Rejected(context.Response).ExecuteAsync(context);
                    return;
                }
            }

            await next(context);
        });

    /// <summary>After authorization: a request that got here passed CSRF, rate limiting and authorization and reaches its endpoint.</summary>
    public static IApplicationBuilder UseBrowserSessionTouch(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Features.Get<BrowserSessionFeature>() is { } feature && context.GetEndpoint() is not null)
            {
                await context.RequestServices.GetRequiredService<BrowserSessionService>().TouchIfDueAsync(feature, context.RequestAborted);
            }

            await next(context);
        });
}
