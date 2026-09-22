// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// Reads and validates the arguments of <c>process.metrics</c>, <c>process.tree</c> and
/// <c>process.modules</c> (ADR-0034). An out-of-range value is rejected with an explanation the
/// model can act on, never clamped: a silently widened or narrowed request is a wrong answer that
/// looks right (D-028).
/// </summary>
public static class ProcessDiagnosticsArguments
{
    /// <summary>Reads the required <c>pid</c>.</summary>
    public static bool TryReadPid(ToolArguments arguments, out int pid, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return TryPid(arguments, "pid", out pid, out error);
    }

    /// <summary>Reads the arguments of <c>process.metrics</c>.</summary>
    public static bool TryReadMetrics(ToolArguments arguments, out int pid, out int sampleMilliseconds, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        sampleMilliseconds = ProcessDiagnosticsLimits.DefaultSampleMilliseconds;

        return TryPid(arguments, "pid", out pid, out error)
            && TryInteger(
                arguments,
                "sampleMilliseconds",
                ProcessDiagnosticsLimits.DefaultSampleMilliseconds,
                ProcessDiagnosticsLimits.MinimumSampleMilliseconds,
                ProcessDiagnosticsLimits.MaximumSampleMilliseconds,
                out sampleMilliseconds,
                out error);
    }

    /// <summary>
    /// Reads the arguments of <c>process.tree</c>. <paramref name="rootPid"/> is <c>null</c> when
    /// the whole visible forest was asked for.
    /// </summary>
    public static bool TryReadTree(ToolArguments arguments, out int? rootPid, out int maxDepth, out int limit, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        rootPid = null;
        maxDepth = ProcessDiagnosticsLimits.DefaultTreeDepth;
        limit = ProcessDiagnosticsLimits.DefaultTreeRows;

        if (IsPresent(arguments, "rootPid"))
        {
            if (!TryPid(arguments, "rootPid", out var requestedRoot, out error))
            {
                return false;
            }

            rootPid = requestedRoot;
        }

        return TryInteger(arguments, "maxDepth", ProcessDiagnosticsLimits.DefaultTreeDepth, 0, ProcessDiagnosticsLimits.MaximumTreeDepth, out maxDepth, out error)
            && TryInteger(arguments, "limit", ProcessDiagnosticsLimits.DefaultTreeRows, 1, ProcessDiagnosticsLimits.MaximumTreeRows, out limit, out error);
    }

    /// <summary>Reads the arguments of <c>process.modules</c>.</summary>
    public static bool TryReadModules(ToolArguments arguments, out int pid, out int limit, out int maxOutputBytes, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        limit = ProcessDiagnosticsLimits.DefaultModules;
        maxOutputBytes = ProcessDiagnosticsLimits.DefaultModuleOutputBytes;

        return TryPid(arguments, "pid", out pid, out error)
            && TryInteger(arguments, "limit", ProcessDiagnosticsLimits.DefaultModules, 1, ProcessDiagnosticsLimits.MaximumModules, out limit, out error)
            && TryInteger(
                arguments,
                "maxOutputBytes",
                ProcessDiagnosticsLimits.DefaultModuleOutputBytes,
                ProcessDiagnosticsLimits.MinimumModuleOutputBytes,
                ProcessDiagnosticsLimits.MaximumModuleOutputBytes,
                out maxOutputBytes,
                out error);
    }

    private static bool IsPresent(ToolArguments arguments, string name)
    {
        var json = arguments.ToJson();
        return json.ContainsKey(name) && json[name] is not null;
    }

    private static bool TryPid(ToolArguments arguments, string name, out int pid, out string? error)
    {
        pid = 0;

        if (!arguments.TryGet<int>(name, out var requested))
        {
            error = $"{name} must be an integer.";
            return false;
        }

        // PID 0 is not an ordinary process on either system (the idle process on Windows, the
        // kernel's placeholder parent on Linux), so it is rejected rather than reported absent.
        if (requested < 1)
        {
            error = $"{name} must be 1 or greater.";
            return false;
        }

        pid = requested;
        error = null;
        return true;
    }

    private static bool TryInteger(
        ToolArguments arguments, string name, int fallback, int minimum, int maximum, out int value, out string? error)
    {
        value = fallback;
        error = null;
        if (!IsPresent(arguments, name))
        {
            return true;
        }

        if (!arguments.TryGet<int>(name, out value))
        {
            value = fallback;
            error = $"{name} must be an integer.";
            return false;
        }

        if (value < minimum || value > maximum)
        {
            error = $"{name} must be between {minimum} and {maximum}.";
            return false;
        }

        return true;
    }
}
