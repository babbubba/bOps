// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime.Tests;

/// <summary>Deterministic L2 reasoning over recorded typed host evidence.</summary>
public sealed class ServiceCrashScenarioTests
{
    [Fact]
    public void FailedServiceWithMatchingEventAndCrash_IsCorrelatedAndFixtureDiscriminates()
    {
        var matching = RecordedServiceCrashPlanner.Diagnose(Evidence(
            status: "failed; stopped unexpectedly", events: "service api failure at T1 pid 42",
            crashes: "process api pid 42 crash at T1"));
        var nonmatching = RecordedServiceCrashPlanner.Diagnose(Evidence(
            status: "failed; stopped unexpectedly", events: "service api failure at T1 pid 42",
            crashes: "no matching process"));

        Assert.Contains("service.status", matching.Requested);
        Assert.Contains("system.events", matching.Requested);
        Assert.Contains("system.crashes", matching.Requested);
        Assert.Contains(matching.Correlations, x => x.Contains("matching", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(nonmatching.Correlations, x => x.Contains("matching", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(nonmatching.Unknown, x => x.Contains("identity/time", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.Join(" ", matching.Inferences).Contains("exception", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExitedProcessMetricsRemainUnknownAndPreserveEarlierEvidence()
    {
        var d = RecordedServiceCrashPlanner.Diagnose(Evidence(status: "failed after restart", events: "failure event", crashes: "crash record",
            tree: "previous pid not found; exited", metrics: "pid not found", metricsState: DiagnosticEvidenceState.Unavailable));
        Assert.Contains("system.events", d.Requested);
        Assert.Contains("system.crashes", d.Requested);
        Assert.Contains("process.metrics", d.Requested);
        Assert.Contains(d.Unknown, x => x.Contains("no longer observable", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(d.Observed, x => x.Contains("0 CPU", StringComparison.OrdinalIgnoreCase) || x.Contains("0 memory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnavailableCrashSourceDiffersFromEmptyCompleteSource()
    {
        var complete = RecordedServiceCrashPlanner.Diagnose(Evidence(status: "failed", events: "no matching event", crashes: "no matching crash"));
        var unavailable = RecordedServiceCrashPlanner.Diagnose(Evidence(status: "failed", events: "no matching event", crashState: DiagnosticEvidenceState.Unavailable, crashes: "backend unavailable"));
        Assert.Contains(complete.Unknown, x => x.Contains("no host-side root cause", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(unavailable.Unknown, x => x.Contains("crash confirmation unavailable", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.Join(" ", unavailable.Inferences).Contains("no crash occurred", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(complete.Unknown, unavailable.Unknown);
    }

    [Fact]
    public void CoincidentStorageAndMemoryPressureStayCorrelationsNotCauses()
    {
        var pressured = RecordedServiceCrashPlanner.Diagnose(Evidence(status: "failed", events: "failure event", crashes: "crash record",
            tree: "process found", metrics: "normal", storage: "elevated latency and queue at T1", memory: "high memory pressure", swap: "swap pressure"));
        var clear = RecordedServiceCrashPlanner.Diagnose(Evidence(status: "failed", events: "failure event", crashes: "crash record",
            tree: "process found", metrics: "normal", storage: "normal", memory: "normal", swap: "normal"));
        Assert.Contains(pressured.Correlations, x => x.Contains("storage", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pressured.Correlations, x => x.Contains("memory/swap", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.Join(" ", pressured.Inferences).Contains("caused the crash", StringComparison.OrdinalIgnoreCase) || string.Join(" ", pressured.Inferences).Contains("out of memory", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(clear.Correlations, x => x.Contains("storage", StringComparison.OrdinalIgnoreCase) || x.Contains("memory/swap", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pressured.Unknown, x => x.Contains("application/service-specific", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MemoryAndSwapPressureAloneDoNotEstablishOutOfMemoryCause()
    {
        var d = RecordedServiceCrashPlanner.Diagnose(Evidence(status: "failed", events: "no OOM event", crashes: "no matching crash",
            tree: "process found", metrics: "normal", storage: "normal", memory: "high memory pressure", swap: "swap pressure"));
        Assert.Contains(d.Correlations, x => x.Contains("memory/swap", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(d.Inferences, x => x.Contains("OOM-kill", StringComparison.OrdinalIgnoreCase) || x.Contains("out of memory", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(d.Unknown, x => x.Contains("no host-side root cause", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FailedDependencyDoesNotBecomeTargetCrash()
    {
        var failed = RecordedServiceCrashPlanner.Diagnose(Evidence(status: "stopped", dependency: "database dependency failed"));
        var running = RecordedServiceCrashPlanner.Diagnose(Evidence(status: "stopped", dependency: "database dependency running"));
        Assert.Contains("service.dependencies", failed.Requested);
        Assert.Contains(failed.Inferences, x => x.Contains("dependency state may explain", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(failed.Inferences, x => x.Contains("target binary crashed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(running.Inferences, x => x.Contains("dependency state may explain", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CompleteEvidenceWithoutCauseHandsOffToApplicationLogs()
    {
        var d = RecordedServiceCrashPlanner.Diagnose(Evidence(status: "failed/restarted", events: "no specific cause", crashes: "no specific cause",
            tree: "process found", metrics: "normal", storage: "normal", memory: "normal", swap: "normal"));
        Assert.Contains(d.Unknown, x => x.Contains("no host-side root cause", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(d.Handoff, x => x.Contains("application/domain-specific logs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(d.Inferences, x => x.Contains("internal application", StringComparison.OrdinalIgnoreCase));
    }

    private static DiagnosticScenarioHarness Evidence(
        string status, string events = "no specific cause", string crashes = "no matching crash",
        DiagnosticEvidenceState eventState = DiagnosticEvidenceState.Available,
        DiagnosticEvidenceState crashState = DiagnosticEvidenceState.Available,
        string dependency = "required dependency running", string? tree = null, string? metrics = null,
        DiagnosticEvidenceState metricsState = DiagnosticEvidenceState.Available,
        string? storage = null, string? memory = null, string? swap = null)
    {
        var items = new List<DiagnosticEvidence>
        {
            DiagnosticScenarioHarness.Available("service.status", status),
            DiagnosticScenarioHarness.Available("service.config", "configuration observed"),
            DiagnosticScenarioHarness.Available("service.dependencies", dependency),
            new("system.events", eventState, events), new("system.crashes", crashState, crashes),
        };
        if (tree is not null) items.Add(DiagnosticScenarioHarness.Available("process.tree", tree));
        if (metrics is not null) items.Add(new("process.metrics", metricsState, metrics));
        else if (tree is not null) items.Add(DiagnosticScenarioHarness.Available("process.metrics", "normal sampled metrics"));
        if (storage is not null) items.Add(DiagnosticScenarioHarness.Available("storage.io", storage));
        if (memory is not null) items.Add(DiagnosticScenarioHarness.Available("system.memory", memory));
        if (swap is not null) items.Add(DiagnosticScenarioHarness.Available("system.swap", swap));
        return new(items.ToArray());
    }
}

internal sealed record ServiceCrashDecision(IReadOnlyList<string> Requested, IReadOnlyList<string> Observed,
    IReadOnlyList<string> Inferences, IReadOnlyList<string> Correlations, IReadOnlyList<string> Unknown, IReadOnlyList<string> Handoff);

internal static class RecordedServiceCrashPlanner
{
    public static ServiceCrashDecision Diagnose(DiagnosticScenarioHarness h)
    {
        var observed = new List<string>(); var inferred = new List<string>(); var correlations = new List<string>(); var unknown = new List<string>(); var handoff = new List<string>();
        DiagnosticEvidence Get(string name) { var e = h.Request(name); observed.Add($"{name}: {e.Observation} [{e.State}]"); if (!e.IsComplete) unknown.Add($"{name} evidence is {e.State}"); return e; }
        var status = Get("service.status"); Get("service.config"); var deps = Get("service.dependencies");
        var events = Get("system.events"); var crashes = Get("system.crashes");
        if (status.Observation.Contains("failed", StringComparison.OrdinalIgnoreCase) || status.Observation.Contains("stopped", StringComparison.OrdinalIgnoreCase))
            inferred.Add("Target service failure is observed in service.status.");
        if (deps.Observation.Contains("dependency", StringComparison.OrdinalIgnoreCase) && deps.Observation.Contains("failed", StringComparison.OrdinalIgnoreCase))
            inferred.Add("Dependency state may explain target startup/runtime failure; this does not establish target binary crash.");
        var eventMatch = events.IsComplete && events.Observation.Contains("failure", StringComparison.OrdinalIgnoreCase) && events.Observation.Contains("T1", StringComparison.OrdinalIgnoreCase);
        var crashMatch = crashes.IsComplete && crashes.Observation.Contains("crash", StringComparison.OrdinalIgnoreCase) && crashes.Observation.Contains("T1", StringComparison.OrdinalIgnoreCase);
        if (eventMatch && crashMatch) correlations.Add("Matching event/crash identity and time provide strong correlated evidence of abnormal termination; internal cause is not established.");
        else unknown.Add("Event/crash identity/time correlation does not establish a matching cause.");
        if (!events.IsComplete) unknown.Add("Event confirmation is incomplete.");
        if (!crashes.IsComplete) unknown.Add("Crash confirmation unavailable; absence of a record cannot establish that no crash occurred.");

        if (h.HasNext("process.tree")) { var tree = Get("process.tree"); if (tree.Observation.Contains("not found", StringComparison.OrdinalIgnoreCase) || tree.Observation.Contains("exited", StringComparison.OrdinalIgnoreCase)) unknown.Add("Previous process has exited and is no longer observable."); }
        if (h.HasNext("process.metrics")) { var metrics = Get("process.metrics"); if (!metrics.IsComplete) unknown.Add("Live process metrics are unavailable because the process is no longer observable; unavailable is not zero or normal."); }
        if (h.HasNext("storage.io")) { var storage = Get("storage.io"); if (storage.Observation.Contains("elevated", StringComparison.OrdinalIgnoreCase) || storage.Observation.Contains("pressure", StringComparison.OrdinalIgnoreCase)) correlations.Add("Storage pressure is correlated in the observation window and is only a possible contributing condition, not established cause."); }
        if (h.HasNext("system.memory")) { var memory = Get("system.memory"); var swap = h.HasNext("system.swap") ? Get("system.swap") : null; if (memory.Observation.Contains("pressure", StringComparison.OrdinalIgnoreCase) || (swap?.Observation.Contains("pressure", StringComparison.OrdinalIgnoreCase) ?? false)) correlations.Add("Memory/swap pressure is host-level correlated evidence and only a possible contributing condition; no OOM cause is established."); }
        if (correlations.Count == 0 || (!h.HasNext("process.tree") && !h.HasNext("process.metrics"))) { }
        if (!observed.Any(x => x.Contains("root cause", StringComparison.OrdinalIgnoreCase)) && !events.Observation.Contains("cause established", StringComparison.OrdinalIgnoreCase) && !crashes.Observation.Contains("cause established", StringComparison.OrdinalIgnoreCase))
        { unknown.Add("No host-side root cause is established by the available evidence."); handoff.Add("Continue with application/domain-specific logs or Skills."); }
        if (observed.Any(x => x.Contains("storage.io:", StringComparison.Ordinal))) unknown.Add("Application/service-specific evidence is needed to establish whether host pressure contributed causally.");
        return new(h.RequestedCapabilities, observed, inferred, correlations, unknown.Distinct().ToArray(), handoff);
    }
}
