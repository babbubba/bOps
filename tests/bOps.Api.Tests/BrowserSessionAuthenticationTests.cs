// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace bOps.Api.Tests;

/// <summary>ADR-0043 §8, §17: scheme precedence, the browser-session handler, and identical authorization under both schemes.</summary>
public sealed class BrowserSessionAuthenticationTests
{
    private static readonly string[] AllRoles = ["viewer", "operator", "approver", "administrator"];

    // §17 row 1
    [Fact]
    public async Task ValidBearer_WithoutCookie_Is200()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();

        using var response = await BrowserSession.SendAsync(client, HttpMethod.Get, "/api/session/me", null, bearer: $"Bearer {factory.ApiKey}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // §17 row 2
    [Fact]
    public async Task ValidBearer_AndValidCookie_UnsafeWithoutCsrfHeaders_IsBearer_AndTheCookieIsNotTouched()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-05T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        using var factory = new TestAppFactory { Clock = clock };
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);
        var lastSeen = LastSeen(factory);
        clock.Advance(TimeSpan.FromMinutes(5));

        using var response = await BrowserSession.SendAsync(
            client, HttpMethod.Delete, $"/api/agents/tasks/{Guid.NewGuid()}", cookie, bearer: $"Bearer {factory.ApiKey}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(BrowserSession.SessionSetCookies(response));
        Assert.Equal(lastSeen, LastSeen(factory));
    }

    // §17 row 3
    [Fact]
    public async Task ValidBearer_AndInvalidCookie_IsBearer_AndTheCookieIsNotDeleted()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();

        using var response = await BrowserSession.SendAsync(
            client, HttpMethod.Get, "/api/session/me", "not-a-valid-session-cookie-value", bearer: $"Bearer {factory.ApiKey}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(BrowserSession.SessionSetCookies(response));
    }

    // §17 rows 4 and 5: an explicit Authorization header always decides; never a downgrade to a valid cookie.
    [Theory]
    [InlineData("Bearer wrong-key")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer")]
    [InlineData("Bearer    ")]
    [InlineData("Token abc")]
    public async Task InvalidOrMalformedAuthorization_WithAValidCookie_Is401(string authorization)
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        using var response = await BrowserSession.SendAsync(client, HttpMethod.Get, "/api/session/me", cookie, bearer: authorization);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(BrowserSession.SessionSetCookies(response));
        using var stillValid = await BrowserSession.GetMeAsync(client, cookie);
        Assert.Equal(HttpStatusCode.OK, stillValid.StatusCode);
    }

    // §17 row 5: an empty Authorization field still selects Bearer (HttpClient drops empty headers, so the field is set on the server context).
    [Fact]
    public async Task EmptyAuthorizationField_WithAValidCookie_Is401()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        var context = await factory.Server.SendAsync(http =>
        {
            http.Request.Method = "GET";
            http.Request.Path = "/api/session/me";
            http.Request.Headers.Authorization = string.Empty;
            http.Request.Headers.Cookie = $"{BrowserSession.CookieName}={cookie}";
        });

        Assert.True(context.Request.Headers.ContainsKey("Authorization"));
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // §17 row 6 and §10.2: /api/session/me works and returns the same body under both schemes.
    [Fact]
    public async Task Me_ReturnsTheSameIdentity_UnderBearerAndUnderTheSession()
    {
        using var factory = new TestAppFactory { Roles = AllRoles };
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        using var viaCookie = await BrowserSession.GetMeAsync(client, cookie);
        using var viaBearer = await BrowserSession.SendAsync(client, HttpMethod.Get, "/api/session/me", null, bearer: $"Bearer {factory.ApiKey}");

        Assert.Equal(HttpStatusCode.OK, viaCookie.StatusCode);
        Assert.Equal("no-store", viaCookie.Headers.CacheControl?.ToString());
        Assert.Equal(await viaBearer.Content.ReadAsStringAsync(), await viaCookie.Content.ReadAsStringAsync());
    }

