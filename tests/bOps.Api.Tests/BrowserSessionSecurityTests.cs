// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using bOps.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace bOps.Api.Tests;

/// <summary>ADR-0043 §11, §12, §13: login rate limiting, configuration validation and the negative secret-leakage contract.</summary>
public sealed class BrowserSessionSecurityTests
{
    // ---- §11 rate limiting ----------------------------------------------------------------------------------

    [Fact]
    public async Task Login_TenPerMinutePerAddress_ThenA429WithRetryAfter_AndNoCredentialMaterial()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();

        for (var i = 0; i < 10; i++)
        {
            using var attempt = await BrowserSession.LoginAsync(client, $"wrong-{i}");
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        using var limited = await BrowserSession.LoginAsync(client, factory.ApiKey);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", await BrowserSessionLoginTests.CodeAsync(limited));
        Assert.Equal("no-store", limited.Headers.CacheControl?.ToString());
        Assert.True(limited.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero && delta <= TimeSpan.FromMinutes(1));
        Assert.Empty(BrowserSession.SessionSetCookies(limited));
        BrowserSession.AssertAbsent(await limited.Content.ReadAsStringAsync(), factory.ApiKey, "the 429 body");
        Assert.Equal(0, BrowserSession.RowCount(factory));

        // A fresh partition (a new host) starts a new window.
        using var fresh = new TestAppFactory();
        using var freshClient = fresh.CreateSessionClient();
        using var allowed = await BrowserSession.LoginAsync(freshClient, fresh.ApiKey);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Login_OriginRejections_CountTowardsTheLimit()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();

        for (var i = 0; i < 10; i++)
        {
            using var attempt = await BrowserSession.LoginAsync(client, factory.ApiKey, origin: "http://localhost:4300");
            Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
        }

        using var limited = await BrowserSession.LoginAsync(client, factory.ApiKey);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    // ---- §12 configuration -----------------------------------------------------------------------------------

    [Fact]
    public void AbsentSection_UsesTheDocumentedDefaults()
    {
        var settings = BrowserSessionSettings.Load(new ConfigurationBuilder().Build());

        Assert.Equal(TimeSpan.FromHours(1), settings.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(12), settings.AbsoluteTimeout);
        Assert.Equal("sessions.db", settings.FilePath);
        Assert.Equal([new BrowserOrigin("http", "localhost", 4200)], settings.Origins);
    }

    [Theory]
    [InlineData("Origins", "")]
    [InlineData("Origins", " , ")]
    [InlineData("Origins", "http://localhost:4200,")]
    [InlineData("Origins", "http://localhost:4200/")]
    [InlineData("Origins", "http://localhost:4200/ui")]
    [InlineData("Origins", "http://localhost:4200?x=1")]
    [InlineData("Origins", "http://localhost:4200#f")]
    [InlineData("Origins", "http://user@localhost:4200")]
    [InlineData("Origins", "http://*.localhost:4200")]
    [InlineData("Origins", "ftp://localhost:4200")]
    [InlineData("Origins", "localhost:4200")]
    [InlineData("Origins", "http://bops.example.test")]
    [InlineData("Origins", "http://localhost:4200,HTTP://LOCALHOST:4200")]
    [InlineData("Origins", "https://a.test,https://b.test,https://c.test,https://d.test,https://e.test,https://f.test,https://g.test,https://h.test,https://i.test")]
    [InlineData("IdleTimeout", "00:04:59")]
    [InlineData("IdleTimeout", "1.00:00:01")]
    [InlineData("IdleTimeout", "00:30:00.5")]
    [InlineData("IdleTimeout", "soon")]
    [InlineData("IdleTimeout", "13:00:00")]
    [InlineData("AbsoluteTimeout", "00:14:59")]
    [InlineData("AbsoluteTimeout", "7.00:00:01")]
    [InlineData("AbsoluteTimeout", "00:20:00")]
    [InlineData("FilePath", "")]
    [InlineData("FilePath", "   ")]
    public void InvalidValue_FailsWithAMessageNamingTheKey(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"BrowserSession:{key}"] = value })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => BrowserSessionSettings.Load(configuration));

        Assert.Contains("'BrowserSession:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OriginsAsAnArray_IsRefused_InsteadOfFallingBackToTheDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["BrowserSession:Origins:0"] = "https://bops.example.test" })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => BrowserSessionSettings.Load(configuration));

        Assert.Contains("'BrowserSession:Origins'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://bops.example.test, http://127.0.0.1:4200,http://[::1]:4200")]
    [InlineData("http://localhost:4200,http://localhost:4201")]
    public void ValidOrigins_AreAccepted(string origins)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["BrowserSession:Origins"] = origins })
            .Build();

        Assert.NotEmpty(BrowserSessionSettings.Load(configuration).Origins);
    }

    [Fact]
    public void InvalidConfiguration_FailsHostStartup()
    {
        using var factory = new TestAppFactory
        {
            ExtraConfiguration = new Dictionary<string, string?> { ["BrowserSession:Origins"] = "http://bops.example.test" },
        };

        Assert.ThrowsAny<Exception>(() => factory.Services);
    }

    // ---- §13 secret leakage ----------------------------------------------------------------------------------

    [Fact]
    public async Task NeitherKeyNorToken_AppearsInResponsesOrLogs_AcrossTheSessionLifecycle()
    {
        using var logs = new CapturingLoggerProvider();
        using var factory = new TestAppFactory
        {
            ConfigureExtraServices = services => services.AddSingleton<ILoggerProvider>(logs),
        };
        using var client = factory.CreateSessionClient();
        var responses = new List<string>();

        async Task Capture(HttpResponseMessage response, bool allowSetCookie = false)
        {
            using (response)
            {
                responses.Add(await response.Content.ReadAsStringAsync());
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                {
                    if (allowSetCookie && header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    responses.Add($"{header.Key}: {string.Join(",", header.Value)}");
                }
            }
        }

        using var login = await BrowserSession.LoginAsync(client, factory.ApiKey);
        var cookie = BrowserSession.CookieValue(login)!;
        await Capture(login, allowSetCookie: true);
        await Capture(await BrowserSession.GetMeAsync(client, cookie));
        await Capture(await BrowserSession.LoginAsync(client, factory.ApiKey + "x", cookie: cookie));
        await Capture(await BrowserSession.SendAsync(client, HttpMethod.Post, "/api/session", cookie, csrf: true, origin: BrowserSession.UiOrigin,
            json: $$"""{"apiKey":"{{factory.ApiKey}}","unexpected":true}"""));
        await Capture(await BrowserSession.SendAsync(client, HttpMethod.Delete, "/api/session", cookie));
        await Capture(await BrowserSession.SendTrustedAsync(client, HttpMethod.Delete, "/api/session", cookie));
        await Capture(await BrowserSession.GetMeAsync(client, cookie));

        var all = string.Join("\n", responses) + "\n" + logs.Text();
        BrowserSession.AssertAbsent(all, factory.ApiKey, "responses or logs");
        BrowserSession.AssertAbsent(all, cookie, "responses or logs");
        Assert.Contains(logs.Lines, line => line.Contains("Browser session created", StringComparison.Ordinal));
        Assert.Contains(logs.Lines, line => line.Contains("Browser session revoked (logout)", StringComparison.Ordinal));
        Assert.Contains(logs.Lines, line => line.Contains("Browser sign-in rejected (invalid_credential)", StringComparison.Ordinal));
    }

    // §13: an unhandled exception in Development (the WebApplicationFactory default) yields a problem body without request data.
    [Fact]
    public async Task UnhandledException_InDevelopment_NeverEchoesTheCookieOrAuthorization()
    {
        using var logs = new CapturingLoggerProvider();
        using var factory = new TestAppFactory
        {
            ConfigureExtraServices = services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);
                services.Replace(ServiceDescriptor.Singleton<IBrowserSessionStore>(sp =>
                    new BrowserSessionPersistenceTests.FailingFindStore(new SqliteBrowserSessionStore(sp.GetRequiredService<BrowserSessionSettings>().FilePath))));
                services.Replace(ServiceDescriptor.Singleton<ISecretProvider>(new ThrowingForApiKeysSecretProvider()));
            },
        };
        using var client = factory.CreateSessionClient();
        const string cookie = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string bearer = "Bearer distinctive-bearer-value-0123456789";

        using var viaCookie = await BrowserSession.SendAsync(client, HttpMethod.Get, "/api/session/me", cookie);
        using var viaBearer = await BrowserSession.SendAsync(client, HttpMethod.Get, "/api/session/me", null, bearer: bearer);

        foreach (var response in new[] { viaCookie, viaBearer })
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.StartsWith("application/problem+json", response.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
            BrowserSession.AssertAbsent(body, cookie, "the problem body");
            BrowserSession.AssertAbsent(body, "distinctive-bearer-value", "the problem body");
            Assert.DoesNotContain("Cookie", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Authorization", body, StringComparison.OrdinalIgnoreCase);
        }

        BrowserSession.AssertAbsent(logs.Text(), "distinctive-bearer-value", "logs");
        BrowserSession.AssertAbsent(logs.Text(), cookie, "logs");
    }

    private sealed class ThrowingForApiKeysSecretProvider : ISecretProvider
    {
        private readonly bOps.Runtime.EnvironmentSecretProvider _inner = new();

        public string? GetSecret(SecretReference reference) =>
            reference.Name.StartsWith("BOPS_TEST_API_KEY_", StringComparison.Ordinal)
                ? throw new InvalidOperationException("simulated secret provider failure")
                : _inner.GetSecret(reference);
    }

    /// <summary>Records every log line as formatted text plus every structured value and exception, for secret scans.</summary>
    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public string Text() => string.Join("\n", _lines);

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(";", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                    : string.Empty;
                lines.Enqueue($"{category} {logLevel}: {formatter(state, exception)} [{values}] {exception}");
            }
        }
    }
}
