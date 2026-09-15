using System.Net;
using System.Net.Http.Json;
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
