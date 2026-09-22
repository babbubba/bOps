// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text;

namespace bOps.Packages.Filesystem;

internal static class WindowsRestartManager
{
    private const int ErrorMoreData = 234;
    private const int CchRmSessionKey = 32;

    /// <summary>
    /// Queries the Restart Manager for every process holding <paramref name="path"/> open.
    /// <c>Complete</c> is false whenever the diagnostic source itself failed or could not be
    /// queried fully — never inferred from an empty result, so a Restart Manager API failure can
    /// never be reported as a trustworthy "no holders found" answer.
    /// </summary>
    public static (IReadOnlyList<RestartManagerProcess> Rows, bool Complete) Find(string path, int limit)
    {
        if (!OperatingSystem.IsWindows()) return ([], false);
        var key = new char[CchRmSessionKey + 1];
        if (RmStartSession(out var session, 0, key) != 0) return ([], false);
        try
        {
            if (RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, null) != 0) return ([], false);
            uint needed = 0;
            uint count = 0;
            var result = RmGetList(session, out needed, ref count, null, out _);
            if (needed == 0 && result == 0) return ([], true);
            if (result != ErrorMoreData) return ([], false);
            var requested = Math.Min(needed, (uint)limit);
            var info = new RmProcessInfo[requested];
            count = (uint)info.Length;
            result = RmGetList(session, out needed, ref count, info, out _);
            if (result != 0) return ([], false);
            var rows = info.Take((int)count)
                .Select(value => new RestartManagerProcess(value.Process.ProcessId, value.ApplicationName))
                .ToArray();
            // Complete only when every affected process fit within the caller's limit; otherwise
            // the scan was truncated and must not be reported as exhaustive.
            return (rows, needed <= requested);
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, [Out] char[] sessionKey);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint sessionHandle, uint fileCount, string[]? fileNames, uint applicationCount, IntPtr applications, uint serviceCount, string[]? serviceNames);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint sessionHandle, out uint processInfoNeeded, ref uint processInfo, [In, Out] RmProcessInfo[]? affectedApplications, out uint rebootReasons);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);
}
