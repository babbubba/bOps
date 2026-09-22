// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace bOps.Packages.Filesystem;

internal static class LinuxFileStatus
{
    public static bool TryRead(string path, out LinuxStat stat)
    {
        stat = default;
        return OperatingSystem.IsLinux() && NativeStat(path, out stat) == 0;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true, EntryPoint = "stat")]
    private static extern int NativeStat(string path, out LinuxStat buffer);
}
