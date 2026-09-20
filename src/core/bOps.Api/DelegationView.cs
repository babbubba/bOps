// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Api;

/// <summary>
/// The shape of a delegated run the API sends to a client. The store keeps, for the run's own use, the raw data of every piece of
/// evidence (what a tool returned) and the full authority of every role; a client gets neither. It gets what a person needs to
/// follow and judge a run: where it stands, what each role did and consumed, what was found and which evidence it cites, the plan
/// hash and who approved it, the step journal and how each step ended. Enums are sent as their names. While a run waits for a human to
/// decide its plan it is <c>Running</c> and <see cref="AwaitingPlanApproval"/> is <c>true</c>: the runtime holds no separate status for it.
/// </summary>
internal sealed record DelegationView(
    Guid Id,
    string Status,
    string Objective,
    string ActorId,
    string? ActorDisplayName,
    bool RunningInThisHost,
    bool AwaitingPlanApproval,
    string? PlanHash,
    DelegationApprovalView? Approval,
    IReadOnlyList<DelegationRoleView> Roles,
    IReadOnlyList<DelegationStepView> Journal,
    int ResumeCount,
    DelegationDenialView? Denial,
    string? ErrorMessage,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

internal sealed record DelegationApprovalView(string PlanHash, string ApproverId, string? ApproverDisplayName, DateTimeOffset ApprovedAtUtc);

internal sealed record DelegationDenialView(string Dimension, string Reason);

internal sealed record DelegationRoleView(
    string Role,
    string AgentId,
    string Status,
    int Steps,
    int Tokens,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<DelegationFindingView> Findings,
    IReadOnlyList<DelegationEvidenceView> Evidence,
    string? PlanHash,
    DelegationVerificationView? Verification,
    string? ErrorMessage);

internal sealed record DelegationFindingView(string Id, string Summary, string? Severity, IReadOnlyList<string> EvidenceIds);

/// <summary>A piece of evidence without its data: which tool produced it and when, never what the tool returned.</summary>
internal sealed record DelegationEvidenceView(string Id, string Kind, string Description, string SourceTool, DateTimeOffset ObservedAtUtc);

internal sealed record DelegationVerificationView(string Status, string? Detail, IReadOnlyList<DelegationEvidenceView> Evidence);

internal sealed record DelegationStepView(
    int StepIndex, string Tool, string ArgumentsHash, DateTimeOffset IntentAtUtc, string? Outcome, string? Verification, string? Reconciliation);

internal static class DelegationViews
{
    private const int MaximumText = 500;

    /// <summary>Projects <paramref name="run"/> for a client. <paramref name="runningInThisHost"/> says whether a process of this host is executing it now, <paramref name="awaitingPlanApproval"/> whether it waits for a human to decide its plan.</summary>
    internal static DelegationView From(DelegationRun run, bool runningInThisHost, bool awaitingPlanApproval)
    {
        ArgumentNullException.ThrowIfNull(run);

        return new DelegationView(
            run.Id,
            run.Status.ToString(),
            Bound(run.Objective)!,
            run.Actor.Id,
            run.Actor.DisplayName,
            runningInThisHost,
            awaitingPlanApproval,
            run.PlanHash,
            run.Approval is { } approval
                ? new DelegationApprovalView(approval.PlanHash, approval.Approver.Id, approval.Approver.DisplayName, approval.ApprovedAtUtc)
                : null,
            [.. run.Roles.Select(Role)],
            [.. run.Journal.Select(Step)],
            run.ResumeCount,
            run.Denial is { } denial ? new DelegationDenialView(denial.Dimension.ToString(), Bound(denial.Reason)!) : null,
            Bound(run.ErrorMessage),
            run.CreatedAtUtc,
            run.UpdatedAtUtc);
    }

    private static DelegationRoleView Role(DelegationRoleRun role) => new(
        role.Agent.Role.ToString(),
        role.Agent.Id.ToString(),
        role.Status.ToString(),
        role.Consumed.Steps,
        role.Consumed.Tokens,
        role.StartedAtUtc,
        role.CompletedAtUtc,
        [.. (role.Report?.Findings ?? []).Select(f => new DelegationFindingView(f.Id, Bound(f.Summary)!, f.Severity?.ToString(), f.EvidenceIds))],
        [.. (role.Report?.Evidence ?? []).Select(Evidence)],
        role.Report?.Plan is { } plan ? ExecutionPlanHasher.ComputeHash(plan) : null,
        role.Verification is { } verification
            ? new DelegationVerificationView(verification.Status.ToString(), Bound(verification.Detail), [.. verification.Evidence.Select(Evidence)])
            : null,
        Bound(role.ErrorMessage));

    private static DelegationEvidenceView Evidence(Evidence evidence) =>
        new(evidence.Id, evidence.Kind.ToString(), Bound(evidence.Description)!, evidence.SourceTool, evidence.ObservedAtUtc);

    private static DelegationStepView Step(StepJournalEntry entry) => new(
        entry.StepIndex,
        entry.ToolName,
        entry.ArgumentsHash,
        entry.IntentAtUtc,
        entry.Outcome?.Kind.ToString(),
        (entry.Outcome?.Verification ?? entry.Reconciliation?.Verification)?.ToString(),
        entry.Reconciliation?.Action.ToString());

    private static string? Bound(string? text) =>
        text is { Length: > MaximumText } ? string.Concat(text.AsSpan(0, MaximumText), "…") : text;
}
