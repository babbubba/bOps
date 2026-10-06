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
    DateTimeOffset UpdatedAtUtc)
{
    public bool LegacyConfigurationMigrated { get; init; }
}

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
    string? ErrorMessage)
{
    /// <summary>
    /// The role's model-loop evidence limitations (ADR-0044 section 16), typed; <c>null</c> when not recorded (another role kind, or a
    /// run stored before they were recorded) — never to be shown as "none".
    /// </summary>
    public IReadOnlyList<EvidenceLimitationView>? EvidenceLimitations { get; init; }

    /// <summary>How many further limitations were recorded beyond <see cref="EvidenceLimitations"/>.</summary>
    public int EvidenceLimitationsOmitted { get; init; }

    /// <summary>How the Diagnostic role's reply was read; <c>null</c> for another role or when not recorded.</summary>
    public DiagnosticReplyView? FindingsReply { get; init; }
}

/// <summary>
/// One finding. <see cref="RestsOnLimitedEvidence"/> is <c>true</c> only when the finding cites Evidence that is itself typed as
/// limited; <c>false</c> means only that no cited Evidence item is marked limited (a failed or denied read produces no Evidence at
/// all), and <c>null</c> means the run did not record limitations, so the join cannot be made.
/// </summary>
internal sealed record DelegationFindingView(string Id, string Summary, string? Severity, IReadOnlyList<string> EvidenceIds)
{
    public bool? RestsOnLimitedEvidence { get; init; }
}

/// <summary>One typed limitation, enums by name. It carries no tool output, error text, argument or model text.</summary>
internal sealed record EvidenceLimitationView(
    int StepIndex, string? ToolName, bool UnknownTool, string Outcome, string FailureKind, string Completeness, int? ShortenedFromCharacters, string? EvidenceId);

/// <summary>How the Diagnostic reply was read: <c>status</c> Valid, Absent or Malformed, and its <c>problem</c>.</summary>
internal sealed record DiagnosticReplyView(string Status, string Problem, int DiscardedFindings);

/// <summary>
/// The limitation metadata of a run's Discovery and Diagnostic roles, as the approval view shows it beside a plan.
/// <see cref="Available"/> is <c>false</c> when the run could not be loaded; a role whose <see cref="RoleLimitationsView.Recorded"/>
/// is <c>false</c> has none on record. Neither is ever "no limitations".
/// </summary>
internal sealed record PlanLimitationsView(bool Available, IReadOnlyList<RoleLimitationsView> Roles);

