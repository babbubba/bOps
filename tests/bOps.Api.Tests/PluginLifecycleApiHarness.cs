// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using bOps.Abstractions;
using bOps.PluginHost;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

/// <summary>
/// One real API host (the production composition root) plus a real signed-plugin fixture and an administrator HTTP client.
/// All evidence is taken from the backend and the filesystem — lifecycle status, the store bytes, the plugins tree, and a marker
/// file the emitted plugin appends to when (and only when) its constructor runs — never from an HTTP status alone.
/// </summary>
internal sealed class PluginLifecycleApiHarness : IDisposable
{
    internal static readonly string[] AdministratorRoles = ["viewer", "operator", "approver", "administrator"];
    internal static readonly string[] NonAdministratorRoles = ["viewer", "operator", "approver"];
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PluginArchiveFixture _fixture = new();
    private readonly List<HttpClient> _clients = [];

    internal PluginLifecycleApiHarness(
        IReadOnlyList<string>? roles = null,
        IReadOnlyDictionary<string, string?>? configuration = null,
        bool trusted = true,
        Action<IServiceCollection>? configureServices = null)
    {
        Factory = new TestAppFactory { Roles = roles ?? AdministratorRoles, ExtraConfiguration = configuration, ConfigureExtraServices = configureServices };
        _fixture.WriteTrust(Path.Combine(Factory.TempDirectory, "publisher-trust.json"), trusted: trusted);
        Client = Track(Factory.CreateClient());
    }

    internal TestAppFactory Factory { get; }

    /// <summary>The authenticated client (administrator when the harness was built with administrator roles).</summary>
    internal HttpClient Client { get; }

    internal PluginLifecycleService Lifecycle => Factory.Services.GetRequiredService<PluginLifecycleService>();

    internal PluginManager Manager => Factory.Services.GetRequiredService<PluginManager>();

    internal string MarkerPath => Path.Combine(Factory.TempDirectory, "plugin-activations.marker");

    internal string PluginsRoot => Path.Combine(Factory.TempDirectory, "plugins");

