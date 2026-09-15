// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

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

/// <summary>
/// Response of <c>GET /api/providers</c> (ADR-0019). <see cref="Active"/> is <c>null</c> if the
/// host has no valid <c>ModelProvider</c> configuration section at all — distinct from a
/// configured-but-keyless provider, which still reports with <see cref="ActiveProviderInfo.HasApiKey"/> false.
/// </summary>
internal sealed record ProvidersResponse(IReadOnlyList<string> RegisteredProviderIds, ActiveProviderInfo? Active);

/// <summary>
/// The provider this host is actually configured to use. Never carries the API key's value
/// (ADR-0019) — only whether one is present, since this endpoint has no authentication (ADR-0018)
/// and is reachable by anyone who can reach the host.
/// </summary>
internal sealed record ActiveProviderInfo(string Provider, string Model, string BaseUrl, bool HasApiKey);