/// <summary>One model role's limitation metadata for the approval view.</summary>
internal sealed record RoleLimitationsView(
    string Role, bool Recorded, IReadOnlyList<EvidenceLimitationView>? EvidenceLimitations, int EvidenceLimitationsOmitted, DiagnosticReplyView? FindingsReply);

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
            [.. run.Roles.Select(role => Role(role, run))],
            [.. run.Journal.Select(Step)],
            run.ResumeCount,
            run.Denial is { } denial ? new DelegationDenialView(denial.Dimension.ToString(), Bound(denial.Reason)!) : null,
            Bound(run.ErrorMessage),
            run.CreatedAtUtc,
            run.UpdatedAtUtc)
        {
            LegacyConfigurationMigrated = run.LegacyConfigurationMigrated,
        };
    }

    private static DelegationRoleView Role(DelegationRoleRun role, DelegationRun run) => new(
        role.Agent.Role.ToString(),
        role.Agent.Id.ToString(),
        role.Status.ToString(),
        role.Consumed.Steps,
        role.Consumed.Tokens,
        role.StartedAtUtc,
        role.CompletedAtUtc,
        [.. (role.Report?.Findings ?? []).Select(f => Finding(f, run))],
        [.. (role.Report?.Evidence ?? []).Select(Evidence)],
        role.Report?.Plan is { } plan ? ExecutionPlanHasher.ComputeHash(plan) : null,
        role.Verification is { } verification
            ? new DelegationVerificationView(verification.Status.ToString(), Bound(verification.Detail), [.. verification.Evidence.Select(Evidence)])
            : null,
        Bound(role.ErrorMessage))
    {
        EvidenceLimitations = role.EvidenceLimitations?.Select(Limitation).ToList(),
        EvidenceLimitationsOmitted = role.EvidenceLimitationsOmitted,
        FindingsReply = Reply(role.FindingsReply),
    };

    /// <summary>
    /// A finding with its <c>restsOnLimitedEvidence</c> flag: an exact join of its evidence ids with the run's typed limitations,
    /// computed here and never stored. No materiality is inferred and no id is made up.
    /// </summary>
    private static DelegationFindingView Finding(Finding finding, DelegationRun? run) =>
        new(finding.Id, Bound(finding.Summary)!, finding.Severity?.ToString(), finding.EvidenceIds)
        {
            RestsOnLimitedEvidence = run is not null && LimitedEvidenceIds(run) is { } limited
                ? finding.EvidenceIds.Any(limited.Contains)
                : null,
        };

    /// <summary>
    /// The ids of Evidence typed as limited in the run, or <c>null</c> when a model role that ran did not record its limitations,
    /// so the join cannot be trusted to be complete.
    /// </summary>
    private static HashSet<string>? LimitedEvidenceIds(DelegationRun run)
    {
        var modelRoles = run.Roles.Where(role => role.Agent.Role is AgentRoleKind.Discovery or AgentRoleKind.Diagnostic && role.Status == DelegationRoleStatus.Completed).ToList();
        if (modelRoles.Count == 0 || modelRoles.Any(role => role.EvidenceLimitations is null))
        {
            return null;
        }

        return modelRoles
            .SelectMany(role => role.EvidenceLimitations!)
            .Select(limitation => limitation.EvidenceId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// A pending plan with the persisted limitation metadata of its run's Discovery and Diagnostic roles (ADR-0044 section 16), joined
    /// by delegation id. <paramref name="run"/> <c>null</c> (not found, or failed to load) shows the limitations as unavailable.
    /// </summary>
    internal static PendingPlanApproval WithLimitations(PendingPlanApproval plan, DelegationRun? run)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (run is null)
        {
            return plan with
            {
                Limitations = new PlanLimitationsView(false, []),
                Findings = [.. plan.Findings.Select(f => f with { RestsOnLimitedEvidence = null })],
            };
        }

        var roles = new List<RoleLimitationsView>();
        foreach (var kind in new[] { AgentRoleKind.Discovery, AgentRoleKind.Diagnostic })
        {
            var role = run.Roles.LastOrDefault(r => r.Agent.Role == kind && r.Status == DelegationRoleStatus.Completed);
            roles.Add(new RoleLimitationsView(
                kind.ToString(),
                role?.EvidenceLimitations is not null,
                role?.EvidenceLimitations?.Select(Limitation).ToList(),
                role?.EvidenceLimitationsOmitted ?? 0,
                Reply(role?.FindingsReply)));
        }

        var limited = LimitedEvidenceIds(run);
        return plan with
        {
            Limitations = new PlanLimitationsView(true, roles),
            Findings = [.. plan.Findings.Select(f => f with { RestsOnLimitedEvidence = limited is null ? null : f.EvidenceIds.Any(limited.Contains) })],
        };
    }

    private static EvidenceLimitationView Limitation(EvidenceLimitation limitation) => new(
        limitation.StepIndex,
        limitation.ToolName,
        limitation.UnknownTool,
        limitation.Outcome.ToString(),
        limitation.FailureKind.ToString(),
        limitation.Completeness.ToString(),
        limitation.ShortenedFromCharacters,
        limitation.EvidenceId);

    private static DiagnosticReplyView? Reply(DiagnosticReplyOutcome? outcome) =>
        outcome is null ? null : new DiagnosticReplyView(outcome.Status.ToString(), outcome.Problem.ToString(), outcome.DiscardedFindings);

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
