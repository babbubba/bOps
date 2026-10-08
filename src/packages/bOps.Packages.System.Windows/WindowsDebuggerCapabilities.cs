// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// Prerequisites owned by the Windows system package for Microsoft Debugging Tools for Windows
/// (ADR-0048, integrated with ADR-0049). Checks are read-only: they only inspect the bounded
/// deterministic debugger locations and never launch, install or download anything.
/// </summary>
public static class WindowsDebuggerCapabilities
{
    /// <summary>A usable <c>kd.exe</c> is required for kernel dump analysis.</summary>
    public const string KernelDumpAnalysis = "windows.debugger.kd";

    /// <summary><c>dumpchk.exe</c> is optional; when absent the KD analysis still runs without the integrity preflight.</summary>
    public const string DumpCheck = "windows.debugger.dumpchk";

    /// <summary>Operator-facing descriptor for the required KD prerequisite.</summary>
    public static PrerequisiteDescriptor KernelDumpAnalysisDescriptor { get; } = new(
        KernelDumpAnalysis,
        "Microsoft Debugging Tools for Windows (KD)",
        "kd.exe is required by system.dump_analyze to open and analyze Windows kernel crash dumps.",
        PrerequisiteKind.Executable)
    {
        Remediation = "Install the Microsoft "Debugging Tools for Windows" component so kd.exe is available, then let bOps refresh prerequisites.",
    };

    /// <summary>Operator-facing descriptor for the optional DumpChk integrity preflight.</summary>
    public static PrerequisiteDescriptor DumpCheckDescriptor { get; } = new(
        DumpCheck,
        "Microsoft DumpChk",
        "dumpchk.exe provides an optional integrity preflight before KD analyzes a Windows kernel dump.",
        PrerequisiteKind.Executable)
    {
        Remediation = "Install the Microsoft "Debugging Tools for Windows" component if you want the optional DumpChk preflight.",
    };

    /// <summary>True when an acceptable <c>kd.exe</c> is installed.</summary>
    public static Task<bool> IsKernelDumpAnalysisAvailableAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(WindowsDebuggerLocator.Locate() is not null);
    }

    /// <summary>True when the located Debugging Tools installation also contains an acceptable <c>dumpchk.exe</c>.</summary>
    public static Task<bool> IsDumpCheckAvailableAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(WindowsDebuggerLocator.Locate()?.DumpChkPath is not null);
    }
}
