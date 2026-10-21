// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;

namespace bOps.Api.Tests;

/// <summary>A small stand-in for the Angular build output, in a temp directory; the reserved files sit next to it, never in it.</summary>
internal sealed class WebUiFixture : IDisposable
{
    public const string IndexHtml =
        "<!doctype html><html><head><base href=\"/\"><link rel=\"stylesheet\" href=\"styles-ABCD1234.css\"></head>"
        + "<body><app-root></app-root><script src=\"main-ABCD1234.js\" type=\"module\"></script></body></html>";

    public WebUiFixture()
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("bops-webui-tests-").FullName;
        RootPath = System.IO.Path.Combine(Directory, "wwwroot-ui");
        System.IO.Directory.CreateDirectory(RootPath);
        File.WriteAllText(System.IO.Path.Combine(RootPath, "index.html"), IndexHtml);
        File.WriteAllText(System.IO.Path.Combine(RootPath, "main-ABCD1234.js"), "console.log('bops');");
        File.WriteAllText(System.IO.Path.Combine(RootPath, "styles-ABCD1234.css"), "body{margin:0}");
        File.WriteAllBytes(System.IO.Path.Combine(RootPath, "logo.png"), [0x89, 0x50, 0x4E, 0x47]);

        // Sensitive files in the directory that holds the UI folder, as in an install directory.
        foreach (var name in new[] { "appsettings.json", "sessions.db", "audit.jsonl", "vault.dat", "policy.yaml", "settings.json", "tasks.db" })
        {
            File.WriteAllText(System.IO.Path.Combine(Directory, name), "secret");
        }
    }

    public string Directory { get; }

    public string RootPath { get; }

    public IReadOnlyDictionary<string, string?> Configuration(IDictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?> { ["WebUi:Enabled"] = "true", ["WebUi:RootPath"] = RootPath };
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        return settings;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}

/// <summary>F-26 (BOPS-0120): the compiled SPA is served by the API host without touching the API, the session or the CSRF gate.</summary>
public sealed class WebUiHostingTests : IDisposable
{
    private readonly WebUiFixture _ui = new();

    public void Dispose() => _ui.Dispose();

    private TestAppFactory Factory(IDictionary<string, string?>? extra = null) =>
        new() { ExtraConfiguration = _ui.Configuration(extra) };

    /// <summary>A browser navigation: the request a refresh or a typed URL sends.</summary>
    private static async Task<HttpResponseMessage> NavigateAsync(HttpClient client, string path, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path) => client.GetAsync(new Uri(path, UriKind.Relative));

