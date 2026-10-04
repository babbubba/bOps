// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;

namespace bOps.Api.Tests;

/// <summary>
/// ADR-0043 §2, §18: the Bearer handler behaves exactly as before the credential loop moved into <c>ApiCredentialAuthority</c> —
/// prefix, trimming, empty and foreign schemes, role de-duplication and first-matching entry.
/// </summary>
public sealed class BearerAuthenticationRegressionTests
{
    [Theory]
    [InlineData("Bearer {0}")]
    [InlineData("bearer {0}")]
    [InlineData("BEARER {0}")]
    [InlineData("Bearer    {0}   ")]
    public async Task BearerPrefix_IsCaseInsensitive_AndTheKeyIsTrimmed(string format)
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();

        using var response = await BrowserSession.SendAsync(
            client, HttpMethod.Get, "/api/session/me", null, bearer: string.Format(System.Globalization.CultureInfo.InvariantCulture, format, factory.ApiKey));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(BrowserSession.SessionSetCookies(response));
    }

    [Theory]
    [InlineData("Bearer ")]
    [InlineData("Bearer      ")]
    [InlineData("Bearer")]
    [InlineData("Basic {0}")]
    [InlineData("{0}")]
    [InlineData("Bearer {0}x")]
    public async Task EmptyForeignOrWrongCredentials_Are401(string format)
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();

        using var response = await BrowserSession.SendAsync(
            client, HttpMethod.Get, "/api/session/me", null, bearer: string.Format(System.Globalization.CultureInfo.InvariantCulture, format, factory.ApiKey));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(BrowserSession.SessionSetCookies(response));
    }

    [Fact]
    public async Task FirstMatchingEntry_Wins_BlankIdsAndEmptySecretsAreSkipped()
    {
        using var secrets = new SessionSecrets();
        var (shared, key) = secrets.Create();
        var (empty, _) = secrets.Create(string.Empty);
        using var factory = new TestAppFactory
        {
            ExtraConfiguration = SessionSecrets.Configuration(
                (" ", shared, "viewer,administrator"),
                ("empty", empty, "viewer,administrator"),
                ("first", shared, "viewer"),
                ("second", shared, "viewer,administrator")),
        };
        using var client = factory.CreateSessionClient();

        using var me = await BrowserSession.SendAsync(client, HttpMethod.Get, "/api/session/me", null, bearer: $"Bearer {key}");
        using var settings = await BrowserSession.SendAsync(client, HttpMethod.Get, "/api/settings", null, bearer: $"Bearer {key}");

        Assert.Equal("first", (await BrowserSessionCredentialTests.IdentityAsync(me)).Id);
        Assert.Equal(HttpStatusCode.Forbidden, settings.StatusCode);

        // The browser login resolves the same entry for the same key.
        using var login = await BrowserSession.LoginAsync(client, key);
        Assert.Equal("first", (await BrowserSessionCredentialTests.IdentityAsync(login)).Id);
    }

    [Fact]
    public async Task Roles_AreDistinct_CaseInsensitively_AndTrimmed()
    {
        using var factory = new TestAppFactory { Roles = ["viewer", " VIEWER", "approver", "approver "] };
        using var client = factory.CreateClient();

        using var me = await client.GetAsync(new Uri("/api/session/me", UriKind.Relative));

        Assert.Equal(["viewer", "approver"], (await BrowserSessionCredentialTests.IdentityAsync(me)).Roles);
    }
}
