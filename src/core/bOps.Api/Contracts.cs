// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Api;

/// <summary>Body of <c>POST /api/agents/tasks</c>.</summary>
internal sealed record StartTaskRequest(string Goal);

/// <summary>Response of <c>POST /api/agents/tasks</c> — the task has started, not finished.</summary>
internal sealed record TaskAcceptedResponse(Guid TaskId);

/// <summary>
/// Response of an accepted <c>POST /api/agents/tasks/{id}/resume</c> (ADR-0040 §9): the transition that was persisted and
/// admitted — the task is <c>Running</c> under <see cref="ExecutionAttempt"/> — not a snapshot read before it.
/// <see cref="TaskId"/> is kept for clients written against the earlier response.
/// </summary>
internal sealed record TaskResumeAcceptedResponse(
    Guid TaskId,
    bOps.Abstractions.AgentTaskStatus Status,
    int ExecutionAttempt,
    bool Executing,
    bool Resumable,
    TaskErrorResponse? ResumeBlockedReason,
    bool LegacyConfigurationMigrated = false);

/// <summary>The body of a refused task request (409, 501, 503): a stable <see cref="Code"/> and an operator <see cref="Message"/>.</summary>
internal sealed record TaskErrorResponse(string Code, string Message);

/// <summary>Body of <c>POST /api/delegations</c> (ADR-0030 section 9). Without <see cref="Remediation"/> the run only diagnoses.</summary>
internal sealed record StartDelegationRequest(string Objective, int? MaxSteps, int? MaxTokens, DelegationRemediationBody? Remediation);

/// <summary>The change a delegated run may prepare. <see cref="BlastRadius"/> is <c>single</c>, <c>multiple</c> or <c>fleet</c>; omitted, <c>single</c>.</summary>
internal sealed record DelegationRemediationBody(
    string SkillId, string CapabilityName, string Target, string Environment, string? BlastRadius, bool DryRun, System.Text.Json.Nodes.JsonObject? Input);

/// <summary>Response of <c>POST /api/delegations</c> and <c>POST /api/delegations/{id}/resume</c>: the run exists, it has not finished.</summary>
internal sealed record DelegationAcceptedResponse(Guid DelegationId, bool LegacyConfigurationMigrated = false);

/// <summary>Body of <c>POST /api/delegations/{id}/reconcile</c>. <see cref="Decision"/> is <c>accept</c> (the unsettled steps are done) or <c>abandon</c> (end the run).</summary>
internal sealed record ReconcileDelegationRequest(string Decision, string? Note);

/// <summary>Body of <c>POST /api/delegations/{id}/approval</c>. <see cref="PlanHash"/> is the hash of the plan being decided, so an answer cannot land on a different plan.</summary>
internal sealed record RespondToPlanApprovalRequest(string PlanHash, bool Approved, string? Note);

/// <summary>Body of <c>POST /api/approvals/{approvalId}/respond</c>. Approver identity comes only from the authenticated principal (ADR-0022).</summary>
internal sealed record RespondToApprovalRequest(
    bool Approved,
    string? Note,
    bool AcknowledgePermanentDeletion = false);

internal sealed record PrepareDeletionManifestRequest(
    Guid TaskId,
    IReadOnlyList<string> Roots,
    int? MaxDepth,
    int? MaxEntries,
    int? MaxDurationMilliseconds);

/// <summary>
/// Response of <c>GET /api/providers</c> (ADR-0019). <see cref="Active"/> describes the
/// currently published effective provider; the nullable shape preserves the existing API contract.
/// </summary>
internal sealed record ProvidersResponse(IReadOnlyList<string> RegisteredProviderIds, ActiveProviderInfo? Active);

/// <summary>
/// The provider newly admitted executions use. Never carries the API key's value (ADR-0019),
/// only whether a current API key exists.
/// </summary>
internal sealed record ActiveProviderInfo(string Provider, string Model, string BaseUrl, bool HasApiKey);
