// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using bOps.Abstractions;

namespace bOps.Api;

/// <summary>One approval request currently waiting for an HTTP client to answer it, as shown by <c>GET /api/approvals/pending</c>.</summary>
internal sealed record PendingApproval(string Id, Guid? TaskId, string Tool, string Reason, DateTimeOffset RequestedAtUtc);

/// <summary>
/// The HTTP channel's <see cref="IApprovalProvider"/> (ADR-0018) — an approval queue instead of
/// <c>bOps.Cli</c>'s blocking console prompt, exactly as <c>ConsoleApprovalProvider</c>'s own doc
/// comment already anticipated. <see cref="RequestApprovalAsync"/> is called from deep inside
/// <c>AgentRunner</c>, which never sees or passes a task id (rule: the interface predates a
/// multi-task host), so this recovers "which task is this approval for" from
/// <see cref="CurrentTaskId"/>, an <see cref="AsyncLocal{T}"/> the endpoint that starts or resumes
/// a task sets before it does — <see cref="AsyncLocal{T}"/> flows through the awaited call chain
/// into this class without <c>AgentRunner</c> or the policy engine needing to know it exists.
/// </summary>
internal sealed class ApiApprovalProvider : IApprovalProvider
{
    /// <summary>Set by whatever starts or resumes a task, for the duration of that call, so a nested approval request can recover which task it belongs to.</summary>
    public static AsyncLocal<Guid?> CurrentTaskId { get; } = new();

    private sealed record Entry(PendingApproval Info, TaskCompletionSource<ApprovalDecision> Completion);

    private readonly ConcurrentDictionary<string, Entry> _pending = new();

    /// <summary>Every approval currently awaiting a decision.</summary>
    public IReadOnlyList<PendingApproval> ListPending() => _pending.Values.Select(e => e.Info).ToList();

    /// <inheritdoc />
    public async Task<ApprovalDecision> RequestApprovalAsync(
        ToolManifest manifest, ToolArguments arguments, VerificationSpec? verification, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Entry(new PendingApproval(id, CurrentTaskId.Value, manifest.Name, reason, DateTimeOffset.UtcNow), completion);
        _pending[id] = entry;

        await using var registration = ct.Register(() => completion.TrySetCanceled(ct));
        try
        {
            return await completion.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Resolves a pending approval from a separate HTTP request than the one that raised it.
    /// Returns <c>false</c> if <paramref name="approvalId"/> is not currently pending — already
    /// answered, cancelled with its task, or never existed.
    /// </summary>
    public bool TryRespond(string approvalId, bool approved, string? note, ActorIdentity approver)
    {
        if (!_pending.TryGetValue(approvalId, out var entry))
        {
            return false;
        }

        return entry.Completion.TrySetResult(new ApprovalDecision(approved, approver, note));
    }
}
