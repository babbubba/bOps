// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;

namespace bOps.Api;

internal static class IdentityEndpoints
{
    internal static void MapIdentityEndpoints(this WebApplication app) =>
        app.MapGet("/api/session/me", (ClaimsPrincipal principal) => Results.Ok(new
        {
            id = principal.FindFirstValue(ClaimTypes.NameIdentifier),
            displayName = principal.Identity?.Name,
            roles = principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray(),
        })).RequireAuthorization(ApiAuthorization.ViewerPolicy);
}
