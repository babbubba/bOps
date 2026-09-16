// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using bOps.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace bOps.Api.Tests;

/// <summary>
/// Drives <c>bOps.Api</c>'s HTTP surface end to end (ADR-0018) against a deterministic
/// <see cref="QueueChatModel"/> — the composition under test is the real one
/// (<see cref="TestAppFactory"/> boots the actual <c>Program</c>), only the model and, where
/// needed, the policy engine are substituted.
/// </summary>
public sealed class AgentsAndApprovalsEndpointsTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Api_RejectsAnonymousRequests()
    {
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel() };
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(new Uri("/api/tools", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_CannotStartTasks()
    {
        using var factory = new TestAppFactory
        {
            ChatModel = new QueueChatModel(),
            Roles = ["viewer"],
        };
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest("forbidden mutation"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StartTask_Returns400_WhenGoalIsMissing()
    {
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel() };
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StartTask_RunsToCompletion_AndIsObservableThroughGetAndPolling()
    {
        using var factory = new TestAppFactory
        {
            ChatModel = new QueueChatModel(QueueChatModel.PlanResponse(), QueueChatModel.Final("all done")),
        };
        using var client = factory.CreateClient();

        var accepted = await client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest("do the thing"));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var started = await accepted.Content.ReadFromJsonAsync<TaskAcceptedResponse>();

        var finalTask = await PollUntilTerminalAsync(client, started!.TaskId);

        Assert.Equal(AgentTaskStatus.Completed, finalTask.Status);
        Assert.Contains(finalTask.Steps, s => s.Observation == "all done");
    }

    [Fact]
    public async Task StartTask_ReusesTheTaskId_ForTheSameActorAndIdempotencyKey()
    {
        using var factory = new TestAppFactory
        {
            ChatModel = new QueueChatModel(QueueChatModel.PlanResponse(), QueueChatModel.Final("done")),
        };
        using var client = factory.CreateClient();

        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/api/agents/tasks")
        {
            Content = JsonContent.Create(new StartTaskRequest("do this once")),
        };
        firstRequest.Headers.Add("Idempotency-Key", "one-logical-start");
        using var secondRequest = new HttpRequestMessage(HttpMethod.Post, "/api/agents/tasks")
        {
            Content = JsonContent.Create(new StartTaskRequest("do this once")),
        };
        secondRequest.Headers.Add("Idempotency-Key", "one-logical-start");

        var first = await client.SendAsync(firstRequest);
        var second = await client.SendAsync(secondRequest);
        var firstBody = await first.Content.ReadFromJsonAsync<TaskAcceptedResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<TaskAcceptedResponse>();

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(firstBody!.TaskId, secondBody!.TaskId);
    }

    [Fact]
    public async Task CancelTask_StopsTheRun_AndPersistsCancelledState()
    {
        using var factory = new TestAppFactory { ChatModel = new BlockingChatModel() };
        using var client = factory.CreateClient();

        var accepted = await client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest("wait forever"));
        var started = await accepted.Content.ReadFromJsonAsync<TaskAcceptedResponse>();
        var cancelled = await client.DeleteAsync(new Uri($"/api/agents/tasks/{started!.TaskId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
        var final = await PollUntilTerminalAsync(client, started.TaskId);
        Assert.Equal(AgentTaskStatus.Cancelled, final.Status);
    }

    [Fact]
    public async Task StartTask_RejectsWorkBeyondTheConfiguredConcurrencyLimit()
    {
        using var factory = new TestAppFactory { ChatModel = new BlockingChatModel() };
        using var client = factory.CreateClient();

        var first = await client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest("first"));
        var second = await client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest("second"));
        var third = await client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest("third"));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, third.StatusCode);

        var firstTask = await first.Content.ReadFromJsonAsync<TaskAcceptedResponse>();
        var secondTask = await second.Content.ReadFromJsonAsync<TaskAcceptedResponse>();
        await client.DeleteAsync(new Uri($"/api/agents/tasks/{firstTask!.TaskId}", UriKind.Relative));
        await client.DeleteAsync(new Uri($"/api/agents/tasks/{secondTask!.TaskId}", UriKind.Relative));
    }

    [Fact]
    public async Task TaskEvents_ToleratesOpeningTheStream_BeforeTheBackgroundRunHasSavedAnything()
    {
        // Regression test for a real race this session's own manual UI testing caught: POST
        // returns 202 (with the task id) the instant it starts the detached background run
        // (ADR-0018) — well before that run has saved its first Running snapshot. A client that
        // immediately opens GET .../events for that same id (exactly what the Angular dashboard
        // does) can and did observe "task not found" on the very first read, permanently, because
        // the endpoint originally gave up after a single null ITaskStore.LoadAsync. This opens the
        // stream with NO pre-poll at all, unlike every other test here.
        using var factory = new TestAppFactory
        {
            ChatModel = new QueueChatModel(QueueChatModel.PlanResponse(), QueueChatModel.Final("all done")),
        };
        using var client = factory.CreateClient();

        var accepted = await client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest("do the thing"));
        var started = await accepted.Content.ReadFromJsonAsync<TaskAcceptedResponse>();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/agents/tasks/{started!.TaskId}/events", UriKind.Relative));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        string? eventName = null;
        string? dataLine = null;
        while (!timeoutCts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(timeoutCts.Token);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                dataLine = line["data: ".Length..];
                break;
            }
        }

        Assert.Equal("snapshot", eventName);
        Assert.NotNull(dataLine);
        using var json = JsonDocument.Parse(dataLine);
        Assert.Equal(started.TaskId, json.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task TaskEvents_StreamsCamelCaseJsonSnapshots_LikeEveryOtherEndpoint()
    {
        // Regression test: a bare JsonSerializer.Serialize(task) in the SSE write loop used the
        // BCL's PascalCase default, silently disagreeing with Results.Ok(...)'s ASP.NET Core Web
        // defaults (camelCase) used by every other endpoint here — caught by hand-testing the
        // real Angular UI against this host, not by any earlier automated test, since none of the
        // earlier tests read the SSE stream's raw bytes, only the plain GET/POST JSON responses.
        using var factory = new TestAppFactory
        {
            ChatModel = new QueueChatModel(QueueChatModel.PlanResponse(), QueueChatModel.Final("all done")),
        };
        using var client = factory.CreateClient();

        var accepted = await client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest("do the thing"));
        var started = await accepted.Content.ReadFromJsonAsync<TaskAcceptedResponse>();

        // Wait for the task to actually exist in the store before opening the stream — otherwise
        // this can race the detached background run and observe the "task not found" SSE event
        // instead of a real snapshot (unrelated to what this test is regression-guarding).
        await PollUntilAsync(() => client.GetFromJsonAsync<TaskState>($"/api/agents/tasks/{started!.TaskId}"), t => t is not null);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/agents/tasks/{started!.TaskId}/events", UriKind.Relative));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        string? dataLine = null;
        while (!timeoutCts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(timeoutCts.Token);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                dataLine = line["data: ".Length..];
                break;
            }
        }

        Assert.NotNull(dataLine);
        using var json = JsonDocument.Parse(dataLine);
        Assert.True(json.RootElement.TryGetProperty("id", out _), "expected camelCase 'id'");
        Assert.True(json.RootElement.TryGetProperty("goal", out _), "expected camelCase 'goal'");
        Assert.False(json.RootElement.TryGetProperty("Id", out _), "PascalCase 'Id' would mean the regression is back");
    }

    [Fact]
    public async Task GetTask_Returns404_ForAnUnknownId()
    {
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel() };
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri($"/api/agents/tasks/{Guid.NewGuid()}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResumeTask_Returns404_ForAnUnknownId()
    {
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel() };
        using var client = factory.CreateClient();

        var response = await client.PostAsync(new Uri($"/api/agents/tasks/{Guid.NewGuid()}/resume", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResumeTask_ContinuesAStoredRunningTask_ToCompletion()
    {
        // A model that can finish a resumed task in one more step — the resume endpoint's job is
        // to hand a stored TaskState to AgentRunner.ResumeAsync (V0.7) and let it continue, not to
        // plan again, so no planning response is queued here.
        using var factory = new TestAppFactory
        {
            ChatModel = new QueueChatModel(QueueChatModel.Final("resumed and done")),
        };
        using var client = factory.CreateClient();

        var taskStore = factory.Services.GetRequiredService<ITaskStore>();
        var partial = new TaskState(
            Guid.NewGuid(), NodeId.Local, "check things", AgentTaskStatus.Running,
            [new PlanStep(0, "system.cpu", new ModelToolCall("call-0", "system.cpu", ToolArguments.Empty),
                ToolCallResult.Success("42%"), "42%", 0)],
            [new AgentPlan(0, "test plan", [])],
            DateTimeOffset.UtcNow);
        await taskStore.SaveAsync(partial);

        var resumed = await client.PostAsync(new Uri($"/api/agents/tasks/{partial.Id}/resume", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);

        var finalTask = await PollUntilTerminalAsync(client, partial.Id);
        Assert.Equal(AgentTaskStatus.Completed, finalTask.Status);
        Assert.Equal(2, finalTask.Steps.Count);
    }

    [Fact]
    public async Task ApprovalFlow_BlocksThenExecutes_WhenAnOperatorRespondsThroughARequest()
    {
        using var factory = new TestAppFactory
        {
            ChatModel = new QueueChatModel(
                QueueChatModel.PlanResponse(),
                QueueChatModel.ToolCall("system.cpu"),
                QueueChatModel.Final("checked cpu")),
            PolicyEngine = new FixedPolicyEngine(PolicyMode.Approval),
        };
        using var client = factory.CreateClient();

        var accepted = await client.PostAsJsonAsync("/api/agents/tasks", new StartTaskRequest("check cpu"));
        var started = await accepted.Content.ReadFromJsonAsync<TaskAcceptedResponse>();

        var pending = await PollUntilAsync(
            () => client.GetFromJsonAsync<List<PendingApproval>>("/api/approvals/pending"),
            list => list is { Count: > 0 });
        var approval = Assert.Single(pending!);
        Assert.Equal("system.cpu", approval.Tool);
        Assert.Equal(started!.TaskId, approval.TaskId);

        var respond = await client.PostAsJsonAsync(
            $"/api/approvals/{approval.Id}/respond", new RespondToApprovalRequest(true, "looks fine"));
        Assert.Equal(HttpStatusCode.NoContent, respond.StatusCode);

        var finalTask = await PollUntilTerminalAsync(client, started.TaskId);
        Assert.Equal(AgentTaskStatus.Completed, finalTask.Status);

        var stillPending = await client.GetFromJsonAsync<List<PendingApproval>>("/api/approvals/pending");
        Assert.Empty(stillPending!);

        var audit = await File.ReadAllTextAsync(Path.Combine(factory.TempDirectory, "audit.jsonl"));
        Assert.Contains("test-user", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("anonymous", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RespondToApproval_Returns404_ForAnUnknownApprovalId()
    {
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel() };
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/approvals/does-not-exist/respond", new RespondToApprovalRequest(true, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetTools_ReturnsTheRegisteredManifests()
    {
        using var factory = new TestAppFactory { ChatModel = new QueueChatModel() };
        using var client = factory.CreateClient();

        var manifests = await client.GetFromJsonAsync<List<ToolManifest>>("/api/tools");

        Assert.NotNull(manifests);
        Assert.NotEmpty(manifests);
        Assert.Contains(manifests!, m => m.Name == "system.cpu");
    }

    private static async Task<TaskState> PollUntilTerminalAsync(HttpClient client, Guid taskId)
    {
        var task = await PollUntilAsync(
            () => client.GetFromJsonAsync<TaskState>($"/api/agents/tasks/{taskId}"),
            t => t is not null && t.Status != AgentTaskStatus.Running);
        return task!;
    }

    private static async Task<T?> PollUntilAsync<T>(Func<Task<T?>> fetch, Func<T?, bool> isDone)
    {
        using var timeoutCts = new CancellationTokenSource(PollTimeout);
        while (true)
        {
            T? value;
            try
            {
                // Immediately after POST returns 202, the background task may not have written
                // its first Running snapshot yet — a 404 here is "not started observing yet," not
                // a real failure, so it is treated the same as "not done" rather than aborting
                // the poll (agentic/04-testing-rules.md tests real timing, not a synchronous fake).
                value = await fetch();
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
            {
                value = default;
            }

            if (isDone(value))
            {
                return value;
            }

            timeoutCts.Token.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeoutCts.Token);
        }
    }
}

internal sealed class BlockingChatModel : IChatModel
{
    public ChatModelDescriptor Descriptor { get; } = new("test", "blocking");

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        throw new InvalidOperationException("The blocking test model should only complete through cancellation.");
    }
}
