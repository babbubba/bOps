// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using System.Security.Claims;

namespace bOps.Api;

/// <summary>Maps <c>/api/approvals</c> — listing and answering pending approvals (ADR-0018).</summary>
internal static class ApprovalsEndpoints
{
    internal static void MapApprovalsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/approvals");

        group.MapGet("/pending", (ApiApprovalProvider approvals) => Results.Ok(approvals.ListPending()))
            .RequireAuthorization(ApiAuthorization.ApproverPolicy);

        group.MapPost("/{approvalId}/respond", (string approvalId, RespondToApprovalRequest request, ApiApprovalProvider approvals, ClaimsPrincipal principal) =>
        {
            var approver = AgentsEndpoints.ApiActor(principal);
            var resolved = approvals.TryRespond(approvalId, request.Approved, request.Note, approver);
            return resolved
                ? Results.NoContent()
                : Results.NotFound(new { message = $"No pending approval with id '{approvalId}'." });
        }).RequireAuthorization(ApiAuthorization.ApproverPolicy);
    }
}
