// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// Reads one counter of one process, degrading to <c>null</c> instead of throwing. A process can
/// exit, or belong to another user, or be a protected system process, between the moment a handle
/// is obtained and the moment a property is read — that race is expected, not a bug, and losing one
/// field to it must never cost the whole observation (ADR-0034).
/// </summary>
internal static class WindowsProcessCounters
{
    /// <summary>Reads a counter that always has a value when it can be read at all.</summary>
    public static T? Value<T>(Func<T> read)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return read();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return null;
        }
    }

    /// <summary>Reads a counter that can legitimately answer "no value" as well as fail.</summary>
    public static T? Optional<T>(Func<T?> read)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return read();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return null;
        }
    }

    /// <summary>Reads a string counter, or <c>null</c> when this identity cannot read it.</summary>
    public static string? Text(Func<string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return read();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return null;
        }
    }

    /// <summary>Open kernel handles, via <c>GetProcessHandleCount</c>.</summary>
    public static int? HandleCount(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return Optional<int>(() => NativeMethods.GetProcessHandleCount(process.SafeHandle, out var count) && count <= int.MaxValue
            ? (int)count
            : null);
    }

    /// <summary>Cumulative bytes read since the process started, via <c>GetProcessIoCounters</c>.</summary>
    public static long? ReadTransferBytes(Process process) => IoCounter(process, counters => counters.ReadTransferCount);

    /// <summary>Cumulative bytes written since the process started, via <c>GetProcessIoCounters</c>.</summary>
    public static long? WriteTransferBytes(Process process) => IoCounter(process, counters => counters.WriteTransferCount);

    /// <summary>Cumulative page faults since the process started, via <c>K32GetProcessMemoryInfo</c>.</summary>
    public static long? PageFaults(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return Optional<long>(() =>
        {
            var size = (uint)Marshal.SizeOf<NativeMethods.ProcessMemoryCounters>();
            return NativeMethods.GetProcessMemoryInfo(process.SafeHandle, out var counters, size)
                ? counters.PageFaultCount
                : null;
        });
    }

    private static long? IoCounter(Process process, Func<NativeMethods.IoCounters, ulong> select)
    {
        ArgumentNullException.ThrowIfNull(process);
        return Optional<long>(() =>
        {
            if (!NativeMethods.GetProcessIoCounters(process.SafeHandle, out var counters))
            {
                return null;
            }

            var value = select(counters);
            return value <= long.MaxValue ? (long)value : null;
        });
    }

    private static bool IsExpected(Exception exception) =>
        exception is Win32Exception or InvalidOperationException or NotSupportedException or PlatformNotSupportedException;
}
