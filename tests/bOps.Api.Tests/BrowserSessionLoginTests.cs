// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using bOps.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace bOps.Api.Tests;

/// <summary>ADR-0043 §3, §7, §10.1: <c>POST /api/session</c> — validation order, cookie, supersede, and what a failure leaves alone.</summary>
public sealed class BrowserSessionLoginTests
{
    private static readonly string[] AllRoles = ["viewer", "operator", "approver", "administrator"];

    [Fact]
    public async Task ValidKey_ReturnsIdentity_AndAHardenedBrowserSessionCookie()
    {
        using var factory = new TestAppFactory { Roles = AllRoles };
        using var client = factory.CreateSessionClient();

        using var response = await BrowserSession.LoginAsync(client, factory.ApiKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("test-user", body.RootElement.GetProperty("id").GetString());
        Assert.Equal("Test User", body.RootElement.GetProperty("displayName").GetString());
        Assert.Equal(AllRoles, body.RootElement.GetProperty("roles").EnumerateArray().Select(role => role.GetString()!).ToArray());
        Assert.Equal(3, body.RootElement.EnumerateObject().Count());

        var line = Assert.Single(BrowserSession.SessionSetCookies(response));
        var value = BrowserSession.CookieValue(response)!;
        Assert.Matches("^[A-Za-z0-9_-]{43}$", value);
        var attributes = line[(BrowserSession.CookieName.Length + 1 + value.Length)..]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(attribute => attribute.ToLowerInvariant())
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["httponly", "path=/", "samesite=strict", "secure"], attributes);
    }

    [Fact]
    public async Task KeepSignedIn_SetsMaxAge_ToTheAbsoluteLifetime_AndFalseKeepsASessionCookie()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();

