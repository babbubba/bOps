// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Network.Native.Core;

/// <summary>
/// Reads and validates the arguments of every native network.* tool (V1.3-D, ADR-0035). An
/// out-of-range value is rejected with an explanation the model can act on, never clamped: a
/// silently widened or narrowed request is a wrong answer that looks right (D-028).
/// </summary>
public static class NetworkNativeArguments
{
    /// <summary>Reads the arguments of <c>network.sockets</c>.</summary>
    public static bool TryReadSockets(
        ToolArguments arguments,
        out string protocol,
        out string? state,
        out int? pid,
        out int? localPort,
        out int limit,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        protocol = "all";
        state = null;
        pid = null;
        localPort = null;
        limit = NetworkNativeLimits.DefaultRows;

        if (IsPresent(arguments, "protocol"))
        {
            if (!arguments.TryGet<string>("protocol", out var requested) || requested is null)
            {
                error = "protocol must be a string.";
                return false;
            }

            if (requested is not ("all" or "tcp" or "udp"))
            {
                error = "protocol must be one of: all, tcp, udp.";
                return false;
            }

            protocol = requested;
        }

        if (IsPresent(arguments, "state"))
        {
            if (!arguments.TryGet<string>("state", out state) || string.IsNullOrWhiteSpace(state))
            {
                error = "state must be a non-empty string.";
                return false;
            }
        }

        if (!TryOptionalPort(arguments, "pid", 1, int.MaxValue, out pid, out error))
        {
            return false;
        }

        if (!TryOptionalPort(arguments, "localPort", 1, 65_535, out localPort, out error))
        {
            return false;
        }

        return TryInteger(arguments, "limit", NetworkNativeLimits.DefaultRows, 1, NetworkNativeLimits.MaximumRows, out limit, out error);
    }

    /// <summary>Reads the arguments of <c>network.routes</c>.</summary>
    public static bool TryReadRoutes(ToolArguments arguments, out string addressFamily, out int limit, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        addressFamily = "all";
        limit = NetworkNativeLimits.DefaultRows;

        if (!TryAddressFamily(arguments, "addressFamily", out addressFamily, out error))
        {
            return false;
        }

        return TryInteger(arguments, "limit", NetworkNativeLimits.DefaultRows, 1, NetworkNativeLimits.MaximumRows, out limit, out error);
    }

    /// <summary>Reads the arguments of <c>network.neighbors</c>.</summary>
    public static bool TryReadNeighbors(
        ToolArguments arguments, out string addressFamily, out string? interfaceName, out int limit, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        interfaceName = null;
        limit = NetworkNativeLimits.DefaultRows;

        if (!TryAddressFamily(arguments, "addressFamily", out addressFamily, out error))
        {
            return false;
        }

        if (IsPresent(arguments, "interfaceName"))
        {
            if (!arguments.TryGet<string>("interfaceName", out interfaceName) || string.IsNullOrWhiteSpace(interfaceName))
            {
                error = "interfaceName must be a non-empty string.";
                return false;
            }
        }

        return TryInteger(arguments, "limit", NetworkNativeLimits.DefaultRows, 1, NetworkNativeLimits.MaximumRows, out limit, out error);
    }

    /// <summary>Reads the arguments of <c>network.interface_stats</c>.</summary>
    public static bool TryReadInterfaceStats(
        ToolArguments arguments, out string? interfaceName, out int sampleMilliseconds, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        interfaceName = null;

        if (IsPresent(arguments, "interfaceName"))
        {
            if (!arguments.TryGet<string>("interfaceName", out interfaceName) || string.IsNullOrWhiteSpace(interfaceName))
            {
                error = "interfaceName must be a non-empty string.";
                sampleMilliseconds = NetworkNativeLimits.DefaultSampleMilliseconds;
                return false;
            }
        }

        return TryInteger(
            arguments,
            "sampleMilliseconds",
            NetworkNativeLimits.DefaultSampleMilliseconds,
            NetworkNativeLimits.MinimumSampleMilliseconds,
            NetworkNativeLimits.MaximumSampleMilliseconds,
            out sampleMilliseconds,
            out error);
    }

    /// <summary>Reads the arguments of <c>network.dns_query</c>.</summary>
    public static bool TryReadDnsQuery(
        ToolArguments arguments, out string name, out string recordType, out string? server, out int timeoutMilliseconds, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        recordType = "A";
        server = null;
        timeoutMilliseconds = NetworkNativeLimits.DefaultDnsTimeoutMilliseconds;

        if (!arguments.TryGet<string>("name", out name!) || string.IsNullOrWhiteSpace(name))
        {
            name = string.Empty;
            error = "name is required and must be a non-empty string.";
            return false;
        }

        if (name.Length > NetworkNativeLimits.NameCharacters)
        {
            error = $"name must be at most {NetworkNativeLimits.NameCharacters} characters.";
            return false;
        }

        if (!arguments.TryGet<string>("recordType", out recordType!) || recordType is not ("A" or "AAAA" or "PTR"))
        {
            error = "recordType is required and must be one of: A, AAAA, PTR.";
            return false;
        }

        if (IsPresent(arguments, "server"))
        {
            if (!arguments.TryGet<string>("server", out server) || string.IsNullOrWhiteSpace(server))
            {
                error = "server must be a non-empty string.";
                return false;
            }
        }

        return TryInteger(
            arguments,
            "timeout",
            NetworkNativeLimits.DefaultDnsTimeoutMilliseconds,
            NetworkNativeLimits.MinimumDnsTimeoutMilliseconds,
            NetworkNativeLimits.MaximumDnsTimeoutMilliseconds,
            out timeoutMilliseconds,
            out error);
    }

