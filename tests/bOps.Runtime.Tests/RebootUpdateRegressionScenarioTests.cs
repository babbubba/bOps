// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace bOps.Runtime.Tests;

/// <summary>Recorded, evidence-dependent L7 reasoning; this scenario only diagnoses and never mutates the host.</summary>
public sealed class RebootUpdateRegressionScenarioTests
{
    private static readonly string[] Sequence = ["system.reboot_pending", "system.update_history", "system.updates", "system.events", "system.drivers"];
    private static readonly DateTimeOffset RegressionAt = DateTimeOffset.Parse("2026-09-24T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void PendingRebootAfterKnownInstalledUpdate_IsObservedButNeitherCauseNorGuaranteedFix()
    {
        var pending = RecordedRebootUpdatePlanner.Diagnose(Evidence(reboot: "pending=true; reasons=WindowsUpdate; source=registry; complete=true", history: "timestampUtc=2026-09-24T10:00:00Z; id=KB900001; name=Security Update; result=success; source=windows-update", updates: "items=[]", events: "no matching event", drivers: "items=[]"), RegressionAt);
        var notPending = RecordedRebootUpdatePlanner.Diagnose(Evidence(reboot: "pending=false; reasons=[]; source=registry; complete=true", history: "timestampUtc=2026-09-24T10:00:00Z; id=KB900001; name=Security Update; result=success; source=windows-update", updates: "items=[]", events: "no matching event", drivers: "items=[]"), RegressionAt);

        Assert.Contains(pending.Observed, x => x.Contains("reboot is pending", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pending.Observed, x => x.Contains("recent installed update", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pending.Inferences, x => x.Contains("CORRELATED", StringComparison.Ordinal));
        Assert.DoesNotContain(pending.AllReasoning, x => x.Contains("caused the regression", StringComparison.OrdinalIgnoreCase) || x.Contains("reboot will fix", StringComparison.OrdinalIgnoreCase) || x.Contains("must be rebooted", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(notPending.AllReasoning, x => x.Contains("reboot is pending", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(notPending.Observed, x => x.Contains("recent installed update", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FailedUpdate_IsDistinctFromInstalledUpdate_AndSuccessRemovesFailureConclusion()
    {
        var failed = RecordedRebootUpdatePlanner.Diagnose(Evidence(history: "timestampUtc=2026-09-24T10:00:00Z; id=KB900002; name=Driver Update; result=failure; source=windows-update"), RegressionAt);
        var successful = RecordedRebootUpdatePlanner.Diagnose(Evidence(history: "timestampUtc=2026-09-24T10:00:00Z; id=KB900002; name=Driver Update; result=success; source=windows-update"), RegressionAt);

        Assert.Contains(failed.Observed, x => x.Contains("recent failed update", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(failed.AllReasoning, x => x.Contains("caused the regression", StringComparison.OrdinalIgnoreCase) || x.Contains("corrupted", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(successful.AllReasoning, x => x.Contains("failed update", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PendingInventory_IsNotInstalledHistory_AndChangesWhenInventoryChanges()
    {
        var pending = RecordedRebootUpdatePlanner.Diagnose(Evidence(history: "items=[]", updates: "id=KB900003; name=Pending Security Update; currentVersion=1; availableVersion=2; source=windows-update"), RegressionAt);
        var empty = RecordedRebootUpdatePlanner.Diagnose(Evidence(history: "items=[]", updates: "items=[]"), RegressionAt);

        Assert.Contains(pending.Observed, x => x.Contains("pending/available update", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(pending.AllReasoning, x => x.Contains("installed update", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(empty.AllReasoning, x => x.Contains("pending/available update", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CurrentDriverState_IsNotChangeHistory_AndMatchingEventCreatesOnlyCorrelation()
    {
        var correlated = RecordedRebootUpdatePlanner.Diagnose(Evidence(history: "items=[]", updates: "items=[]", events: "timestampUtc=2026-09-24T11:00:00Z; source=kernel; driver=acme-net; message=driver package installed", drivers: "name=acme-net; loaded=true; version=2.0; source=kernel"), RegressionAt);
        var noEvent = RecordedRebootUpdatePlanner.Diagnose(Evidence(history: "items=[]", updates: "items=[]", events: "no matching event", drivers: "name=acme-net; loaded=true; version=2.0; source=kernel"), RegressionAt);

        Assert.Contains(correlated.Observed, x => x.Contains("loaded driver/module", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(correlated.Inferences, x => x.Contains("driver/module change evidence", StringComparison.OrdinalIgnoreCase) && x.Contains("CORRELATED", StringComparison.Ordinal));
        Assert.Contains(correlated.Inferences, x => x.Contains("not proof that the driver caused the regression", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(noEvent.AllReasoning, x => x.Contains("driver/module change evidence", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(noEvent.AllReasoning, x => x.Contains("changed recently", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MatchingSystemEvent_IsSupportingEvidenceNotRootCause()
    {
        var matching = RecordedRebootUpdatePlanner.Diagnose(Evidence(history: "timestampUtc=2026-09-24T10:00:00Z; id=KB900004; name=Kernel Update; result=success; source=windows-update", updates: "items=[]", events: "timestampUtc=2026-09-24T10:05:00Z; source=windows-update; id=KB900004; message=installation completed", drivers: "items=[]"), RegressionAt);
        var unrelated = RecordedRebootUpdatePlanner.Diagnose(Evidence(history: "timestampUtc=2026-09-24T10:00:00Z; id=KB900004; name=Kernel Update; result=success; source=windows-update", updates: "items=[]", events: "timestampUtc=2026-09-24T10:05:00Z; source=kernel; message=unrelated telemetry", drivers: "items=[]"), RegressionAt);

        Assert.Contains(matching.Inferences, x => x.Contains("matching system event", StringComparison.OrdinalIgnoreCase) && x.Contains("supporting/correlated", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(unrelated.AllReasoning, x => x.Contains("matching system event", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(matching.AllReasoning, x => x.Contains("is the root cause", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData((int)DiagnosticEvidenceState.Partial)]
    [InlineData((int)DiagnosticEvidenceState.Unavailable)]
    [InlineData((int)DiagnosticEvidenceState.Truncated)]
    public void IncompleteHistory_IsNotCompleteEmpty(int stateValue)
    {
        var incomplete = RecordedRebootUpdatePlanner.Diagnose(Evidence(history: "history source unavailable", historyState: (DiagnosticEvidenceState)stateValue), RegressionAt);
        var empty = RecordedRebootUpdatePlanner.Diagnose(Evidence(history: "items=[]"), RegressionAt);

        Assert.Contains(incomplete.Unknown, x => x.Contains("update history cannot be fully established", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(incomplete.AllReasoning, x => x.Contains("no relevant recent update history", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(empty.Observed, x => x.Contains("no relevant recent update history", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData((int)DiagnosticEvidenceState.Partial)]
    [InlineData((int)DiagnosticEvidenceState.Unavailable)]
    [InlineData((int)DiagnosticEvidenceState.Truncated)]
    public void IncompletePendingInventory_IsNotCompleteEmpty(int stateValue)
    {
        var incomplete = RecordedRebootUpdatePlanner.Diagnose(Evidence(updates: "catalog unavailable; catalogAge=unknown", updatesState: (DiagnosticEvidenceState)stateValue), RegressionAt);
        var empty = RecordedRebootUpdatePlanner.Diagnose(Evidence(updates: "items=[]; catalogAge=60"), RegressionAt);

        Assert.Contains(incomplete.Unknown, x => x.Contains("pending-update inventory cannot be fully established", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(incomplete.AllReasoning, x => x.Contains("no pending updates", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(empty.Observed, x => x.Contains("no pending updates observed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecentChangeIsOnlyPlausibleLead_AndOldChangeDoesNotRemainRecent()
    {
        var recent = RecordedRebootUpdatePlanner.Diagnose(Evidence(history: "timestampUtc=2026-09-24T10:00:00Z; id=KB900005; name=Platform Update; result=success; source=windows-update", updates: "items=[]", events: "no matching event", drivers: "items=[]"), RegressionAt);
        var old = RecordedRebootUpdatePlanner.Diagnose(Evidence(history: "timestampUtc=2026-09-20T10:00:00Z; id=KB900005; name=Platform Update; result=success; source=windows-update", updates: "items=[]", events: "no matching event", drivers: "items=[]"), RegressionAt);

        Assert.Contains(recent.Inferences, x => x.Contains("plausible investigation lead", StringComparison.OrdinalIgnoreCase) && x.Contains("causality is not established", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(recent.AllReasoning, x => x.Contains("caused the regression", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(old.AllReasoning, x => x.Contains("recent installed update", StringComparison.OrdinalIgnoreCase) || x.Contains("plausible investigation lead", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CompleteNegativeEvidence_LeavesBoundedNoExplanationConclusion_WithoutRemediation()
    {
        var decision = RecordedRebootUpdatePlanner.Diagnose(Evidence(reboot: "pending=false; reasons=[]; complete=true", history: "items=[]", updates: "items=[]", events: "no matching event", drivers: "items=[]"), RegressionAt);

        Assert.Contains(decision.Inferences, x => x.Contains("No update/reboot/driver-related explanation was established from the available host evidence", StringComparison.Ordinal));
        Assert.Equal(Sequence, decision.Requested);
        Assert.DoesNotContain(string.Join(" ", decision.AllReasoning), "caused the regression", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(decision.Requested, x => x is "system.reboot" or "system.update.install" or "system.update.rollback" or "system.update.remove" or "driver.reload" or "driver.unload");
        Assert.DoesNotContain(string.Join(" ", decision.AllReasoning), "rollback", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(string.Join(" ", decision.AllReasoning), "uninstall", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(string.Join(" ", decision.AllReasoning), "reboot now", StringComparison.OrdinalIgnoreCase);
    }

    private static DiagnosticScenarioHarness Evidence(string reboot = "pending=false; reasons=[]; complete=true", DiagnosticEvidenceState rebootState = DiagnosticEvidenceState.Available, string history = "items=[]", DiagnosticEvidenceState historyState = DiagnosticEvidenceState.Available, string updates = "items=[]", DiagnosticEvidenceState updatesState = DiagnosticEvidenceState.Available, string events = "no matching event", DiagnosticEvidenceState eventsState = DiagnosticEvidenceState.Available, string drivers = "items=[]", DiagnosticEvidenceState driversState = DiagnosticEvidenceState.Available) => new(
        new DiagnosticEvidence("system.reboot_pending", rebootState, reboot),
        new DiagnosticEvidence("system.update_history", historyState, history),
        new DiagnosticEvidence("system.updates", updatesState, updates),
        new DiagnosticEvidence("system.events", eventsState, events),
        new DiagnosticEvidence("system.drivers", driversState, drivers));
}

internal sealed record RebootUpdateDecision(IReadOnlyList<string> Requested, IReadOnlyList<string> Observed, IReadOnlyList<string> Inferences, IReadOnlyList<string> Unknown)
{
    public IReadOnlyList<string> AllReasoning => [.. Observed, .. Inferences, .. Unknown];
}

internal static class RecordedRebootUpdatePlanner
{
    private static readonly TimeSpan RelevantWindow = TimeSpan.FromHours(24);

    public static RebootUpdateDecision Diagnose(DiagnosticScenarioHarness harness, DateTimeOffset regressionAt)
    {
        var observed = new List<string>(); var inferences = new List<string>(); var unknown = new List<string>();
        DiagnosticEvidence Get(string capability) { var evidence = harness.Request(capability); observed.Add($"{capability}: {evidence.Observation} [{evidence.State}]"); return evidence; }
        var reboot = Get("system.reboot_pending");
        var history = Get("system.update_history");
        var updates = Get("system.updates");
        var events = Get("system.events");
        var drivers = Get("system.drivers");

        if (!reboot.IsComplete) unknown.Add("Reboot-pending evidence is incomplete; no reboot-pending conclusion is valid.");
        else if (Has(reboot, "pending=true")) observed.Add("OBSERVED: a reboot is pending according to the reported source; this is neither root-cause proof nor a guaranteed symptom fix.");

        var recentHistory = false;
        if (!history.IsComplete) unknown.Add("Recent installed/failed update history cannot be fully established because system.update_history is incomplete; this does not prove there were no updates.");
        else if (Has(history, "items=[]")) observed.Add("OBSERVED: no relevant recent update history was observed in the queried complete window.");
        else
        {
            recentHistory = IsRecent(history, regressionAt);
            if (Has(history, "result=failure")) observed.Add($"OBSERVED: {(recentHistory ? "recent " : string.Empty)}failed update attempt; timestamp, result, and source are retained as evidence, not a proven symptom cause.");
            if (Has(history, "result=success")) observed.Add($"OBSERVED: {(recentHistory ? "recent " : string.Empty)}installed update history; it is past change evidence, not a pending inventory record.");
        }

        if (!updates.IsComplete) unknown.Add("Pending-update inventory cannot be fully established because system.updates is incomplete; the system cannot be called fully up to date and catalog freshness remains unknown.");
        else if (Has(updates, "items=[]")) observed.Add("OBSERVED: no pending updates observed in the complete inventory at this time.");
        else if (Has(updates, "availableVersion=")) observed.Add("OBSERVED: pending/available update inventory reports a known change; it is not installed-update history and does not explain an existing regression.");

        var matchingEvent = events.IsComplete && !Has(events, "no matching event") && (Has(events, "id=KB") || Has(events, "driver="));
        if (!events.IsComplete) unknown.Add("System-event evidence is incomplete; matching update/reboot/driver events cannot be ruled out.");
        else if (matchingEvent) inferences.Add("Matching system event is supporting/correlated evidence; its time/source/message alignment does not by itself establish root cause.");

        var loadedDriver = drivers.IsComplete && Has(drivers, "loaded=true");
        if (!drivers.IsComplete) unknown.Add("Driver/module visibility is incomplete; current loaded state cannot be fully established.");
        else if (loadedDriver) observed.Add("OBSERVED: current loaded driver/module state is reported; current inventory is not historical change evidence.");

        var explicitDriverChange = loadedDriver && matchingEvent && Has(events, "driver=");
        if (explicitDriverChange) inferences.Add("Explicit driver/module change evidence matches current loaded state: CORRELATED only, not proof that the driver caused the regression.");
        if (recentHistory || explicitDriverChange)
            inferences.Add("Recent system change is a plausible investigation lead / CORRELATED condition; causality is not established and further symptom-specific evidence is required.");

        if (reboot.IsComplete && !Has(reboot, "pending=true") && history.IsComplete && Has(history, "items=[]") && updates.IsComplete && Has(updates, "items=[]") && events.IsComplete && Has(events, "no matching event") && drivers.IsComplete && Has(drivers, "items=[]"))
            inferences.Add("No update/reboot/driver-related explanation was established from the available host evidence; this is bounded by the queried observation window and sources.");
        return new(harness.RequestedCapabilities, observed, inferences, unknown);
    }

    private static bool Has(DiagnosticEvidence evidence, string value) => evidence.Observation.Contains(value, StringComparison.OrdinalIgnoreCase);
    private static bool IsRecent(DiagnosticEvidence evidence, DateTimeOffset regressionAt) => DateTimeOffset.TryParse(Value(evidence.Observation, "timestampUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) && timestamp <= regressionAt && regressionAt - timestamp <= RelevantWindow;
    private static string? Value(string text, string key) { var marker = key + "="; var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase); if (start < 0) return null; start += marker.Length; var end = text.IndexOf(';', start); return text[start..(end < 0 ? text.Length : end)].Trim(); }
}
