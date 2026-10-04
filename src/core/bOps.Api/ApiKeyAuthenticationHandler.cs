// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace bOps.Api;

/// <summary>
/// <c>Authorization: Bearer &lt;api-key&gt;</c>. The credential loop itself is <see cref="ApiCredentialAuthority"/> (ADR-0043 §2), shared
/// with the browser session so both always select the same configured entry for the same secret; this handler's observable
/// behaviour (prefix, trimming, <c>NoResult</c>/<c>Fail</c> cases, claims, challenge) is unchanged.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    ApiCredentialAuthority authority)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    public const string SchemeName = "bops-api-key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = authorization["Bearer ".Length..].Trim();
        if (presented.Length == 0)
        {
            return Task.FromResult(AuthenticateResult.Fail("The bearer credential is empty."));
        }

        if (authority.FindByPresentedKey(presented) is not { } match)
        {
            return Task.FromResult(AuthenticateResult.Fail("The bearer credential is not valid."));
        }

        var principal = ApiCredentialAuthority.CreatePrincipal(match, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
