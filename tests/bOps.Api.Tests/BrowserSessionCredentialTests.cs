// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;

namespace bOps.Api.Tests;

/// <summary>
/// ADR-0043 §2, §4, §17 rows 19, 28–30: the session stays bound to its credential identity, its roles are re-resolved per request,
/// and it never has more authority than the same key presented as Bearer. Key and role changes take effect at a host restart, so
/// each case restarts the real host on the same files (<see cref="TestAppFactory.KeepTempDirectory"/>).
/// </summary>
public sealed class BrowserSessionCredentialTests
{
    // §18: the normative B-1 test (§4 case D).
    [Fact]
    public async Task Session_IsInvalidated_WhenItsSecretNowResolvesToAnotherBearerCredential()
    {
        using var secrets = new SessionSecrets();
        var (variable, key) = secrets.Create();
        var directory = NewDirectory();
        string cookie;
        using (var before = Host(directory, ("ops", variable, "viewer,administrator")))
        {
            using var client = before.CreateSessionClient();
            cookie = await BrowserSession.SignInAsync(client, key);
        }

        using var after = Host(directory, ("readonly", variable, "viewer"), ("ops", variable, "viewer,administrator"));
        using var http = after.CreateSessionClient();

        using var bearer = await BrowserSession.SendAsync(http, HttpMethod.Get, "/api/session/me", null, bearer: $"Bearer {key}");
        Assert.Equal(HttpStatusCode.OK, bearer.StatusCode);
        var bearerIdentity = await IdentityAsync(bearer);
        Assert.Equal("readonly", bearerIdentity.Id);
        Assert.Equal(["viewer"], bearerIdentity.Roles);

        using var session = await BrowserSession.GetMeAsync(http, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        Assert.True(BrowserSession.DeletesCookie(session));
        Assert.Equal(0, BrowserSession.RowCount(after));

        using var settings = await BrowserSession.SendAsync(http, HttpMethod.Get, "/api/settings", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, settings.StatusCode);
        using var again = await BrowserSession.GetMeAsync(http, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    // §4 case E: a later entry sharing the secret does not change which entry Bearer selects; the session stays the original identity.
    [Fact]
    public async Task Session_StaysValid_WhenALaterCredentialSharesItsSecret()
    {
        using var secrets = new SessionSecrets();
        var (variable, key) = secrets.Create();
        var directory = NewDirectory();
        string cookie;
        using (var before = Host(directory, ("ops", variable, "viewer,administrator")))
        {
            using var client = before.CreateSessionClient();
            cookie = await BrowserSession.SignInAsync(client, key);
        }

        using var after = Host(directory, ("ops", variable, "viewer,administrator"), ("readonly", variable, "viewer"));
        using var http = after.CreateSessionClient();

        using var bearer = await BrowserSession.SendAsync(http, HttpMethod.Get, "/api/session/me", null, bearer: $"Bearer {key}");
        Assert.Equal("ops", (await IdentityAsync(bearer)).Id);

        using var session = await BrowserSession.GetMeAsync(http, cookie);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        var identity = await IdentityAsync(session);
        Assert.Equal("ops", identity.Id);
        Assert.Equal(["viewer", "administrator"], identity.Roles);
        Assert.Equal(1, BrowserSession.RowCount(after));

        using var settings = await BrowserSession.SendAsync(http, HttpMethod.Get, "/api/settings", cookie);
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
    }

    // Duplicate ids with different secrets: the candidate is the entry whose binding matches, not merely the first entry with the id.
    [Fact]
    public async Task DuplicateIds_WithDifferentSecrets_ResolveTheEntryTheSessionWasCreatedFor()
    {
        using var secrets = new SessionSecrets();
        var (firstVariable, firstKey) = secrets.Create();
        var (secondVariable, secondKey) = secrets.Create();
        using var factory = Host(NewDirectory(), ("ops", firstVariable, "viewer"), ("ops", secondVariable, "viewer,approver"));
        using var http = factory.CreateSessionClient();

        var secondCookie = await BrowserSession.SignInAsync(http, secondKey);
        var firstCookie = await BrowserSession.SignInAsync(http, firstKey);

        using var second = await BrowserSession.GetMeAsync(http, secondCookie);
        using var first = await BrowserSession.GetMeAsync(http, firstCookie);
        Assert.Equal(["viewer", "approver"], (await IdentityAsync(second)).Roles);
        Assert.Equal(["viewer"], (await IdentityAsync(first)).Roles);

        using var approvalsAsSecond = await BrowserSession.SendAsync(http, HttpMethod.Get, "/api/approvals/pending", secondCookie);
        using var approvalsAsFirst = await BrowserSession.SendAsync(http, HttpMethod.Get, "/api/approvals/pending", firstCookie);
        Assert.Equal(HttpStatusCode.OK, approvalsAsSecond.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, approvalsAsFirst.StatusCode);
    }

    // §17 row 19: credential removed → 401, row gone (and the startup sweep already removed it).
    [Fact]
    public async Task RemovedCredential_InvalidatesItsSessions_AndTheStartupSweepDeletesThem()
    {
        using var secrets = new SessionSecrets();
        var (opsVariable, opsKey) = secrets.Create();
        var (otherVariable, otherKey) = secrets.Create();
        var directory = NewDirectory();
        string opsCookie;
        using (var before = Host(directory, ("ops", opsVariable, "viewer"), ("other", otherVariable, "viewer")))
        {
            using var client = before.CreateSessionClient();
            opsCookie = await BrowserSession.SignInAsync(client, opsKey);
            _ = await BrowserSession.SignInAsync(client, otherKey);
            Assert.Equal(2, BrowserSession.RowCount(before));
        }

        using var after = Host(directory, ("other", otherVariable, "viewer"));
        using var http = after.CreateSessionClient();
        _ = after.Services;
        Assert.Equal(1, BrowserSession.RowCount(after));

        using var response = await BrowserSession.GetMeAsync(http, opsCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(BrowserSession.DeletesCookie(response));
    }

    // §17 row 19: rotated secret (same id, new value) → binding mismatch, 401, row deleted.
    [Fact]
    public async Task RotatedSecret_InvalidatesTheSession_AndDeletesTheRow()
    {
        using var secrets = new SessionSecrets();
        var (variable, key) = secrets.Create();
        var directory = NewDirectory();
        string cookie;
        using (var before = Host(directory, ("ops", variable, "viewer")))
        {
            using var client = before.CreateSessionClient();
            cookie = await BrowserSession.SignInAsync(client, key);
        }

        SessionSecrets.Set(variable, $"{key}-rotated");
        using var after = Host(directory, ("ops", variable, "viewer"));
        using var http = after.CreateSessionClient();
        _ = after.Services;
        Assert.Equal(1, BrowserSession.RowCount(after));

        using var response = await BrowserSession.GetMeAsync(http, cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(BrowserSession.DeletesCookie(response));
        Assert.Equal(0, BrowserSession.RowCount(after));
    }

    // §4: changing only the SecretReference to a variable holding the same value is not a rotation.
    [Fact]
    public async Task SameSecretValue_UnderADifferentVariable_KeepsTheSession()
    {
        using var secrets = new SessionSecrets();
        var (variable, key) = secrets.Create();
        var (renamed, _) = secrets.Create(key);
        var directory = NewDirectory();
        string cookie;
        using (var before = Host(directory, ("ops", variable, "viewer")))
        {
            using var client = before.CreateSessionClient();
            cookie = await BrowserSession.SignInAsync(client, key);
        }

        using var after = Host(directory, ("ops", renamed, "viewer"));
        using var http = after.CreateSessionClient();

        using var response = await BrowserSession.GetMeAsync(http, cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // §4: a secret that resolves empty (variable missing) fails closed.
    [Fact]
    public async Task SecretResolvingEmpty_InvalidatesTheSession()
    {
        using var secrets = new SessionSecrets();
        var (variable, key) = secrets.Create();
        var directory = NewDirectory();
        string cookie;
        using (var before = Host(directory, ("ops", variable, "viewer")))
        {
            using var client = before.CreateSessionClient();
            cookie = await BrowserSession.SignInAsync(client, key);
        }

        Environment.SetEnvironmentVariable(variable, null);
        using var after = Host(directory, ("ops", variable, "viewer"));
        using var http = after.CreateSessionClient();

        using var response = await BrowserSession.GetMeAsync(http, cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, BrowserSession.RowCount(after));
    }

    // §4: roles are re-resolved, never snapshotted — a removed role never survives in a session, a granted one appears.
    [Fact]
    public async Task RoleChanges_TakeEffect_ForAnExistingSession_AtTheNextHostStart()
    {
        using var secrets = new SessionSecrets();
        var (variable, key) = secrets.Create();
        var directory = NewDirectory();
        string cookie;
        using (var before = Host(directory, ("ops", variable, "viewer,administrator")))
        {
            using var client = before.CreateSessionClient();
            cookie = await BrowserSession.SignInAsync(client, key);
            using var allowed = await BrowserSession.SendAsync(client, HttpMethod.Get, "/api/settings", cookie);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var reduced = Host(directory, ("ops", variable, "viewer,approver"));
        using var http = reduced.CreateSessionClient();

        using var settings = await BrowserSession.SendAsync(http, HttpMethod.Get, "/api/settings", cookie);
        using var approvals = await BrowserSession.SendAsync(http, HttpMethod.Get, "/api/approvals/pending", cookie);
        using var bearerSettings = await BrowserSession.SendAsync(http, HttpMethod.Get, "/api/settings", null, bearer: $"Bearer {key}");

        Assert.Equal(HttpStatusCode.Forbidden, settings.StatusCode);
        Assert.Empty(BrowserSession.SessionSetCookies(settings));
        Assert.Equal(HttpStatusCode.OK, approvals.StatusCode);
        Assert.Equal(bearerSettings.StatusCode, settings.StatusCode);
    }

    internal static string NewDirectory() => Directory.CreateTempSubdirectory("bops-session-tests-").FullName;

    /// <summary>A real host over <paramref name="directory"/> with exactly the given ordered credentials.</summary>
    internal static TestAppFactory Host(string directory, params (string Id, string Variable, string Roles)[] credentials) =>
        new()
        {
            TempDirectory = directory,
            KeepTempDirectory = true,
            VaultMasterKey = $"vault-master-key-{Path.GetFileName(directory)}",
            ExtraConfiguration = SessionSecrets.Configuration(credentials),
        };

    internal static async Task<(string? Id, string[] Roles)> IdentityAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (
            body.RootElement.GetProperty("id").GetString(),
            body.RootElement.GetProperty("roles").EnumerateArray().Select(role => role.GetString()!).ToArray());
    }
}