    /// <summary>How many times any emitted plugin's constructor ran (i.e. plugin code executed) in this host.</summary>
    internal int Executions => File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath).Length : 0;

    internal byte[] Archive(string version = "1.0.0", bool throwOnActivate = false, bool sign = true, string? notes = null, string id = PluginArchiveFixture.PluginId) =>
        _fixture.Build(version, MarkerPath, throwOnActivate, id, sign, notes);

    internal HttpClient Anonymous() => Track(Factory.CreateAnonymousClient());

    // ---- Requests --------------------------------------------------------------------------------------

    /// <summary>
    /// Sends one request and owns its lifetime (request, content and headers are disposed once the buffered response is back). Content
    /// is supplied as a factory so ownership passes to the request; <paramref name="headers"/> may repeat a name to send it twice.
    /// </summary>
    internal static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string uri,
        Func<HttpContent>? content = null,
        string? contentType = null,
        IReadOnlyList<(string Name, string Value)>? headers = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content?.Invoke() };
        if (contentType is not null && request.Content is not null)
        {
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        foreach (var (name, value) in headers ?? [])
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    internal static List<(string Name, string Value)> Preconditions(string? ifMatch, bool createOnly, string? key)
    {
        var headers = new List<(string, string)>();
        if (ifMatch is not null)
        {
            headers.Add(("If-Match", ifMatch));
        }

        if (createOnly)
        {
            headers.Add(("If-None-Match", "*"));
        }

        if (key is not null)
        {
            headers.Add(("Idempotency-Key", key));
        }

        return headers;
    }

    internal static Task<HttpResponseMessage> UploadNewAsync(HttpClient client, byte[] archive, string? key = null, bool createOnly = true, string? ifMatch = null, string contentType = "application/zip") =>
        SendAsync(client, HttpMethod.Post, "/api/plugins/archives", () => new ByteArrayContent(archive), contentType, Preconditions(ifMatch, createOnly, key));

    internal static Task<HttpResponseMessage> UploadContentAsync(
        HttpClient client, Func<HttpContent> content, string? key = null, CancellationToken cancellationToken = default) =>
        SendAsync(client, HttpMethod.Post, "/api/plugins/archives", content, "application/zip", Preconditions(null, true, key), cancellationToken);

    internal static Task<HttpResponseMessage> UploadForIdAsync(HttpClient client, string id, byte[] archive, string? ifMatch, bool createOnly = false, string? key = null) =>
        SendAsync(client, HttpMethod.Put, $"/api/plugins/{id}/archive", () => new ByteArrayContent(archive), "application/zip", Preconditions(ifMatch, createOnly, key));

    internal static Task<HttpResponseMessage> ActionAsync(HttpClient client, string id, string action, string? ifMatch, object? body = null, string? key = null) =>
        SendAsync(
            client,
            HttpMethod.Post,
            $"/api/plugins/{id}/{action}",
            body is null ? null : () => JsonContent.Create(body, options: Json),
            headers: Preconditions(ifMatch, false, key));

    internal static Task<HttpResponseMessage> EnableAsync(HttpClient client, string id, string? ifMatch, string? confirmedVersion, string? key = null) =>
        ActionAsync(client, id, "enable", ifMatch, new { confirmedVersion }, key);

    internal static Task<HttpResponseMessage> RecoverAsync(HttpClient client, string id, string? ifMatch, bool confirmed = true, string? key = null) =>
        ActionAsync(client, id, "recover", ifMatch, new { confirmed }, key);

    /// <summary>Installs through the real endpoint as the administrator and asserts it succeeded; returns the new ETag.</summary>
    internal async Task<string> InstallAsync(string version = "1.0.0", bool throwOnActivate = false)
    {
        using var response = await UploadNewAsync(Client, Archive(version, throwOnActivate));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    /// <summary>Installs through the backend directly (not HTTP), for tests whose subject is that another caller cannot change it.</summary>
    internal async Task<string> SeedInstalledAsync(string version = "1.0.0")
    {
        var context = new PluginLifecycleRequestContext(NodeId.Local, new ActorIdentity("api-user", "seed", null));
        var result = await Lifecycle.InstallArchiveAsync(context, new MemoryStream(Archive(version)));
        Assert.Equal(PluginLifecycleResultCategory.Succeeded, result.Category);
        return result.ETag;
    }

    internal static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    internal static async Task<string> CategoryAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("category").GetString()!;

    internal async Task<JsonElement> StatusAsync(string id = PluginArchiveFixture.PluginId)
    {
        using var response = await Client.GetAsync(new Uri($"/api/plugins/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }

    // ---- Evidence --------------------------------------------------------------------------------------

    /// <summary>
    /// The observable durable and process state: store bytes, plugins tree, plugin executions and activation. The lifecycle-owned work-area
    /// directories are scaffolding created on first use, not state, so only the files inside them count; the tests that care assert the
    /// work area is empty explicitly (see <see cref="WorkAreaEntries"/>).
    /// </summary>
    internal Snapshot TakeSnapshot() => new(
        File.Exists(Path.Combine(Factory.TempDirectory, "plugins.json"))
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Factory.TempDirectory, "plugins.json"))))
            : null,
        Directory.Exists(PluginsRoot)
            ? [.. Directory.GetFileSystemEntries(PluginsRoot, "*", SearchOption.AllDirectories)
                .Where(path => File.Exists(path) || !Path.GetRelativePath(PluginsRoot, path).StartsWith(".lifecycle", StringComparison.Ordinal))
                .Select(path => Path.GetRelativePath(PluginsRoot, path) + (File.Exists(path) ? ":" + new FileInfo(path).Length : string.Empty))
                .Order(StringComparer.Ordinal)]
            : [],
        Executions,
        Manager.List().Select(record => record.Id).Any(id => Manager.IsActivated(id)));

    /// <summary>The entries currently in one lifecycle work-area directory (<c>uploads</c>, <c>staging</c>, ...); none when it was never created.</summary>
    internal string[] WorkAreaEntries(string directory)
    {
        var path = Path.Combine(PluginsRoot, ".lifecycle", directory);
        return Directory.Exists(path) ? Directory.GetFileSystemEntries(path) : [];
    }

    internal string AuditText() =>
        File.Exists(Path.Combine(Factory.TempDirectory, "audit.jsonl"))
            ? File.ReadAllText(Path.Combine(Factory.TempDirectory, "audit.jsonl"), Encoding.UTF8)
            : string.Empty;

    private static readonly char[] NewLine = [(char)10];

    /// <summary>The audit events (the chained envelope's inner event JSON), oldest first.</summary>
    internal IReadOnlyList<JsonElement> AuditEvents() =>
    [
        .. AuditText().Split(NewLine, StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var envelope = JsonDocument.Parse(line);
            var inner = envelope.RootElement.EnumerateObject().First(property => property.Name.Equals("eventJson", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
            return JsonDocument.Parse(inner).RootElement.Clone();
        }),
    ];

    /// <summary>Reads a property of an audit event regardless of the casing the serializer used.</summary>
    internal static JsonElement Property(JsonElement element, string name) =>
        element.EnumerateObject().First(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Asserts a response body carries no path, stack, exception or install-layout detail.</summary>
    internal void AssertSanitized(string body)
    {
        Assert.DoesNotContain(Factory.TempDirectory, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".lifecycle", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publisher-trust", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plugins.json", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("activation-sentinel-failure", body, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN ", body, StringComparison.Ordinal);
    }

    /// <summary>The GC passes a failed activation's collectible context needs before its files can be moved (mirrors the backend's own tests).</summary>
    internal static void ReleasePluginContexts()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private HttpClient Track(HttpClient client)
    {
        _clients.Add(client);
        return client;
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        // A loaded plugin holds its assembly file open until its collectible context is unloaded: disable through the backend first.
        foreach (var record in Manager.List().Where(record => Manager.IsActivated(record.Id)).ToList())
        {
            var status = Lifecycle.GetStatus(record.Id)!;
            Lifecycle.DisableAsync(new PluginLifecycleRequestContext(NodeId.Local, new ActorIdentity("test", "cleanup", null)), record.Id, status.LifecycleVersion)
                .GetAwaiter().GetResult();
        }

        Factory.Dispose();
        _fixture.Dispose();
    }

    /// <summary>Asserts every observable durable and process fact is byte-for-byte what it was: no lifecycle mutation, no execution.</summary>
    internal void AssertUnchanged(Snapshot before)
    {
        var after = TakeSnapshot();
        Assert.Equal(before.StoreHash, after.StoreHash);
        Assert.Equal(before.Tree, after.Tree);
        Assert.Equal(before.Executions, after.Executions);
        Assert.Equal(before.AnyActivated, after.AnyActivated);
    }

    internal sealed record Snapshot(string? StoreHash, IReadOnlyList<string> Tree, int Executions, bool AnyActivated);
}