    // §17 row 20
    [Theory]
    [InlineData("__Host-bops_session=short")]
    [InlineData("__Host-bops_session=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData("__Host-bops_session=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("__Host-bops_session=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("__Host-bops_session")]
    [InlineData("__Host-bops_session=")]
    public async Task MalformedCookie_Is401_WithCookieDeletion(string rawCookie)
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();

        using var response = await BrowserSession.SendAsync(client, HttpMethod.Get, "/api/session/me", null, rawCookieHeader: rawCookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(BrowserSession.DeletesCookie(response));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    // §17 row 20: two session cookies (cookie tossing) are refused even when one of them is valid.
    [Fact]
    public async Task DuplicatedSessionCookie_Is401_EvenWhenOneIsValid()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        using var oneHeader = await BrowserSession.SendAsync(
            client, HttpMethod.Get, "/api/session/me", null, rawCookieHeader: $"__Host-bops_session={cookie}; __Host-bops_session={cookie}");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session/me");
        request.Headers.TryAddWithoutValidation("Cookie", [$"__Host-bops_session={cookie}", "theme=dark; __Host-bops_session=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"]);
        using var twoHeaders = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, oneHeader.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, twoHeaders.StatusCode);
    }

    [Fact]
    public async Task UnrelatedCookies_AroundTheSessionCookie_DoNotMatter()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        using var response = await BrowserSession.SendAsync(
            client, HttpMethod.Get, "/api/session/me", null, rawCookieHeader: $"language=it;  __Host-bops_session={cookie} ; theme=dark");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // §17 row 21: no credential at all keeps the unchanged anonymous behaviour (no cookie deletion).
    [Fact]
    public async Task NoCredential_OnAProtectedEndpoint_IsTheUnchanged401()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();

        using var response = await client.GetAsync(new Uri("/api/session/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(BrowserSession.SessionSetCookies(response));
    }

    // §17 row 16: HEAD and OPTIONS carry no CSRF requirement.
    [Fact]
    public async Task SafeMethods_NeedNoCsrfHeaders()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        using var head = await BrowserSession.SendAsync(client, HttpMethod.Head, "/api/session/me", cookie);
        using var options = await BrowserSession.SendAsync(client, HttpMethod.Options, "/api/session/me", cookie);

        Assert.NotEqual(HttpStatusCode.Forbidden, head.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, options.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, head.StatusCode);
    }

    // ADR-0043 §17 (last paragraph): every policy yields the same 200/403 under Bearer and under the session for the same roles.
    private static readonly (string Policy, string Method, string Path)[] PolicyEndpoints =
    [
        ("bops.viewer", "GET", "/api/session/me"),
        ("bops.operator", "DELETE", "/api/agents/tasks/00000000-0000-0000-0000-000000000001"),
        ("bops.approver", "GET", "/api/approvals/pending"),
        ("bops.administrator", "GET", "/api/settings"),
    ];

    [Theory]
    [InlineData("viewer,operator,approver,administrator")]
    [InlineData("viewer")]
    [InlineData("viewer,operator")]
    [InlineData("viewer,approver")]
    [InlineData("viewer,administrator")]
    public async Task EveryPolicy_GivesTheSameResult_UnderBothSchemes(string roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        using var factory = new TestAppFactory { Roles = roles.Split(',') };
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        foreach (var (policy, method, path) in PolicyEndpoints)
        {
            using var viaBearer = await BrowserSession.SendAsync(client, new HttpMethod(method), path, null, bearer: $"Bearer {factory.ApiKey}");
            using var viaCookie = await BrowserSession.SendAsync(client, new HttpMethod(method), path, cookie, csrf: true, origin: BrowserSession.UiOrigin);

            Assert.True(viaBearer.StatusCode == viaCookie.StatusCode, $"{policy}: Bearer {(int)viaBearer.StatusCode}, session {(int)viaCookie.StatusCode}");
            Assert.NotEqual(HttpStatusCode.Unauthorized, viaCookie.StatusCode);
        }
    }

    [Fact]
    public async Task RolesAreDeduplicated_AndTheSessionCarriesTheSameClaims()
    {
        using var factory = new TestAppFactory { Roles = ["viewer", "Viewer", "operator", " operator "] };
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        using var me = await BrowserSession.GetMeAsync(client, cookie);

        using var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal(["viewer", "operator"], body.RootElement.GetProperty("roles").EnumerateArray().Select(role => role.GetString()!).ToArray());
    }

    internal static long LastSeen(TestAppFactory factory)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={BrowserSession.SessionsDb(factory)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_seen_at_unix_ms FROM browser_sessions;";
        return (long)command.ExecuteScalar()!;
    }
}
