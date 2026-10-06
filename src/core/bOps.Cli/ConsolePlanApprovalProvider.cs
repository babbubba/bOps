// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Cli;

/// <summary>
/// Asks the operator at the console to approve or reject one plan of a delegated run by its hash (ADR-0030 sections 2 and 5).
/// It shows what is being authorized: the plan's hash and steps, the findings the plan rests on, the persisted evidence
/// limitations of the run's Discovery and Diagnostic roles (ADR-0044 section 16, read from <paramref name="store"/> by delegation
/// id; a run that cannot be read shows them as unavailable, never as none), and the authority the change will run under. The
/// decision is the person at the terminal's, never an agent's; the runtime refuses any that is not. Anything but an explicit
/// yes, including a closed input, is a no.
/// </summary>
internal sealed class ConsolePlanApprovalProvider(TextReader input, TextWriter output, ActorIdentity approver, IDelegationStore? store) : IPlanApprovalProvider
{
    /// <inheritdoc />
    public async Task<ApprovalDecision> RequestPlanApprovalAsync(PlanApprovalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await output.WriteLineAsync();
        await output.WriteLineAsync($"Plan approval required: delegation {request.DelegationId}");
        await output.WriteLineAsync($"  Change:    {request.SkillId} / {request.CapabilityName} on '{request.Target}' ({request.Environment}), blast radius {request.BlastRadius}");
        await output.WriteLineAsync($"  Plan hash: {request.PlanHash}");
        await output.WriteLineAsync($"  Rationale: {request.Plan.Rationale}");
        await output.WriteLineAsync("  Steps:");
        foreach (var step in request.Plan.Steps)
        {
            await output.WriteLineAsync($"    [{step.Index}] {step.ToolName} {step.Arguments.ToJson().ToJsonString()}");
            if (!string.IsNullOrWhiteSpace(step.Description))
            {
                await output.WriteLineAsync($"        {step.Description}");
            }
        }

        var run = await LoadRunAsync(request.DelegationId, ct);
        var limited = run is null ? null : LimitationText.LimitedEvidenceIds(run);

        await output.WriteLineAsync("  Findings:");
        if (request.Findings.Count == 0)
        {
            await output.WriteLineAsync("    (none)");
        }

        foreach (var finding in request.Findings)
        {
            var severity = finding.Severity is { } level ? $"[{level}] " : string.Empty;
            var marker = limited is not null && finding.EvidenceIds.Any(limited.Contains) ? " [rests on limited evidence]" : string.Empty;
            await output.WriteLineAsync($"    {severity}{finding.Summary} (evidence: {string.Join(", ", finding.EvidenceIds)}){marker}");
        }

        await WriteLimitationsAsync(run);
        await WriteAuthorityAsync(request.Authority);
        await output.WriteAsync("Approve exactly this plan? [y/N] ");

        var response = await input.ReadLineAsync(ct);
        var approved = response?.Trim() is { } answer
            && (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase));

        string? note = null;
        if (approved)
        {
            await output.WriteAsync("Optional note: ");
            note = await input.ReadLineAsync(ct);
            note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        }

        return new ApprovalDecision(approved, approver, note);
    }

    private async Task<DelegationRun?> LoadRunAsync(Guid delegationId, CancellationToken ct)
    {
        if (store is null)
        {
            return null;
        }

        try
        {
            return await store.LoadAsync(delegationId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Shown as unavailable below; the approval itself is not affected.
            return null;
        }
    }

    private async Task WriteLimitationsAsync(DelegationRun? run)
    {
        await output.WriteLineAsync("  Evidence limitations:");
        if (run is null)
        {
            await output.WriteLineAsync("    unavailable / not recorded (the run could not be read).");
            return;
        }

        foreach (var kind in new[] { AgentRoleKind.Discovery, AgentRoleKind.Diagnostic })
        {
            if (run.Roles.LastOrDefault(r => r.Agent.Role == kind && r.Status == DelegationRoleStatus.Completed) is not { } role)
            {
                await output.WriteLineAsync($"    {kind} evidence limitations: not recorded.");
                continue;
            }

            foreach (var line in LimitationText.Lines(role))
            {
                await output.WriteLineAsync($"    {line}");
            }
        }
    }

    private async Task WriteAuthorityAsync(AuthorityEnvelope? authority)
    {
        await output.WriteLineAsync("  Authority the change will run under:");
        if (authority is null)
        {
            await output.WriteLineAsync("    (not stated)");
            return;
        }

        await output.WriteLineAsync($"    Tools:        {string.Join(", ", authority.AllowedTools)}");
        await output.WriteLineAsync($"    Up to:        {authority.MaxRisk} risk, {authority.MaxBlastRadius} blast radius");
        await output.WriteLineAsync($"    Targets:      {string.Join(", ", authority.AllowedTargets)}");
        await output.WriteLineAsync($"    Environments: {string.Join(", ", authority.AllowedEnvironments)}");
        await output.WriteLineAsync($"    Budget:       {authority.Budget.MaxSteps} steps, until {authority.Budget.DeadlineUtc:u}");
        if (authority.Window is { } window)
        {
            await output.WriteLineAsync($"    Window:       {window.StartUtc:u} to {window.EndUtc:u}");
        }
    }
}
