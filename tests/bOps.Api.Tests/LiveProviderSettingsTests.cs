// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using bOps.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

public sealed class LiveProviderSettingsTests
{
    [Fact]
    public async Task SettingsSwitch_PinsEachNewTaskInTheSameApiProcess()
    {
        using var factory = new TestAppFactory
        {
            Roles = ["viewer", "operator", "approver", "administrator"],
            ChatModel = new QueueChatModel(
                QueueChatModel.PlanResponse(), QueueChatModel.Final("first"),
                QueueChatModel.PlanResponse(), QueueChatModel.Final("second")),
        };
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ITaskStore>();

        var first = await StartAndWait(client, store);
        var firstPin = Assert.IsType<PinnedProviderConfiguration>(first.PinnedProviderConfiguration);
        Assert.Equal("OpenRouter", firstPin.ProviderId);
        Assert.Equal("openrouter/free", firstPin.Model);
        var firstCall = Assert.Single(first.Plans.SelectMany(plan => plan.ModelCalls ?? []));
        Assert.Equal(firstPin.Generation, firstCall.ConfigurationGeneration);
        Assert.Equal(firstPin.SnapshotHash, firstCall.ConfigurationSnapshotHash);

        var profile = await client.PutAsJsonAsync("/api/settings/providers/Anthropic/profile",
            new { baseUrl = "https://api.anthropic.test", model = "sonnet-x",
                supportsNativeToolCalling = false, extraParameters = (Dictionary<string, string>?)null });
        Assert.Equal(HttpStatusCode.NoContent, profile.StatusCode);
        var key = await client.PutAsJsonAsync("/api/settings/providers/Anthropic/key",
            new { apiKey = "ANTHROPIC_KEY", expectedVersion = 0 });
        Assert.Equal(HttpStatusCode.NoContent, key.StatusCode);
        var selected = await client.PutAsJsonAsync("/api/settings/active-provider", new { providerId = "anthropic" });
        Assert.Equal(HttpStatusCode.NoContent, selected.StatusCode);
        Assert.Equal("published", selected.Headers.GetValues("X-bOps-Settings-Effect").Single());
        var providers = await client.GetFromJsonAsync<ProvidersResponse>("/api/providers");
        Assert.Equal("Anthropic", providers!.Active!.Provider);
        Assert.Equal("sonnet-x", providers.Active.Model);

        var second = await StartAndWait(client, store);
        var secondPin = Assert.IsType<PinnedProviderConfiguration>(second.PinnedProviderConfiguration);
        Assert.Equal("Anthropic", secondPin.ProviderId);
        Assert.Equal("sonnet-x", secondPin.Model);
        Assert.Equal("https://api.anthropic.test", secondPin.BaseUrl);
        Assert.False(secondPin.SupportsNativeToolCalling);
        Assert.Equal(firstPin, (await store.LoadAsync(first.Id))!.PinnedProviderConfiguration);
        Assert.True(secondPin.Generation > firstPin.Generation);
        var secondCall = Assert.Single(second.Plans.SelectMany(plan => plan.ModelCalls ?? []));
        Assert.Equal(secondPin.Generation, secondCall.ConfigurationGeneration);
        Assert.Equal(secondPin.SnapshotHash, secondCall.ConfigurationSnapshotHash);
        var audit = await File.ReadAllTextAsync(Path.Combine(factory.TempDirectory, "audit.jsonl"));
        Assert.Contains(secondPin.SnapshotHash, audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyTaskResume_CapturesOnePinAndSurfacesMigration()
    {
        using var factory = new TestAppFactory
        {
            Roles = ["viewer", "operator", "approver", "administrator"],
            ChatModel = new QueueChatModel(QueueChatModel.Final("resumed")),
        };
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ITaskStore>();
        var legacy = new TaskState(Guid.NewGuid(), NodeId.Local, "resume legacy", AgentTaskStatus.Cancelled,
            [], [new AgentPlan(0, "test", [])], DateTimeOffset.UtcNow)
        { Origin = TaskOrigin.Ordinary };
        await store.SaveAsync(legacy);

        var response = await client.PostAsync(new Uri($"/api/agents/tasks/{legacy.Id}/resume", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TaskResumeAcceptedResponse>();
        Assert.True(body!.LegacyConfigurationMigrated);
        var completed = await WaitTerminal(store, legacy.Id);
        Assert.NotNull(completed.PinnedProviderConfiguration);
        var audit = await File.ReadAllTextAsync(Path.Combine(factory.TempDirectory, "audit.jsonl"));
        Assert.Contains("legacyConfigurationMigrated", audit, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<TaskState> StartAndWait(HttpClient client, ITaskStore store)
    {
        var response = await client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest("test"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TaskAcceptedResponse>();
        return await WaitTerminal(store, body!.TaskId);
    }

    private static async Task<TaskState> WaitTerminal(ITaskStore store, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var task = await store.LoadAsync(id, timeout.Token);
            if (task is { Status: not AgentTaskStatus.Running }) return task;
            await Task.Delay(20, timeout.Token);
        }
    }
}
