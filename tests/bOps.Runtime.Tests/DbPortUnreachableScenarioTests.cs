// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime.Tests;

/// <summary>Recorded-model L1 decisions for host evidence around database TCP ports, not DB-native diagnosis.</summary>
public sealed class DbPortUnreachableScenarioTests
{
    private static readonly string[] FullSequence =
    [
        "network.dns_query", "network.routes", "network.sockets", "firewall.status", "firewall.rules",
        "service.status", "service.config", "system.events", "network.port_check",
    ];

    [Fact]
    public void NameResolutionFailure_PreservesDownstreamPathAsUnknown()
    {
        var harness = new DiagnosticScenarioHarness(DiagnosticScenarioHarness.Unavailable("network.dns_query", "hostname resolution unavailable"));

        var decision = RecordedDbPortPlanner.Diagnose(harness);

        Assert.Equal(DbPortReasoningCategory.NameResolutionFailure, decision.Category);
        Assert.Equal(["network.dns_query"], harness.RequestedCapabilities);
        Assert.Contains("hostname resolution unavailable", decision.Observed);
        Assert.Contains("route and downstream network path", decision.Unknown);
        Assert.DoesNotContain(decision.Inferences, x => x.Contains("firewall", StringComparison.OrdinalIgnoreCase) || x.Contains("service", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RouteAbsent_DoesNotImplyServiceOrRemoteFirewallFailure()
    {
        var harness = new DiagnosticScenarioHarness(
            DiagnosticScenarioHarness.Available("network.dns_query", "target resolves to 10.0.0.8"),
            DiagnosticScenarioHarness.Available("network.routes", "no usable route to 10.0.0.8"));

        var decision = RecordedDbPortPlanner.Diagnose(harness);

        Assert.Equal(DbPortReasoningCategory.RouteUnavailable, decision.Category);
        Assert.Equal(["network.dns_query", "network.routes"], harness.RequestedCapabilities);
        Assert.Contains("no usable route to 10.0.0.8", decision.Observed);
        Assert.Contains("listener, service, and remote firewall state", decision.Unknown);
    }

    [Fact]
    public void NoLocalListener_IsDistinctFromServiceStopped()
    {
        var decision = RecordedDbPortPlanner.Diagnose(FullEvidence(
            sockets: "no local listener observed on 5432",
            status: "service status running",
            port: "TCP port unreachable"));

        Assert.Equal(DbPortReasoningCategory.NoLocalListener, decision.Category);
        Assert.Contains("no local listener observed on 5432", decision.Observed);
        Assert.DoesNotContain(decision.Inferences, x => x.Contains("service stopped", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(FullSequence, decision.Requested);
    }

    [Fact]
    public void UnexpectedBind_IsNotLocalFirewallRootCause()
    {
        var decision = RecordedDbPortPlanner.Diagnose(FullEvidence(
            sockets: "listener on 1433 bound only to 127.0.0.1; expected endpoint is 10.0.0.8",
            status: "service status running",
            port: "TCP port unreachable"));

        Assert.Equal(DbPortReasoningCategory.UnexpectedBind, decision.Category);
        Assert.Contains("listener on 1433 bound only to 127.0.0.1; expected endpoint is 10.0.0.8", decision.Observed);
        Assert.Contains(decision.Inferences, x => x.Contains("does not satisfy", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(decision.Inferences, x => x.Contains("firewall root cause", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ServiceFailed_UsesMatchingEventAsCorrelationOnly()
    {
        var decision = RecordedDbPortPlanner.Diagnose(FullEvidence(
            sockets: "no local listener observed on 5432",
            status: "service status failed",
            config: "service configuration enabled",
            events: "matching service failure event recorded",
            port: "TCP port unreachable"));

        Assert.Equal(DbPortReasoningCategory.ServiceDown, decision.Category);
        Assert.Contains("service status failed", decision.Observed);
        Assert.Contains(decision.Inferences, x => x.Contains("correlates", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(decision.Inferences, x => x.Contains("PostgreSQL root cause", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LocalFirewallBlock_EstablishesOnlyLocalFirewallEvidence()
    {
        var decision = RecordedDbPortPlanner.Diagnose(FullEvidence(
            sockets: "listener on 5432 bound to 10.0.0.8",
            firewallRules: "explicit local inbound block for TCP 5432",
            status: "service status running",
            port: "TCP port unreachable"));

        Assert.Equal(DbPortReasoningCategory.LocalFirewallBlockEvidence, decision.Category);
        Assert.Contains("explicit local inbound block for TCP 5432", decision.Observed);
        Assert.Contains(decision.Inferences, x => x.Contains("local firewall", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("remote firewall state", decision.Unknown);
    }

    [Fact]
    public void ReachableTcpPort_DoesNotEstablishDatabaseHealth_AndHandsOffToCommercialSkill()
    {
        var decision = RecordedDbPortPlanner.Diagnose(FullEvidence(
            sockets: "listener on 1433 bound to 10.0.0.8",
            status: "service status running",
            port: "TCP port reachable"));

        Assert.Equal(DbPortReasoningCategory.TcpReachable, decision.Category);
        Assert.Contains(decision.Inferences, x => x.Contains("TCP endpoint is reachable", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("database protocol health", decision.Unknown);
        Assert.Contains("commercial DB Skill", decision.Handoff);
        Assert.DoesNotContain(decision.Inferences, x => x.Contains("database is healthy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PartialFirewallEvidence_ChangesReasoningAndCannotProveAnOpenPath()
    {
        var complete = RecordedDbPortPlanner.Diagnose(FullEvidence(
            sockets: "listener on 5432 bound to 10.0.0.8",
            status: "service status running",
            port: "TCP port unreachable"));
        var partial = RecordedDbPortPlanner.Diagnose(FullEvidence(
            sockets: "listener on 5432 bound to 10.0.0.8",
            firewallRulesState: DiagnosticEvidenceState.Partial,
            firewallRules: "local rule visibility restricted",
            status: "service status running",
            port: "TCP port unreachable"));

        Assert.Equal(DbPortReasoningCategory.PathUnconfirmed, complete.Category);
        Assert.Equal(DbPortReasoningCategory.IncompleteEvidence, partial.Category);
        Assert.Contains("firewall.rules evidence is partial", partial.Unknown);
        Assert.DoesNotContain(partial.Inferences, x => x.Contains("firewall is open", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("remote firewall state", partial.Unknown);
    }

    private static DiagnosticScenarioHarness FullEvidence(
        string sockets, string status, string port, string firewallRules = "no explicit local blocking rule observed",
        string config = "service configuration observed", string events = "no matching service event observed",
        DiagnosticEvidenceState firewallRulesState = DiagnosticEvidenceState.Available) => new(
            DiagnosticScenarioHarness.Available("network.dns_query", "target resolves to 10.0.0.8"),
            DiagnosticScenarioHarness.Available("network.routes", "usable route to 10.0.0.8"),
            DiagnosticScenarioHarness.Available("network.sockets", sockets),
            DiagnosticScenarioHarness.Available("firewall.status", "local firewall status observed"),
            new DiagnosticEvidence("firewall.rules", firewallRulesState, firewallRules),
            DiagnosticScenarioHarness.Available("service.status", status),
            DiagnosticScenarioHarness.Available("service.config", config),
            DiagnosticScenarioHarness.Available("system.events", events),
            DiagnosticScenarioHarness.Available("network.port_check", port));
}

internal enum DbPortReasoningCategory { NameResolutionFailure, RouteUnavailable, NoLocalListener, UnexpectedBind, ServiceDown, LocalFirewallBlockEvidence, TcpReachable, IncompleteEvidence, PathUnconfirmed }

internal sealed record DbPortDecision(DbPortReasoningCategory Category, IReadOnlyList<string> Requested, IReadOnlyList<string> Observed, IReadOnlyList<string> Inferences, IReadOnlyList<string> Unknown, string? Handoff = null);

internal static class RecordedDbPortPlanner
{
    public static DbPortDecision Diagnose(DiagnosticScenarioHarness harness)
    {
        var observed = new List<string>(); var inferred = new List<string>(); var unknown = new List<string>();
        var dns = Request(harness, "network.dns_query", observed, unknown);
        if (!dns.IsComplete || Has(dns, "unavailable") || Has(dns, "failed"))
            return Decision(DbPortReasoningCategory.NameResolutionFailure, harness, observed, inferred, ["route and downstream network path"]);

        var routes = Request(harness, "network.routes", observed, unknown);
        if (!routes.IsComplete || Has(routes, "no usable route"))
            return Decision(DbPortReasoningCategory.RouteUnavailable, harness, observed, inferred, ["listener, service, and remote firewall state"]);

        var sockets = Request(harness, "network.sockets", observed, unknown);
        var firewallStatus = Request(harness, "firewall.status", observed, unknown);
        var firewallRules = Request(harness, "firewall.rules", observed, unknown);
        var serviceStatus = Request(harness, "service.status", observed, unknown);
        var serviceConfig = Request(harness, "service.config", observed, unknown);
        var events = Request(harness, "system.events", observed, unknown);
        var port = Request(harness, "network.port_check", observed, unknown);

        if (!firewallStatus.IsComplete || !firewallRules.IsComplete)
            return Decision(DbPortReasoningCategory.IncompleteEvidence, harness, observed, inferred, ["firewall.rules evidence is partial", "remote firewall state"]);
        if (Has(port, "TCP port reachable"))
            return Decision(DbPortReasoningCategory.TcpReachable, harness, observed, ["TCP endpoint is reachable at this observation time."], ["database protocol health"], "Persisting symptoms require a future commercial DB Skill for DB-native evidence.");
        if (Has(firewallRules, "explicit local inbound block"))
            return Decision(DbPortReasoningCategory.LocalFirewallBlockEvidence, harness, observed, ["Explicit local firewall evidence applies to the endpoint."], ["remote firewall state"]);
        if (Has(serviceStatus, "stopped") || Has(serviceStatus, "failed"))
        {
            if (Has(events, "matching")) inferred.Add("The matching system event correlates with the observed service state; it does not prove application root cause.");
            return Decision(DbPortReasoningCategory.ServiceDown, harness, observed, inferred, ["DB-native root cause"]);
        }
        if (Has(sockets, "no local listener"))
            return Decision(DbPortReasoningCategory.NoLocalListener, harness, observed, inferred, ["remote firewall state"]);
        if (Has(sockets, "bound only"))
            return Decision(DbPortReasoningCategory.UnexpectedBind, harness, observed, ["A listener exists but does not satisfy the intended endpoint exposure."], ["remote firewall state"]);
        return Decision(DbPortReasoningCategory.PathUnconfirmed, harness, observed, inferred, ["remote firewall state", "end-to-end network path"]);
    }

    private static DiagnosticEvidence Request(DiagnosticScenarioHarness harness, string capability, List<string> observed, List<string> unknown)
    {
        var evidence = harness.Request(capability);
        observed.Add(evidence.Observation);
        if (!evidence.IsComplete) unknown.Add($"{capability} evidence is {evidence.State.ToString().ToLowerInvariant()}");
        return evidence;
    }

    private static DbPortDecision Decision(DbPortReasoningCategory category, DiagnosticScenarioHarness harness, List<string> observed, List<string> inferred, IEnumerable<string> unknown, string? handoff = null) =>
        new(category, harness.RequestedCapabilities, observed, inferred, unknown.ToArray(), handoff);

    private static bool Has(DiagnosticEvidence evidence, string value) => evidence.Observation.Contains(value, StringComparison.OrdinalIgnoreCase);
}