    [Fact]
    public async Task Root_ServesTheSpaShell_WithoutAnyCredential_AndHardeningHeaders()
    {
        using var factory = Factory();
        using var client = factory.CreateAnonymousClient();

        using var response = await NavigateAsync(client, "/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(WebUiFixture.IndexHtml, await response.Content.ReadAsStringAsync());
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/approvals")]
    [InlineData("/delegations/some-id/steps/3")]
    [InlineData("/settings/")]
    public async Task DeepLinks_AreServedTheShell_SoARefreshWorks(string path)
    {
        using var factory = Factory();
        using var client = factory.CreateAnonymousClient();

        using var response = await NavigateAsync(client, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(WebUiFixture.IndexHtml, await response.Content.ReadAsStringAsync());
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task HeadOnADeepLink_HasNoBody_AndOtherMethodsAreNotFound()
    {
        using var factory = Factory();
        using var client = factory.CreateAnonymousClient();

        using var head = await NavigateAsync(client, "/approvals", HttpMethod.Head);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(WebUiFixture.IndexHtml.Length, head.Content.Headers.ContentLength);

        using var post = await NavigateAsync(client, "/approvals", HttpMethod.Post);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        using var delete = await NavigateAsync(client, "/approvals", HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task ARequestThatDoesNotAcceptHtml_IsNotGivenTheShell()
    {
        using var factory = Factory();
        using var client = factory.CreateAnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/dashboard");
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Assets_AreServedWithTheirTypes_HashedOnesImmutable_TheRestRevalidated()
    {
        using var factory = Factory();
        using var client = factory.CreateAnonymousClient();

        using var script = await GetAsync(client, "/main-ABCD1234.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("javascript", script.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        Assert.Equal("public, max-age=31536000, immutable", script.Headers.CacheControl?.ToString());
        Assert.Equal("console.log('bops');", await script.Content.ReadAsStringAsync());

        using var style = await GetAsync(client, "/styles-ABCD1234.css");
        Assert.Equal("text/css", style.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", style.Headers.CacheControl?.ToString());

        using var logo = await GetAsync(client, "/logo.png");
        Assert.Equal("image/png", logo.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", logo.Headers.CacheControl?.ToString());

        using var index = await GetAsync(client, "/index.html");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("no-cache", index.Headers.CacheControl?.ToString());
        Assert.True(index.Headers.Contains("Content-Security-Policy"));
    }

    [Theory]
    [InlineData("/missing.js")]
    [InlineData("/assets/missing-ABCD1234.css")]
    [InlineData("/favicon-nope.ico")]
    public async Task AMissingAsset_IsNotFound_NeverTheShell(string path)
    {
        using var factory = Factory();
        using var client = factory.CreateAnonymousClient();

        using var response = await NavigateAsync(client, path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/appsettings.json")]
    [InlineData("/sessions.db")]
    [InlineData("/audit.jsonl")]
    [InlineData("/vault.dat")]
    [InlineData("/policy.yaml")]
    [InlineData("/settings.json")]
    [InlineData("/tasks.db")]
    [InlineData("/../appsettings.json")]
    [InlineData("/%2e%2e/appsettings.json")]
    [InlineData("/%2e%2e%2fappsettings.json")]
    [InlineData("/wwwroot-ui/../sessions.db")]
    [InlineData("/web.config")]
    public async Task ReservedAndPrivateFiles_AreNeverServed(string path)
    {
        using var factory = Factory();
        using var client = factory.CreateAnonymousClient();

        using var response = await NavigateAsync(client, path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKeys", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_StillWorks_ProtectedRoutesStillRequireAuthorization_AndUnknownRoutesAreNotTheShell()
    {
        using var factory = Factory();
        using var authorized = factory.CreateClient();
        using var anonymous = factory.CreateAnonymousClient();

        using var me = await GetAsync(authorized, "/api/session/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Contains("test-user", await me.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var protectedAnonymous = await GetAsync(anonymous, "/api/tools");
        Assert.Equal(HttpStatusCode.Unauthorized, protectedAnonymous.StatusCode);

        foreach (var path in new[] { "/api/nope", "/api/nope/deeper", "/api", "/api/", "/API/nope", "/api/tools/does-not-exist/x" })
        {
            using var unknown = await NavigateAsync(authorized, path);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.NotEqual("text/html", unknown.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain("<app-root>", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using var content = new StringContent("{}");
        using var unknownPost = await authorized.PostAsync(new Uri("/api/nope", UriKind.Relative), content);
        Assert.Equal(HttpStatusCode.NotFound, unknownPost.StatusCode);
    }

    [Fact]
    public async Task Healthz_IsAnonymousAndNotTheShell()
    {
        using var factory = Factory();
        using var client = factory.CreateAnonymousClient();

        using var response = await NavigateAsync(client, "/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        using var below = await NavigateAsync(client, "/healthz/anything");
        Assert.Equal(HttpStatusCode.NotFound, below.StatusCode);
    }

    [Fact]
    public async Task Healthz_IsNotRateLimited_SoAProxyProbeCannotStarveSignIn()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateAnonymousClient();

        // The anonymous bucket holds 120 tokens; 200 probes would exhaust it if /healthz drew on it.
        for (var i = 0; i < 200; i++)
        {
            using var response = await GetAsync(client, "/healthz");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var login = await BrowserSession.LoginAsync(factory.CreateSessionClient(), factory.ApiKey);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task WithoutTheWebUiSection_TheHostIsApiOnly()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateAnonymousClient();

        using var root = await NavigateAsync(client, "/");
        using var deep = await NavigateAsync(client, "/approvals");
        using var health = await GetAsync(client, "/healthz");

        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deep.StatusCode);
        Assert.False(root.Headers.Contains("Content-Security-Policy"));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task SessionLifecycle_IsUnchanged_AndOnlyTheConfiguredProductionOriginIsTrusted()
    {
        const string productionOrigin = "https://bops.example.com";
        using var factory = Factory(new Dictionary<string, string?> { ["BrowserSession:Origins"] = productionOrigin });
        using var client = factory.CreateSessionClient();
        string[] foreignOrigins = [BrowserSession.UiOrigin, "https://evil.example.com", "https://bops.example.com:8443", "http://bops.example.com", "null"];

        // Login from the configured origin: the cookie is as hardened as ever.
        using var login = await BrowserSession.SendAsync(client, HttpMethod.Post, "/api/session", null, csrf: true, origin: productionOrigin,
            json: $$"""{"apiKey":"{{factory.ApiKey}}"}""");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var line = Assert.Single(BrowserSession.SessionSetCookies(login));
        foreach (var attribute in new[] { "httponly", "secure", "samesite=strict", "path=/" })
        {
            Assert.Contains(attribute, line, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("domain", line, StringComparison.OrdinalIgnoreCase);
        var cookie = BrowserSession.CookieValue(login)!;

        // A spoofed forwarded host or scheme changes nothing: the check is on Origin alone.
        using var spoofed = await BrowserSession.SendAsync(client, HttpMethod.Post, "/api/session", null, csrf: true, origin: BrowserSession.UiOrigin,
            json: $$"""{"apiKey":"{{factory.ApiKey}}"}""",
            headers: [("X-Forwarded-Host", "bops.example.com"), ("X-Forwarded-Proto", "https"), ("X-Forwarded-For", "203.0.113.9")]);
        Assert.Equal(HttpStatusCode.Forbidden, spoofed.StatusCode);

        foreach (var origin in foreignOrigins)
        {
            using var rejected = await BrowserSession.SendAsync(client, HttpMethod.Post, "/api/session", null, csrf: true, origin: origin,
                json: $$"""{"apiKey":"{{factory.ApiKey}}"}""");
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        }

        // The session works for reads; an unsafe cookie request needs both the header and the origin.
        using var me = await BrowserSession.GetMeAsync(client, cookie);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var noHeader = await BrowserSession.SendAsync(client, HttpMethod.Delete, "/api/session", cookie, csrf: false, origin: productionOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);
        using var wrongOrigin = await BrowserSession.SendAsync(client, HttpMethod.Delete, "/api/session", cookie, csrf: true, origin: "https://evil.example.com");
        Assert.Equal(HttpStatusCode.Forbidden, wrongOrigin.StatusCode);

        using var logout = await BrowserSession.SendAsync(client, HttpMethod.Delete, "/api/session", cookie, csrf: true, origin: productionOrigin);
        Assert.True(logout.IsSuccessStatusCode);
        Assert.True(BrowserSession.DeletesCookie(logout));
        using var after = await BrowserSession.GetMeAsync(client, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task TheShellRequestDoesNotTouchOrCreateASession()
    {
        using var factory = Factory();
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);
        var rowsBefore = BrowserSession.RowCount(factory);

        using var shell = await BrowserSession.SendAsync(client, HttpMethod.Get, "/dashboard", cookie, headers: [("Accept", "text/html")]);

        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.False(shell.Headers.Contains("Set-Cookie"));
        Assert.Equal(rowsBefore, BrowserSession.RowCount(factory));
    }
}

/// <summary>F-26: the <c>WebUi</c> and <c>ReverseProxy</c> sections fail startup on a bad value instead of serving something unintended.</summary>
public sealed class WebUiSettingsTests : IDisposable
{
    private readonly WebUiFixture _ui = new();

    public void Dispose() => _ui.Dispose();

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => pair.Value)).Build();

    [Fact]
    public void Unset_IsAutomatic_OffWithoutTheDirectory_OnWithIt()
    {
        using var absent = WebUiSettings.Load(Config(), _ui.Directory + Path.DirectorySeparatorChar + "nowhere");
        Assert.False(absent.Enabled);
        Assert.Null(absent.Files);

        using var present = WebUiSettings.Load(Config(), _ui.Directory);
        Assert.True(present.Enabled);
        Assert.Equal(_ui.RootPath, present.RootPath);
    }

    [Fact]
    public void RelativeRootPath_IsResolvedAgainstTheApplicationDirectory_NotTheWorkingDirectory()
    {
        using var settings = WebUiSettings.Load(Config(("WebUi:RootPath", "wwwroot-ui")), _ui.Directory);

        Assert.Equal(_ui.RootPath, settings.RootPath);
    }

    [Fact]
    public void ExplicitFalse_NeverServes_EvenWhenTheDirectoryExists()
    {
        using var settings = WebUiSettings.Load(Config(("WebUi:Enabled", "false")), _ui.Directory);

        Assert.False(settings.Enabled);
    }

    [Fact]
    public void ExplicitTrue_WithoutTheDirectory_FailsStartup_NamingTheKey()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => WebUiSettings.Load(Config(("WebUi:Enabled", "true"), ("WebUi:RootPath", "missing")), _ui.Directory));

        Assert.Contains("'WebUi:RootPath'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true")]
    [InlineData(null)]
    public void ADirectoryWithoutIndexHtml_FailsStartup_AnIncompleteUiIsNeverServedSilently(string? enabled)
    {
        File.Delete(Path.Combine(_ui.RootPath, "index.html"));

        var error = Assert.Throws<InvalidOperationException>(() => WebUiSettings.Load(Config(("WebUi:Enabled", enabled)), _ui.Directory));

        Assert.Contains("index.html", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("enabled")]
    public void ANonBooleanEnabled_FailsStartup(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => WebUiSettings.Load(Config(("WebUi:Enabled", value)), _ui.Directory));

        Assert.Contains("'WebUi:Enabled'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankRootPath_FailsStartup()
    {
        Assert.Throws<InvalidOperationException>(() => WebUiSettings.Load(Config(("WebUi:RootPath", " ")), _ui.Directory));
    }

    [Fact]
    public void ReverseProxy_DefaultsToTrustingNoForwardedHeader()
    {
        var settings = ReverseProxySettings.Load(Config());

        Assert.False(settings.Enabled);
    }

    [Fact]
    public void ReverseProxy_HonoursOnlyTheListedProxies_ForTheClientAddressAndSchemeOnly()
    {
        var settings = ReverseProxySettings.Load(Config(("ReverseProxy:KnownProxies", "127.0.0.1, ::1"), ("ReverseProxy:KnownNetworks", "10.1.0.0/24")));
        var options = settings.ToOptions();

        Assert.True(settings.Enabled);
        Assert.Equal(Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
        Assert.Equal(2, options.KnownProxies.Count);
        Assert.Single(options.KnownIPNetworks);
    }

    [Theory]
    [InlineData("ReverseProxy:KnownProxies", "not-an-ip")]
    [InlineData("ReverseProxy:KnownProxies", "10.0.0.0/8")]
    [InlineData("ReverseProxy:KnownNetworks", "10.0.0.1")]
    [InlineData("ReverseProxy:KnownNetworks", "0.0.0.0/0")]
    [InlineData("ReverseProxy:KnownNetworks", "::/0")]
    [InlineData("ReverseProxy:KnownProxies:0", "127.0.0.1")]
    public void ReverseProxy_RefusesAnythingThatWouldTrustTheWorldOrIsMalformed(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => ReverseProxySettings.Load(Config((key, value))));
    }

    [Fact]
    public void AMisconfiguredSection_StopsTheHostFromStarting()
    {
        using var factory = new TestAppFactory
        {
            ExtraConfiguration = new Dictionary<string, string?> { ["ReverseProxy:KnownNetworks"] = "0.0.0.0/0" },
        };

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateAnonymousClient());

        Assert.Contains("ReverseProxy:KnownNetworks", error.ToString(), StringComparison.Ordinal);
    }
}
