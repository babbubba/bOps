// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Api.Tests;

/// <summary>
/// V1.1-H release-gate checks that span more than one endpoint group: the API role matrix for the
/// plugin catalog and Settings, the catalog's read-only guarantee, and Settings persistence across
/// a genuine host restart. Roles are independent claims (no hierarchy), so every role is exercised
/// on its own rather than assuming a stronger role implies a weaker one.
/// </summary>
public sealed class V11ReleaseGateTests
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("viewer", "/api/plugins", HttpStatusCode.OK)]
    [InlineData("operator", "/api/plugins", HttpStatusCode.Forbidden)]
    [InlineData("approver", "/api/plugins", HttpStatusCode.Forbidden)]
    [InlineData("administrator", "/api/plugins", HttpStatusCode.Forbidden)]
    [InlineData("viewer", "/api/plugins/acme.absent", HttpStatusCode.NotFound)]
    [InlineData("operator", "/api/plugins/acme.absent", HttpStatusCode.Forbidden)]
    [InlineData("viewer", "/api/settings", HttpStatusCode.Forbidden)]
    [InlineData("operator", "/api/settings", HttpStatusCode.Forbidden)]
    [InlineData("approver", "/api/settings", HttpStatusCode.Forbidden)]
    [InlineData("administrator", "/api/settings", HttpStatusCode.OK)]
    public async Task ReadEndpoints_AreGatedByExactlyTheirDeclaredRole(string role, string path, HttpStatusCode expected)
    {
        using var factory = new TestAppFactory { Roles = [role] };
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/plugins")]
    [InlineData("/api/plugins/acme.absent")]
    [InlineData("/api/settings")]
    public async Task ReadEndpoints_RejectAnAnonymousCaller(string path)
    {
        using var factory = new TestAppFactory { Roles = ["viewer", "operator", "approver", "administrator"] };
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/plugins")]
    [InlineData("PUT", "/api/plugins/acme.absent")]
    [InlineData("PATCH", "/api/plugins/acme.absent")]
    [InlineData("DELETE", "/api/plugins/acme.absent")]
    [InlineData("POST", "/api/plugins/acme.absent/enable")]
    public async Task PluginCatalog_ExposesNoMutationPath_EvenToTheStrongestCaller(string method, string path)
    {
        using var factory = new TestAppFactory { Roles = ["viewer", "operator", "approver", "administrator"] };
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        var response = await client.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"{method} {path} returned {(int)response.StatusCode}; the catalog must stay read-only.");
    }

    [Fact]
    public async Task SystemEvidenceThenWebResearch_RunThroughThePolicyGatedPath_AndUnsafeDestinationsStayDenied()
    {
        using var factory = new TestAppFactory
        {
            ChatModel = new QueueChatModel(
                QueueChatModel.PlanResponse("collect system evidence, then research on the web"),
                QueueChatModel.ToolCall("system.info"),
                QueueChatModel.ToolCall("web.fetch", new JsonObject { ["url"] = "http://127.0.0.1:9/internal-admin" }),
                QueueChatModel.Final("evidence collected; the loopback destination was refused")),
        };
        using var client = factory.CreateClient();

        var tools = await client.GetFromJsonAsync<List<ToolManifest>>("/api/tools", ResponseJsonOptions);
        var names = tools!.Select(tool => tool.Name).ToList();
        Assert.Contains("system.info", names);
        Assert.Contains("web.fetch", names);
        Assert.DoesNotContain("web.search", names);
        Assert.DoesNotContain(names, name => name is "shell.run" or "system.exec" or "process.start");

        var accepted = await client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest("diagnose, then research"));
        var started = await accepted.Content.ReadFromJsonAsync<TaskAcceptedResponse>();
        var task = await PollUntilTerminalAsync(client, started!.TaskId);

        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        var systemStep = Assert.Single(task.Steps, step => step.ToolCall?.ToolName == "system.info");
        var webStep = Assert.Single(task.Steps, step => step.ToolCall?.ToolName == "web.fetch");
        Assert.DoesNotContain("denied", systemStep.Observation ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("denied by policy", webStep.Observation ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var toolEvents = (await File.ReadAllLinesAsync(Path.Combine(factory.TempDirectory, "audit.jsonl")))
            .Select(line => JsonDocument.Parse(line))
            .Select(document => JsonDocument.Parse(document.RootElement.GetProperty("EventJson").GetString()!).RootElement)
            .Where(element => element.GetProperty("eventType").GetString() == "toolCall")
            .ToDictionary(element => element.GetProperty("Tool").GetString()!, element => element.GetProperty("Outcome").GetInt32());
        Assert.Equal((int)ToolOutcome.Success, toolEvents["system.info"]);
        Assert.NotEqual((int)ToolOutcome.Success, toolEvents["web.fetch"]);
    }

    private static async Task<TaskState> PollUntilTerminalAsync(HttpClient client, Guid taskId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var response = await client.GetAsync(new Uri($"/api/agents/tasks/{taskId}", UriKind.Relative), timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                var task = await response.Content.ReadFromJsonAsync<TaskState>(ResponseJsonOptions, timeout.Token);
                if (task is not null && task.Status != AgentTaskStatus.Running)
                {
                    return task;
                }
            }

            // A 404 right after the 202 only means the first Running snapshot is not written yet.
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    [Fact]
    public async Task SettingsChanges_SurviveARealHostRestart_WithoutEverExposingTheKey()
    {
        const string apiKey = "sk-ant-restartpersistence0123456789";
        var stateDirectory = Directory.CreateTempSubdirectory("bops-api-restart-").FullName;
        var masterKey = $"restart-master-key-{Guid.NewGuid():N}";

        try
        {
            using (var first = new TestAppFactory
            {
                Roles = ["administrator"],
                TempDirectory = stateDirectory,
                KeepTempDirectory = true,
                VaultMasterKey = masterKey,
            })
            using (var client = first.CreateClient())
            {
                Assert.Equal(
                    HttpStatusCode.NoContent,
                    (await client.PutAsJsonAsync(
                        new Uri("/api/settings/providers/Anthropic/profile", UriKind.Relative),
                        new { baseUrl = "https://api.anthropic.com", model = "claude-sonnet-4-5", supportsNativeToolCalling = true, extraParameters = (Dictionary<string, string>?)null })).StatusCode);
                Assert.Equal(
                    HttpStatusCode.NoContent,
                    (await client.PutAsJsonAsync(
                        new Uri("/api/settings/providers/Anthropic/key", UriKind.Relative),
                        new { apiKey, expectedVersion = 0 })).StatusCode);
                Assert.Equal(
                    HttpStatusCode.NoContent,
                    (await client.PutAsJsonAsync(
                        new Uri("/api/settings/active-provider", UriKind.Relative),
                        new { providerId = "Anthropic" })).StatusCode);
            }

            using var second = new TestAppFactory
            {
                Roles = ["administrator"],
                TempDirectory = stateDirectory,
                KeepTempDirectory = true,
                VaultMasterKey = masterKey,
            };
            using var restarted = second.CreateClient();

            var raw = await restarted.GetStringAsync(new Uri("/api/settings", UriKind.Relative));
            Assert.DoesNotContain(apiKey, raw, StringComparison.Ordinal);

            var view = JsonSerializer.Deserialize<SettingsView>(raw, ResponseJsonOptions)!;
            Assert.Equal(1, view.VaultVersion);
            Assert.Equal("Anthropic", view.ActiveProviderId);
            Assert.Equal("Settings", view.ActiveProviderSource);
            var anthropic = view.Providers.Single(provider => provider.ProviderId == "Anthropic");
            Assert.True(anthropic.IsActive);
            Assert.True(anthropic.HasStoredKey);
            Assert.Equal("sk-ant", anthropic.KeyMaskPrefix);
            Assert.Equal("6789", anthropic.KeyMaskSuffix);
            Assert.Equal("https://api.anthropic.com", anthropic.BaseUrl);
            Assert.Equal("claude-sonnet-4-5", anthropic.Model);

            Assert.DoesNotContain(
                apiKey,
                await File.ReadAllTextAsync(Path.Combine(stateDirectory, "vault.dat")),
                StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only, same as TestAppFactory.
            }
        }
    }
}
