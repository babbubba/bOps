// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>Finite limits for <c>process.metrics</c>, <c>process.tree</c> and <c>process.modules</c> (ADR-0034).</summary>
public static class ProcessDiagnosticsLimits
{
    /// <summary>Default interval between the two <c>process.metrics</c> samples, in milliseconds.</summary>
    public const int DefaultSampleMilliseconds = 500;

    /// <summary>Shortest interval a caller may request: below this the counters are dominated by their own resolution.</summary>
    public const int MinimumSampleMilliseconds = 200;

    /// <summary>Longest interval a caller may request, kept well inside the default tool timeout.</summary>
    public const int MaximumSampleMilliseconds = 5_000;

    /// <summary>Default number of generations below the root that <c>process.tree</c> walks.</summary>
    public const int DefaultTreeDepth = 4;

    /// <summary>Deepest walk a caller may request.</summary>
    public const int MaximumTreeDepth = 16;

    /// <summary>Default number of rows <c>process.tree</c> returns.</summary>
    public const int DefaultTreeRows = 200;

    /// <summary>Largest number of rows a caller may request.</summary>
    public const int MaximumTreeRows = 2_000;

    /// <summary>Most processes a tree collector examines for one call.</summary>
    public const int TreeScanCeiling = 10_000;

    /// <summary>Default number of modules <c>process.modules</c> returns.</summary>
    public const int DefaultModules = 200;

    /// <summary>Largest number of modules a caller may request.</summary>
    public const int MaximumModules = 1_000;

    /// <summary>Most mapped regions a module collector examines for one call.</summary>
    public const int ModuleScanCeiling = 8_000;

    /// <summary>Default maximum UTF-8 output size for <c>process.modules</c>.</summary>
    public const int DefaultModuleOutputBytes = 32_768;

    /// <summary>Smallest supported UTF-8 output budget for <c>process.modules</c>.</summary>
    public const int MinimumModuleOutputBytes = 4_096;

    /// <summary>Largest supported UTF-8 output budget for <c>process.modules</c>.</summary>
    public const int MaximumModuleOutputBytes = 131_072;

    /// <summary>Longest process or module name returned.</summary>
    public const int NameCharacters = 256;

    /// <summary>Longest executable or module path returned.</summary>
    public const int PathCharacters = 1_024;

    /// <summary>Longest command line returned.</summary>
    public const int CommandLineCharacters = 2_048;

    /// <summary>Longest user name returned.</summary>
    public const int UserCharacters = 256;

    /// <summary>Longest module version returned.</summary>
    public const int VersionCharacters = 128;
}
