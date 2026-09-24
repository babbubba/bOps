// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime.Tests;

/// <summary>Deterministic L3 reasoning over host performance evidence; it deliberately has no DB-native diagnosis.</summary>
public sealed class DbApplicationSlowScenarioTests
{
    private static readonly string[] FullSequence =
    [
        "system.cpu", "system.memory", "system.swap", "process.metrics", "storage.io", "storage.health",
        "system.events", "network.interface_stats",
    ];

    [Fact]
    public void CpuPressure_WithRelevantProcessCpu_IsObservedAndCorrelated_NotProvenCause()
    {
        var pressured = RecordedDbApplicationSlowPlanner.Diagnose(Evidence(cpu: "sustained high CPU pressure", process: "database pid 42 uses substantial CPU"));
        var clear = RecordedDbApplicationSlowPlanner.Diagnose(Evidence(cpu: "no material CPU pressure", process: "database pid 42 normal CPU"));

        Assert.Equal(FullSequence, pressured.Requested);
        Assert.Contains(pressured.Observed, x => x.Contains("CPU pressure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pressured.Correlations, x => x.Contains("process CPU", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pressured.PossibleContributors, x => x.Contains("CPU", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(clear.Observed, x => x.Contains("CPU pressure", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(clear.PossibleContributors, x => x.Contains("CPU", StringComparison.OrdinalIgnoreCase));
        AssertNoFabricatedCause(pressured);
    }

    [Fact]
    public void MemoryAndSwapPressure_ChangesWhenRemoved_AndDoesNotClaimLeakOrOom()
    {
        var pressured = RecordedDbApplicationSlowPlanner.Diagnose(Evidence(memory: "low available memory; high memory pressure", swap: "swap usage and activity high", process: "database pid 42 substantial memory"));
        var clear = RecordedDbApplicationSlowPlanner.Diagnose(Evidence(memory: "no material memory pressure", swap: "no material swap pressure", process: "database pid 42 normal memory"));

        Assert.Contains(pressured.Observed, x => x.Contains("memory/swap pressure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pressured.Correlations, x => x.Contains("process memory", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pressured.PossibleContributors, x => x.Contains("memory/swap", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(clear.PossibleContributors, x => x.Contains("memory/swap", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pressured.PossibleContributors, x => x.Contains("neither memory leak nor OOM is established", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StoragePressure_WithHealthyDevice_RemainsPossibleContributor_NotDeviceFailure()
    {
        var pressured = RecordedDbApplicationSlowPlanner.Diagnose(Evidence(storage: "high latency and queue pressure", health: "device health reports no hardware fault", events: "storage timeout event in observation window"));
        var clear = RecordedDbApplicationSlowPlanner.Diagnose(Evidence(storage: "no material latency or queue pressure", health: "device health reports no hardware fault"));

        Assert.Contains(pressured.Observed, x => x.Contains("storage I/O pressure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pressured.Observed, x => x.Contains("no hardware fault", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pressured.Correlations, x => x.Contains("storage event", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pressured.PossibleContributors, x => x.Contains("storage", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(clear.PossibleContributors, x => x.Contains("storage", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pressured.Observed, x => x.Contains("no hardware fault", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NetworkErrors_AreCorrelatedEvidence_AndDisappearWhenCountersAreClear()
    {
        var degraded = RecordedDbApplicationSlowPlanner.Diagnose(Evidence(network: "relevant interface receive errors and drops observed"));
        var clear = RecordedDbApplicationSlowPlanner.Diagnose(Evidence(network: "no material interface errors or drops"));

        Assert.Contains(degraded.Observed, x => x.Contains("network-interface degradation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(degraded.PossibleContributors, x => x.Contains("network", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(clear.PossibleContributors, x => x.Contains("network", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("network caused", string.Join(" ", degraded.AllReasoning), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompleteNormalHostEvidence_EstablishesNoHostBottleneck_AndHandsOffToDbSkill()
    {
        var decision = RecordedDbApplicationSlowPlanner.Diagnose(Evidence());

        Assert.Equal(DbApplicationSlowCategory.NoHostBottleneckEstablished, decision.Category);
        Assert.Contains("No host-side bottleneck was established by the available evidence.", decision.Inferences);
        Assert.Contains(decision.Handoff, x => x.Contains("postgres.stat_activity", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(decision.Handoff, x => x.Contains("sqlserver.wait_stats", StringComparison.OrdinalIgnoreCase));
        var wording = string.Join(" ", decision.AllReasoning);
        Assert.DoesNotContain("host is healthy", wording, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("database is healthy", wording, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("database is the root cause", wording, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IncompleteEvidence_IsNotNormalComplete_AndMissingProcessIsNotZero()
    {
        var complete = RecordedDbApplicationSlowPlanner.Diagnose(Evidence());
        var incomplete = RecordedDbApplicationSlowPlanner.Diagnose(Evidence(
            process: "process exited before sample", processState: DiagnosticEvidenceState.Unavailable,
            health: "storage health backend unavailable", healthState: DiagnosticEvidenceState.Unavailable,
            network: "interface counters unavailable", networkState: DiagnosticEvidenceState.Unavailable));

        Assert.Equal(DbApplicationSlowCategory.NoHostBottleneckEstablished, complete.Category);
        Assert.Equal(DbApplicationSlowCategory.EvidenceIncomplete, incomplete.Category);
        Assert.Contains(incomplete.Unknown, x => x.Contains("process.metrics", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(incomplete.Unknown, x => x.Contains("storage.health", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(incomplete.Unknown, x => x.Contains("network.interface_stats", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(incomplete.Observed, x => x.Contains("0 CPU", StringComparison.OrdinalIgnoreCase) || x.Contains("0 memory", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(incomplete.Inferences, x => x.Contains("no host-side bottleneck", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertNoFabricatedCause(DbApplicationSlowDecision decision)
    {
        var wording = string.Join(" ", decision.AllReasoning);
        Assert.DoesNotContain("CPU caused the slowdown", wording, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("database is slow because of locks", wording, StringComparison.OrdinalIgnoreCase);
    }

    private static DiagnosticScenarioHarness Evidence(
        string cpu = "no material CPU pressure", string memory = "no material memory pressure", string swap = "no material swap pressure",
        string process = "no obvious host-resource hotspot", string storage = "no material latency or queue pressure",
        string health = "no relevant hardware fault evidence", string events = "no matching host failure evidence",
        string network = "no material interface errors or drops", DiagnosticEvidenceState processState = DiagnosticEvidenceState.Available,
        DiagnosticEvidenceState healthState = DiagnosticEvidenceState.Available, DiagnosticEvidenceState networkState = DiagnosticEvidenceState.Available) => new(
            DiagnosticScenarioHarness.Available("system.cpu", cpu),
            DiagnosticScenarioHarness.Available("system.memory", memory),
            DiagnosticScenarioHarness.Available("system.swap", swap),
            new DiagnosticEvidence("process.metrics", processState, process),
            DiagnosticScenarioHarness.Available("storage.io", storage),
            new DiagnosticEvidence("storage.health", healthState, health),
            DiagnosticScenarioHarness.Available("system.events", events),
            new DiagnosticEvidence("network.interface_stats", networkState, network));
}

internal enum DbApplicationSlowCategory { HostPressureObserved, NoHostBottleneckEstablished, EvidenceIncomplete }

internal sealed record DbApplicationSlowDecision(
    DbApplicationSlowCategory Category, IReadOnlyList<string> Requested, IReadOnlyList<string> Observed,
    IReadOnlyList<string> Correlations, IReadOnlyList<string> PossibleContributors, IReadOnlyList<string> Inferences,
    IReadOnlyList<string> Unknown, IReadOnlyList<string> Handoff)
{
    public IEnumerable<string> AllReasoning => Observed.Concat(Correlations).Concat(PossibleContributors).Concat(Inferences).Concat(Unknown).Concat(Handoff);
}

internal static class RecordedDbApplicationSlowPlanner
{
    public static DbApplicationSlowDecision Diagnose(DiagnosticScenarioHarness h)
    {
        var observed = new List<string>(); var correlations = new List<string>(); var possible = new List<string>();
        var inferences = new List<string>(); var unknown = new List<string>(); var handoff = new List<string>();
        DiagnosticEvidence Get(string name)
        {
            var evidence = h.Request(name);
            if (!evidence.IsComplete) unknown.Add($"{name} evidence is {evidence.State.ToString().ToLowerInvariant()}; this domain cannot be ruled out from host evidence.");
            return evidence;
        }

        var cpu = Get("system.cpu");
        var memory = Get("system.memory");
        var swap = Get("system.swap");
        var process = Get("process.metrics");
        var storage = Get("storage.io");
        var health = Get("storage.health");
        var events = Get("system.events");
        var network = Get("network.interface_stats");

        if (Has(cpu, "high CPU pressure")) { observed.Add("Host CPU pressure is observed."); possible.Add("CPU pressure is a plausible host-side contributing condition, not a proven cause of DB/application slowness."); }
        if (IsMemoryOrSwapPressured(memory, swap)) { observed.Add("Host memory/swap pressure is observed."); possible.Add("Memory/swap pressure is a possible host-side contributing condition; neither memory leak nor OOM is established."); }
        if (!process.IsComplete) unknown.Add("Live process evidence is unavailable; a missing process is not 0 CPU, 0 memory, or a healthy process.");
        else if (Has(process, "substantial CPU") && Has(cpu, "high CPU pressure")) correlations.Add("Relevant process CPU usage strengthens CPU correlation; the operation causing load is unknown.");
        else if (Has(process, "substantial memory") && (Has(memory, "pressure") || Has(swap, "pressure") || Has(swap, "activity high"))) correlations.Add("Relevant process memory usage strengthens memory correlation; a memory leak is not established.");
        if (!Has(storage, "no material") && (Has(storage, "high latency") || Has(storage, "queue pressure"))) { observed.Add("Storage I/O pressure is observed."); possible.Add("Storage latency/queue pressure is a possible host-side contributing condition, not a DB query root cause."); }
        if (health.IsComplete && Has(health, "no hardware fault")) observed.Add("Storage health reports no hardware fault; this does not erase observed I/O pressure.");
        if (Has(events, "storage") && (Has(storage, "high latency") || Has(storage, "queue pressure"))) correlations.Add("Relevant storage event strengthens storage correlation but does not prove performance causality.");
        if (!Has(network, "no material") && (Has(network, "errors") || Has(network, "drops"))) { observed.Add("Network-interface degradation is observed from local counters."); possible.Add("Network-interface degradation is correlated/possible contributing evidence; remote network and database protocol health are unknown."); }

        var hasPressure = possible.Count != 0;
        if (!hasPressure && unknown.Count == 0)
        {
            inferences.Add("No host-side bottleneck was established by the available evidence.");
            handoff.Add("Host telemetry ends here: future DB Skills add typed DB-native evidence such as postgres.stat_activity, postgres.locks, postgres.vacuum, sqlserver.wait_stats, sqlserver.blocking, and sqlserver.query_store.");
        }
        else if (!hasPressure)
            handoff.Add("Incomplete host evidence prevents ruling out every host domain; DB-native diagnostics remain a separate future Skill boundary.");
        else
            handoff.Add("Host pressure is not a proven root cause; DB-native waits, locks, query, and application-specific evidence remain unknown.");

        var category = unknown.Count != 0 ? DbApplicationSlowCategory.EvidenceIncomplete : hasPressure ? DbApplicationSlowCategory.HostPressureObserved : DbApplicationSlowCategory.NoHostBottleneckEstablished;
        return new(category, h.RequestedCapabilities, observed, correlations, possible, inferences, unknown, handoff);
    }

    private static bool Has(DiagnosticEvidence evidence, string value) => evidence.Observation.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static bool IsMemoryOrSwapPressured(DiagnosticEvidence memory, DiagnosticEvidence swap) =>
        !Has(memory, "no material") && (Has(memory, "high memory pressure") || Has(memory, "low available memory"))
        || !Has(swap, "no material") && (Has(swap, "swap pressure") || Has(swap, "activity high"));
}
