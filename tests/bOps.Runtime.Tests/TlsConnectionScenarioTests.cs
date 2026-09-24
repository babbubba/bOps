// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace bOps.Runtime.Tests;

/// <summary>Recorded, evidence-dependent L5 reasoning. These fixtures model typed tool observations.</summary>
public sealed class TlsConnectionScenarioTests
{
    private static readonly string[] Sequence = ["network.dns_query", "network.routes", "network.port_check", "network.tls_probe", "certificate.inspect", "system.time", "network.ntp_probe"];

    [Fact]
    public void TcpRefused_DoesNotInventTlsOrCertificateFailure_AndReachableRemovesTransportConclusion()
    {
        var refused = RecordedTlsPlanner.Diagnose(Evidence(port: "TCP refused"));
        var reachable = RecordedTlsPlanner.Diagnose(Evidence(port: "TCP reachable", tls: "handshake succeeded; host=api.example.test; chainValid=true; identityMatch=true"));

        Assert.Contains(refused.Inferences, x => x.Contains("TCP connection failed", StringComparison.Ordinal));
        Assert.DoesNotContain(refused.AllReasoning, x => x.Contains("TLS handshake failed", StringComparison.Ordinal) || x.Contains("certificate trust failure", StringComparison.Ordinal) || x.Contains("hostname mismatch", StringComparison.Ordinal));
        Assert.DoesNotContain(refused.Requested, x => x is "network.tls_probe" or "certificate.inspect");
        Assert.DoesNotContain(reachable.AllReasoning, x => x.Contains("TCP connection failed", StringComparison.Ordinal));
    }

    [Fact]
    public void HandshakeFailure_PreservesSuccessfulTcp_AndDoesNotInventTrustFailure()
    {
        var decision = RecordedTlsPlanner.Diagnose(Evidence(port: "TCP reachable", tls: "handshake failed; protocol negotiation error"));
        Assert.Contains(decision.Observed, x => x.Contains("TCP reachable", StringComparison.Ordinal));
        Assert.Contains(decision.Inferences, x => x.Contains("TLS handshake failed", StringComparison.Ordinal));
        Assert.DoesNotContain(decision.AllReasoning, x => x.Contains("TCP connection failed", StringComparison.Ordinal) || x.Contains("trust validation failed", StringComparison.Ordinal) || x.Contains("application is healthy", StringComparison.Ordinal));
        Assert.DoesNotContain(decision.Requested, x => x is "certificate.inspect" or "system.time" or "network.ntp_probe");
    }

