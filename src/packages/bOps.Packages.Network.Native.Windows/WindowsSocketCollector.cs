// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using bOps.Packages.Network.Native.Core;

namespace bOps.Packages.Network.Native.Windows;

/// <summary>
/// Reads TCP and UDP sockets via <c>GetExtendedTcpTable</c>/<c>GetExtendedUdpTable</c> with the
/// owner-PID table class (V1.3-D, ADR-0035). Process names are resolved best-effort with
/// <see cref="Process.GetProcessById(int)"/>, matching <c>process.metrics</c>' Windows collection.
/// </summary>
internal static class WindowsSocketCollector
{
    private static readonly string[] TcpStateNames =
    [
        "unknown", "closed", "listen", "synSent", "synReceived", "established",
        "finWait1", "finWait2", "closeWait", "closing", "lastAck", "timeWait", "deleteTcb",
    ];

    internal static SocketsSnapshot Collect()
    {
        var entries = new List<SocketEntry>();
        entries.AddRange(ReadTcpTable(NativeMethods.AfInet));
        entries.AddRange(ReadTcpTable(NativeMethods.AfInet6));
        entries.AddRange(ReadUdpTable(NativeMethods.AfInet));
        entries.AddRange(ReadUdpTable(NativeMethods.AfInet6));

        var truncated = entries.Count > NetworkNativeLimits.CollectionScanCeiling;
        if (truncated)
        {
            entries = entries.Take(NetworkNativeLimits.CollectionScanCeiling).ToList();
        }

        var withNames = entries.Select(entry => entry with { ProcessName = ResolveProcessName(entry.Pid) }).ToArray();
        return new SocketsSnapshot(withNames, PidMappingComplete: true, PidMappingDetail: null, truncated);
    }

    private static string? ResolveProcessName(int? pid)
    {
        if (pid is not { } value || value <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(value);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static unsafe List<SocketEntry> ReadTcpTable(uint addressFamily)
    {
        uint size = 0;
        _ = NativeMethods.GetExtendedTcpTable(0, ref size, order: true, addressFamily, NativeMethods.TcpTableOwnerPidAll, 0);
        if (size == 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var status = NativeMethods.GetExtendedTcpTable(buffer, ref size, order: true, addressFamily, NativeMethods.TcpTableOwnerPidAll, 0);
            if (status != NativeMethods.NoError)
            {
                return [];
            }

            var basePtr = (byte*)buffer;
            var count = *(uint*)basePtr;
            var rowPtr = basePtr + 4;
            var rowSize = addressFamily == NativeMethods.AfInet6 ? 56 : 24;
            var results = new List<SocketEntry>((int)count);

            for (var i = 0; i < count; i++)
            {
                var row = rowPtr + (i * rowSize);
                results.Add(addressFamily == NativeMethods.AfInet6 ? ParseTcp6Row(row) : ParseTcp4Row(row));
            }

            return results;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static unsafe SocketEntry ParseTcp4Row(byte* row)
    {
        var localAddress = new IPAddress(new ReadOnlySpan<byte>(row + 4, 4));
        var localPort = ReadPort(row + 8);
        var remoteAddress = new IPAddress(new ReadOnlySpan<byte>(row + 12, 4));
        var remotePort = ReadPort(row + 16);
        var state = *(uint*)row;
        var pid = *(int*)(row + 20);

        return new SocketEntry("tcp", "ipv4", localAddress.ToString(), localPort, remoteAddress.ToString(), remotePort, StateName(state), pid, null);
    }

    private static unsafe SocketEntry ParseTcp6Row(byte* row)
    {
        var localAddress = new IPAddress(new ReadOnlySpan<byte>(row, 16));
        var localPort = ReadPort(row + 20);
        var remoteAddress = new IPAddress(new ReadOnlySpan<byte>(row + 24, 16));
        var remotePort = ReadPort(row + 44);
        var state = *(uint*)(row + 48);
        var pid = *(int*)(row + 52);

        return new SocketEntry("tcp", "ipv6", localAddress.ToString(), localPort, remoteAddress.ToString(), remotePort, StateName(state), pid, null);
    }

    private static unsafe List<SocketEntry> ReadUdpTable(uint addressFamily)
    {
        uint size = 0;
        _ = NativeMethods.GetExtendedUdpTable(0, ref size, order: true, addressFamily, NativeMethods.UdpTableOwnerPid, 0);
        if (size == 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var status = NativeMethods.GetExtendedUdpTable(buffer, ref size, order: true, addressFamily, NativeMethods.UdpTableOwnerPid, 0);
            if (status != NativeMethods.NoError)
            {
                return [];
            }

            var basePtr = (byte*)buffer;
            var count = *(uint*)basePtr;
            var rowPtr = basePtr + 4;
            var rowSize = addressFamily == NativeMethods.AfInet6 ? 28 : 12;
            var results = new List<SocketEntry>((int)count);

            for (var i = 0; i < count; i++)
            {
                var row = rowPtr + (i * rowSize);
                results.Add(addressFamily == NativeMethods.AfInet6 ? ParseUdp6Row(row) : ParseUdp4Row(row));
            }

            return results;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static unsafe SocketEntry ParseUdp4Row(byte* row)
    {
        var localAddress = new IPAddress(new ReadOnlySpan<byte>(row, 4));
        var localPort = ReadPort(row + 4);
        var pid = *(int*)(row + 8);
        return new SocketEntry("udp", "ipv4", localAddress.ToString(), localPort, null, null, null, pid, null);
    }

    private static unsafe SocketEntry ParseUdp6Row(byte* row)
    {
        var localAddress = new IPAddress(new ReadOnlySpan<byte>(row, 16));
        var localPort = ReadPort(row + 20);
        var pid = *(int*)(row + 24);
        return new SocketEntry("udp", "ipv6", localAddress.ToString(), localPort, null, null, null, pid, null);
    }

    // Port fields occupy only the low 16 bits of a DWORD, stored in network (big-endian) byte order.
    private static unsafe int ReadPort(byte* portField) => (portField[0] << 8) | portField[1];

    private static string StateName(uint state) => state < TcpStateNames.Length ? TcpStateNames[state] : "unknown";
}