        using var persistent = await BrowserSession.LoginAsync(client, factory.ApiKey, keepSignedIn: true);
        var persistentLine = Assert.Single(BrowserSession.SessionSetCookies(persistent));
        Assert.Contains("max-age=43200", persistentLine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", persistentLine, StringComparison.OrdinalIgnoreCase);

        using var explicitFalse = await BrowserSession.LoginAsync(client, factory.ApiKey, keepSignedIn: false);
        var sessionLine = Assert.Single(BrowserSession.SessionSetCookies(explicitFalse));
        Assert.DoesNotContain("max-age", sessionLine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", sessionLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KeepSignedIn_MaxAge_FollowsAConfiguredAbsoluteTimeout()
    {
        using var factory = new TestAppFactory
        {
            ExtraConfiguration = new Dictionary<string, string?> { ["BrowserSession:AbsoluteTimeout"] = "02:00:00", ["BrowserSession:IdleTimeout"] = "00:30:00" },
        };
        using var client = factory.CreateSessionClient();

        using var response = await BrowserSession.LoginAsync(client, factory.ApiKey, keepSignedIn: true);

        Assert.Contains("max-age=7200", Assert.Single(BrowserSession.SessionSetCookies(response)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidKey_Is401_WithNoCookie_AndLeavesAPresentedSessionValid()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        using var failed = await BrowserSession.LoginAsync(client, "not-the-key", cookie: cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        Assert.Empty(BrowserSession.SessionSetCookies(failed));
        Assert.Equal("invalid_credential", await CodeAsync(failed));
        using var me = await BrowserSession.GetMeAsync(client, cookie);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(1, BrowserSession.RowCount(factory));
    }

    [Fact]
    public async Task KeyWithoutViewer_Is403_WithNoSession_ButBearerStillWorks()
    {
        using var factory = new TestAppFactory { Roles = ["operator"] };
        using var client = factory.CreateSessionClient();

        using var response = await BrowserSession.LoginAsync(client, factory.ApiKey);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("viewer_role_required", await CodeAsync(response));
        Assert.Empty(BrowserSession.SessionSetCookies(response));
        Assert.Equal(0, BrowserSession.RowCount(factory));

        using var bearer = factory.CreateClient();
        using var cancel = await bearer.DeleteAsync(new Uri($"/api/agents/tasks/{Guid.NewGuid()}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData("application/json; charset=latin1")]
    [InlineData("application/jsonx")]
    [InlineData(null)]
    public async Task NonJsonMediaType_Is415_BeforeTheBodyOrKey(string? contentType)
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes($$"""{"apiKey":"{{factory.ApiKey}}"}""")),
        };
        if (contentType is not null)
        {
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        request.Headers.Add("X-bOps-Request", "1");
        request.Headers.Add("Origin", BrowserSession.UiOrigin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(BrowserSession.SessionSetCookies(response));
    }

    [Fact]
    public async Task JsonWithUtf8Charset_IsAccepted()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session")
        {
            Content = new StringContent($$"""{"apiKey":"{{factory.ApiKey}}"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-bOps-Request", "1");
        request.Headers.Add("Origin", BrowserSession.UiOrigin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BodyOver4KiB_Is413()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();
        var padding = new string(' ', 4100);

        using var response = await BrowserSession.SendAsync(
            client, HttpMethod.Post, "/api/session", cookie: null, csrf: true, origin: BrowserSession.UiOrigin,
            json: $$"""{"apiKey":"{{factory.ApiKey}}"}{{padding}}""");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("payload_too_large", await CodeAsync(response));
        Assert.Equal(0, BrowserSession.RowCount(factory));
    }

    [Theory]
    [InlineData("""{"apiKey":"KEY","extra":1}""")]
    [InlineData("""{"apiKey":"KEY","apiKey":"KEY"}""")]
    [InlineData("""{"apiKey":"KEY"} {}""")]
    [InlineData("""{"apiKey":"KEY",}""")]
    [InlineData("""{"ApiKey":"KEY"}""")]
    [InlineData("""{"apiKey":"KEY","keepSignedIn":"true"}""")]
    [InlineData("""{"apiKey":"KEY","keepSignedIn":null}""")]
    [InlineData("""{"apiKey":"KEY","keepSignedIn":1}""")]
    [InlineData("""{"apiKey":42}""")]
    [InlineData("""{"apiKey":null}""")]
    [InlineData("""{"apiKey":"   "}""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    [InlineData("")]
    [InlineData("""{"apiKey":"KEY" /* c */}""")]
    public async Task MalformedShape_Is400_WithAFixedBody_AndNoSession(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();

        using var response = await BrowserSession.SendAsync(
            client, HttpMethod.Post, "/api/session", cookie: null, csrf: true, origin: BrowserSession.UiOrigin,
            json: template.Replace("KEY", factory.ApiKey, StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal("invalid_request", JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
        BrowserSession.AssertAbsent(text, factory.ApiKey, "the 400 body");
        Assert.Equal(0, BrowserSession.RowCount(factory));
    }

    [Fact]
    public async Task KeyIsTrimmed_ExactlyAsBearerTrimsIt()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();

        using var response = await BrowserSession.LoginAsync(client, $"  {factory.ApiKey}\t ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EverySuccessfulLogin_IssuesAFreshToken()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();

        var first = await BrowserSession.SignInAsync(client, factory.ApiKey);
        var second = await BrowserSession.SignInAsync(client, factory.ApiKey);

        Assert.NotEqual(first, second);
        Assert.Equal(2, BrowserSession.RowCount(factory));
    }

    [Fact]
    public async Task LoginPresentingASession_SupersedesIt_InOneStep()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();
        var old = await BrowserSession.SignInAsync(client, factory.ApiKey);

        var renewed = await BrowserSession.SignInAsync(client, factory.ApiKey, cookie: old);

        Assert.NotEqual(old, renewed);
        Assert.Equal(1, BrowserSession.RowCount(factory));
        using var replay = await BrowserSession.GetMeAsync(client, old);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using var current = await BrowserSession.GetMeAsync(client, renewed);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    [Fact]
    public async Task LoginSucceeds_WhenThePresentedCookieIsGarbage_AndNeverEmitsTwoSessionCookies()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();

        using var response = await BrowserSession.LoginAsync(client, factory.ApiKey, cookie: "garbage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(BrowserSession.SessionSetCookies(response));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://localhost:4300", true)]
    [InlineData("http://127.0.0.1:4200", true)]
    [InlineData("null", true)]
    [InlineData("http://localhost:4200/", true)]
    [InlineData(BrowserSession.UiOrigin, false)]
    public async Task OriginGateFailures_Are403_AndNeverValidateTheKey(string? origin, bool csrfHeader)
    {
        var secrets = new CountingSecretProvider();
        using var factory = new TestAppFactory
        {
            ConfigureExtraServices = services => services.Replace(ServiceDescriptor.Singleton<ISecretProvider>(secrets)),
        };
        using var client = factory.CreateSessionClient();
        _ = factory.Services;
        secrets.Reset();

        using var response = await BrowserSession.LoginAsync(client, factory.ApiKey, origin: origin, csrfHeader: csrfHeader);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("csrf_rejected", await CodeAsync(response));
        Assert.Equal(0, secrets.Calls);
        Assert.Empty(BrowserSession.SessionSetCookies(response));
        Assert.Equal(0, BrowserSession.RowCount(factory));
    }

    [Fact]
    public async Task RefererFallback_AppliesToLogin_OnlyWithoutOrigin()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();
        var json = $$"""{"apiKey":"{{factory.ApiKey}}"}""";

        using var sameReferer = await BrowserSession.SendAsync(
            client, HttpMethod.Post, "/api/session", null, csrf: true, json: json, headers: [("Referer", "http://localhost:4200/dashboard?task=1")]);
        using var foreignReferer = await BrowserSession.SendAsync(
            client, HttpMethod.Post, "/api/session", null, csrf: true, json: json, headers: [("Referer", "http://localhost:4300/")]);

        Assert.Equal(HttpStatusCode.OK, sameReferer.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignReferer.StatusCode);
    }

    internal static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? null : JsonDocument.Parse(text).RootElement.GetProperty("code").GetString();
    }

    private sealed class CountingSecretProvider : ISecretProvider
    {
        private readonly bOps.Runtime.EnvironmentSecretProvider _inner = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Reset() => Interlocked.Exchange(ref _calls, 0);

        public string? GetSecret(SecretReference reference)
        {
            // Only API-key resolutions count (the vault and model-provider secrets are resolved by unrelated host services).
            if (reference.Name.StartsWith("BOPS_TEST_API_KEY_", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _calls);
            }

            return _inner.GetSecret(reference);
        }
    }
}
