// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// Reads the parent, image path, command line and owner of a process from WMI's
/// <c>Win32_Process</c> (ADR-0034). This is the managed CIM client, not a command: there is no
/// PowerShell, no <c>wmic.exe</c>, no elevation and no <c>SeDebugPrivilege</c>. A process this
/// identity may not read degrades to <c>null</c> fields, never to an exception that would cost the
/// whole observation.
/// <para>
/// The only value ever interpolated into a WQL query here is a PID that
/// <see cref="ProcessDiagnosticsArguments"/> already validated as an integer, so no model-produced
/// text reaches the query language (agentic/02-coding-standards.md, "Forbidden").
/// </para>
/// </summary>
internal static class WindowsProcessInformation
{
    /// <summary>Reads one process, or <c>null</c> when WMI could not answer for it at all.</summary>
    public static WindowsProcessIdentity? TryRead(int pid)
    {
        try
        {
            var query = new ObjectQuery(string.Create(
                CultureInfo.InvariantCulture,
                $"SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process WHERE ProcessId = {pid}"));
            using var searcher = new ManagementObjectSearcher(query);
            using var results = searcher.Get();
            foreach (var row in results)
            {
                using (row)
                {
                    return ToIdentity(row);
                }
            }

            return null;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return null;
        }
    }

    /// <summary>Reads the owner of one process as <c>DOMAIN\user</c>, or <c>null</c> when it cannot be read.</summary>
    public static string? TryReadOwner(int pid)
    {
        try
        {
            // Bound by object path rather than by a query, because GetOwner is a method on the
            // instance and a projected query result carries no path to invoke it against.
            using var process = new ManagementObject(string.Create(CultureInfo.InvariantCulture, $"Win32_Process.Handle='{pid}'"));
            using var owner = process.InvokeMethod("GetOwner", null, null);
            if (owner is null || Convert.ToInt32(owner["ReturnValue"], CultureInfo.InvariantCulture) != 0)
            {
                return null;
            }

            var user = owner["User"] as string;
            if (string.IsNullOrEmpty(user))
            {
                return null;
            }

            var domain = owner["Domain"] as string;
            return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Reads every visible process's PID, parent PID and name in one query. Deliberately does not
    /// ask for the command line: a whole-machine listing has no business carrying one.
    /// </summary>
    public static ProcessTreeSnapshot ReadTree(int scanCeiling, CancellationToken ct)
    {
        var entries = new List<ProcessTreeEntry>();
        var skipped = 0;
        var truncated = false;

        try
        {
            using var searcher = new ManagementObjectSearcher(new ObjectQuery("SELECT ProcessId, ParentProcessId, Name FROM Win32_Process"));
            using var results = searcher.Get();
            foreach (var row in results)
            {
                ct.ThrowIfCancellationRequested();
                using (row)
                {
                    if (entries.Count >= scanCeiling)
                    {
                        truncated = true;
                        break;
                    }

                    // Read only what this query projected: asking a projected result for a property
                    // the SELECT left out is a WMI error, not a null.
                    if (row["ProcessId"] is not uint pid)
                    {
                        skipped++;
                        continue;
                    }

                    entries.Add(new ProcessTreeEntry((int)pid, ToParentPid(row["ParentProcessId"]), row["Name"] as string));
                }
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            // A WMI failure part-way through leaves what was already read; the rows that were not
            // reached are the difference between this listing and the machine, and the caller is
            // told the answer is not complete rather than being handed a plausible short tree.
            skipped++;
        }

        return new ProcessTreeSnapshot(entries, skipped, truncated);
    }

    private static WindowsProcessIdentity? ToIdentity(ManagementBaseObject row)
    {
        if (row["ProcessId"] is not uint pid)
        {
            return null;
        }

        return new WindowsProcessIdentity(
            (int)pid,
            ToParentPid(row["ParentProcessId"]),
            row["Name"] as string,
            row["ExecutablePath"] as string,
            row["CommandLine"] as string);
    }

    private static int? ToParentPid(object? value) =>
        value is uint parent and <= int.MaxValue ? (int)parent : null;

    private static bool IsExpected(Exception exception) =>
        exception is ManagementException or UnauthorizedAccessException or COMException
            or InvalidCastException or NotSupportedException or InvalidOperationException;
}
