// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Security;
using Xunit.Sdk;

namespace bOps.Packages.Security.Tests;

#pragma warning disable CA2000 // TlsServer owns and disposes the certificate after its handshake observation completes.
#pragma warning disable CA5398 // Explicit protocol choices are the semantics being proven.
#pragma warning disable CA1812 // Constructed by the static fixture factory.
public sealed class SecurityToolsTests
{
    [Fact]
    public void Provider_ContributesOnlyThreeReadOnlyDiagnosticTools()
    {
        var tools = new SecurityToolProvider().GetTools().ToArray();
        Assert.Equal(["network.tls_probe", "certificate.list", "certificate.inspect"], tools.Select(t => t.Manifest.Name));
        Assert.All(tools, t => Assert.Equal(RiskLevel.Read, t.Manifest.Risk));
    }

    [Fact]
    public async Task TlsProbe_HostnameMismatch_IsSuccessfulCertificateEvidence_AndSendsNoApplicationData()
    {
        await using var server = await TlsServer.StartAsync(CreateCertificate("certificate-a.local"), SslProtocols.Tls12);
        var output = SuccessfulOutput(await Probe(server.Port, "certificate-b.local"));
        Assert.False(output["chainValid"]!.GetValue<bool>());
        Assert.Contains("RemoteCertificateNameMismatch", output["policyErrors"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(output["complete"]!.GetValue<bool>());
        Assert.Equal("certificate-a.local", output["certificate"]!["dnsSans"]![0]!.GetValue<string>());
        Assert.Equal("certificate-b.local", await server.GetSniHostAsync());
        Assert.True(await server.GetNoApplicationDataAsync());
    }

    [Theory]
    [InlineData(-4, -2, "NotTimeValid")]
    [InlineData(2, 4, "NotTimeValid")]
    public async Task TlsProbe_TimeInvalidCertificate_IsObservedAsUntrustedEvidence(int notBeforeDays, int notAfterDays, string expectedStatus)
    {
        await using var server = await TlsServer.StartAsync(CreateCertificate("time.local", DateTimeOffset.UtcNow.AddDays(notBeforeDays), DateTimeOffset.UtcNow.AddDays(notAfterDays)), SslProtocols.Tls12);
        var output = SuccessfulOutput(await Probe(server.Port, "time.local"));
        Assert.False(output["chainValid"]!.GetValue<bool>());
        Assert.NotEmpty(output["chainStatuses"]!.AsArray());
        Assert.Contains(expectedStatus, string.Join(' ', output["chainStatuses"]!.AsArray().Select(static x => x!.GetValue<string>())), StringComparison.OrdinalIgnoreCase);
        var remaining = output["certificate"]!["daysRemaining"]!.GetValue<double>();
        Assert.True(notAfterDays < 0 ? remaining < 0 : remaining > 0);
    }

    [Fact]
    public async Task TlsProbe_Tls13_IsProvenOrSkippedWhenThePlatformDoesNotSupportIt()
    {
        try
        {
            await using var server = await TlsServer.StartAsync(CreateCertificate("tls13.local"), SslProtocols.Tls13);
            var output = SuccessfulOutput(await Probe(server.Port, "tls13.local", "tls13"));
            Assert.Equal("Tls13", output["protocol"]!.GetValue<string>());
        }
        catch (AuthenticationException ex) when (ex.Message.Contains("support", StringComparison.OrdinalIgnoreCase) || ex.InnerException is not null)
        {
            throw SkipException.ForSkip($"TLS 1.3 is not supported by this runtime/OS fixture: {ex.Message}");
        }
    }

    [Fact]
    public async Task TlsProbe_ClassifiesRefusedTcpNonTlsHandshakeAndTimeoutAsFailures()
    {
        using var refused = new TcpListener(IPAddress.Loopback, 0); refused.Start(); var refusedPort = ((IPEndPoint)refused.LocalEndpoint).Port; refused.Stop();
        var tcpFailure = await Probe(refusedPort, "localhost", timeout: 1000);
        Assert.Equal(ToolOutcome.Failure, tcpFailure.Outcome); Assert.DoesNotContain("chainValid", tcpFailure.ErrorMessage!, StringComparison.Ordinal);

        await using (var nonTls = await PlainServer.StartAsync(static async stream => await stream.WriteAsync("not TLS"u8.ToArray())))
        {
            var handshake = await Probe(nonTls.Port, "localhost", timeout: 1000);
            Assert.Equal(ToolOutcome.Failure, handshake.Outcome); Assert.Contains("TLS handshake", handshake.ErrorMessage!, StringComparison.Ordinal);
        }
        await using (var stalled = await PlainServer.StartAsync(static async _ => await Task.Delay(2000)))
        {
            var timeout = await Probe(stalled.Port, "localhost", timeout: 100);
            Assert.Equal(ToolOutcome.Failure, timeout.Outcome); Assert.Contains("timed out", timeout.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task CertificateListAndInspect_ProveFiltersOrderingNormalizationAndPublicOnlyOutput()
    {
        using var expired = CreateCertificate("expired-store.local", DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddDays(-2));
        using var expiring = CreateCertificate("expiring-store.local", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        using var later = CreateCertificate("later-store.local", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var fixture = CertificateStoreFixture.TryCreate([expired, expiring, later]) ?? throw SkipException.ForSkip("CurrentUser/My store mutation is unavailable in this test environment.");
        var list = SuccessfulOutput(await new CertificateListTool().ExecuteAsync(Args(("location", "currentUser"), ("store", "my"), ("limit", 2000))));
        var fixtureThumbprints = new[] { expired.Thumbprint, expiring.Thumbprint, later.Thumbprint };
        var rows = list["certificates"]!.AsArray().Where(row => fixtureThumbprints.Contains(row!["thumbprint"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(3, rows.Length); Assert.Equal(rows.OrderBy(row => row!["thumbprint"]!.GetValue<string>(), StringComparer.Ordinal), rows);
        Assert.All(rows, row => Assert.IsAssignableFrom<JsonValue>(row!["hasPrivateKey"])); Assert.DoesNotContain("PRIVATE KEY", list.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        var filtered = SuccessfulOutput(await new CertificateListTool().ExecuteAsync(Args(("location", "currentUser"), ("store", "my"), ("expiringWithinDays", 3), ("limit", 2000))));
        var filteredThumbprints = filtered["certificates"]!.AsArray().Select(row => row!["thumbprint"]!.GetValue<string>()).ToArray();
        Assert.Contains(expired.Thumbprint!, filteredThumbprints, StringComparer.OrdinalIgnoreCase); Assert.Contains(expiring.Thumbprint!, filteredThumbprints, StringComparer.OrdinalIgnoreCase); Assert.DoesNotContain(later.Thumbprint!, filteredThumbprints, StringComparer.OrdinalIgnoreCase);
        var original = SuccessfulOutput(await new CertificateInspectTool().ExecuteAsync(Args(("location", "currentUser"), ("store", "my"), ("thumbprint", expiring.Thumbprint!))));
        var spaced = string.Join(' ', expiring.Thumbprint!.Chunk(4).Select(static part => new string(part))).ToLowerInvariant();
        var normalized = SuccessfulOutput(await new CertificateInspectTool().ExecuteAsync(Args(("location", "currentUser"), ("store", "my"), ("thumbprint", spaced))));
        Assert.Equal(original.ToJsonString(), normalized.ToJsonString()); Assert.NotNull(original["chainStatuses"]); Assert.DoesNotContain("PRIVATE KEY", original.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        var missing = await new CertificateInspectTool().ExecuteAsync(Args(("location", "currentUser"), ("store", "my"), ("thumbprint", "00 11 22")));
        Assert.Equal(ToolOutcome.Failure, missing.Outcome); Assert.Contains("not found", missing.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<ToolCallResult> Probe(int port, string sniHost, string minTls = "tls12", int timeout = 5000) => new TlsProbeTool().ExecuteAsync(Args(("host", "127.0.0.1"), ("port", port), ("sniHost", sniHost), ("minTls", minTls), ("timeoutMilliseconds", timeout)));
    private static JsonObject SuccessfulOutput(ToolCallResult result) { Assert.True(result.Succeeded, result.ErrorMessage); return JsonNode.Parse(result.Output!)!.AsObject(); }
    private static ToolArguments Args(params (string Name, object Value)[] values) { var json = new JsonObject(); foreach (var (name, value) in values) json[name] = JsonValue.Create(value); return ToolArguments.FromJson(json); }
    private static X509Certificate2 CreateCertificate(string dnsName, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        using var rsa = RSA.Create(2048); var request = new CertificateRequest($"CN={dnsName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(dnsName); request.CertificateExtensions.Add(san.Build()); request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        using var ephemeral = request.CreateSelfSigned(notBefore ?? DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddDays(2));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }
    private sealed class CertificateStoreFixture : IDisposable
    {
        private readonly X509Store store; private readonly IReadOnlyList<X509Certificate2> certificates;
        private CertificateStoreFixture(X509Store store, IReadOnlyList<X509Certificate2> certificates) { this.store = store; this.certificates = certificates; }
        public static CertificateStoreFixture? TryCreate(IReadOnlyList<X509Certificate2> certificates) { var store = new X509Store(StoreName.My, StoreLocation.CurrentUser); try { store.Open(OpenFlags.ReadWrite); foreach (var certificate in certificates) store.Add(certificate); return new CertificateStoreFixture(store, certificates); } catch (CryptographicException) { store.Dispose(); return null; } }
        public void Dispose() { foreach (var certificate in certificates) store.Remove(certificate); store.Dispose(); }
    }
    private sealed class TlsServer : IAsyncDisposable
    {
        private readonly TcpListener listener; private readonly X509Certificate2 certificate; private readonly Task<(bool NoData, string? Sni)> observation;
        private TlsServer(TcpListener listener, X509Certificate2 certificate, Task<(bool NoData, string? Sni)> observation) { this.listener = listener; this.certificate = certificate; this.observation = observation; }
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public async Task<bool> GetNoApplicationDataAsync() => (await observation).NoData;
        public async Task<string?> GetSniHostAsync() => (await observation).Sni;
        public static Task<TlsServer> StartAsync(X509Certificate2 certificate, SslProtocols protocols)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); string? sni = null;
            var observation = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = new SslStream(client.GetStream(), false);
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { EnabledSslProtocols = protocols, ServerCertificateSelectionCallback = (_, serverName) => { sni = serverName; return certificate; } });
                var buffer = new byte[1];
                using var cts = new CancellationTokenSource(500);
                try
                {
                    // Any application byte remains a semantic failure, even if the peer then resets.
                    return (await stream.ReadAsync(buffer, cts.Token) == 0, sni);
                }
                catch (OperationCanceledException) { return (true, sni); }
                catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
                {
                    // Windows may report an abrupt TLS peer close as reset after handshake.
                    // This is treated as close only because ReadAsync observed no application byte.
                    return (true, sni);
                }
            });
            return Task.FromResult(new TlsServer(listener, certificate, observation));
        }
        public async ValueTask DisposeAsync() { listener.Stop(); await observation; certificate.Dispose(); }
    }
    private sealed class PlainServer : IAsyncDisposable
    {
        private readonly TcpListener listener; private readonly Task task; private PlainServer(TcpListener listener, Task task) { this.listener = listener; this.task = task; }
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public static Task<PlainServer> StartAsync(Func<NetworkStream, Task> handler) { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var task = Task.Run(async () => { using var client = await listener.AcceptTcpClientAsync(); await handler(client.GetStream()); }); return Task.FromResult(new PlainServer(listener, task)); }
        public async ValueTask DisposeAsync() { listener.Stop(); await task; }
    }
}
