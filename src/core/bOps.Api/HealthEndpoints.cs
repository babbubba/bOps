// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace bOps.Api;

/// <summary>
/// <c>GET /healthz</c> (F-26, BOPS-0120): an anonymous liveness probe for service managers and reverse proxies. It answers 200 when the
/// process serves requests and says nothing else — no version, path, configuration or dependency state.
/// </summary>
internal static class HealthEndpoints
{
    internal static void MapHealthEndpoints(this WebApplication app) =>
        app.MapGet("/healthz", (HttpResponse response) =>
        {
            response.Headers[HeaderNames.CacheControl] = "no-store";
            return Results.Text("ok", "text/plain");
        }).AllowAnonymous().DisableRateLimiting(); // A proxy's frequent probe must not use up the address bucket that sign-in attempts share.
}
