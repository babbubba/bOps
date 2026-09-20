// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Api;

/// <summary>One plan of a delegated run waiting for a human to approve or reject it, as shown by <c>GET /api/delegations/approvals</c>.</summary>
internal sealed record PendingPlanApproval(
    Guid DelegationId,
    string PlanHash,
    DateTimeOffset RequestedAtUtc,
    string SkillId,
    string CapabilityName,
    string Target,
    string Environment,
    string BlastRadius,
    string Rationale,
    IReadOnlyList<PendingPlanStep> Steps,
    IReadOnlyList<PendingPlanFinding> Findings,
    PendingPlanAuthority? Authority);

/// <summary>One step of a plan waiting for approval.</summary>
internal sealed record PendingPlanStep(int Index, string Tool, JsonObject Arguments, string? Description);

/// <summary>One finding the plan rests on. The evidence itself is not sent: only which evidence it cites.</summary>
internal sealed record PendingPlanFinding(string Id, string Summary, string? Severity, IReadOnlyList<string> EvidenceIds);

/// <summary>The authority the change will run under, so the approver sees what the plan is allowed to touch.</summary>
internal sealed record PendingPlanAuthority(
    IReadOnlyList<string> Tools, string MaxRisk, string MaxBlastRadius, IReadOnlyList<string> Targets, IReadOnlyList<string> Environments, int MaxSteps, DateTimeOffset DeadlineUtc);

/// <summary>How <see cref="ApiPlanApprovalProvider.TryRespond"/> ended.</summary>
internal enum PlanApprovalResponse
{
    /// <summary>The decision was delivered to the run.</summary>
    Delivered,

    /// <summary>No plan of that run is waiting: it was already decided, the run ended, or it never asked.</summary>
    NotPending,

    /// <summary>A plan is waiting, but not the one whose hash was given. Nothing was decided.</summary>
    HashMismatch,
}

/// <summary>
/// The HTTP channel's <see cref="IPlanApprovalProvider"/> (ADR-0030 sections 2, 5 and 9): an approval queue beside
/// <see cref="ApiApprovalProvider"/>, answered by a separate request from an authenticated principal with the approver role.
/// A decision names the plan hash it is about, so an answer to one plan can never authorize another; the runtime binds it again.
/// Nothing is persisted: a plan that is waiting when the host stops is asked again when its run is resumed.
/// </summary>
internal sealed class ApiPlanApprovalProvider : IPlanApprovalProvider
{
    private sealed record Entry(PendingPlanApproval Info, TaskCompletionSource<ApprovalDecision> Completion);

    private readonly ConcurrentDictionary<Guid, Entry> _pending = new();

    /// <summary>Every plan currently awaiting a decision.</summary>
    public IReadOnlyList<PendingPlanApproval> ListPending() => [.. _pending.Values.Select(e => e.Info).OrderBy(i => i.RequestedAtUtc)];

    /// <summary>Whether a plan of <paramref name="delegationId"/> is waiting for a decision now.</summary>
    public bool IsPending(Guid delegationId) => _pending.ContainsKey(delegationId);

    /// <inheritdoc />
    public async Task<ApprovalDecision> RequestPlanApprovalAsync(PlanApprovalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var completion = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Entry(Describe(request), completion);
        if (!_pending.TryAdd(request.DelegationId, entry))
        {
            throw new InvalidOperationException($"Delegation {request.DelegationId} is already waiting for a plan decision.");
        }

        await using var registration = ct.Register(() => completion.TrySetCanceled(ct));
        try
        {
            return await completion.Task;
        }
        finally
        {
            _pending.TryRemove(request.DelegationId, out _);
        }
    }

    /// <summary>Delivers the decision of <paramref name="approver"/> on the plan of <paramref name="delegationId"/> whose hash is <paramref name="planHash"/>.</summary>
    public PlanApprovalResponse TryRespond(Guid delegationId, string planHash, bool approved, string? note, ActorIdentity approver)
    {
        ArgumentNullException.ThrowIfNull(approver);

        if (!_pending.TryGetValue(delegationId, out var entry))
        {
            return PlanApprovalResponse.NotPending;
        }

        if (!string.Equals(entry.Info.PlanHash, planHash, StringComparison.Ordinal))
        {
            return PlanApprovalResponse.HashMismatch;
        }

        return entry.Completion.TrySetResult(new ApprovalDecision(approved, approver, note))
            ? PlanApprovalResponse.Delivered
            : PlanApprovalResponse.NotPending;
    }

    private static PendingPlanApproval Describe(PlanApprovalRequest request) => new(
        request.DelegationId,
        request.PlanHash,
        DateTimeOffset.UtcNow,
        request.SkillId,
        request.CapabilityName,
        request.Target,
        request.Environment,
        request.BlastRadius.ToString(),
        request.Plan.Rationale,
        [.. request.Plan.Steps.Select(step => new PendingPlanStep(step.Index, step.ToolName, step.Arguments.ToJson(), step.Description))],
        [.. request.Findings.Select(f => new PendingPlanFinding(f.Id, f.Summary, f.Severity?.ToString(), f.EvidenceIds))],
        request.Authority is { } authority
            ? new PendingPlanAuthority(
                authority.AllowedTools, authority.MaxRisk.ToString(), authority.MaxBlastRadius.ToString(), authority.AllowedTargets,
                authority.AllowedEnvironments, authority.Budget.MaxSteps, authority.Budget.DeadlineUtc)
            : null);
}
