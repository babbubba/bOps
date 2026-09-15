// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Api;

/// <summary>Maps <c>/api/approvals</c> — listing and answering pending approvals (ADR-0018).</summary>
internal static class ApprovalsEndpoints
{
    internal static void MapApprovalsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/approvals");

        group.MapGet("/pending", (ApiApprovalProvider approvals) => Results.Ok(approvals.ListPending()));

        group.MapPost("/{approvalId}/respond", (string approvalId, RespondToApprovalRequest request, ApiApprovalProvider approvals) =>
        {
            var approver = new ActorIdentity("api-user", string.IsNullOrWhiteSpace(request.Approver) ? "anonymous" : request.Approver, null);
            var resolved = approvals.TryRespond(approvalId, request.Approved, request.Note, approver);
            return resolved
                ? Results.NoContent()
                : Results.NotFound(new { message = $"No pending approval with id '{approvalId}'." });
        });
    }
}
