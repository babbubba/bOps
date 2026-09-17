// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;

namespace bOps.Packages.Web;

/// <summary>
/// Deny-by-default IP-range policy for <c>web.fetch</c> (ADR-0028, decision D-023). Denies
/// loopback, link-local (including the <c>169.254.169.254</c> cloud-metadata address), RFC 1918
/// private ranges, RFC 6598 shared carrier-grade-NAT space, IPv6 unique-local and link-local,
/// multicast and unspecified addresses. An explicit, narrow operator allowlist is the only
/// override, checked before the deny rules. IPv4-mapped IPv6 addresses are normalized to their
/// IPv4 form before either check runs, so the range they actually wrap cannot slip past the
/// IPv6-shaped rules.
/// </summary>
public sealed class IpAddressPolicy
{
    private readonly IReadOnlyList<IPAddress> _allowedAddresses;
    private readonly IReadOnlyList<(IPAddress Network, int PrefixLength)> _allowedNetworks;

    public IpAddressPolicy(IEnumerable<string>? allowedAddresses = null, IEnumerable<string>? allowedNetworks = null)
    {
        _allowedAddresses = (allowedAddresses ?? [])
            .Select(value => IPAddress.Parse(value))
            .Select(Normalize)
            .ToList();

        _allowedNetworks = (allowedNetworks ?? [])
            .Select(ParseCidr)
            .ToList();
    }

    /// <summary>The empty policy: default deny rules only, no operator allowlist.</summary>
    public static IpAddressPolicy Default { get; } = new();

    /// <summary>True when <paramref name="address"/> may be connected to under this policy.</summary>
    public bool IsAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var normalized = Normalize(address);

        return IsExplicitlyAllowed(normalized) || !IsDeniedByDefault(normalized);
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private bool IsExplicitlyAllowed(IPAddress address) =>
        _allowedAddresses.Any(allowed => allowed.Equals(address)) ||
        _allowedNetworks.Any(network => IsInNetwork(address, network.Network, network.PrefixLength));

    private static bool IsDeniedByDefault(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsDeniedIPv4(address),
            AddressFamily.InterNetworkV6 => IsDeniedIPv6(address),
            _ => true, // Unknown family: fail closed rather than assume it is safe.
        };
    }

    private static bool IsDeniedIPv4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 0                                        // 0.0.0.0/8 — "this network"
            || b[0] == 10                                       // 10.0.0.0/8 — RFC 1918
            || b[0] == 127                                      // 127.0.0.0/8 — loopback
            || (b[0] == 100 && b[1] is >= 64 and <= 127)         // 100.64.0.0/10 — RFC 6598 CGNAT
            || (b[0] == 172 && b[1] is >= 16 and <= 31)          // 172.16.0.0/12 — RFC 1918
            || (b[0] == 169 && b[1] == 254)                      // 169.254.0.0/16 — link-local, incl. cloud metadata
            || (b[0] == 192 && b[1] == 168)                      // 192.168.0.0/16 — RFC 1918
            || b[0] is >= 224 and <= 239                         // 224.0.0.0/4 — multicast
            || address.Equals(IPAddress.Broadcast);
    }

    private static bool IsDeniedIPv6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
        {
            return true;
        }

        var b = address.GetAddressBytes();
        return (b[0] & 0xFE) == 0xFC; // fc00::/7 — unique local
    }

    private static bool IsInNetwork(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)~(0xFF >> remainingBits);
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }

    private static (IPAddress Network, int PrefixLength) ParseCidr(string cidr)
    {
        var parts = cidr.Split('/', 2);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var prefixLength))
        {
            throw new FormatException($"'{cidr}' is not a valid CIDR network (expected 'address/prefixLength').");
        }

        var maxPrefix = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxPrefix)
        {
            throw new FormatException($"'{cidr}' has a prefix length out of range for its address family.");
        }

        return (Normalize(network), prefixLength);
    }
}
