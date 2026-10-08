// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace bOps.Packages.Sys.Windows;

/// <summary>An installed Debugging Tools for Windows command-line debugger.</summary>
/// <param name="KdPath">The full path of <c>kd.exe</c>.</param>
/// <param name="DumpChkPath">The full path of <c>dumpchk.exe</c> in the same directory, or null.</param>
/// <param name="Version">The file version of <c>kd.exe</c>, or null.</param>
internal sealed record DebuggerInstallation(string KdPath, string? DumpChkPath, string? Version);

/// <summary>
/// Finds <c>kd.exe</c> deterministically (ADR-0048 §4): first each fully qualified <c>PATH</c> entry in order, then the
/// standard Windows Kits 10 Debuggers directory for this architecture under Program Files (x86) and Program Files. There is
/// no repository configuration for a debugger location, no recursive search, and nothing is launched or installed.
/// </summary>
internal static class WindowsDebuggerLocator
{
    internal const string KdFileName = "kd.exe";
    internal const string DumpChkFileName = "dumpchk.exe";
    private const int MaximumPathEntries = 64;

    /// <summary>The installation on this machine, or null.</summary>
    internal static DebuggerInstallation? Locate() => Locate(Candidates(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        RuntimeInformation.OSArchitecture));

    /// <summary>The ordered directories examined for <c>kd.exe</c>.</summary>
    internal static IReadOnlyList<string> Candidates(string? path, string? programFilesX86, string? programFiles, Architecture architecture)
    {
        var directories = new List<string>();
        foreach (var entry in (path ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaximumPathEntries))
        {
            var directory = entry.Trim('"');
            if (directory.Length > 0 && !directory.StartsWith(@"\\", StringComparison.Ordinal) && Path.IsPathFullyQualified(directory))
            {
                directories.Add(directory);
            }
        }

        var folder = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => null,
        };
        if (folder is not null)
        {
            foreach (var root in new[] { programFilesX86, programFiles })
            {
                if (!string.IsNullOrEmpty(root))
                {
                    directories.Add(Path.Combine(root, "Windows Kits", "10", "Debuggers", folder));
                }
            }
        }

        return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>The first acceptable installation among <paramref name="directories"/>, in order.</summary>
    internal static DebuggerInstallation? Locate(IEnumerable<string> directories)
    {
        foreach (var directory in directories)
        {
            var kd = Acceptable(Path.Combine(directory, KdFileName));
            if (kd is null)
            {
                continue;
            }

            var dumpChk = Acceptable(Path.Combine(directory, DumpChkFileName));
            return new DebuggerInstallation(kd.Value.Path, dumpChk?.Path, kd.Value.Version);
        }

        return null;
    }

    /// <summary>A regular, non-reparse file whose version resource names Microsoft; never launched to find out.</summary>
    private static (string Path, string? Version)? Acceptable(string candidate)
    {
        try
        {
            var info = new FileInfo(candidate);
            if (!info.Exists || (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                return null;
            }

            var version = FileVersionInfo.GetVersionInfo(info.FullName);
            return version.CompanyName?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true
                ? (info.FullName, version.FileVersion)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