    [Fact]
    public void TrustAndIdentityAreIndependentOfTransport_AndConclusionsChangeWithEvidence()
    {
        var untrusted = RecordedTlsPlanner.Diagnose(Evidence(port: "TCP reachable", tls: "handshake succeeded; host=api.example.test; chainValid=false; policyErrors=RemoteCertificateChainErrors; identityMatch=true"));
        var trusted = RecordedTlsPlanner.Diagnose(Evidence(port: "TCP reachable", tls: "handshake succeeded; host=api.example.test; chainValid=true; identityMatch=true"));
        var mismatch = RecordedTlsPlanner.Diagnose(Evidence(port: "TCP reachable", tls: "handshake succeeded; host=api.example.test; chainValid=true; identityMatch=false; dnsSans=other.example.test"));
        var matched = RecordedTlsPlanner.Diagnose(Evidence(port: "TCP reachable", tls: "handshake succeeded; host=other.example.test; chainValid=true; identityMatch=true; dnsSans=other.example.test"));

        Assert.Contains(untrusted.Inferences, x => x.Contains("TCP REACHABLE", StringComparison.Ordinal) && x.Contains("TRUST FAILURE", StringComparison.Ordinal));
        Assert.DoesNotContain(untrusted.Inferences, x => x.Contains("TCP connection failed", StringComparison.Ordinal) || x.Contains("hostname mismatch", StringComparison.Ordinal));
        Assert.DoesNotContain(trusted.AllReasoning, x => x.Contains("trust failure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mismatch.Inferences, x => x.Contains("hostname mismatch", StringComparison.OrdinalIgnoreCase) && x.Contains("api.example.test", StringComparison.Ordinal));
        Assert.DoesNotContain(matched.AllReasoning, x => x.Contains("hostname mismatch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mismatch.AllReasoning, x => x.Contains("certificate is unusable for every hostname", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidityDatesDriveExpiryAndNotYetValid_AndClockIsOnlyExplanatoryWhenConsistent()
    {
        var expired = RecordedTlsPlanner.Diagnose(Evidence(port: "TCP reachable", tls: "handshake succeeded; host=api.example.test; chainValid=true; identityMatch=true", certificate: "notBefore=2026-01-01T00:00:00Z; notAfter=2026-09-01T00:00:00Z", time: "utcNow=2026-09-24T10:00:00Z; ntpSynchronized=true", ntp: "valid=true; offsetMilliseconds=5"));
        var validDate = RecordedTlsPlanner.Diagnose(Evidence(port: "TCP reachable", tls: "handshake succeeded; host=api.example.test; chainValid=true; identityMatch=true", certificate: "notBefore=2026-01-01T00:00:00Z; notAfter=2027-09-01T00:00:00Z", time: "utcNow=2026-09-24T10:00:00Z; ntpSynchronized=true", ntp: "valid=true; offsetMilliseconds=5"));
        var future = RecordedTlsPlanner.Diagnose(Evidence(port: "TCP reachable", tls: "handshake succeeded; host=api.example.test; chainValid=true; identityMatch=true", certificate: "notBefore=2026-10-01T00:00:00Z; notAfter=2027-09-01T00:00:00Z", time: "utcNow=2026-09-24T10:00:00Z; ntpSynchronized=true", ntp: "valid=true; offsetMilliseconds=5"));
        var drift = RecordedTlsPlanner.Diagnose(Evidence(port: "TCP reachable", tls: "handshake succeeded; host=api.example.test; chainValid=true; identityMatch=true", certificate: "notBefore=2026-01-01T00:00:00Z; notAfter=2026-09-01T00:00:00Z", time: "utcNow=2026-09-24T10:00:00Z; ntpSynchronized=false", ntp: "valid=true; offsetMilliseconds=600000"));
        var noDrift = RecordedTlsPlanner.Diagnose(Evidence(port: "TCP reachable", tls: "handshake succeeded; host=api.example.test; chainValid=true; identityMatch=true", certificate: "notBefore=2026-01-01T00:00:00Z; notAfter=2026-09-01T00:00:00Z", time: "utcNow=2026-09-24T10:00:00Z; ntpSynchronized=true", ntp: "valid=true; offsetMilliseconds=5"));

        Assert.Contains(expired.Inferences, x => x.Contains("expired", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(expired.Inferences, x => x.Contains("clock definitely caused", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(validDate.Inferences, x => x.Contains("expired", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(future.Inferences, x => x.Contains("not-yet-valid", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(future.Inferences, x => x.Contains("expired", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(drift.Inferences, x => x.Contains("may explain or contribute", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(drift.Inferences, x => x.Contains("clock definitely caused", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(noDrift.Inferences, x => x.Contains("may explain or contribute", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void MissingCertificateOrTimeEvidence_RemainsUnknownNotNormal(int stateValue)
    {
        var state = (DiagnosticEvidenceState)stateValue;
        var noCertificate = RecordedTlsPlanner.Diagnose(Evidence(port: "TCP reachable", tls: "handshake succeeded; host=api.example.test; chainValid=true; identityMatch=true", certificate: "", certState: state));
        var noTime = RecordedTlsPlanner.Diagnose(Evidence(port: "TCP reachable", tls: "handshake succeeded; host=api.example.test; chainValid=true; identityMatch=true", certificate: "notBefore=2026-01-01T00:00:00Z; notAfter=2027-09-01T00:00:00Z", time: "", timeState: state, ntp: "", ntpState: state));
        Assert.Contains(noCertificate.Unknown, x => x.Contains("certificate-specific conclusions remain unknown", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(noTime.Unknown, x => x.Contains("clock", StringComparison.OrdinalIgnoreCase) && x.Contains("unknown", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(noTime.Inferences, x => x.Contains("clock is normal", StringComparison.OrdinalIgnoreCase) || x.Contains("clock caused", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TlsSuccessOnlyEstablishesTlsConnectivity_AndPrivateKeyIsNeverInferred()
    {
        var decision = RecordedTlsPlanner.Diagnose(Evidence(port: "TCP reachable", tls: "handshake succeeded; host=api.example.test; chainValid=true; identityMatch=true; protocol=Tls13", certificate: "subject=CN=api.example.test; issuer=Example CA; notBefore=2026-01-01T00:00:00Z; notAfter=2027-09-01T00:00:00Z", time: "utcNow=2026-09-24T10:00:00Z; ntpSynchronized=true", ntp: "valid=true; offsetMilliseconds=5"));
        Assert.Contains(decision.Inferences, x => x.Contains("TLS connectivity is established", StringComparison.Ordinal));
        Assert.Contains(decision.Unknown, x => x.Contains("Application, database", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(decision.AllReasoning, x => x.Contains("application is healthy", StringComparison.OrdinalIgnoreCase) || x.Contains("DB is healthy", StringComparison.OrdinalIgnoreCase) || x.Contains("HTTP/API request would succeed", StringComparison.OrdinalIgnoreCase) || x.Contains("authentication would succeed", StringComparison.OrdinalIgnoreCase) || x.Contains("private key", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Sequence, decision.Requested);
    }

    private static DiagnosticScenarioHarness Evidence(string port, string tls = "", DiagnosticEvidenceState tlsState = DiagnosticEvidenceState.Available, string certificate = "", DiagnosticEvidenceState certState = DiagnosticEvidenceState.Available, string time = "", DiagnosticEvidenceState timeState = DiagnosticEvidenceState.Available, string ntp = "", DiagnosticEvidenceState ntpState = DiagnosticEvidenceState.Available) => new(
        DiagnosticScenarioHarness.Available("network.dns_query", "success=true; answers=10.0.0.8"),
        DiagnosticScenarioHarness.Available("network.routes", "usable route to 10.0.0.8"),
        DiagnosticScenarioHarness.Available("network.port_check", port),
        new DiagnosticEvidence("network.tls_probe", tlsState, tls),
        new DiagnosticEvidence("certificate.inspect", certState, certificate),
        new DiagnosticEvidence("system.time", timeState, time),
        new DiagnosticEvidence("network.ntp_probe", ntpState, ntp));
}

internal sealed record TlsDecision(IReadOnlyList<string> Requested, IReadOnlyList<string> Observed, IReadOnlyList<string> Inferences, IReadOnlyList<string> Unknown)
{
    public IReadOnlyList<string> AllReasoning => [.. Observed, .. Inferences, .. Unknown];
}

internal static class RecordedTlsPlanner
{
    public static TlsDecision Diagnose(DiagnosticScenarioHarness harness)
    {
        var observed = new List<string>(); var inferences = new List<string>(); var unknown = new List<string>();
        DiagnosticEvidence Get(string capability) { var e = harness.Request(capability); observed.Add($"{capability}: {e.Observation} [{e.State}]"); return e; }
        bool Has(DiagnosticEvidence e, string key) => e.Observation.Contains(key, StringComparison.OrdinalIgnoreCase);
        var dns = Get("network.dns_query");
        if (!dns.IsComplete || Has(dns, "success=false") || Has(dns, "no resolution")) { unknown.Add("Name resolution was not established; route and downstream layers remain unknown."); return Done(); }
        var route = Get("network.routes");
        if (!route.IsComplete || Has(route, "no usable route")) { unknown.Add("Routing is unavailable or unusable; later endpoint layers remain unknown."); return Done(); }
        var port = Get("network.port_check");
        if (port.IsComplete && (Has(port, "refused") || Has(port, "unreachable"))) { inferences.Add("TCP connection failed (refused/unreachable); TLS was not established and certificate, trust, and hostname conclusions are unsupported."); return Done(); }
        if (!port.IsComplete || !Has(port, "reachable")) { unknown.Add("TCP reachability could not be established."); return Done(); }
        inferences.Add("TCP REACHABLE: network.port_check observed a successful TCP connection.");
        var tls = Get("network.tls_probe");
        if (!tls.IsComplete || tls.Observation.Length == 0) { unknown.Add("TLS handshake outcome is unknown because the TLS observation is incomplete."); return Done(); }
        if (Has(tls, "handshake failed")) { inferences.Add("TLS handshake failed after TCP succeeded; this alone does not establish that certificate chain validation failed."); return Done(); }
        if (!Has(tls, "handshake succeeded")) { unknown.Add("TLS handshake outcome is unknown; no success or failure is inferred."); return Done(); }
        if (Has(tls, "chainValid=false")) inferences.Add("TCP REACHABLE; TRUST FAILURE: TLS evidence reports chain validation failure.");
        if (Has(tls, "identityMatch=false"))
        {
            var host = Value(tls.Observation, "host");
            inferences.Add($"Hostname mismatch: certificate identity does not match requested hostname {host ?? "(unknown)"}; no claim is made for other hostnames.");
        }
        var cert = Get("certificate.inspect");
        DateTimeOffset? before = Date(cert, "notBefore"); DateTimeOffset? after = Date(cert, "notAfter");
        var time = Get("system.time"); var now = Date(time, "utcNow");
        var ntp = Get("network.ntp_probe");
        if (!cert.IsComplete || !before.HasValue || !after.HasValue) unknown.Add("Certificate inspection is incomplete; certificate-specific conclusions remain unknown.");
        if (now.HasValue && before.HasValue && after.HasValue)
        {
            if (after.Value < now.Value) inferences.Add($"Certificate validity-window failure: expired (NotAfter {after:O} < observed local/current time {now:O}).");
            else if (before.Value > now.Value) inferences.Add($"Certificate validity-window failure: not-yet-valid (NotBefore {before:O} > observed local/current time {now:O}).");
        }
        var validitySymptom = inferences.Any(x => x.Contains("validity-window failure", StringComparison.Ordinal));
        var materialDrift = ntp.IsComplete && Has(ntp, "valid=true") && double.TryParse(Value(ntp.Observation, "offsetMilliseconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var offset) && Math.Abs(offset) >= 300000;
        var timeConcern = time.IsComplete && Has(time, "ntpSynchronized=false") || materialDrift;
        if (!time.IsComplete || !ntp.IsComplete || !now.HasValue) unknown.Add("Local clock/time synchronization remains unknown because time evidence is incomplete or lacks an observed current time.");
        if (validitySymptom && timeConcern) inferences.Add("Observed time evidence may explain or contribute to the certificate validity symptom; it does not prove the clock caused the TLS failure.");
        if (Has(tls, "chainValid=true") && Has(tls, "identityMatch=true") && !validitySymptom) { inferences.Add("TLS connectivity is established at observation time with acceptable reported trust and identity evidence."); unknown.Add("Application, database, HTTP/API, and authentication health remain unknown; TLS does not test them."); }
        return Done();

        TlsDecision Done() => new(harness.RequestedCapabilities, observed, inferences, unknown);
        static string? Value(string text, string key) { var marker = key + "="; var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase); if (start < 0) return null; start += marker.Length; var end = text.IndexOf(';', start); return text[start..(end < 0 ? text.Length : end)].Trim(); }
        static DateTimeOffset? Date(DiagnosticEvidence e, string key) => e.IsComplete && DateTimeOffset.TryParse(Value(e.Observation, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }
}
