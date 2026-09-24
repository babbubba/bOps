// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime.Tests;

/// <summary>Recorded, evidence-dependent L6 reasoning; scheduler payloads are never executed.</summary>
public sealed class ScheduledJobScenarioTests
{
    [Fact]
    public void DisabledSchedulerItem_IsObservedWithoutAnExecutionFailure_AndDisappearsWhenEnabled()
    {
        var disabled = RecordedScheduledJobPlanner.Diagnose(new DiagnosticScenarioHarness(
            DiagnosticScenarioHarness.Available("scheduler.inspect", "id=nightly; enabled=false; command=backup.exe")));
        var enabled = RecordedScheduledJobPlanner.Diagnose(Evidence(inspect: "id=nightly; enabled=true; command=backup.exe", history: "complete=true; window=60m; matchingRuns=[]"));

        Assert.Contains(disabled.Inferences, x => x.Contains("scheduled item is disabled", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(disabled.AllReasoning, x => x.Contains("execution was attempted and failed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(enabled.AllReasoning, x => x.Contains("scheduled item is disabled", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(disabled.Requested, x => x is "scheduler.enable" or "scheduler.disable" or "process.start");
    }

    [Fact]
    public void FailedHistoryAttempt_IsDistinctFromNoStart_AndSuccessRemovesFailure()
    {
        var failed = RecordedScheduledJobPlanner.Diagnose(Evidence(
            inspect: "id=nightly; enabled=true; command=backup.exe",
            history: "complete=true; window=60m; matchingRun timestamp=2026-09-24T10:00:00Z; result=failed; source=task-scheduler"));
        var successful = RecordedScheduledJobPlanner.Diagnose(Evidence(
            inspect: "id=nightly; enabled=true; command=backup.exe",
            history: "complete=true; window=60m; matchingRun timestamp=2026-09-24T10:00:00Z; result=success; source=task-scheduler"));

        Assert.Contains(failed.Inferences, x => x.Contains("execution was attempted and failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(failed.Observed, x => x.Contains("timestamp=2026-09-24T10:00:00Z", StringComparison.Ordinal));
        Assert.DoesNotContain(failed.Inferences, x => x.Contains("did not start", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(successful.AllReasoning, x => x.Contains("execution was attempted and failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TargetServiceState_IsSeparateFromSchedulerState_AndDisappearsWhenRunning()
    {
        var stopped = RecordedScheduledJobPlanner.Diagnose(Evidence(
            inspect: "id=nightly; enabled=true; targetService=backup-agent",
            history: "complete=true; window=60m; matchingRuns=[]",
            serviceStatus: "name=backup-agent; exists=true; status=stopped",
            serviceConfig: "name=backup-agent; enabled=false"));
        var running = RecordedScheduledJobPlanner.Diagnose(Evidence(
            inspect: "id=nightly; enabled=true; targetService=backup-agent",
            history: "complete=true; window=60m; matchingRuns=[]",
            serviceStatus: "name=backup-agent; exists=true; status=running",
            serviceConfig: "name=backup-agent; enabled=true"));

        Assert.Contains(stopped.Inferences, x => x.Contains("target service is stopped or disabled", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(stopped.AllReasoning, x => x.Contains("scheduler is broken", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(running.AllReasoning, x => x.Contains("target service is stopped or disabled", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(stopped.Requested, x => x is "service.start" or "service.restart" or "service.enable");
    }

    [Fact]
    public void IncompleteIdentityEvidence_IsNotIdentityAbsence_AndCompleteAbsenceIsDifferent()
    {
        var partial = RecordedScheduledJobPlanner.Diagnose(Evidence(
            inspect: "id=nightly; enabled=true; runAs=svc-backup",
            history: "complete=true; window=60m; matchingRuns=[]",
            users: "visibility restricted; rows may be missing", usersState: DiagnosticEvidenceState.Partial));
        var absent = RecordedScheduledJobPlanner.Diagnose(Evidence(
            inspect: "id=nightly; enabled=true; runAs=svc-backup",
            history: "complete=true; window=60m; matchingRuns=[]",
            users: "complete=true; user svc-backup not found"));

        Assert.Contains(partial.Unknown, x => x.Contains("Identity evidence is incomplete", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(partial.AllReasoning, x => x.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(absent.Inferences, x => x.Contains("not found in the complete identity result", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(partial.AllReasoning, absent.AllReasoning);
    }

    [Fact]
    public void PermissionEvidence_IsCorrelatedNotProvenCause_AndDisappearsWithSufficientAccess()
    {
        var denied = RecordedScheduledJobPlanner.Diagnose(Evidence(
            inspect: "id=nightly; enabled=true; runAs=svc-backup; path=C:\\jobs\\backup.ps1",
            history: "complete=true; window=60m; matchingRuns=[]",
            permissions: "path=C:\\jobs\\backup.ps1; execution identity svc-backup denied read/execute access"));
        var access = RecordedScheduledJobPlanner.Diagnose(Evidence(
            inspect: "id=nightly; enabled=true; runAs=svc-backup; path=C:\\jobs\\backup.ps1",
            history: "complete=true; window=60m; matchingRuns=[]",
            permissions: "path=C:\\jobs\\backup.ps1; execution identity svc-backup has sufficient observed access"));

        Assert.Contains(denied.Inferences, x => x.Contains("possible explanatory or correlated condition", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(denied.AllReasoning, x => x.Contains("permissions caused", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(access.AllReasoning, x => x.Contains("possible explanatory or correlated condition", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(denied.Requested, x => x is "fs.chmod" or "fs.chown" or "process.start");
    }

    [Fact]
    public void UnavailableHistory_IsNotCompleteNoMatch()
    {
        var unavailable = RecordedScheduledJobPlanner.Diagnose(Evidence(
            inspect: "id=nightly; enabled=true; command=backup.exe",
            history: "history backend unavailable", historyState: DiagnosticEvidenceState.Unavailable));
        var empty = RecordedScheduledJobPlanner.Diagnose(Evidence(
            inspect: "id=nightly; enabled=true; command=backup.exe",
            history: "complete=true; window=60m; matchingRuns=[]"));

        Assert.Contains(unavailable.Unknown, x => x.Contains("Execution history cannot be established", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(unavailable.AllReasoning, x => x.Contains("no matching execution was observed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(empty.Inferences, x => x.Contains("no matching execution was observed in the queried complete history window", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(empty.Unknown, x => x.Contains("Execution history cannot be established", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SystemEventCorrelation_StrengthensMatchingFailure_AndDisappearsWhenRemoved()
    {
        var correlated = RecordedScheduledJobPlanner.Diagnose(Evidence(
            inspect: "id=nightly; enabled=true; command=backup.exe",
            history: "complete=true; window=60m; matchingRun timestamp=2026-09-24T10:00:00Z; result=failed; source=task-scheduler",
            events: "complete=true; matching event timestamp=2026-09-24T10:00:01Z; source=task-scheduler; access denied"));
        var unrelated = RecordedScheduledJobPlanner.Diagnose(Evidence(
            inspect: "id=nightly; enabled=true; command=backup.exe",
            history: "complete=true; window=60m; matchingRun timestamp=2026-09-24T10:00:00Z; result=failed; source=task-scheduler",
            events: "complete=true; no matching event"));

        Assert.Contains(correlated.Inferences, x => x.Contains("matching system-event evidence correlates", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(unrelated.AllReasoning, x => x.Contains("matching system-event evidence correlates", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(correlated.Inferences, x => x.Contains("proves an unrelated host event caused", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoRootCauseEstablished_PreservesUnknownAndNeverRecommendsRemediation()
    {
        var decision = RecordedScheduledJobPlanner.Diagnose(Evidence(
            inspect: "id=nightly; enabled=true; runAs=svc-backup; targetService=backup-agent; path=C:\\jobs\\backup.ps1",
            history: "complete=true; window=60m; matchingRun timestamp=2026-09-24T10:00:00Z; result=failed; source=task-scheduler",
            current: "complete=true; identity=svc-backup", users: "complete=true; user svc-backup found", groups: "complete=true; required group BackupOperators contains svc-backup", sessions: "complete=true; session evidence has no relevant concern",
            serviceStatus: "name=backup-agent; exists=true; status=running", serviceConfig: "name=backup-agent; enabled=true",
            events: "complete=true; no matching event", permissions: "path=C:\\jobs\\backup.ps1; execution identity svc-backup has sufficient observed access"));

        Assert.Contains(decision.Unknown, x => x.Contains("does not establish why", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(string.Join(" ", decision.AllReasoning), "run the command manually", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(string.Join(" ", decision.AllReasoning), "restart", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(string.Join(" ", decision.AllReasoning), "enable the scheduled", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(decision.Requested, x => x is "scheduler.enable" or "service.start" or "service.restart" or "process.start");
    }

    private static DiagnosticScenarioHarness Evidence(string inspect, string history, DiagnosticEvidenceState historyState = DiagnosticEvidenceState.Available, string? current = null, string? users = null, DiagnosticEvidenceState usersState = DiagnosticEvidenceState.Available, string? groups = null, string? sessions = null, string? serviceStatus = null, string? serviceConfig = null, string? events = null, string? permissions = null)
    {
        var evidence = new List<DiagnosticEvidence> { DiagnosticScenarioHarness.Available("scheduler.inspect", inspect), new("scheduler.history", historyState, history) };
        if (current is not null) evidence.Add(DiagnosticScenarioHarness.Available("identity.current", current));
        if (users is not null) evidence.Add(new("identity.users", usersState, users));
        if (groups is not null) evidence.Add(DiagnosticScenarioHarness.Available("identity.groups", groups));
        if (sessions is not null) evidence.Add(DiagnosticScenarioHarness.Available("identity.sessions", sessions));
        if (serviceStatus is not null) evidence.Add(DiagnosticScenarioHarness.Available("service.status", serviceStatus));
        if (serviceConfig is not null) evidence.Add(DiagnosticScenarioHarness.Available("service.config", serviceConfig));
        if (events is not null) evidence.Add(DiagnosticScenarioHarness.Available("system.events", events));
        if (permissions is not null) evidence.Add(DiagnosticScenarioHarness.Available("fs.permissions", permissions));
        return new DiagnosticScenarioHarness(evidence.ToArray());
    }
}

internal sealed record ScheduledJobDecision(IReadOnlyList<string> Requested, IReadOnlyList<string> Observed, IReadOnlyList<string> Inferences, IReadOnlyList<string> Unknown)
{
    public IReadOnlyList<string> AllReasoning => [.. Observed, .. Inferences, .. Unknown];
}

internal static class RecordedScheduledJobPlanner
{
    public static ScheduledJobDecision Diagnose(DiagnosticScenarioHarness harness)
    {
        var observed = new List<string>(); var inferences = new List<string>(); var unknown = new List<string>();
        DiagnosticEvidence Get(string capability) { var e = harness.Request(capability); observed.Add($"{capability}: {e.Observation} [{e.State}]"); return e; }
        static bool Has(DiagnosticEvidence e, string value) => e.Observation.Contains(value, StringComparison.OrdinalIgnoreCase);
        var inspect = Get("scheduler.inspect");
        if (!inspect.IsComplete) { unknown.Add("Scheduler configuration/state could not be established because scheduler.inspect is incomplete."); return Done(); }
        if (Has(inspect, "enabled=false")) { inferences.Add("The scheduled item is disabled; this is observed scheduler state, distinct from execution-result and target-state evidence."); return Done(); }
        if (!Has(inspect, "enabled=true")) { unknown.Add("Scheduler enablement is not definite; no execution conclusion is valid."); return Done(); }
        var history = Get("scheduler.history");
        var failedAttempt = history.IsComplete && Has(history, "result=failed");
        if (!history.IsComplete) unknown.Add("Execution history cannot be established because scheduler.history is incomplete or unavailable; this does not prove the task never ran.");
        else if (failedAttempt) inferences.Add("Scheduler history establishes that execution was attempted and failed; timestamp, result, and source remain observed evidence rather than an invented internal cause.");
        else if (Has(history, "matchingRuns=[]")) inferences.Add("No matching execution was observed in the queried complete history window; that bounded result does not establish the task never ran.");

        if (Has(inspect, "runAs=") && harness.HasNext("identity.current"))
        {
            Get("identity.current");
            if (harness.HasNext("identity.users"))
            {
                var users = Get("identity.users");
                if (!users.IsComplete) unknown.Add("Identity evidence is incomplete; execution-account absence, group membership, and session state are not inferred.");
                else if (Has(users, "not found")) inferences.Add("The configured execution identity was not found in the complete identity result; this does not establish why the job did not run.");
            }
            if (harness.HasNext("identity.groups")) Get("identity.groups");
            if (harness.HasNext("identity.sessions")) Get("identity.sessions");
        }
        else if (Has(inspect, "runAs=") && harness.HasNext("identity.users"))
        {
            var users = Get("identity.users");
            if (!users.IsComplete) unknown.Add("Identity evidence is incomplete; execution-account absence, group membership, and session state are not inferred.");
            else if (Has(users, "not found")) inferences.Add("The configured execution identity was not found in the complete identity result; this does not establish why the job did not run.");
        }
        if (Has(inspect, "targetService=") && harness.HasNext("service.status"))
        {
            var status = Get("service.status");
            var config = harness.HasNext("service.config") ? Get("service.config") : null;
            if (Has(status, "status=stopped") || Has(status, "status=failed") || (config is not null && Has(config, "enabled=false")))
                inferences.Add("The target service is stopped or disabled; this is separate target-service evidence, independent of scheduler health.");
        }
        if (harness.HasNext("system.events"))
        {
            var events = Get("system.events");
            if (failedAttempt && events.IsComplete && Has(events, "matching event") && !Has(events, "no matching event")) inferences.Add("Matching system-event evidence correlates with the observed failed scheduler attempt; correlation alone does not prove an unrelated host event caused it.");
        }
        if (Has(inspect, "path=") && harness.HasNext("fs.permissions"))
        {
            var permissions = Get("fs.permissions");
            if (permissions.IsComplete && Has(permissions, "denied")) inferences.Add("Observed permission/access evidence is a possible explanatory or correlated condition; it is not a proven scheduler-failure cause without matching execution/event evidence.");
        }
        if (failedAttempt && !inferences.Any(x => x.Contains("possible explanatory", StringComparison.OrdinalIgnoreCase) || x.Contains("correlates", StringComparison.OrdinalIgnoreCase)) && !unknown.Any(x => x.Contains("Identity", StringComparison.OrdinalIgnoreCase)))
            unknown.Add("Host evidence does not establish why the scheduled job failed/did not execute; collect further domain or application-specific evidence.");
        return Done();

        ScheduledJobDecision Done() => new(harness.RequestedCapabilities, observed, inferences, unknown);
    }
}
