// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;

namespace bOps.Api;

internal static class IdentityEndpoints
{
    /// <summary>
    /// The caller's identity under either scheme (Bearer or browser session, ADR-0043 §10.2). Unchanged body; never says why a
    /// credential is invalid and never contains the key, token, digest or binding.
    /// </summary>
    internal static void MapIdentityEndpoints(this WebApplication app) =>
        app.MapGet("/api/session/me", (HttpContext http, ClaimsPrincipal principal) =>
        {
            BrowserSessionCookie.NoStore(http.Response);
            return Results.Ok(new
            {
                id = principal.FindFirstValue(ClaimTypes.NameIdentifier),
                displayName = principal.Identity?.Name,
                roles = principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray(),
            });
        }).RequireAuthorization(ApiAuthorization.ViewerPolicy);
}
