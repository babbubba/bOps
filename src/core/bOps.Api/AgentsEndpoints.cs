// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Security.Claims;
using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.Api;

/// <summary>Maps <c>/api/agents/tasks</c> — starting, resuming, reading and streaming tasks (ADR-0018).</summary>
internal static class AgentsEndpoints
{
    /// <summary>How often <c>GET /api/agents/tasks/{id}/events</c> re-reads <see cref="ITaskStore"/> to check for a new step. A fixed MVP interval — ADR-0018 explicitly defers tuning this to a session with a real UI to measure against.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long this endpoint tolerates a task not existing yet before it gives up and reports it
    /// as genuinely not found. <c>POST /api/agents/tasks</c> returns <c>202</c> the instant it has
    /// generated an id — before the detached background run has saved anything (ADR-0018) — so a
    /// client that immediately opens the event stream for that id can legitimately race the very
    /// first <see cref="ITaskStore.SaveAsync"/>. Bailing on the first null read (as this endpoint
    /// did originally) turns that ordinary race into a permanent "not found" the client can never
    /// recover from without a fresh retry of its own.
    /// </summary>
    private static readonly TimeSpan NotFoundGracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The exact defaults ASP.NET Core's Minimal API JSON output uses (camelCase property names,
    /// case-insensitive reads) — <c>Results.Ok(task)</c> elsewhere in this file gets these
    /// automatically from the framework; this hand-rolled SSE write loop bypasses that pipeline
    /// entirely, so it must apply them explicitly or silently disagree with every other endpoint.
    /// </summary>
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web);

    internal static void MapAgentsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/agents/tasks");

        group.MapPost("/", async (StartTaskRequest request, AgentTaskLauncher launcher, TaskIdempotencyStore idempotency,
            ClaimsPrincipal principal, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(request.Goal))
            {
                return Results.BadRequest(new { message = "'goal' is required." });
            }

            var actor = ApiActor(principal);
            Guid? taskId;
            try
            {
                taskId = await idempotency.GetOrStartAsync(actor.Id, http.Request.Headers["Idempotency-Key"].FirstOrDefault(),
                    () => launcher.TryStartAsync(request.Goal, actor, http.RequestAborted));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }

            return taskId is null
                ? ExecutorUnavailable(http, "Every execution slot of this host is busy; retry later.")
                : Results.Accepted($"/api/agents/tasks/{taskId}", new TaskAcceptedResponse(taskId.Value));
        }).RequireAuthorization(ApiAuthorization.OperatorPolicy);

        // ADR-0040 §4.3: 202 only once the task has been atomically moved to Running under a new execution attempt AND the
        // launcher has admitted that attempt. A reader after the 202 never sees the pre-resume terminal state.
        group.MapPost("/{id:guid}/resume", async (Guid id, AgentRunner runner, AgentTaskLauncher launcher,
            ClaimsPrincipal principal, HttpContext http) =>
        {
            var actor = ApiActor(principal);
            var acquisition = await runner.TryAcquireResumeAsync(id, actor, http.RequestAborted);
            switch (acquisition.Outcome)
            {
                case TaskResumeOutcome.NotFound:
                    return Results.NotFound(new { message = $"No stored task with id '{id}'." });
                case TaskResumeOutcome.Refused:
                    var refusal = acquisition.Refusal!;
                    return Results.Json(
                        new TaskErrorResponse(refusal.Code, refusal.Message),
                        statusCode: refusal.Code == TaskResumeRefusal.TransitionUnsupported
                            ? StatusCodes.Status501NotImplemented
                            : StatusCodes.Status409Conflict);
            }

            var acquired = acquisition.Task!;
            if (!launcher.TryAdmit(acquired, actor))
            {
                // The acquired attempt never runs: it is contained Failed, never left Running, and this is not a 202.
                await runner.ContainUnadmittedResumeAsync(acquired, actor, CancellationToken.None);
                return ExecutorUnavailable(http,
                    $"No executor admitted execution attempt {acquired.ExecutionAttempt}; the task was marked Failed and can be resumed later.");
            }

            var blocked = runner.EvaluateResume(acquired).Refusal;
            return Results.Accepted($"/api/agents/tasks/{id}", new TaskResumeAcceptedResponse(
                id, acquired.Status, acquired.ExecutionAttempt, Executing: true, Resumable: false,
                blocked is null ? null : new TaskErrorResponse(blocked.Code, blocked.Message)));
        }).RequireAuthorization(ApiAuthorization.OperatorPolicy);

        group.MapDelete("/{id:guid}", (Guid id, AgentTaskLauncher launcher) =>
            launcher.Cancel(id)
                ? Results.Accepted($"/api/agents/tasks/{id}")
                : Results.NotFound(new { message = $"Task '{id}' is not running in this host." }))
            .RequireAuthorization(ApiAuthorization.OperatorPolicy);

        group.MapGet("/{id:guid}", async (Guid id, ITaskStore store, AgentRunner runner, AgentTaskLauncher launcher) =>
        {
            var task = await store.LoadAsync(id);
            return task is null
                ? Results.NotFound(new { message = $"No stored task with id '{id}'." })
                : Results.Ok(View(task, runner, launcher));
        }).RequireAuthorization(ApiAuthorization.ViewerPolicy);

        group.MapGet("/", async (string? status, ITaskStore store, AgentRunner runner, AgentTaskLauncher launcher) =>
        {
            if (!Enum.TryParse<AgentTaskStatus>(status ?? nameof(AgentTaskStatus.Running), ignoreCase: true, out var parsed))
            {
                return Results.BadRequest(new { message = $"Unknown status '{status}'." });
            }

            return Results.Ok((await store.ListByStatusAsync(parsed)).Select(task => View(task, runner, launcher)));
        }).RequireAuthorization(ApiAuthorization.ViewerPolicy);

        group.MapGet("/{id:guid}/events", StreamTaskEventsAsync)
            .RequireAuthorization(ApiAuthorization.ViewerPolicy);
    }

    private static System.Text.Json.Nodes.JsonObject View(TaskState task, AgentRunner runner, AgentTaskLauncher launcher) =>
        TaskStateView.ToView(task, launcher.IsExecuting(task.Id), runner.EvaluateResume(task));

    /// <summary>A temporary executor condition (ADR-0040 §9): 503 with a body and <c>Retry-After</c>. Never used for a task-state conflict.</summary>
    private static IResult ExecutorUnavailable(HttpContext http, string message)
    {
        http.Response.Headers.RetryAfter = "5";
        return Results.Json(new TaskErrorResponse("executor_unavailable", message), statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// Server-Sent Events: a task view snapshot every time its step count, its status or its execution attempt changes,
    /// until the task reaches a terminal status — implemented as a plain <c>text/event-stream</c> write loop rather than a
    /// typed SSE result helper, so it does not depend on the exact shape of whatever SSE support a given ASP.NET Core
    /// version ships (ADR-0018). Because a resume is persisted before its 202 (ADR-0040 §4.3), a stream opened after a
    /// resume starts on the new execution attempt and never ends on the stale pre-resume snapshot.
    /// </summary>
    private static async Task StreamTaskEventsAsync(Guid id, HttpContext http, ITaskStore store, AgentRunner runner, AgentTaskLauncher launcher)
    {
        var response = http.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";

        var ct = http.RequestAborted;
        (int Steps, AgentTaskStatus Status, int ExecutionAttempt)? last = null;
        var firstSeenAtUtc = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            var task = await store.LoadAsync(id, ct);
            if (task is null)
            {
                if (DateTimeOffset.UtcNow - firstSeenAtUtc < NotFoundGracePeriod)
                {
                    await Task.Delay(PollInterval, ct);
                    continue;
                }

                await WriteEventAsync(response, "error", """{"message":"task not found"}""", ct);
                return;
            }

            var current = (task.Steps.Count, task.Status, task.ExecutionAttempt);
            if (last != current)
            {
                last = current;
                await WriteEventAsync(response, "snapshot", View(task, runner, launcher).ToJsonString(SseJsonOptions), ct);
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

    internal static ActorIdentity ApiActor(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("An authenticated API principal has no name identifier.");
        return new ActorIdentity("api-user", id, principal.Identity?.Name);
    }
}
