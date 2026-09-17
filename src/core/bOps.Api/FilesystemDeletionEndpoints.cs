// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Claims;
using System.Text.Json;
using bOps.Abstractions;
using bOps.Packages.Filesystem;

namespace bOps.Api;

internal static class FilesystemDeletionEndpoints
{
    internal static void MapFilesystemDeletionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/filesystem/deletion-manifests")
            .RequireAuthorization(ApiAuthorization.OperatorPolicy);

        group.MapPost("/", async (
            PrepareDeletionManifestRequest request,
            FilesystemDeletionService deletion,
            FilesystemInventoryOptions options,
            ITaskStore taskStore,
            ClaimsPrincipal principal,
            HttpContext http) =>
        {
            var task = await taskStore.LoadAsync(request.TaskId, http.RequestAborted);
            if (task is null)
            {
                return Results.NotFound(new { message = $"No stored task with id '{request.TaskId}'." });
            }

            var context = new ToolExecutionContext(task.Node, task.Id, AgentsEndpoints.ApiActor(principal));
            var manifestRequest = new DeletionManifestRequest(
                request.Roots,
                request.MaxDepth ?? options.DefaultMaxDepth,
                request.MaxEntries ?? options.DefaultMaxEntries,
                request.MaxDurationMilliseconds is { } milliseconds
                    ? TimeSpan.FromMilliseconds(milliseconds)
                    : options.DefaultDuration);
            try
            {
                var summary = await deletion.PrepareAsync(manifestRequest, context, http.RequestAborted);
                return Results.Created(
                    $"/api/filesystem/deletion-manifests/{task.Id}/{summary.Id}",
                    summary);
            }
            catch (FilesystemDeletionException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        group.MapGet("/{taskId:guid}/{manifestId}", async (
            Guid taskId,
            string manifestId,
            FilesystemDeletionService deletion,
            ClaimsPrincipal principal,
            HttpContext http) =>
        {
            var context = Context(taskId, principal);
            var summary = await deletion.TryGetSummaryAsync(manifestId, context, http.RequestAborted);
            return summary is null
                ? Results.NotFound(new { message = "Deletion manifest not found in this actor/task scope." })
                : Results.Ok(summary);
        });

        group.MapGet("/{taskId:guid}/{manifestId}/entries", async (
            Guid taskId,
            string manifestId,
            string? cursor,
            int? limit,
            string? search,
            FilesystemDeletionService deletion,
            ClaimsPrincipal principal,
            HttpContext http) =>
        {
            try
            {
                var page = await deletion.TryGetPageAsync(
                    manifestId,
                    Context(taskId, principal),
                    cursor,
                    limit,
                    search,
                    http.RequestAborted);
                return page is null
                    ? Results.NotFound(new { message = "Deletion manifest not found in this actor/task scope." })
                    : Results.Ok(page);
            }
            catch (FilesystemDeletionException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        group.MapGet("/{taskId:guid}/{manifestId}/download", DownloadAsync);
    }

    private static async Task DownloadAsync(
        Guid taskId,
        string manifestId,
        FilesystemDeletionService deletion,
        ClaimsPrincipal principal,
        HttpContext http)
    {
        var context = Context(taskId, principal);
        if (await deletion.TryGetSummaryAsync(manifestId, context, http.RequestAborted) is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        http.Response.ContentType = "application/x-ndjson";
        http.Response.Headers.ContentDisposition = $"attachment; filename=deletion-manifest-{manifestId}.ndjson";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await foreach (var entry in deletion.ReadEntriesAsync(manifestId, context, http.RequestAborted))
        {
            await http.Response.WriteAsync(JsonSerializer.Serialize(entry, options), http.RequestAborted);
            await http.Response.WriteAsync("\n", http.RequestAborted);
        }
    }

    private static ToolExecutionContext Context(Guid taskId, ClaimsPrincipal principal) =>
        new(NodeId.Local, taskId, AgentsEndpoints.ApiActor(principal));
}
