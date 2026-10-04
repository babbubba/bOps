// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace bOps.Api.Tests;

/// <summary>ADR-0043 §9, §10.3, §17 rows 7–16 and 27–28: the CSRF gate on cookie-authenticated unsafe requests, and logout.</summary>
public sealed class BrowserSessionCsrfTests
{
    private static readonly string[] AllRoles = ["viewer", "operator", "approver", "administrator"];

    /// <summary>One target per unsafe method, each answering something other than 403 once past the gate.</summary>
    public static TheoryData<string, string, HttpStatusCode> UnsafeRequests => new()
    {
        { "POST", "/api/agents/tasks/00000000-0000-0000-0000-000000000001/resume", HttpStatusCode.NotFound },
        { "PUT", "/api/settings/providers/Anthropic/profile", HttpStatusCode.NoContent },
        { "PATCH", "/api/agents/tasks/00000000-0000-0000-0000-000000000001", HttpStatusCode.MethodNotAllowed },
        { "DELETE", "/api/agents/tasks/00000000-0000-0000-0000-000000000001", HttpStatusCode.NotFound },
    };

    private const string ProfileJson =
        """{"baseUrl":"https://api.anthropic.com","model":"claude-sonnet-4-5","supportsNativeToolCalling":true,"extraParameters":null}""";

    [Theory]
    [MemberData(nameof(UnsafeRequests))]
    public async Task CookieAuthenticatedUnsafeRequests_PassOnlyWithTheHeaderAndAConfiguredOrigin(string method, string target, HttpStatusCode passed)
    {
        using var factory = new TestAppFactory { Roles = AllRoles };
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);
        var json = method is "PUT" ? ProfileJson : null;

        // §17 rows 8–15: each refused request answers 403 csrf_rejected and changes nothing.
        var refusals = new (string Name, bool Csrf, string? Origin, (string, string)[] Headers)[]
        {
            ("missing header", false, BrowserSession.UiOrigin, []),
            ("header 'true'", false, BrowserSession.UiOrigin, [("X-bOps-Request", "true")]),
            ("header ' 1'", false, BrowserSession.UiOrigin, [("X-bOps-Request", "0")]),
            ("header empty", false, BrowserSession.UiOrigin, [("X-bOps-Request", "")]),
            ("header repeated", true, BrowserSession.UiOrigin, [("X-bOps-Request", "1")]),
            ("header '1, 1'", false, BrowserSession.UiOrigin, [("X-bOps-Request", "1, 1")]),
            ("foreign port", true, "http://localhost:4300", []),
            ("127.0.0.1 vs localhost", true, "http://127.0.0.1:4200", []),
            ("https vs http", true, "https://localhost:4200", []),
            ("suffix match", true, "http://localhost:42000", []),
            ("evil host", true, "http://localhost.evil.test:4200", []),
            ("Origin null", true, "null", [("Referer", "http://localhost:4200/dashboard")]),
            ("Origin NULL", true, "NULL", []),
            ("malformed Origin", true, "localhost:4200", []),
            ("Origin with path", true, "http://localhost:4200/", [("Referer", "http://localhost:4200/")]),
            ("Origin with user-info", true, "http://user@localhost:4200", []),
            ("two Origin values", true, "http://localhost:4200, http://localhost:4200", []),
            ("repeated Origin", true, BrowserSession.UiOrigin, [("Origin", BrowserSession.UiOrigin)]),
            ("foreign Referer", true, null, [("Referer", "http://localhost:4300/dashboard")]),
            ("malformed Referer", true, null, [("Referer", "/dashboard")]),
            ("Referer user-info", true, null, [("Referer", "http://user@localhost:4200/")]),
            ("neither", true, null, []),
            ("forwarded headers only", true, null, [("X-Forwarded-Host", "localhost:4200"), ("X-Forwarded-Proto", "http"), ("Forwarded", "host=localhost:4200")]),
        };

