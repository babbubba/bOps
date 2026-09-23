// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Security;

/// <summary>Performs only a TCP/TLS handshake; it deliberately never writes application bytes.</summary>
public sealed class TlsProbeTool : ITool
{
    public ToolManifest Manifest => SecurityContracts.TlsProbeManifest;

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryArguments(arguments, out var host, out var port, out var targetHost, out var timeout, out var protocols, out var error)) return ToolCallResult.Failure(error!);
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, linked.Token);
            var address = addresses.FirstOrDefault(static a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);
            if (address is null) return ToolCallResult.Failure($"'{host}' resolved to no usable IP address.");
            using var client = new TcpClient(address.AddressFamily);
            var tcp = Stopwatch.StartNew();
            await client.ConnectAsync(address, port, linked.Token);
            tcp.Stop();
            X509Certificate2? observedCertificate = null;
            X509Chain? observedChain = null;
            SslPolicyErrors policyErrors = SslPolicyErrors.None;
#pragma warning disable CA5359 // This callback is isolated to a read-only diagnostic probe; trust is reported independently.
            using var stream = new SslStream(client.GetStream(), false, (_, certificate, chain, errors) =>
            {
                policyErrors = errors;
                observedCertificate = certificate is null ? null : new X509Certificate2(certificate);
                observedChain = chain;
                // This is scoped diagnostic capture only. The output computes trust separately;
                // allowing completion makes an untrusted peer observable, never trusted.
                return true;
            });
#pragma warning restore CA5359
            var handshake = Stopwatch.StartNew();
            try
            {
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = targetHost, EnabledSslProtocols = protocols, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, linked.Token);
            }
            catch (AuthenticationException ex)
            {
                return ToolCallResult.Failure($"TLS handshake with '{host}:{port}' failed: {SecurityContracts.Bound(ex.Message)}");
            }
            finally { handshake.Stop(); }
            if (observedCertificate is null) return ToolCallResult.Failure($"TLS handshake with '{host}:{port}' completed without certificate evidence.");
            using (observedCertificate)
            using (observedChain)
            {
                var chainStatuses = observedChain?.ChainStatus ?? [];
                var chainValid = policyErrors == SslPolicyErrors.None && chainStatuses.Length == 0;
                var output = new JsonObject { ["schemaVersion"] = 1, ["host"] = host, ["resolvedAddress"] = address.ToString(), ["port"] = port, ["tcpConnectMs"] = tcp.ElapsedMilliseconds, ["tlsHandshakeMs"] = handshake.ElapsedMilliseconds, ["protocol"] = stream.SslProtocol.ToString(), ["cipherSuite"] = stream.NegotiatedCipherSuite.ToString(), ["alpn"] = stream.NegotiatedApplicationProtocol.Protocol.Length == 0 ? null : stream.NegotiatedApplicationProtocol.ToString(), ["certificate"] = SecurityContracts.Certificate(observedCertificate), ["chainValid"] = chainValid, ["policyErrors"] = policyErrors.ToString(), ["chainStatuses"] = SecurityContracts.ChainStatuses(chainStatuses), ["complete"] = true };
                return ToolCallResult.Success(output.ToJsonString());
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ToolCallResult.Failure($"TLS probe timed out after {timeout} ms."); }
        catch (SocketException ex) { return ToolCallResult.Failure($"TCP connection to '{host}:{port}' failed: {SecurityContracts.Bound(ex.Message)}"); }
        catch (ArgumentException ex) { return ToolCallResult.Failure($"TLS probe arguments are invalid: {SecurityContracts.Bound(ex.Message)}"); }
    }

    private static bool TryArguments(ToolArguments a, out string host, out int port, out string targetHost, out int timeout, out SslProtocols protocols, out string? error)
    {
        host = string.Empty; port = 0; targetHost = string.Empty; timeout = SecurityContracts.DefaultTimeoutMs; protocols = SslProtocols.None;
        if (!a.TryGet<string>("host", out host!) || string.IsNullOrWhiteSpace(host) || host.Length > 253) { error = "host is required and must be at most 253 characters."; return false; }
        if (!a.TryGet<int>("port", out port) || port is < 1 or > 65535) { error = "port must be between 1 and 65535."; return false; }
        targetHost = host;
        if (a.ToJson().ContainsKey("sniHost")) { if (!a.TryGet<string>("sniHost", out var sni) || string.IsNullOrWhiteSpace(sni) || sni.Length > 253) { error = "sniHost must be a non-empty hostname of at most 253 characters."; return false; } targetHost = sni; }
        if (a.ToJson().ContainsKey("timeoutMilliseconds") && (!a.TryGet<int>("timeoutMilliseconds", out timeout) || timeout < 1 || timeout > SecurityContracts.MaximumTimeoutMs)) { error = "timeoutMilliseconds must be between 1 and 15000."; return false; }
        var minTls = "system";
        if (a.ToJson().ContainsKey("minTls") && (!a.TryGet<string>("minTls", out minTls!) || minTls is not ("system" or "tls12" or "tls13"))) { error = "minTls must be system, tls12, or tls13."; return false; }
#pragma warning disable CA5398 // Explicit versions are the user-requested diagnostic contract.
        protocols = minTls switch { "system" => SslProtocols.None, "tls12" => SslProtocols.Tls12 | SslProtocols.Tls13, _ => SslProtocols.Tls13 };
#pragma warning restore CA5398
        error = null; return true;
    }
}
