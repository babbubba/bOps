// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace bOps.Api;

/// <summary>
/// Hosts the compiled Angular SPA from the API process (F-26, BOPS-0120) with the native static-file middleware and a client-route
/// fallback. Only the SPA directory is ever exposed (a dedicated file provider; the content root, the SQLite files, the audit log, the
/// vault, the policy and the settings file are not reachable through it). <c>/api</c> and <c>/healthz</c> are never served from it, and
/// the fallback answers <c>GET</c>/<c>HEAD</c> for extension-less paths only, so an unknown API path, a missing asset and any other
/// method are still <c>404</c>. Authentication, ADR-0043 sessions and CSRF are untouched: this only serves public build output.
/// </summary>
internal static partial class WebUiHosting
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; "
        + "connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";

    private const string ImmutableCache = "public, max-age=31536000, immutable";
    private const string RevalidateCache = "no-cache";

    // The Angular application builder names bundles <name>-<8 upper-case alphanumerics>.<js|css>; those never change content.
    [GeneratedRegex(@"^.+-[A-Z0-9]{8}\.(js|css)$", RegexOptions.CultureInvariant)]
    private static partial Regex HashedAsset();

    /// <summary>Paths that belong to the server, not to the SPA, whatever files exist.</summary>
    public static bool IsServerPath(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/healthz", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hardening headers on every response of a host that serves a UI, set when the response starts and never overriding a handler's own.</summary>
    public static IApplicationBuilder UseWebUiSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                ApplyHardeningHeaders(((HttpContext)state).Response);
                return Task.CompletedTask;
            }, context);
            return next(context);
        });

    /// <summary>Static assets, ahead of authentication: public build output needs no session and does not consume the API rate limit.</summary>
    public static IApplicationBuilder UseWebUiStaticFiles(this IApplicationBuilder app, WebUiSettings settings)
    {
        var provider = settings.Files!;
        return app.UseWhen(
            context => !IsServerPath(context.Request.Path),
            branch => branch.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = provider,
                ServeUnknownFileTypes = false,
                OnPrepareResponse = context =>
                {
                    var response = context.Context.Response;
                    response.Headers[HeaderNames.CacheControl] = HashedAsset().IsMatch(context.File.Name) ? ImmutableCache : RevalidateCache;
                    if (string.Equals(context.File.Name, WebUiSettings.IndexFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyDocumentHeaders(response);
                    }
                },
            }));
    }

    /// <summary>
    /// <c>index.html</c> for deep links. A catch-all route with the lowest priority, restricted to extension-less paths by the
    /// <c>nonfile</c> constraint, and short-circuited so the pipeline (authentication, rate limiter, session touch) is not run for a
    /// page load; the shell holds no data and every API call it makes is authorized on its own.
    /// </summary>
    public static void MapWebUiFallback(this WebApplication app, WebUiSettings settings)
    {
        var provider = settings.Files!;
        app.MapFallback("{*path:nonfile}", context => ServeIndexAsync(context, provider)).ShortCircuit();
    }

    private static async Task ServeIndexAsync(HttpContext context, PhysicalFileProvider provider)
    {
        var request = context.Request;
        var response = context.Response;
        if (IsServerPath(request.Path)
            || !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            || !AcceptsHtml(request))
        {
            ApplyHardeningHeaders(response);
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var index = provider.GetFileInfo(WebUiSettings.IndexFileName);
        if (!index.Exists)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        response.ContentType = "text/html; charset=utf-8";
        response.Headers[HeaderNames.CacheControl] = RevalidateCache;
        ApplyDocumentHeaders(response);
        response.ContentLength = index.Length;
        if (HttpMethods.IsGet(request.Method))
        {
            await response.SendFileAsync(index, context.RequestAborted);
        }
    }

    private static bool AcceptsHtml(HttpRequest request)
    {
        var accept = request.Headers.Accept;
        if (accept.Count == 0)
        {
            return true;
        }

        return accept.Any(value => value is not null
            && (value.Contains("text/html", StringComparison.OrdinalIgnoreCase) || value.Contains("*/*", StringComparison.Ordinal)));
    }

    private static void ApplyDocumentHeaders(HttpResponse response)
    {
        ApplyHardeningHeaders(response);
        response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
    }

    // Also applied by the fallback itself: a short-circuited endpoint skips the middleware above.
    private static void ApplyHardeningHeaders(HttpResponse response)
    {
        response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
        response.Headers.TryAdd("Referrer-Policy", "no-referrer");
        response.Headers.TryAdd("X-Frame-Options", "DENY");
    }
}
