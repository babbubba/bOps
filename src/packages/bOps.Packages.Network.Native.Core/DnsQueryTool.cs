// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using bOps.Abstractions;

namespace bOps.Packages.Network.Native.Core;

/// <summary>
/// <c>network.dns_query</c> (V1.3-D, ADR-0035). Needs no OS-specific collection: the system
/// resolver is BCL, and an explicit server is queried with <see cref="DnsUdpClient"/>. Both
/// native packages contribute the same instance under their own platform id.
/// </summary>
public sealed class DnsQueryTool(string platform) : ITool
{
    /// <inheritdoc />
    public ToolManifest Manifest { get; } = NetworkNativeManifests.DnsQuery(platform);

    /// <inheritdoc />
    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!NetworkNativeArguments.TryReadDnsQuery(arguments, out var name, out var recordType, out var server, out var timeoutMilliseconds, out var error))
        {
            return ToolCallResult.Failure(error!);
        }

        ct.ThrowIfCancellationRequested();
        var result = server is null
            ? await QuerySystemResolverAsync(name, recordType, timeoutMilliseconds, ct)
            : await DnsUdpClient.QueryAsync(name, recordType, server, timeoutMilliseconds, ct);

        return ToolCallResult.Success(NetworkNativeFormatting.Format(result));
    }

    private static async Task<DnsQueryResult> QuerySystemResolverAsync(string name, string recordType, int timeoutMilliseconds, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMilliseconds));

        try
        {
            IReadOnlyList<DnsAnswer> answers = recordType switch
            {
                "PTR" => [new DnsAnswer(name, "PTR", (await Dns.GetHostEntryAsync(IPAddress.Parse(name)).WaitAsync(timeoutCts.Token)).HostName, null)],
                "AAAA" => (await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetworkV6, timeoutCts.Token))
                    .Select(address => new DnsAnswer(name, "AAAA", address.ToString(), null)).ToArray(),
                _ => (await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, timeoutCts.Token))
                    .Select(address => new DnsAnswer(name, "A", address.ToString(), null)).ToArray(),
            };

            var elapsed = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            return new DnsQueryResult(name, recordType, "system", answers, elapsed, Success: true, ErrorMessage: null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var elapsed = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            return new DnsQueryResult(name, recordType, "system", [], elapsed, Success: false, ErrorMessage: $"Timed out after {timeoutMilliseconds} ms.");
        }
        catch (SocketException ex)
        {
            var elapsed = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            return new DnsQueryResult(name, recordType, "system", [], elapsed, Success: false, ErrorMessage: ex.Message);
        }
        catch (FormatException ex)
        {
            var elapsed = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            return new DnsQueryResult(name, recordType, "system", [], elapsed, Success: false, ErrorMessage: $"name is not a valid IP address for a PTR query: {ex.Message}");
        }
    }
}
