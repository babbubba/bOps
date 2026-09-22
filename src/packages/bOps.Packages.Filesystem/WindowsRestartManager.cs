// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text;

namespace bOps.Packages.Filesystem;

internal static class WindowsRestartManager
{
    private const int ErrorMoreData = 234;
    private const int CchRmSessionKey = 32;

    public static IReadOnlyList<RestartManagerProcess> Find(string path, int limit)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var key = new char[CchRmSessionKey + 1];
        if (RmStartSession(out var session, 0, key) != 0) return [];
        try
        {
            if (RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, null) != 0) return [];
            uint needed = 0;
            uint count = 0;
            var result = RmGetList(session, out needed, ref count, null, out _);
            if (result != ErrorMoreData || needed == 0) return [];
            var info = new RmProcessInfo[Math.Min(needed, (uint)limit)];
            count = (uint)info.Length;
            result = RmGetList(session, out needed, ref count, info, out _);
            if (result != 0) return [];
            return info.Take((int)count)
                .Select(value => new RestartManagerProcess(value.Process.ProcessId, value.ApplicationName))
                .ToArray();
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
