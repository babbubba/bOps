// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;

namespace bOps.Api.Tests;

/// <summary>ADR-0043 §6 with a fake clock: equality expires, absolute never slides, idle slides once a minute and only on accepted requests.</summary>
public sealed class BrowserSessionExpiryTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-05T08:00:00Z", CultureInfo.InvariantCulture);
    private static readonly TimeSpan Idle = TimeSpan.FromHours(1);

    [Fact]
    public async Task Idle_OneMillisecondBeforeTheLimit_IsValid()
    {
        var clock = new ManualClock(Start);
        using var factory = new TestAppFactory { Clock = clock };
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        clock.Advance(Idle - TimeSpan.FromMilliseconds(1));
        using var response = await BrowserSession.GetMeAsync(client, cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Idle_AtTheLimit_IsExpired_AndTheRowIsDeleted()
    {
        var clock = new ManualClock(Start);
        using var factory = new TestAppFactory { Clock = clock };
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        clock.Advance(Idle);
        using var response = await BrowserSession.GetMeAsync(client, cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(BrowserSession.DeletesCookie(response));
        Assert.Equal(0, BrowserSession.RowCount(factory));
    }

    [Fact]
    public async Task Absolute_NeverSlides_AcrossManyTouches_AndExpiresAtTheBoundary()
    {
        var clock = new ManualClock(Start);
        using var factory = new TestAppFactory { Clock = clock };
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        var elapsed = TimeSpan.Zero;
        var absolute = TimeSpan.FromHours(12);
        while (elapsed + TimeSpan.FromMinutes(50) < absolute)
        {
            clock.Advance(TimeSpan.FromMinutes(50));
            elapsed += TimeSpan.FromMinutes(50);
            using var touched = await BrowserSession.GetMeAsync(client, cookie);
            Assert.Equal(HttpStatusCode.OK, touched.StatusCode);
        }

        clock.Advance(absolute - elapsed - TimeSpan.FromMilliseconds(1));
        using (var lastMoment = await BrowserSession.GetMeAsync(client, cookie))
        {
            Assert.Equal(HttpStatusCode.OK, lastMoment.StatusCode);
        }

        clock.Advance(TimeSpan.FromMilliseconds(1));
        using var expired = await BrowserSession.GetMeAsync(client, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(0, BrowserSession.RowCount(factory));
    }

    [Fact]
    public async Task Touch_ExtendsIdle_ButNotWithinSixtySeconds()
    {
        var clock = new ManualClock(Start);
        using var factory = new TestAppFactory { Clock = clock };
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);
        var created = BrowserSessionAuthenticationTests.LastSeen(factory);

        clock.Advance(TimeSpan.FromSeconds(59));
        (await BrowserSession.GetMeAsync(client, cookie)).Dispose();
        Assert.Equal(created, BrowserSessionAuthenticationTests.LastSeen(factory));

        clock.Advance(TimeSpan.FromSeconds(1));
        (await BrowserSession.GetMeAsync(client, cookie)).Dispose();
        Assert.Equal(created + 60_000, BrowserSessionAuthenticationTests.LastSeen(factory));

        // Idle now counts from the touch: the original idle limit has passed, the extended one has not.
        clock.Advance(Idle - TimeSpan.FromMilliseconds(1));
        using var stillValid = await BrowserSession.GetMeAsync(client, cookie);
        Assert.Equal(HttpStatusCode.OK, stillValid.StatusCode);
    }

    [Fact]
    public async Task RejectedRequests_NeverExtendIdle()
    {
        var clock = new ManualClock(Start);
        using var factory = new TestAppFactory { Clock = clock, Roles = ["viewer", "operator"] };
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);
        var created = BrowserSessionAuthenticationTests.LastSeen(factory);
        clock.Advance(TimeSpan.FromMinutes(10));

        // 403 from the CSRF gate.
        using (var csrf = await BrowserSession.SendAsync(client, HttpMethod.Delete, $"/api/agents/tasks/{Guid.NewGuid()}", cookie))
        {
            Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        }

        // 403 from a role policy.
        using (var role = await BrowserSession.SendAsync(client, HttpMethod.Get, "/api/settings", cookie))
        {
            Assert.Equal(HttpStatusCode.Forbidden, role.StatusCode);
        }

        // 401 because an explicit (invalid) Authorization header decides.
        using (var bearer = await BrowserSession.SendAsync(client, HttpMethod.Get, "/api/session/me", cookie, bearer: "Bearer nope"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, bearer.StatusCode);
        }

        Assert.Equal(created, BrowserSessionAuthenticationTests.LastSeen(factory));

        // 429 from the global limiter: exhaust the strict write bucket first (no touch: under a minute on the fake clock), then
        // move the fake clock past the granularity; the limiter's own clock is real, so the bucket stays empty.
        clock.Advance(-TimeSpan.FromMinutes(10));
        var limited = false;
        for (var i = 0; i < 200 && !limited; i++)
        {
            using var write = await BrowserSession.SendTrustedAsync(client, HttpMethod.Delete, $"/api/agents/tasks/{Guid.NewGuid()}", cookie);
            limited = write.StatusCode == HttpStatusCode.TooManyRequests;
        }

        Assert.True(limited);
        clock.Advance(TimeSpan.FromMinutes(10));
        using (var rejected = await BrowserSession.SendTrustedAsync(client, HttpMethod.Delete, $"/api/agents/tasks/{Guid.NewGuid()}", cookie))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        }

        Assert.Equal(created, BrowserSessionAuthenticationTests.LastSeen(factory));
    }

    [Fact]
    public async Task ARequestThatReachedItsEndpoint_ExtendsIdle_EvenWhenTheEndpointAnswersWithAnError()
    {
        var clock = new ManualClock(Start);
        using var factory = new TestAppFactory { Clock = clock, Roles = ["viewer", "operator"] };
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);
        var created = BrowserSessionAuthenticationTests.LastSeen(factory);
        clock.Advance(TimeSpan.FromMinutes(2));

        using var notFound = await BrowserSession.SendTrustedAsync(client, HttpMethod.Delete, $"/api/agents/tasks/{Guid.NewGuid()}", cookie);

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(created + 120_000, BrowserSessionAuthenticationTests.LastSeen(factory));
    }

    [Fact]
    public async Task LoweringAbsoluteTimeout_OnRestart_ShortensLiveSessions()
    {
        var clock = new ManualClock(Start);
        var directory = BrowserSessionCredentialTests.NewDirectory();
        var key = $"k-{Guid.NewGuid():N}";
        string cookie;
        using (var before = Restartable(directory, key, clock, absolute: "12:00:00", idle: "01:00:00"))
        {
            using var client = before.CreateSessionClient();
            cookie = await BrowserSession.SignInAsync(client, key);
        }

        using var after = Restartable(directory, key, clock, absolute: "02:00:00", idle: "01:00:00");
        using var http = after.CreateSessionClient();
        await KeepAliveAsync(http, cookie, clock);
        clock.Advance(TimeSpan.FromMinutes(20) - TimeSpan.FromMilliseconds(1));
        using (var lastMoment = await BrowserSession.GetMeAsync(http, cookie))
        {
            Assert.Equal(HttpStatusCode.OK, lastMoment.StatusCode);
        }

        clock.Advance(TimeSpan.FromMilliseconds(1));
        using var expired = await BrowserSession.GetMeAsync(http, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task RaisingAbsoluteTimeout_OnRestart_NeverLengthensLiveSessions()
    {
        var clock = new ManualClock(Start);
        var directory = BrowserSessionCredentialTests.NewDirectory();
        var key = $"k-{Guid.NewGuid():N}";
        string cookie;
        using (var before = Restartable(directory, key, clock, absolute: "02:00:00", idle: "01:00:00"))
        {
            using var client = before.CreateSessionClient();
            cookie = await BrowserSession.SignInAsync(client, key);
        }

        using var after = Restartable(directory, key, clock, absolute: "12:00:00", idle: "01:00:00");
        using var http = after.CreateSessionClient();
        await KeepAliveAsync(http, cookie, clock);
        clock.Advance(TimeSpan.FromMinutes(20));

        using var expired = await BrowserSession.GetMeAsync(http, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task PersistentCookie_DoesNotExtendTheServerSideLifetime()
    {
        var clock = new ManualClock(Start);
        using var factory = new TestAppFactory { Clock = clock };
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey, keepSignedIn: true);

        clock.Advance(Idle);
        using var response = await BrowserSession.GetMeAsync(client, cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Two touches 50 minutes apart: 100 minutes in, with idle counting from the last one.</summary>
    private static async Task KeepAliveAsync(HttpClient client, string cookie, ManualClock clock)
    {
        for (var i = 0; i < 2; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(50));
            using var response = await BrowserSession.GetMeAsync(client, cookie);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static TestAppFactory Restartable(string directory, string key, ManualClock clock, string absolute, string idle) =>
        new()
        {
            TempDirectory = directory,
            KeepTempDirectory = true,
            ApiKey = key,
            Clock = clock,
            VaultMasterKey = $"vault-master-key-{Path.GetFileName(directory)}",
            ExtraConfiguration = new Dictionary<string, string?>
            {
                ["BrowserSession:AbsoluteTimeout"] = absolute,
                ["BrowserSession:IdleTimeout"] = idle,
            },
        };
}
