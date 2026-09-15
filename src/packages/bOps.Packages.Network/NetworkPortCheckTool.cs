// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net.Sockets;
using bOps.Abstractions;

namespace bOps.Packages.Network;

/// <summary>
/// Attempts a TCP connection to <c>host:port</c> and reports whether it is open, or the reason it
/// is not (refused, timed out, or the host could not be resolved). Read-risk: it never sends
/// application data, only completes or abandons the TCP handshake. A closed or unreachable port is
/// a successful observation of a negative fact, not <see cref="ToolOutcome.Failure"/> — the same
/// distinction <c>fs.stat</c> draws for a missing path.
/// </summary>
public sealed class NetworkPortCheckTool : ITool
{
    private const int DefaultTimeoutMs = 2000;
    private const int MaxTimeoutMs = 10000;

    public ToolManifest Manifest { get; } = new()
    {
        Name = "network.port_check",
        Description = "Attempts a TCP connection to host:port and reports whether it is open, and the connection time, or the reason it is not.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters =
        [
            new ToolParameter("host", ToolParameterType.String, "The hostname or IP address to check."),
            new ToolParameter("port", ToolParameterType.Integer, "The TCP port to check."),
            new ToolParameter("timeoutMs", ToolParameterType.Integer, "Milliseconds to wait for a connection. Defaults to 2000, capped at 10000.", Required: false),
        ],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var host = arguments.GetRequired<string>("host");
        var port = arguments.GetRequired<int>("port");
        var requestedTimeout = arguments.TryGet<int>("timeoutMs", out var requested) && requested > 0 ? requested : DefaultTimeoutMs;
        var timeoutMs = Math.Min(requestedTimeout, MaxTimeoutMs);

        if (port is < 1 or > 65535)
        {
            return ToolCallResult.Failure($"'{port}' is not a valid TCP port (1-65535).");
        }

        using var client = new TcpClient();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await client.ConnectAsync(host, port, linkedCts.Token);
            stopwatch.Stop();
            return ToolCallResult.Success(string.Format(
                CultureInfo.InvariantCulture, "{0}:{1} is open (connected in {2} ms).", host, port, stopwatch.ElapsedMilliseconds));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolCallResult.Success(string.Format(
                CultureInfo.InvariantCulture, "{0}:{1} is not reachable: timed out after {2} ms.", host, port, timeoutMs));
        }
        catch (SocketException ex)
        {
            return ToolCallResult.Success($"{host}:{port} is not reachable: {ex.Message}");
        }
    }
}
