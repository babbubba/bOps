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
            ITaskStore store, ProviderConfigurationCoordinator coordinator,
            ClaimsPrincipal principal, HttpContext http) =>
        {
            var actor = ApiActor(principal);
            var existing = await store.LoadAsync(id, http.RequestAborted);
            if (existing?.Origin == TaskOrigin.Delegated)
            {
                // A role task is owned by its DelegationRun. Keep the runtime's audited, stable refusal,
                // but do not let ordinary resume inspect or create any provider configuration for it.
                var delegated = await runner.TryAcquireResumeAsync(id, actor, http.RequestAborted);
                var refusal = delegated.Refusal!;
                return Results.Conflict(new TaskErrorResponse(refusal.Code, refusal.Message));
            }
            if (existing?.PinnedProviderConfiguration is { } persistedPin)
            {
                try { coordinator.ValidatePin(persistedPin); }
                catch (Exception ex) when (ex is InvalidOperationException or ProviderNotSupportedException or ArgumentException)
                {
                    return Results.Conflict(new TaskErrorResponse("pinned_provider_unavailable", ex.Message));
                }
            }
            var legacyPin = existing is not null && existing.PinnedProviderConfiguration is null
                ? coordinator.Current.Pin : null;
            var acquisition = await runner.TryAcquireResumeAsync(id, actor, legacyPin, http.RequestAborted);
            switch (acquisition.Outcome)
            {
                case TaskResumeOutcome.NotFound:
                    return Results.NotFound(new { message = $"No stored task with id '{id}'." });
                case TaskResumeOutcome.Refused:
                    var refusal = acquisition.Refusal!;
                    return Results.Json(new TaskErrorResponse(refusal.Code, refusal.Message), statusCode: StatusFor(refusal.Code));
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
                blocked is null ? null : new TaskErrorResponse(blocked.Code, blocked.Message),
                acquisition.LegacyConfigurationMigrated));
        }).RequireAuthorization(ApiAuthorization.OperatorPolicy);

        group.MapDelete("/{id:guid}", (Guid id, AgentTaskLauncher launcher) =>
            launcher.Cancel(id)
                ? Results.Accepted($"/api/agents/tasks/{id}")
                : Results.NotFound(new { message = $"Task '{id}' is not running in this host." }))
            .RequireAuthorization(ApiAuthorization.OperatorPolicy);

        group.MapGet("/{id:guid}", async (Guid id, ITaskStore store, AgentRunner runner, AgentTaskLauncher launcher, HttpContext http) =>
        {
            var task = await store.LoadAsync(id, http.RequestAborted);
            return task is null
                ? Results.NotFound(new { message = $"No stored task with id '{id}'." })
                : Results.Ok((await ViewAsync(task, store, runner, launcher.IsExecuting(task.Id), http.RequestAborted)).View);
        }).RequireAuthorization(ApiAuthorization.ViewerPolicy);

        group.MapGet("/", async (string? status, ITaskStore store, AgentRunner runner, AgentTaskLauncher launcher, HttpContext http) =>
        {
            if (!Enum.TryParse<AgentTaskStatus>(status ?? nameof(AgentTaskStatus.Running), ignoreCase: true, out var parsed))
            {
                return Results.BadRequest(new { message = $"Unknown status '{status}'." });
            }

            var views = new List<System.Text.Json.Nodes.JsonObject>();
            foreach (var task in await store.ListByStatusAsync(parsed, http.RequestAborted))
            {
                views.Add((await ViewAsync(task, store, runner, launcher.IsExecuting(task.Id), http.RequestAborted)).View);
            }

            return Results.Ok(views);
        }).RequireAuthorization(ApiAuthorization.ViewerPolicy);

        group.MapGet("/{id:guid}/events", StreamTaskEventsAsync)
            .RequireAuthorization(ApiAuthorization.ViewerPolicy);

        // ADR-0051 §8.3: an administrator's fenced recovery of an orphaned Running attempt. It runs nothing and is not a resume.
        group.MapPost("/{id:guid}/recover", async (Guid id, RecoverTaskRequest? request, AgentRunner runner, AgentTaskLauncher launcher,
            ITaskStore store, ClaimsPrincipal principal, HttpContext http) =>
        {
            if (request?.ExecutionAttempt is not > 0)
            {
                return Results.BadRequest(new { message = "'executionAttempt' is required: the execution attempt you were shown, a positive number." });
            }

            var result = await runner.TryRecoverAsync(id, request.ExecutionAttempt.Value, ApiActor(principal), launcher.IsExecuting(id), http.RequestAborted);
            return result.Outcome switch
            {
                TaskRecoveryOutcome.NotFound => Results.NotFound(new { message = $"No stored task with id '{id}'." }),
                TaskRecoveryOutcome.Refused => Results.Json(new TaskErrorResponse(result.Refusal!.Code, result.Refusal.Message),
                    statusCode: StatusFor(result.Refusal.Code)),
                _ => Results.Ok((await ViewAsync(result.Task!, store, runner, launcher.IsExecuting(id), CancellationToken.None)).View),
            };
        }).RequireAuthorization(ApiAuthorization.AdministratorPolicy);

        // ADR-0051 §9.1: an administrator's reconciliation of a task's unsettled mutations. There is no retry action.
        group.MapPost("/{id:guid}/reconcile", async (Guid id, ReconcileTaskRequest? request, AgentRunner runner, ITaskStore store,
            ClaimsPrincipal principal, HttpContext http) =>
        {
            TaskReconcileAction action;
            switch (request?.Action)
            {
                case "verify":
                    action = TaskReconcileAction.Verify;
                    break;
                case "acceptDone":
                    action = TaskReconcileAction.AcceptDone;
                    break;
                case "abandon":
                    action = TaskReconcileAction.Abandon;
                    break;
                default:
                    return Results.BadRequest(new { message = "'action' is 'verify', 'acceptDone' or 'abandon'." });
            }

            if (request.Note?.Length > AgentRunner.MaxReconcileNoteLength)
            {
                return Results.BadRequest(new { message = $"'note' is at most {AgentRunner.MaxReconcileNoteLength} characters." });
            }

            var result = await runner.ReconcileMutationsAsync(id, action, ApiActor(principal), request.Note, http.RequestAborted);
            if (result.Outcome == TaskReconcileOutcome.NotFound)
            {
                return Results.NotFound(new { message = $"No stored task with id '{id}'." });
            }

            if (result.Outcome == TaskReconcileOutcome.Refused)
            {
                return Results.Json(new TaskErrorResponse(result.Refusal!.Code, result.Refusal.Message), statusCode: StatusFor(result.Refusal.Code));
            }

            var task = await store.LoadAsync(id, CancellationToken.None);
            var (_, journal) = await ReadJournalAsync(task!, store, CancellationToken.None);
            var resume = runner.EvaluateResume(task!, journal);
            return Results.Ok(new ReconcileTaskResponse(
                id,
                [.. result.Results.Select(item => new ReconciledMutationResponse(
                    item.Key.ExecutionAttempt, item.Key.StepIndex, item.State.ToString(),
                    new ReconciliationResponse(item.Reconciliation.Action.ToString(), item.Reconciliation.Verification?.ToString(),
                        item.Reconciliation.ResolvedBy.DisplayName ?? item.Reconciliation.ResolvedBy.Id, item.Reconciliation.AtUtc),
                    item.ReasonCode))],
                result.UnsettledCount,
                resume.Resumable,
                resume.Refusal is { } blocked ? new TaskErrorResponse(blocked.Code, blocked.Message) : null));
        }).RequireAuthorization(ApiAuthorization.AdministratorPolicy);
    }

    /// <summary>
    /// The HTTP status of a refused task operation (ADR-0040 §9, ADR-0051 §8.3, §9.1, §9.4): a store that cannot do it is 501, a
    /// journal that cannot be read now is 503, a malformed request 400, and every refusal about the task's state 409.
    /// </summary>
    internal static int StatusFor(string code) => code switch
    {
        TaskResumeRefusal.TransitionUnsupported or TaskResumeRefusal.MutationJournalUnsupported => StatusCodes.Status501NotImplemented,
        TaskResumeRefusal.MutationJournalUnavailable => StatusCodes.Status503ServiceUnavailable,
        TaskRecoveryRefusal.InvalidRequest => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status409Conflict,
    };

    /// <summary>The task and its mutation journal, read in one snapshot when the store keeps one; the entries are <c>null</c> when they cannot be read.</summary>
    private static async Task<(TaskState Task, IReadOnlyList<TaskMutationJournalEntry>? Journal)> ReadJournalAsync(
        TaskState task, ITaskStore store, CancellationToken ct)
    {
        if (task.MutationJournalMode != TaskMutationJournalMode.Journaled)
        {
            return (task, []);
        }

        if (store is not ITaskMutationJournalStore journalStore)
        {
            return (task, null);
        }

        try
        {
            return await journalStore.LoadWithJournalAsync(task.Id, ct) is { } snapshot ? (snapshot.Task, snapshot.Entries) : (task, []);
        }
        catch (Exception readFailure) when (readFailure is not OperationCanceledException)
        {
            return (task, null);
        }
    }

    /// <summary>The client view of <paramref name="task"/> with its journal (ADR-0051 §14.2), and the journal summary <c>/events</c> watches.</summary>
    private static async Task<(System.Text.Json.Nodes.JsonObject View, string JournalSummary)> ViewAsync(
        TaskState task, ITaskStore store, AgentRunner runner, bool executing, CancellationToken ct)
    {
        var (current, journal) = await ReadJournalAsync(task, store, ct);
        var view = TaskStateView.ToView(current, executing, runner.EvaluateResume(current, journal),
            runner.ProjectExecutionPlan(current, executing, journal), runner.EvaluateRecovery(current, executing), journal);
        return (view, TaskStateView.JournalSummary(journal));
    }

    /// <summary>A temporary executor condition (ADR-0040 §9): 503 with a body and <c>Retry-After</c>. Never used for a task-state conflict.</summary>
    private static IResult ExecutorUnavailable(HttpContext http, string message)
    {
        http.Response.Headers.RetryAfter = "5";
        return Results.Json(new TaskErrorResponse("executor_unavailable", message), statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// Server-Sent Events: a task view snapshot every time its step count, its status, its execution attempt, its number of accepted plan
    /// revisions (an accepted replan is persisted before any step of the new revision exists), its mutation journal's count per state
    /// (ADR-0051 §14.2) or whether this host executes it changes,
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
        (int Steps, AgentTaskStatus Status, int ExecutionAttempt, int Plans, bool Executing, string Journal)? last = null;
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

            var executing = launcher.IsExecuting(task.Id);
            var (view, journalSummary) = await ViewAsync(task, store, runner, executing, ct);
            var current = (task.Steps.Count, task.Status, task.ExecutionAttempt, task.Plans.Count, executing, journalSummary);
            if (last != current)
            {
                last = current;
                await WriteEventAsync(response, "snapshot", view.ToJsonString(SseJsonOptions), ct);
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
