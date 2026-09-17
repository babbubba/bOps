// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace bOps.Packages.Web;

/// <summary>
/// The <see cref="SocketsHttpHandler.ConnectCallback"/> that makes <c>web.fetch</c> SSRF- and
/// DNS-rebinding-safe (ADR-0028, decision D-023). Resolves and validates the target address
/// immediately before connecting to it, inside this single callback invocation, so there is no
/// window between "checked" and "used" — the address validated is the address connected to. Runs
/// again on every new connection, so a redirect to a different host is revalidated for free.
/// </summary>
public sealed class SsrfSafeConnectCallback(IDnsResolver dnsResolver, IpAddressPolicy policy)
{
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IPAddress[] addresses;
        try
        {
            addresses = await dnsResolver.ResolveAsync(host, ct);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new WebFetchDestinationDeniedException($"Could not resolve '{host}'.", ex);
        }

        var allowed = Array.Find(addresses, policy.IsAllowed);
        if (allowed is null)
        {
            throw new WebFetchDestinationDeniedException(
                $"'{host}' resolves only to network destinations denied by policy.");
        }

        // Ownership transfers to NetworkStream(socket, ownsSocket: true) on success; the catch
        // disposes it on every other path. CA2000 cannot model the conditional hand-off.
#pragma warning disable CA2000
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
#pragma warning restore CA2000
        try
        {
            await socket.ConnectAsync(allowed, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Thrown by <see cref="SsrfSafeConnectCallback"/> when no address a hostname resolves to is
/// permitted by <see cref="IpAddressPolicy"/>, or the hostname could not be resolved at all.
/// <see cref="WebFetchService"/> catches this specifically to report an actionable "destination
/// denied by network policy" outcome rather than a generic connection failure.
/// </summary>
public sealed class WebFetchDestinationDeniedException : Exception
{
    public WebFetchDestinationDeniedException(string message)
        : base(message)
    {
    }

    public WebFetchDestinationDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WebFetchDestinationDeniedException()
    {
    }
}