        foreach (var (name, csrf, origin, headers) in refusals)
        {
            using var refused = await BrowserSession.SendAsync(
                client, new HttpMethod(method), target, cookie, csrf: csrf, origin: origin, json: json, headers: headers);
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{method} {name}: {(int)refused.StatusCode}");
            Assert.Equal("csrf_rejected", await BrowserSessionLoginTests.CodeAsync(refused));
            Assert.Equal("no-store", refused.Headers.CacheControl?.ToString());
            Assert.Empty(BrowserSession.SessionSetCookies(refused));
        }

        if (method is "PUT")
        {
            using var settings = await BrowserSession.SendAsync(client, HttpMethod.Get, "/api/settings", cookie);
            Assert.DoesNotContain("claude-sonnet-4-5", await settings.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // §17 row 7: valid header and Origin.
        using var accepted = await BrowserSession.SendAsync(client, new HttpMethod(method), target, cookie, csrf: true, origin: BrowserSession.UiOrigin, json: json);
        Assert.Equal(passed, accepted.StatusCode);

        // §17 row 13: no Origin, same-origin Referer.
        using var viaReferer = await BrowserSession.SendAsync(
            client, new HttpMethod(method), target, cookie, csrf: true, json: json, headers: [("Referer", "http://localhost:4200/dashboard?task=x")]);
        Assert.Equal(passed, viaReferer.StatusCode);

        // An equivalent spelling of the configured origin (scheme case, explicit default port is a different port here).
        using var upperCase = await BrowserSession.SendAsync(client, new HttpMethod(method), target, cookie, csrf: true, origin: "HTTP://LOCALHOST:4200", json: json);
        Assert.Equal(passed, upperCase.StatusCode);
    }

    [Fact]
    public async Task ConfiguredOrigins_AreCanonicalized_WithTheDefaultPort()
    {
        using var factory = new TestAppFactory
        {
            ExtraConfiguration = new Dictionary<string, string?> { ["BrowserSession:Origins"] = "http://localhost, https://bops.example.test" },
        };
        using var client = factory.CreateSessionClient();
        var json = $$"""{"apiKey":"{{factory.ApiKey}}"}""";

        using var defaultPort = await BrowserSession.SendAsync(client, HttpMethod.Post, "/api/session", null, csrf: true, origin: "http://localhost:80", json: json);
        using var https = await BrowserSession.SendAsync(client, HttpMethod.Post, "/api/session", null, csrf: true, origin: "https://bops.example.test:443", json: json);
        using var notConfigured = await BrowserSession.SendAsync(client, HttpMethod.Post, "/api/session", null, csrf: true, origin: BrowserSession.UiOrigin, json: json);

        Assert.Equal(HttpStatusCode.OK, defaultPort.StatusCode);
        Assert.Equal(HttpStatusCode.OK, https.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, notConfigured.StatusCode);
    }

    [Fact]
    public async Task BearerUnsafeRequests_NeedNoBrowserHeaders()
    {
        using var factory = new TestAppFactory { Roles = AllRoles };
        using var bearer = factory.CreateClient();

        using var delete = await bearer.DeleteAsync(new Uri($"/api/agents/tasks/{Guid.NewGuid()}", UriKind.Relative));
        using var profile = new StringContent(ProfileJson, Encoding.UTF8, "application/json");
        using var put = await bearer.PutAsync(new Uri("/api/settings/providers/Anthropic/profile", UriKind.Relative), profile);
        using var foreign = new HttpRequestMessage(HttpMethod.Delete, $"/api/agents/tasks/{Guid.NewGuid()}");
        foreign.Headers.Add("Origin", "http://evil.test");
        using var foreignOrigin = await bearer.SendAsync(foreign);

        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignOrigin.StatusCode);
    }

    // ADR-0043 §18: a plugin lifecycle mutation with a valid administrator session — 403 without the header, no effect, body not read.
    [Fact]
    public async Task PluginLifecycleMutation_WithASession_NeedsTheGate()
    {
        using var harness = new PluginLifecycleApiHarness();
        var etag = await harness.SeedInstalledAsync();
        using var client = harness.Factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, harness.Factory.ApiKey);
        var before = harness.TakeSnapshot();

