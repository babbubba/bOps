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

namespace bOps.Packages.Security.Tests;

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
    public async Task TlsProbe_ReportsUntrustedCertificateAsEvidence_AndSendsNoApplicationData()
    {
        await using var server = await TlsServer.StartAsync("localhost");
        var result = await new TlsProbeTool().ExecuteAsync(Args(("host", "127.0.0.1"), ("port", server.Port), ("sniHost", "localhost"), ("minTls", "tls12")));
        Assert.True(result.Succeeded);
        var output = JsonNode.Parse(result.Output!)!.AsObject();
        Assert.False(output["chainValid"]!.GetValue<bool>());
        Assert.Equal("Tls12", output["protocol"]!.GetValue<string>());
        Assert.Equal("localhost", output["certificate"]!["dnsSans"]![0]!.GetValue<string>());
        Assert.True(await server.NoApplicationData);
    }

    [Fact]
    public async Task TlsProbe_RejectsInvalidPort_AndRefusedConnectionIsFailure()
    {
        Assert.Equal(ToolOutcome.Failure, (await new TlsProbeTool().ExecuteAsync(Args(("host", "localhost"), ("port", 0)))).Outcome);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        Assert.Equal(ToolOutcome.Failure, (await new TlsProbeTool().ExecuteAsync(Args(("host", "127.0.0.1"), ("port", port), ("timeoutMilliseconds", 1000)))).Outcome);
    }

    [Fact]
    public async Task CertificateList_AndInspect_UsePublicOnlyOutputAndNormalizeThumbprint()
    {
        using var certificate = CreateCertificate("store-test.local");
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        try
        {
            store.Open(OpenFlags.ReadWrite); store.Add(certificate);
        }
        catch (CryptographicException)
        {
            return; // Store integration is unsupported in this environment; parser/contract coverage remains deterministic.
        }
        try
        {
            var list = await new CertificateListTool().ExecuteAsync(Args(("location", "currentUser"), ("store", "my"), ("limit", 2000)));
            Assert.True(list.Succeeded); Assert.Contains(certificate.Thumbprint!, list.Output!, StringComparison.OrdinalIgnoreCase);
            var thumbprint = string.Join(' ', certificate.Thumbprint!.Chunk(4).Select(static c => new string(c))).ToLowerInvariant();
            var inspect = await new CertificateInspectTool().ExecuteAsync(Args(("location", "currentUser"), ("store", "my"), ("thumbprint", thumbprint)));
            Assert.True(inspect.Succeeded); Assert.Contains("hasPrivateKey", inspect.Output!, StringComparison.Ordinal); Assert.DoesNotContain("PRIVATE KEY", inspect.Output!, StringComparison.OrdinalIgnoreCase);
        }
        finally { store.Remove(certificate); }
    }

    [Fact]
    public async Task CertificateInspect_NotFoundIsExplicitFailure()
    {
        var result = await new CertificateInspectTool().ExecuteAsync(Args(("location", "currentUser"), ("store", "my"), ("thumbprint", "00 11 22")));
        Assert.Equal(ToolOutcome.Failure, result.Outcome);
        Assert.Contains("not found", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    private static ToolArguments Args(params (string Name, object Value)[] values)
    {
        var json = new JsonObject(); foreach (var (name, value) in values) json[name] = JsonValue.Create(value); return ToolArguments.FromJson(json);
    }
    private static X509Certificate2 CreateCertificate(string dnsName)
    {
        using var rsa = RSA.Create(2048); var request = new CertificateRequest($"CN={dnsName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(dnsName); request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        // Test-only key persistence makes Schannel accept the locally generated server identity.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    private sealed class TlsServer : IAsyncDisposable
    {
        private readonly TcpListener listener; private readonly X509Certificate2 certificate; private readonly Task<bool> observation;
        private TlsServer(TcpListener listener, X509Certificate2 certificate, Task<bool> observation) { this.listener = listener; this.certificate = certificate; this.observation = observation; }
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public Task<bool> NoApplicationData => observation;
        public static async Task<TlsServer> StartAsync(string dnsName)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var certificate = CreateCertificate(dnsName);
#pragma warning disable CA5398 // The test must prove the tls12 diagnostic contract against a TLS 1.2-only endpoint.
            var observation = Task.Run(async () => { using var client = await listener.AcceptTcpClientAsync(); using var stream = new SslStream(client.GetStream(), false); await stream.AuthenticateAsServerAsync(certificate, false, SslProtocols.Tls12, false); var buffer = new byte[1]; using var cts = new CancellationTokenSource(500); try { return await stream.ReadAsync(buffer, cts.Token) == 0; } catch (OperationCanceledException) { return true; } });
#pragma warning restore CA5398
            await Task.Yield(); return new TlsServer(listener, certificate, observation);
        }
        public async ValueTask DisposeAsync() { listener.Stop(); await observation; certificate.Dispose(); }
    }
}
