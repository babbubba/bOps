// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace bOps.Api;

/// <summary>
/// The <c>bops-browser-session</c> scheme (ADR-0043 §8). Reached only through the <c>bops</c> policy scheme, which selects it when the
/// request has no <c>Authorization</c> header and presents <c>__Host-bops_session</c>. Every failure is the same constant message;
/// the challenge is an empty <c>401</c> that also expires the presented cookie. A forbidden request stays the default <c>403</c>.
/// </summary>
internal sealed class BrowserSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    BrowserSessionService sessions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    public const string SchemeName = BrowserSessionService.SchemeName;
    private const string InvalidSession = "The browser session is not valid.";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!BrowserSessionCookie.IsPresented(Request))
        {
            return AuthenticateResult.NoResult();
        }

        var validation = await sessions.ValidateAsync(Request, Context.RequestAborted);
        if (!validation.Succeeded)
        {
            return AuthenticateResult.Fail(InvalidSession);
        }

        Context.Features.Set(validation.Feature);
        return AuthenticateResult.Success(new AuthenticationTicket(validation.Principal!, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        BrowserSessionCookie.NoStore(Response);
        if (BrowserSessionCookie.IsPresented(Request))
        {
            BrowserSessionCookie.Delete(Response);
        }

        return Task.CompletedTask;
    }
}

/// <summary>The default <c>bops</c> policy scheme (ADR-0043 §8): fixed precedence decided from the request before any credential is checked.</summary>
internal static class ApiAuthenticationSchemes
{
    public const string Default = "bops";

    /// <summary>
    /// Any <c>Authorization</c> header (valid, invalid, empty or another scheme) selects Bearer — never a downgrade to the cookie; else
    /// a presented session cookie selects the browser session; else Bearer (the unchanged anonymous behaviour).
    /// </summary>
    public static string Select(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Headers.ContainsKey("Authorization"))
        {
            return ApiKeyAuthenticationHandler.SchemeName;
        }

        return BrowserSessionCookie.IsPresented(context.Request)
            ? BrowserSessionAuthenticationHandler.SchemeName
            : ApiKeyAuthenticationHandler.SchemeName;
    }
}