    /// <summary>Reads the arguments of <c>network.traceroute</c>.</summary>
    public static bool TryReadTraceroute(
        ToolArguments arguments, out string host, out int maxHops, out int timeoutMilliseconds, out string addressFamily, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        maxHops = NetworkNativeLimits.DefaultMaxHops;
        timeoutMilliseconds = NetworkNativeLimits.DefaultTracerouteTimeoutMilliseconds;
        addressFamily = "auto";

        if (!arguments.TryGet<string>("host", out host!) || string.IsNullOrWhiteSpace(host))
        {
            host = string.Empty;
            error = "host is required and must be a non-empty string.";
            return false;
        }

        if (host.Length > NetworkNativeLimits.NameCharacters)
        {
            error = $"host must be at most {NetworkNativeLimits.NameCharacters} characters.";
            return false;
        }

        if (IsPresent(arguments, "addressFamily"))
        {
            if (!arguments.TryGet<string>("addressFamily", out var requested) || requested is not ("auto" or "ipv4" or "ipv6"))
            {
                error = "addressFamily must be one of: auto, ipv4, ipv6.";
                return false;
            }

            addressFamily = requested;
        }

        return TryInteger(arguments, "maxHops", NetworkNativeLimits.DefaultMaxHops, NetworkNativeLimits.MinimumMaxHops, NetworkNativeLimits.MaximumMaxHops, out maxHops, out error)
            && TryInteger(
                arguments,
                "timeout",
                NetworkNativeLimits.DefaultTracerouteTimeoutMilliseconds,
                NetworkNativeLimits.MinimumTracerouteTimeoutMilliseconds,
                NetworkNativeLimits.MaximumTracerouteTimeoutMilliseconds,
                out timeoutMilliseconds,
                out error);
    }

    /// <summary>Reads the arguments of <c>network.ntp_probe</c>.</summary>
    public static bool TryReadNtpProbe(ToolArguments arguments, out string host, out int timeoutMilliseconds, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!arguments.TryGet<string>("host", out host!) || string.IsNullOrWhiteSpace(host))
        {
            host = string.Empty;
            timeoutMilliseconds = NetworkNativeLimits.DefaultNtpTimeoutMilliseconds;
            error = "host is required and must be a non-empty string.";
            return false;
        }

        if (host.Length > NetworkNativeLimits.NameCharacters)
        {
            timeoutMilliseconds = NetworkNativeLimits.DefaultNtpTimeoutMilliseconds;
            error = $"host must be at most {NetworkNativeLimits.NameCharacters} characters.";
            return false;
        }

        return TryInteger(
            arguments,
            "timeout",
            NetworkNativeLimits.DefaultNtpTimeoutMilliseconds,
            NetworkNativeLimits.MinimumNtpTimeoutMilliseconds,
            NetworkNativeLimits.MaximumNtpTimeoutMilliseconds,
            out timeoutMilliseconds,
            out error);
    }

    private static bool TryAddressFamily(ToolArguments arguments, string name, out string value, out string? error)
    {
        value = "all";
        if (!IsPresent(arguments, name))
        {
            error = null;
            return true;
        }

        if (!arguments.TryGet<string>(name, out var requested) || requested is not ("all" or "ipv4" or "ipv6"))
        {
            error = $"{name} must be one of: all, ipv4, ipv6.";
            return false;
        }

        value = requested;
        error = null;
        return true;
    }

    private static bool TryOptionalPort(ToolArguments arguments, string name, int minimum, int maximum, out int? value, out string? error)
    {
        value = null;
        if (!IsPresent(arguments, name))
        {
            error = null;
            return true;
        }

        if (!arguments.TryGet<int>(name, out var requested))
        {
            error = $"{name} must be an integer.";
            return false;
        }

        if (requested < minimum || requested > maximum)
        {
            error = $"{name} must be between {minimum} and {maximum}.";
            return false;
        }

        value = requested;
        error = null;
        return true;
    }

    private static bool IsPresent(ToolArguments arguments, string name)
    {
        var json = arguments.ToJson();
        return json.ContainsKey(name) && json[name] is not null;
    }

    private static bool TryInteger(
        ToolArguments arguments, string name, int fallback, int minimum, int maximum, out int value, out string? error)
    {
        value = fallback;
        error = null;
        if (!IsPresent(arguments, name))
        {
            return true;
        }

        if (!arguments.TryGet<int>(name, out value))
        {
            value = fallback;
            error = $"{name} must be an integer.";
            return false;
        }

        if (value < minimum || value > maximum)
        {
            error = $"{name} must be between {minimum} and {maximum}.";
            return false;
        }

        return true;
    }
}
