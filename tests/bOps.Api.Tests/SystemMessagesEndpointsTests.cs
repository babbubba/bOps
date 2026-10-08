// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

/// <summary>
/// Drives <c>GET /api/system-messages</c> and <c>GET /api/prerequisites</c> (ADR-0049) against the real composition root: filters
/// combine with AND, ordering and keyset pagination are the store's, every invalid input is a 400, and the readiness projection
/// answers "what is unavailable right now".
/// </summary>
public sealed class SystemMessagesEndpointsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    // The docker daemon cannot be reached on port 1 whatever the machine has installed, so the boot state is deterministic.
    private static readonly Dictionary<string, string?> NoDocker = new() { ["Docker:Endpoint"] = "tcp://127.0.0.1:1" };

    [Fact]
    public async Task Anonymous_IsRefused_AndAViewerMaySeeBothEndpoints()
    {
        using var factory = new TestAppFactory { ExtraConfiguration = NoDocker, Roles = ["viewer"] };
        using var anonymous = factory.CreateAnonymousClient();
        using var viewer = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Relative("/api/system-messages"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Relative("/api/prerequisites"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Relative("/api/system-messages"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Relative("/api/prerequisites"))).StatusCode);
    }

    [Fact]
    public async Task TheFiltersCombineWithAnd()
    {
        using var factory = new TestAppFactory { ExtraConfiguration = NoDocker };
        using var client = factory.CreateClient();
        await SeedAsync(factory,
            Message(0, SystemMessageSeverity.Warning, "Docker daemon is unavailable."),
            Message(1, SystemMessageSeverity.Information, "Docker daemon is available again."),
            Message(2, SystemMessageSeverity.Warning, "SearXNG endpoint is unavailable."),
            Message(3, SystemMessageSeverity.Warning, "Docker build contexts are unavailable."));

        // Severity Warning AND text "docker" AND the window 12:00:30–12:03:30: the 12:00 one is before the window, the 12:01
        // Information has the wrong severity, the 12:02 SearXNG one the wrong text; only the 12:03 build-contexts warning satisfies all.
        var all = await Query(client, "severity=Warning&contains=docker&fromUtc=2026-10-01T12:00:30Z&toUtc=2026-10-01T12:03:30Z");

        Assert.Equal(["Docker build contexts are unavailable."], all.Items.Where(i => i.Source == "test/seed").Select(i => i.Message));
        Assert.DoesNotContain(all.Items, i => i.Severity != "Warning");
        Assert.DoesNotContain(all.Items, i => !i.Message.Contains("docker", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all.Items, i => i.TimestampUtc < T0.AddSeconds(30) || i.TimestampUtc > T0.AddSeconds(210));

        var windowed = await Query(client, "severity=warning&contains=DOCKER&fromUtc=2026-10-01T12:00:00Z&toUtc=2026-10-01T12:03:00Z&pageSize=200");
        var seeded = windowed.Items.Where(i => i.Source == "test/seed").Select(i => i.Message).ToArray();
        Assert.Equal(["Docker build contexts are unavailable.", "Docker daemon is unavailable."], seeded);
    }

    [Fact]
    public async Task Pagination_IsNewestFirst_ByOpaqueKeysetCursor_WithoutDuplicates()
    {
        using var factory = new TestAppFactory { ExtraConfiguration = NoDocker };
        using var client = factory.CreateClient();
        await SeedAsync(factory, Enumerable.Range(0, 5).Select(i => Message(i, SystemMessageSeverity.Information, $"seed message {i}")).ToArray());

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await Query(client, $"contains=seed%20message&pageSize=2{(cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))}");
            seen.AddRange(page.Items.Select(i => i.Message));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(["seed message 4", "seed message 3", "seed message 2", "seed message 1", "seed message 0"], seen);
    }

    [Fact]
    public async Task ThePageSize_DefaultsTo50_AndMaxesAt200()
    {
        using var factory = new TestAppFactory { ExtraConfiguration = NoDocker };
        using var client = factory.CreateClient();
        await SeedAsync(factory, Enumerable.Range(0, 205).Select(i => Message(i, SystemMessageSeverity.Information, $"bulk {i}")).ToArray());

        Assert.Equal(50, (await Query(client, "contains=bulk")).Items.Count);
        Assert.Equal(200, (await Query(client, "contains=bulk&pageSize=200")).Items.Count);
    }

    [Theory]
    [InlineData("fromUtc=not-a-date")]
    [InlineData("toUtc=2026-13-45")]
    [InlineData("fromUtc=2026-10-02T00:00:00Z&toUtc=2026-10-01T00:00:00Z")]
    [InlineData("severity=Debug")]
    [InlineData("severity=7")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=201")]
    [InlineData("pageSize=-1")]
    [InlineData("pageSize=abc")]
    [InlineData("cursor=not-a-cursor")]
    [InlineData("cursor=djE6OTk5OTk5OTk5OTk5OTk5OTk5OTk5OTk6eA")]
    public async Task InvalidInput_Is400_WithoutEchoingTheValue(string query)
    {
        using var factory = new TestAppFactory { ExtraConfiguration = NoDocker };
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Relative($"/api/system-messages?{query}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("not-a-date", body, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-cursor", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReadinessEndpoint_ExplainsWhatIsUnavailable_AndWhatItAffects_AndTheBootWarningsAreActionable()
    {
        using var factory = new TestAppFactory { ExtraConfiguration = NoDocker };
        using var client = factory.CreateClient();

        var readiness = JsonNode.Parse(await client.GetStringAsync(Relative("/api/prerequisites")))!;

        var docker = readiness["prerequisites"]!.AsArray().Single(p => p!["id"]!.GetValue<string>() == "docker")!;
        Assert.Equal("Unavailable", docker["state"]!.GetValue<string>());
        Assert.Equal("Docker daemon", docker["displayName"]!.GetValue<string>());
        Assert.Equal("Service", docker["kind"]!.GetValue<string>());
        Assert.Contains("Start the Docker daemon", docker["remediation"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("bops.packages.docker", docker["package"]!.GetValue<string>());
        var requiredBy = docker["requiredBy"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()).ToArray();
        Assert.Contains("docker.containers", requiredBy);
        Assert.All(docker["requiredBy"]!.AsArray(), r => Assert.Equal("tool", r!["type"]!.GetValue<string>()));
        Assert.Contains(
            readiness["components"]!.AsArray(),
            c => c!["id"]!.GetValue<string>() == "docker.containers" && !c["available"]!.GetValue<bool>() && c["registered"]!.GetValue<bool>());

        // The boot refresh wrote one Warning per missing required prerequisite, carrying the remediation.
        var messages = await Query(client, "severity=Warning&contains=docker%20daemon");
        var warning = Assert.Single(messages.Items, m => m.Source == "prerequisite/docker");
        Assert.Equal("prerequisite.missing", warning.Code);
        Assert.StartsWith("Docker daemon is unavailable. Start the Docker daemon", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReadinessEndpoint_DoesNotExposeChecksOrExceptionText()
    {
        using var factory = new TestAppFactory { ExtraConfiguration = NoDocker };
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync(Relative("/api/prerequisites"));

        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("BooleanPrerequisiteCheck", body, StringComparison.Ordinal);
    }

    private static async Task SeedAsync(TestAppFactory factory, params SystemMessage[] messages)
    {
        _ = factory.CreateAnonymousClient(); // builds the host
        var store = factory.Services.GetRequiredService<ISystemMessageStore>();
        foreach (var message in messages)
        {
            await store.AppendAsync(message);
        }
    }

    private static SystemMessage Message(int minute, SystemMessageSeverity severity, string text) => new()
    {
        Id = Guid.CreateVersion7(T0.AddMinutes(minute)),
        TimestampUtc = T0.AddMinutes(minute),
        Node = NodeId.Local,
        Source = "test/seed",
        Severity = severity,
        Code = "test.seeded",
        Message = text,
    };

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private static async Task<PageDto> Query(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<PageDto>(Relative($"/api/system-messages?{query}"), new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

    private readonly record struct PageDto(IReadOnlyList<ItemDto> Items, string? NextCursor);

    private readonly record struct ItemDto(string Source, string Severity, string Code, string Message, DateTimeOffset TimestampUtc);
}
