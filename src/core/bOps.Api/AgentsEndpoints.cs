using System.Text;
using System.Text.Json;
using bOps.Abstractions;

namespace bOps.Api;

/// <summary>Maps <c>/api/agents/tasks</c> — starting, resuming, reading and streaming tasks (ADR-0018).</summary>
internal static class AgentsEndpoints
{
    /// <summary>How often <c>GET /api/agents/tasks/{id}/events</c> re-reads <see cref="ITaskStore"/> to check for a new step. A fixed MVP interval — ADR-0018 explicitly defers tuning this to a session with a real UI to measure against.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    internal static void MapAgentsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/agents/tasks");

        group.MapPost("/", (StartTaskRequest request, AgentTaskLauncher launcher) =>
        {
            if (string.IsNullOrWhiteSpace(request.Goal))
            {
                return Results.BadRequest(new { message = "'goal' is required." });
            }

            var taskId = launcher.Start(request.Goal, ApiActor());
            return Results.Accepted($"/api/agents/tasks/{taskId}", new TaskAcceptedResponse(taskId));
        });

        group.MapPost("/{id:guid}/resume", async (Guid id, ITaskStore store, AgentTaskLauncher launcher) =>
        {
            var existing = await store.LoadAsync(id);
            if (existing is null)
            {
                return Results.NotFound(new { message = $"No stored task with id '{id}'." });
            }

            launcher.Resume(existing, ApiActor());
            return Results.Accepted($"/api/agents/tasks/{id}", new TaskAcceptedResponse(id));
        });

        group.MapGet("/{id:guid}", async (Guid id, ITaskStore store) =>
        {
            var task = await store.LoadAsync(id);
            return task is null ? Results.NotFound(new { message = $"No stored task with id '{id}'." }) : Results.Ok(task);
        });

        group.MapGet("/", async (string? status, ITaskStore store) =>
        {
            if (!Enum.TryParse<AgentTaskStatus>(status ?? nameof(AgentTaskStatus.Running), ignoreCase: true, out var parsed))
            {
                return Results.BadRequest(new { message = $"Unknown status '{status}'." });
            }

            return Results.Ok(await store.ListByStatusAsync(parsed));
        });

        group.MapGet("/{id:guid}/events", StreamTaskEventsAsync);
    }

    /// <summary>
    /// Server-Sent Events: a full <see cref="TaskState"/> snapshot every time its step count
    /// changes, until the task reaches a terminal status — implemented as a plain
    /// <c>text/event-stream</c> write loop rather than a typed SSE result helper, so it does not
    /// depend on the exact shape of whatever SSE support a given ASP.NET Core version ships (ADR-0018).
    /// </summary>
    private static async Task StreamTaskEventsAsync(Guid id, HttpContext http, ITaskStore store)
    {
        var response = http.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";

        var ct = http.RequestAborted;
        var lastStepCount = -1;

        while (!ct.IsCancellationRequested)
        {
            var task = await store.LoadAsync(id, ct);
            if (task is null)
            {
                await WriteEventAsync(response, "error", """{"message":"task not found"}""", ct);
                return;
            }

            if (task.Steps.Count != lastStepCount)
            {
                lastStepCount = task.Steps.Count;
                await WriteEventAsync(response, "snapshot", JsonSerializer.Serialize(task), ct);
            }

            if (task.Status != AgentTaskStatus.Running)
            {
                return;
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    private static async Task WriteEventAsync(HttpResponse response, string eventName, string jsonData, CancellationToken ct)
    {
        var payload = new StringBuilder()
            .Append("event: ").Append(eventName).Append('\n')
            .Append("data: ").Append(jsonData).Append("\n\n")
            .ToString();

        await response.WriteAsync(payload, ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// The actor identity recorded for every call this host makes — <c>"api-user"</c> is exactly
    /// the kind <see cref="ActorIdentity"/>'s own doc comment already reserves for once
    /// <c>bOps.Api</c> exists. There is no real per-request identity yet (ADR-0018: no
    /// authentication in this version), so every request shares the same placeholder id rather
    /// than fabricating a distinct one.
    /// </summary>
    internal static ActorIdentity ApiActor() => new("api-user", "anonymous", null);
}