        using var forged = await BrowserSession.SendAsync(
            client, HttpMethod.Post, $"/api/plugins/{PluginArchiveFixture.PluginId}/disable", cookie,
            json: """{"confirmedVersion":"1.0.0","confirmed":true}""", headers: [("If-Match", etag), ("Origin", BrowserSession.UiOrigin)]);

        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        harness.AssertUnchanged(before);

        using var trusted = await BrowserSession.SendAsync(
            client, HttpMethod.Post, $"/api/plugins/{PluginArchiveFixture.PluginId}/disable", cookie, csrf: true, origin: BrowserSession.UiOrigin,
            json: """{"confirmedVersion":"1.0.0","confirmed":true}""", headers: [("If-Match", etag)]);

        Assert.True(trusted.IsSuccessStatusCode, $"disable returned {(int)trusted.StatusCode}");
    }

    // The gate runs before binding and any body read: a forged upload is refused by the gate, not by the endpoint's own checks.
    [Fact]
    public async Task ForgedUpload_IsRefusedByTheGate_BeforeTheEndpointSeesIt()
    {
        using var factory = new TestAppFactory { Roles = AllRoles };
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        using var forged = new HttpRequestMessage(HttpMethod.Post, "/api/plugins/archives") { Content = new StringContent("not a zip", Encoding.UTF8, "text/plain") };
        forged.Headers.TryAddWithoutValidation("Cookie", $"{BrowserSession.CookieName}={cookie}");
        forged.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var refused = await client.SendAsync(forged);

        using var trusted = new HttpRequestMessage(HttpMethod.Post, "/api/plugins/archives") { Content = new StringContent("not a zip", Encoding.UTF8, "text/plain") };
        trusted.Headers.TryAddWithoutValidation("Cookie", $"{BrowserSession.CookieName}={cookie}");
        trusted.Headers.TryAddWithoutValidation("If-None-Match", "*");
        trusted.Headers.TryAddWithoutValidation("X-bOps-Request", "1");
        trusted.Headers.TryAddWithoutValidation("Origin", BrowserSession.UiOrigin);
        using var reachedEndpoint = await client.SendAsync(trusted);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("csrf_rejected", await BrowserSessionLoginTests.CodeAsync(refused));
        Assert.NotEqual(HttpStatusCode.Forbidden, reachedEndpoint.StatusCode);
    }

    // §17 row 27 and §10.3: cookie logout deletes the row and the cookie; the old cookie replays as 401.
    [Fact]
    public async Task Logout_DeletesTheSession_AndTheOldCookieReplaysAs401()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        using var forged = await BrowserSession.SendAsync(client, HttpMethod.Delete, "/api/session", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Equal(1, BrowserSession.RowCount(factory));

        using var logout = await BrowserSession.SendTrustedAsync(client, HttpMethod.Delete, "/api/session", cookie);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.True(BrowserSession.DeletesCookie(logout));
        Assert.Equal("no-store", logout.Headers.CacheControl?.ToString());
        Assert.Equal(0, BrowserSession.RowCount(factory));

        using var replay = await BrowserSession.GetMeAsync(client, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.True(BrowserSession.DeletesCookie(replay));

        using var replayLogout = await BrowserSession.SendTrustedAsync(client, HttpMethod.Delete, "/api/session", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, replayLogout.StatusCode);
        Assert.True(BrowserSession.DeletesCookie(replayLogout));
    }

    // §17 row 28: a Bearer logout is a no-op, even with a cookie attached.
    [Fact]
    public async Task BearerLogout_Is204_AndLeavesTheBrowserSessionValid()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        using var logout = await BrowserSession.SendAsync(client, HttpMethod.Delete, "/api/session", cookie, bearer: $"Bearer {factory.ApiKey}");

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Empty(BrowserSession.SessionSetCookies(logout));
        using var me = await BrowserSession.GetMeAsync(client, cookie);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task AnonymousLogout_Is401()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();

        using var response = await client.DeleteAsync(new Uri("/api/session", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
