// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Packages.Filesystem;
using System.Security.Claims;
using System.Text.Json;

namespace bOps.Api;

/// <summary>Maps <c>/api/approvals</c> — listing and answering pending approvals (ADR-0018).</summary>
internal static class ApprovalsEndpoints
{
    internal static void MapApprovalsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/approvals");

        group.MapGet("/pending", (ApiApprovalProvider approvals) => Results.Ok(approvals.ListPending()))
            .RequireAuthorization(ApiAuthorization.ApproverPolicy);

        group.MapPost("/{approvalId}/respond", async (
            string approvalId,
            RespondToApprovalRequest request,
            ApiApprovalProvider approvals,
            FilesystemDeletionService deletion,
            ClaimsPrincipal principal,
            HttpContext http) =>
        {
            if (!approvals.TryGet(approvalId, out var pending, out var executionContext) || pending is null)
            {
                return Results.NotFound(new { message = $"No pending approval with id '{approvalId}'." });
            }

            if (request.Approved && pending.PermanentDeletion)
            {
                if (!request.AcknowledgePermanentDeletion)
                {
                    return Results.BadRequest(new
                    {
                        message = "Permanent deletion approval requires acknowledgePermanentDeletion=true.",
                    });
                }

                if (executionContext is null
                    || !TryGetDeletionArguments(pending, out var manifestId, out var approvalHash))
                {
                    return Results.Conflict(new { message = "The pending deletion approval has no valid execution scope or manifest binding." });
                }

                var summary = await deletion.TryGetSummaryAsync(manifestId, executionContext, http.RequestAborted);
                if (summary is null
                    || summary.Status != DeletionManifestStatus.Ready
                    || !string.Equals(summary.ApprovalHash, approvalHash, StringComparison.Ordinal))
                {
                    return Results.Conflict(new
                    {
                        message = "The deletion manifest expired, changed or is no longer ready. Prepare a fresh manifest.",
                    });
                }
            }

            var approver = AgentsEndpoints.ApiActor(principal);
            var resolved = approvals.TryRespond(approvalId, request.Approved, request.Note, approver);
            return resolved
                ? Results.NoContent()
                : Results.NotFound(new { message = $"No pending approval with id '{approvalId}'." });
        }).RequireAuthorization(ApiAuthorization.ApproverPolicy);

        group.MapGet("/{approvalId}/deletion-manifest", async (
            string approvalId,
            ApiApprovalProvider approvals,
            FilesystemDeletionService deletion,
            HttpContext http) =>
        {
            if (!TryGetDeletionApproval(approvals, approvalId, out var pending, out var context, out var manifestId, out _))
            {
                return Results.NotFound(new { message = "No scoped pending deletion approval was found." });
            }

            var summary = await deletion.TryGetSummaryAsync(manifestId, context, http.RequestAborted);
            return summary is null
                ? Results.NotFound(new { message = "The deletion manifest was not found in the pending approval scope." })
                : Results.Ok(summary);
        }).RequireAuthorization(ApiAuthorization.ApproverPolicy);

        group.MapGet("/{approvalId}/deletion-manifest/entries", async (
            string approvalId,
            string? cursor,
            int? limit,
            string? search,
            ApiApprovalProvider approvals,
            FilesystemDeletionService deletion,
            HttpContext http) =>
        {
            if (!TryGetDeletionApproval(approvals, approvalId, out _, out var context, out var manifestId, out _))
            {
                return Results.NotFound(new { message = "No scoped pending deletion approval was found." });
            }

            try
            {
                var page = await deletion.TryGetPageAsync(
                    manifestId, context, cursor, limit, search, http.RequestAborted);
                return page is null
                    ? Results.NotFound(new { message = "The deletion manifest was not found in the pending approval scope." })
                    : Results.Ok(page);
            }
            catch (FilesystemDeletionException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        }).RequireAuthorization(ApiAuthorization.ApproverPolicy);

        group.MapGet("/{approvalId}/deletion-manifest/download", DownloadDeletionManifestAsync)
            .RequireAuthorization(ApiAuthorization.ApproverPolicy);
    }

    private static async Task DownloadDeletionManifestAsync(
        string approvalId,
        ApiApprovalProvider approvals,
        FilesystemDeletionService deletion,
        HttpContext http)
    {
        if (!TryGetDeletionApproval(approvals, approvalId, out _, out var context, out var manifestId, out _))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var summary = await deletion.TryGetSummaryAsync(manifestId, context, http.RequestAborted);
        if (summary is null)
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

    private static bool TryGetDeletionApproval(
        ApiApprovalProvider approvals,
        string approvalId,
        out PendingApproval pending,
        out ToolExecutionContext context,
        out string manifestId,
        out string approvalHash)
    {
        if (approvals.TryGet(approvalId, out var found, out var foundContext)
            && found is { PermanentDeletion: true }
            && foundContext is not null
            && TryGetDeletionArguments(found, out manifestId, out approvalHash))
        {
            pending = found;
            context = foundContext;
            return true;
        }

        pending = null!;
        context = null!;
        manifestId = string.Empty;
        approvalHash = string.Empty;
        return false;
    }

    private static bool TryGetDeletionArguments(
        PendingApproval approval,
        out string manifestId,
        out string approvalHash)
    {
        manifestId = approval.Arguments["manifestId"]?.GetValue<string>() ?? string.Empty;
        approvalHash = approval.Arguments["approvalHash"]?.GetValue<string>() ?? string.Empty;
        return manifestId.Length > 0 && approvalHash.Length > 0;
    }
}
