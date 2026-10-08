// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// The capabilities this package owns for Microsoft Debugging Tools for Windows (ADR-0048 §3). Only a composition root
/// registers the check, so no core project names a debugger (rule A1).
/// </summary>
public static class WindowsDebuggerCapabilities
{
    /// <summary>A usable <c>kd.exe</c> was found by the deterministic, bounded locator.</summary>
    public const string KernelDumpAnalysis = "windows.debugger.kd";

    /// <summary>
    /// True when an acceptable <c>kd.exe</c> is installed. File checks only: nothing is launched, installed or downloaded.
    /// </summary>
    public static Task<bool> IsKernelDumpAnalysisAvailableAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(WindowsDebuggerLocator.Locate() is not null);
    }
}
