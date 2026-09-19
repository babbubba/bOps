// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text.Json;
using bOps.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

/// <summary>
/// The task store keeps the exact request and reply of every model call for troubleshooting; the API tells a client which
/// model answered, how long it took and the tokens, and never sends the bodies (a request repeats the whole conversation,
/// and the events stream re-sends the whole task at every step).
/// </summary>
public sealed class TaskModelCallsEndpointsTests
{
    private const string RequestBody = """{"model":"test-model","messages":[{"role":"user","content":"a very long conversation"}]}""";
    private const string ReplyBody = """{"model":"vendor/picked-model","choices":[]}""";

    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(15);

    private static ModelResponse Answer(string text) =>
        new(text, [], true, new ModelUsage(120, 45, null))
        {
            Details = new ModelCallDetails("vendor/picked-model", "stop", RequestBody, ReplyBody),
        };

    private static async Task<Guid> RunATaskAsync(HttpClient client)
    {
        var accepted = await client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest("do the thing"));
        var started = await accepted.Content.ReadFromJsonAsync<TaskAcceptedResponse>();
        using var timeout = new CancellationTokenSource(PollTimeout);
        while (true)
        {
            using var response = await client.GetAsync(new Uri($"/api/agents/tasks/{started!.TaskId}", UriKind.Relative), timeout.Token);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (json.RootElement.GetProperty("status").ValueKind == JsonValueKind.Number && json.RootElement.GetProperty("status").GetInt32() != (int)AgentTaskStatus.Running)
            {
                return started.TaskId;
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    private static void AssertModelCallWithoutBodies(JsonElement call)
    {
        Assert.Equal("test-model", call.GetProperty("requestedModel").GetString());
        Assert.Equal("vendor/picked-model", call.GetProperty("actualModel").GetString());
        Assert.Equal(120, call.GetProperty("usage").GetProperty("promptTokens").GetInt32());
        Assert.Equal(45, call.GetProperty("usage").GetProperty("completionTokens").GetInt32());
        Assert.True(call.GetProperty("durationMs").GetInt64() >= 0);
        Assert.Equal("stop", call.GetProperty("finishReason").GetString());
        Assert.Equal(JsonValueKind.Null, call.GetProperty("requestJson").ValueKind);
        Assert.Equal(JsonValueKind.Null, call.GetProperty("responseJson").ValueKind);
    }

    [Fact]
    public async Task GetTask_SendsTheModelTheTimeAndTheTokens_ButNotTheBodies_WhileTheStoreKeepsThem()
    {
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel(QueueChatModel.PlanResponse(), Answer("all done")) };
        using var client = factory.CreateClient();

        var taskId = await RunATaskAsync(client);

        var body = await client.GetStringAsync(new Uri($"/api/agents/tasks/{taskId}", UriKind.Relative));
        using var json = JsonDocument.Parse(body);
        AssertModelCallWithoutBodies(json.RootElement.GetProperty("steps")[0].GetProperty("modelCalls")[0]);
        Assert.DoesNotContain("a very long conversation", body, StringComparison.Ordinal);

        var stored = await factory.Services.GetRequiredService<ITaskStore>().LoadAsync(taskId);
        var kept = Assert.Single(stored!.Steps[0].ModelCalls!);
        Assert.Equal(RequestBody, kept.RequestJson);
        Assert.Equal(ReplyBody, kept.ResponseJson);
    }

    [Fact]
    public async Task ListTasks_SendsNoBodies()
    {
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel(QueueChatModel.PlanResponse(), Answer("all done")) };
        using var client = factory.CreateClient();
        await RunATaskAsync(client);

        var body = await client.GetStringAsync(new Uri("/api/agents/tasks?status=Completed", UriKind.Relative));

        using var json = JsonDocument.Parse(body);
        AssertModelCallWithoutBodies(json.RootElement[0].GetProperty("steps")[0].GetProperty("modelCalls")[0]);
        Assert.DoesNotContain("a very long conversation", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskEvents_SendNoBodies()
    {
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel(QueueChatModel.PlanResponse(), Answer("all done")) };
        using var client = factory.CreateClient();
        var taskId = await RunATaskAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/agents/tasks/{taskId}/events", UriKind.Relative));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        using var timeout = new CancellationTokenSource(PollTimeout);

        string? data = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line["data: ".Length..];
                break;
            }
        }

        Assert.NotNull(data);
        using var json = JsonDocument.Parse(data);
        AssertModelCallWithoutBodies(json.RootElement.GetProperty("steps")[0].GetProperty("modelCalls")[0]);
        Assert.DoesNotContain("a very long conversation", data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTask_SendsThePlanCallsToo_WithoutBodies()
    {
        var plan = QueueChatModel.PlanResponse() with { Usage = new ModelUsage(30, 70, null), Details = new ModelCallDetails("vendor/plan-model", "stop", RequestBody, ReplyBody) };
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel(plan, Answer("all done")) };
        using var client = factory.CreateClient();
        var taskId = await RunATaskAsync(client);

        var body = await client.GetStringAsync(new Uri($"/api/agents/tasks/{taskId}", UriKind.Relative));

        using var json = JsonDocument.Parse(body);
        var call = json.RootElement.GetProperty("plans")[0].GetProperty("modelCalls")[0];
        Assert.Equal("vendor/plan-model", call.GetProperty("actualModel").GetString());
        Assert.Equal(70, call.GetProperty("usage").GetProperty("completionTokens").GetInt32());
        Assert.Equal(JsonValueKind.Null, call.GetProperty("requestJson").ValueKind);
    }
}
