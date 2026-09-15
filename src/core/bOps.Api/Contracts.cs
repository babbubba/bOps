namespace bOps.Api;

/// <summary>Body of <c>POST /api/agents/tasks</c>.</summary>
internal sealed record StartTaskRequest(string Goal);

/// <summary>Response of <c>POST /api/agents/tasks</c> and <c>POST /api/agents/tasks/{id}/resume</c> — the task has started, not finished.</summary>
internal sealed record TaskAcceptedResponse(Guid TaskId);

/// <summary>
/// Body of <c>POST /api/approvals/{approvalId}/respond</c>. <see cref="Approver"/> is free text,
/// not a real identity — this version has no authentication (ADR-0018); it is recorded on the
/// audit trail as-is, the same honesty tradeoff the rest of this host makes explicit rather than
/// pretending to a real identity it cannot verify.
/// </summary>
internal sealed record RespondToApprovalRequest(bool Approved, string? Note, string? Approver = null);
