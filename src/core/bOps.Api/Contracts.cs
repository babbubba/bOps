// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Api;

/// <summary>Body of <c>POST /api/agents/tasks</c>.</summary>
internal sealed record StartTaskRequest(string Goal);

/// <summary>Response of <c>POST /api/agents/tasks</c> and <c>POST /api/agents/tasks/{id}/resume</c> — the task has started, not finished.</summary>
internal sealed record TaskAcceptedResponse(Guid TaskId);

/// <summary>Body of <c>POST /api/approvals/{approvalId}/respond</c>. Approver identity comes only from the authenticated principal (ADR-0022).</summary>
internal sealed record RespondToApprovalRequest(bool Approved, string? Note);

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
