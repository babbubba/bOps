// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.AgentFramework.Troubleshooting;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace bOps.Cli;

/// <summary>
/// <c>bops diagnose "&lt;goal&gt;" --workflow maf</c> (C01-R23): runs the MAF multi-agent troubleshooting workflow and shows what it does
/// as it does it. It is opt-in twice: the command names the workflow, and host configuration must enable it (<see cref="EnabledKey"/>,
/// off by default, never enabled by the shipped configuration). It touches no other command: <c>bops "&lt;goal&gt;"</c> still runs the
/// ordinary runner exactly as before. The run is read-only, terminal and non-resumable.
/// </summary>
internal sealed class MafDiagnoseCommand(
    AgentRunner runner,
    IModelInvocationBroker broker,
    IAuditSink audit,
    TimeProvider time,
    ILogger logger,
    TextWriter output,
    TextWriter error)
{
    private readonly Lock outputGate = new();

    /// <summary>The host configuration key that enables the workflow. Absent or <c>false</c> is the default: disabled.</summary>
    internal const string EnabledKey = "Cli:Experimental:MafWorkflow";

    /// <summary>Exit code of a run that ended on a limit: the report was produced but the investigation was cut short.</summary>
    internal const int LimitReachedExitCode = 4;

    /// <summary>Reads the opt-in strictly: absent or empty is off; anything but a boolean is refused so a typo is never a silent "off" or "on".</summary>
    internal static bool ReadEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[EnabledKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return bool.TryParse(raw, out var enabled)
            ? enabled
            : throw new InvalidOperationException($"'{EnabledKey}' must be 'true' or 'false' (found '{raw}').");
    }

    /// <summary>The limits the invocation asks for over the conservative defaults; the hard maxima still bind.</summary>
    internal static MafWorkflowLimits LimitsFor(DiagnoseInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var defaults = new MafWorkflowLimits();
        return new MafWorkflowLimits
        {
            MaxInvestigationRounds = invocation.MaxRounds ?? defaults.MaxInvestigationRounds,
            MaxToolCalls = invocation.MaxToolCalls ?? defaults.MaxToolCalls,
            MaxTokens = invocation.MaxTokens ?? defaults.MaxTokens,
            MaxDuration = invocation.TimeoutMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : defaults.MaxDuration,
        }.Validate();
    }

    /// <summary>Runs the workflow and returns the process exit code: 0 completed, 4 limit reached, 1 nothing investigated or failed, 130 cancelled.</summary>
    internal async Task<int> RunAsync(DiagnoseInvocation invocation, ActorIdentity actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(actor);

        MafWorkflowLimits limits;
        try
        {
            limits = LimitsFor(invocation);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return 1;
        }

        var workflow = new MafTroubleshootingWorkflow(runner, broker, CliDiagnosticSpecialists.Default(), audit, time, logger, limits);
        await output.WriteLineAsync("Workflow: Microsoft Agent Framework multi-agent troubleshooting (read-only; no operation with effects can run).");
        await output.WriteLineAsync($"Limits: {limits.MaxInvestigationRounds} round(s), {limits.MaxToolCalls} tool call(s), {limits.MaxModelCalls} model call(s), {limits.MaxTokens} tokens, {limits.MaxDuration.TotalMinutes:0.#} min.");
        try
        {
            var report = await workflow.RunAsync(invocation.Goal, actor, Show, ct);
            await WriteReportAsync(report);
            return report.Outcome switch
            {
                MafWorkflowOutcome.Completed => 0,
                MafWorkflowOutcome.LimitReached => LimitReachedExitCode,
                _ => 1,
            };
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Cancelled. The run is terminal and cannot be resumed; start a new one to try again.");
            return 130;
        }
        catch (MafWorkflowFailedException ex)
        {
            await error.WriteLineAsync($"Run {ex.RunId} failed: {ex.Message}");
            return 1;
        }
    }

    private void Show(MafWorkflowEvent evt)
    {
        var round = evt.Round > 0 ? $" [round {evt.Round}]" : string.Empty;
        var line = evt.Kind switch
        {
            MafWorkflowEventKind.Started => $"▶ Workflow started: {evt.Detail}",
            MafWorkflowEventKind.Triaged => $"▶ Triage: {evt.Detail}",
            MafWorkflowEventKind.RoundStarted => $"▶ Investigation round{round}: {evt.Detail}",
            MafWorkflowEventKind.ToolRequested => $"    {evt.Agent}{round} requests {evt.Detail}",
            MafWorkflowEventKind.ToolCompleted => $"    {evt.Agent}{round} ← {evt.Detail}",
            MafWorkflowEventKind.SpecialistCompleted => $"  ✓ {evt.Agent}{round}: {evt.Detail}",
            MafWorkflowEventKind.Reviewed => $"▶ Diagnostic review{round}: {evt.Detail}",
            MafWorkflowEventKind.Finished => $"▶ Finished: {evt.Detail}",
            _ => evt.Detail,
        };
        lock (outputGate)
        {
            output.WriteLine(line);
        }
    }

    private async Task WriteReportAsync(MafDiagnosticReport report)
    {
        await output.WriteLineAsync();
        await output.WriteLineAsync($"=== Diagnosis (run {report.RunId}) — {report.Outcome}{(report.LimitCode is null ? string.Empty : $", limit: {report.LimitCode}")} ===");
        await output.WriteLineAsync(report.Diagnosis);
        await Section("Observations citing run evidence (citations checked, content not verified)", [.. report.CitedObservations.Select(claim => $"{claim.Text}  [{string.Join(", ", claim.EvidenceIds)}]")]);
        await Section("Hypotheses (not proven)", [.. report.Hypotheses.Select(claim => claim.EvidenceIds.Count == 0 ? claim.Text : $"{claim.Text}  [{string.Join(", ", claim.EvidenceIds)}]")]);
        await Section("Contradictions", report.Contradictions);
        await Section("Unsupported interpretations", report.Unsupported);
        await Section("Missing information", report.Missing);
        await Section("Next checks", report.NextChecks);
        await Section("Intervention plan (advisory: nothing here is executed)", report.InterventionPlan);
        await Section("Limitations", report.Limitations);
        await Section("Evidence", [.. report.Evidence.Select(entry => $"{entry.EvidenceId}  {entry.Agent} → {entry.Tool}: {entry.Status}, completeness {entry.Completeness}")]);
        if (report.ExcludedSpecialists.Count > 0)
        {
            await Section("Specialists not used", report.ExcludedSpecialists);
        }

        await output.WriteLineAsync();
        await output.WriteLineAsync(
            $"Used: {report.Rounds} round(s), {report.ToolCalls} tool call(s), {report.ModelCalls} model call(s), {report.Tokens} tokens (known usage). "
            + "Audit events carry task id " + report.RunId + ".");
    }

    private async Task Section(string title, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        await output.WriteLineAsync();
        await output.WriteLineAsync($"{title}:");
        foreach (var line in lines)
        {
            await output.WriteLineAsync($"  - {line}");
        }
    }
}
