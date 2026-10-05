// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Data.Sqlite;

namespace bOps.Api.Tests;

/// <summary>A clock whose wall time only moves when a test says so. Timers and timestamps stay real, so the host is unaffected.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long _utcTicks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _utcTicks, by.Ticks);
}

/// <summary>
/// Ephemeral API keys for browser-session tests: each secret is a fresh random value in a randomly named environment variable,
/// removed on dispose. A configuration is an ordered list of <c>(id, secret variable, roles)</c> entries, as
/// <c>Authentication:ApiKeys</c>. Secrets are never written to assertion messages.
/// </summary>
internal sealed class SessionSecrets : IDisposable
{
    private readonly List<string> _variables = [];

    /// <summary>Creates a new secret and returns the environment variable holding it, plus its value for the test to present.</summary>
    public (string Variable, string Value) Create(string? value = null)
    {
        var variable = $"BOPS_SESSION_TEST_{Guid.NewGuid():N}";
        var secret = value ?? $"k-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))}";
        Environment.SetEnvironmentVariable(variable, secret);
        _variables.Add(variable);
        return (variable, secret);
    }

    public static void Set(string variable, string value) => Environment.SetEnvironmentVariable(variable, value);

    /// <summary>The configuration of the ordered credential list, replacing the factory's default entry at index 0.</summary>
    public static Dictionary<string, string?> Configuration(params (string Id, string Variable, string Roles)[] entries)
    {
        var settings = new Dictionary<string, string?>();
        for (var index = 0; index < entries.Length; index++)
        {
            var (id, variable, roles) = entries[index];
            settings[$"Authentication:ApiKeys:{index}:Id"] = id;
            settings[$"Authentication:ApiKeys:{index}:DisplayName"] = $"{id} display";
            settings[$"Authentication:ApiKeys:{index}:Secret:Provider"] = "environment";
            settings[$"Authentication:ApiKeys:{index}:Secret:Name"] = variable;
            settings[$"Authentication:ApiKeys:{index}:Roles"] = roles;
        }

        return settings;
    }

    public void Dispose()
    {
        foreach (var variable in _variables)
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}

/// <summary>HTTP helpers that speak exactly what the browser UI sends (ADR-0043 §9, §10, §14).</summary>
internal static class BrowserSession
{
    public const string CookieName = "__Host-bops_session";
    public const string UiOrigin = "http://localhost:4200";

    /// <summary><c>POST /api/session</c> as the UI sends it: JSON body, <c>X-bOps-Request: 1</c>, the configured <c>Origin</c>.</summary>
    public static Task<HttpResponseMessage> LoginAsync(
        HttpClient client, string apiKey, bool? keepSignedIn = null, string? cookie = null, string? origin = UiOrigin, bool csrfHeader = true)
    {
        var body = keepSignedIn is { } keep
            ? $$"""{"apiKey":{{System.Text.Json.JsonSerializer.Serialize(apiKey)}},"keepSignedIn":{{(keep ? "true" : "false")}}}"""
            : $$"""{"apiKey":{{System.Text.Json.JsonSerializer.Serialize(apiKey)}}}""";
        return SendAsync(client, HttpMethod.Post, "/api/session", cookie, csrf: csrfHeader, origin: origin, json: body);
    }

    /// <summary>Logs in and returns the new cookie value. Fails (without printing the key) if the login did not succeed.</summary>
    public static async Task<string> SignInAsync(HttpClient client, string apiKey, bool? keepSignedIn = null, string? cookie = null)
    {
        using var response = await LoginAsync(client, apiKey, keepSignedIn, cookie);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"login returned {(int)response.StatusCode}");
        return CookieValue(response) ?? throw new InvalidOperationException("The login response set no session cookie.");
    }

    /// <summary>Sends a request; <paramref name="csrf"/> adds <c>X-bOps-Request: 1</c>, <paramref name="origin"/> an <c>Origin</c>.</summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string uri,
        string? cookie,
        bool csrf = false,
        string? origin = null,
        string? json = null,
        IReadOnlyList<(string Name, string Value)>? headers = null,
        string? bearer = null,
        string? rawCookieHeader = null)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        if (rawCookieHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", rawCookieHeader);
        }
        else if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"{CookieName}={cookie}");
        }

        if (csrf)
        {
            request.Headers.TryAddWithoutValidation("X-bOps-Request", "1");
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (bearer is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", bearer);
        }

        foreach (var (name, value) in headers ?? [])
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await client.SendAsync(request);
    }

    /// <summary>A cookie-authenticated request that passes the CSRF gate (custom header and configured origin).</summary>
    public static Task<HttpResponseMessage> SendTrustedAsync(HttpClient client, HttpMethod method, string uri, string cookie, string? json = null) =>
        SendAsync(client, method, uri, cookie, csrf: true, origin: UiOrigin, json: json);

    public static Task<HttpResponseMessage> GetMeAsync(HttpClient client, string cookie) =>
        SendAsync(client, HttpMethod.Get, "/api/session/me", cookie);

    /// <summary>Every raw <c>Set-Cookie</c> line for the session cookie.</summary>
    public static IReadOnlyList<string> SessionSetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Where(value => value.StartsWith(CookieName + "=", StringComparison.Ordinal)).ToArray()
            : [];

    /// <summary>The value of a (non-deleting) session <c>Set-Cookie</c>, or <c>null</c>.</summary>
    public static string? CookieValue(HttpResponseMessage response)
    {
        foreach (var line in SessionSetCookies(response))
        {
            var value = line[(CookieName.Length + 1)..].Split(';')[0];
            if (value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Whether the response expires the session cookie (empty value, 1970 expiry, same path and attributes).</summary>
    public static bool DeletesCookie(HttpResponseMessage response) =>
        SessionSetCookies(response).Any(line =>
            line.StartsWith(CookieName + "=;", StringComparison.Ordinal)
            && line.Contains("expires=Thu, 01 Jan 1970 00:00:00 GMT", StringComparison.OrdinalIgnoreCase)
            && line.Contains("path=/", StringComparison.OrdinalIgnoreCase)
            && line.Contains("secure", StringComparison.OrdinalIgnoreCase)
            && line.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase)
            && line.Contains("httponly", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("domain", StringComparison.OrdinalIgnoreCase));

    public static string SessionsDb(TestAppFactory factory) => Path.Combine(factory.TempDirectory, "sessions.db");

    /// <summary>The number of rows in the session table (read directly, outside the API).</summary>
    public static long RowCount(TestAppFactory factory)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = SessionsDb(factory), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM browser_sessions;";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Every byte of every column of every row, plus the database files' raw bytes, for secret scans.</summary>
    public static byte[] DatabaseBytes(TestAppFactory factory)
    {
        SqliteConnection.ClearAllPools();
        using var buffer = new MemoryStream();
        foreach (var path in new[] { SessionsDb(factory), SessionsDb(factory) + "-wal" })
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.CopyTo(buffer);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Asserts <paramref name="haystack"/> does not contain <paramref name="secret"/> without echoing either.</summary>
    public static void AssertAbsent(string haystack, string secret, string where) =>
        Assert.False(haystack.Contains(secret, StringComparison.Ordinal), $"a secret was found in {where}");

    public static void AssertAbsent(byte[] haystack, string secret, string where)
    {
        var needle = Encoding.UTF8.GetBytes(secret);
        Assert.False(haystack.AsSpan().IndexOf(needle) >= 0, $"a secret was found in {where}");
    }
}
