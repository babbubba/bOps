// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace bOps.Packages.Network.Native.Windows;

/// <summary>
/// <c>iphlpapi.dll</c> declarations for socket/route/neighbor collection (V1.3-D, ADR-0035).
/// <c>LibraryImport</c> source-generated marshalling only — no <c>DllImport</c> — matching
/// <c>bOps.Packages.System.Windows</c>'s kernel32 calls (ADR-0034 precedent).
/// </summary>
internal static partial class NativeMethods
{
    internal const int AfInet = 2;
    internal const int AfInet6 = 23;
    internal const int TcpTableOwnerPidAll = 5;
    internal const int UdpTableOwnerPid = 1;
    internal const int NoError = 0;
    internal const int ErrorInsufficientBuffer = 122;

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint GetExtendedTcpTable(
        nint tcpTable, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint addressFamily, uint tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint GetExtendedUdpTable(
        nint udpTable, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint addressFamily, uint tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint GetIpForwardTable2(ushort addressFamily, out nint table);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint GetIpNetTable2(ushort addressFamily, out nint table);

    [LibraryImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial void FreeMibTable(nint memory);
}
