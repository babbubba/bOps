// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using bOps.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace bOps.Api;

internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<ApiAuthenticationOptions> apiOptions,
    ISecretProvider secretProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    public const string SchemeName = "bops-api-key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var presented = authorization["Bearer ".Length..].Trim();
        if (presented.Length == 0)
        {
            return AuthenticateResult.Fail("The bearer credential is empty.");
        }

        foreach (var credential in apiOptions.Value.ApiKeys)
        {
            if (string.IsNullOrWhiteSpace(credential.Id))
            {
                continue;
            }

            var expected = secretProvider.GetSecret(credential.Secret);
            if (string.IsNullOrEmpty(expected) || !Matches(presented, expected))
            {
                continue;
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, credential.Id),
                new(ClaimTypes.Name, credential.DisplayName ?? credential.Id),
            };
            claims.AddRange(credential.Roles
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(role => new Claim(ClaimTypes.Role, role)));

            var identity = new ClaimsIdentity(claims, SchemeName);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
        }

        return AuthenticateResult.Fail("The bearer credential is not valid.");
    }

    private static bool Matches(string presented, string expected)
    {
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }
}
