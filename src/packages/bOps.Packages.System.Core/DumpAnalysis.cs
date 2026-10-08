// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>The bounds of <c>system.dump_analyze</c> (ADR-0048 §7). Every list and string in the result is cut to these.</summary>
public static class DumpAnalysisLimits
{
    /// <summary>The longest dump path accepted, in characters (MAX_PATH; long-path prefixes are rejected).</summary>
    public const int PathCharacters = 260;

    /// <summary>The shortest dump path that can be absolute and carry a dump extension.</summary>
    public const int MinimumPathCharacters = 7;

    /// <summary>The most stack frames returned.</summary>
    public const int MaximumStackFrames = 64;

    /// <summary>The most implicated or supporting modules returned.</summary>
    public const int MaximumModules = 32;

    /// <summary>The most warnings returned.</summary>
    public const int MaximumWarnings = 32;

    /// <summary>The most characters of any single text field.</summary>
    public const int TextCharacters = 2_048;

    /// <summary>The most characters of a single stack-frame or module field.</summary>
    public const int FieldCharacters = 512;

    /// <summary>The most excerpt lines returned per kernel black box.</summary>
    public const int MaximumBlackboxLines = 64;

    /// <summary>The most characters of one black-box excerpt line.</summary>
    public const int BlackboxLineCharacters = 256;

    /// <summary>The most device instance identifiers returned per kernel black box.</summary>
    public const int MaximumDeviceIds = 32;

    /// <summary>The most characters of the fallback analysis excerpt.</summary>
    public const int RawExcerptCharacters = 4_096;

    /// <summary>The most UTF-8 bytes of the serialized result.</summary>
    public const int OutputBytes = 32_768;
}

/// <summary>The overall state of one dump analysis (ADR-0048 §8).</summary>
public enum DumpAnalysisStatus
{
    /// <summary>Every fixed section was produced and parsed, symbols resolved, and nothing was cut.</summary>
    Complete,

    /// <summary>Useful evidence was produced but at least one limitation applies; the warnings say which.</summary>
    Partial,

    /// <summary>No dump evidence could be produced (not found, access denied, debugger unavailable or failed).</summary>
    Unavailable,

    /// <summary>The dump itself was judged unreadable or corrupt by the integrity preflight.</summary>
    Invalid,
}

/// <summary>Why no or limited evidence was produced. <see cref="None"/> when the debugger ran to completion.</summary>
public enum DumpAnalysisFailure
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>The approved path does not exist.</summary>
    NotFound,

    /// <summary>The file exists or may exist but this identity may not read it; never evidence of absence.</summary>
    AccessDenied,

    /// <summary>The path was not an approved local kernel-dump location.</summary>
    InvalidPath,

    /// <summary>No usable kernel debugger is installed.</summary>
    DebuggerUnavailable,

    /// <summary>The integrity preflight rejected the dump.</summary>
    InvalidDump,

    /// <summary>The debugger did not finish within the bound.</summary>
    Timeout,

    /// <summary>The debugger could not be started or exited without usable output.</summary>
    DebuggerFailure,
}

/// <summary>The dump file the analysis was about.</summary>
/// <param name="Path">The canonical approved path.</param>
/// <param name="FileName">The file name.</param>
/// <param name="SizeBytes">The file size, or null when it could not be read.</param>
/// <param name="LastWriteUtc">The last write time, or null when it could not be read.</param>
/// <param name="Kind">kernel-small, kernel-full or live-kernel-report, from the approved location.</param>
public sealed record DumpFileDescription(string Path, string FileName, long? SizeBytes, DateTimeOffset? LastWriteUtc, string Kind);

/// <summary>Which debugger produced the evidence.</summary>
/// <param name="Engine">The engine name (kd).</param>
/// <param name="Version">The engine file version, or null.</param>
/// <param name="DumpCheckUsed">Whether the integrity preflight ran.</param>
/// <param name="DumpCheckPassed">The preflight verdict, or null when it did not run or did not finish.</param>
public sealed record DumpDebuggerDescription(string Engine, string? Version, bool DumpCheckUsed, bool? DumpCheckPassed);

/// <summary>The bugcheck the debugger reported.</summary>
/// <param name="Code">The bugcheck code as 0x-prefixed lower-case hexadecimal, or null.</param>
/// <param name="Name">The symbolic bugcheck name, or null.</param>
/// <param name="Parameters">P1-P4 in order (null for one the debugger did not print), or empty when it printed none.</param>
public sealed record DumpBugcheck(string? Code, string? Name, IReadOnlyList<string?> Parameters);

/// <summary>
/// The debugger's own automated attribution. Every field is debugger output, a heuristic, never a proven root cause.
/// </summary>
public sealed record DumpDebuggerAttribution(
    string? ProcessName,
    string? ModuleName,
    string? ImageName,
    string? SymbolName,
    string? ExceptionCode,
    string? FailureBucketId,
    string? FailureIdHash,
    string? BucketId)
{
    /// <summary>True when the debugger named no module at all.</summary>
    public bool IsEmpty => ModuleName is null && ImageName is null && SymbolName is null && FailureBucketId is null && BucketId is null;
}

/// <summary>One stack frame from the debugger's analysis stack.</summary>
public sealed record DumpStackFrame(int Index, string? Module, string? Symbol, string? Offset, string? Address);

/// <summary>One module the analysis implicated or that the stack references.</summary>
/// <param name="Name">The debugger module name.</param>
/// <param name="Start">Start address, or null.</param>
/// <param name="End">End address, or null.</param>
/// <param name="SymbolState">The debugger's symbol state for the module, or null when it was not listed.</param>
/// <param name="Implicated">True when the debugger attributed the failure to this module.</param>
public sealed record DumpModule(string Name, string? Start, string? End, string? SymbolState, bool Implicated);

/// <summary>A bounded summary of one kernel black box.</summary>
/// <param name="Available">True only when the debugger printed black-box data; false is absence of data, not health.</param>
/// <param name="DeviceIds">Device instance identifiers found in the data.</param>
/// <param name="Lines">The bounded black-box excerpt.</param>
public sealed record DumpBlackbox(bool Available, IReadOnlyList<string> DeviceIds, IReadOnlyList<string> Lines)
{
    /// <summary>A black box the debugger did not provide.</summary>
    public static DumpBlackbox Absent { get; } = new(false, [], []);
}

/// <summary>The structured, unbounded result of one analysis; <see cref="DumpAnalysisFormatting"/> bounds it.</summary>
public sealed record DumpAnalysisReport
{
    /// <summary>The overall state.</summary>
    public required DumpAnalysisStatus Status { get; init; }

    /// <summary>Why evidence is missing or limited.</summary>
    public DumpAnalysisFailure Failure { get; init; }

    /// <summary>The dump, or null when the path was never authorized.</summary>
    public DumpFileDescription? Dump { get; init; }

    /// <summary>The debugger, or null when none ran.</summary>
    public DumpDebuggerDescription? Debugger { get; init; }

    /// <summary>loaded, partial, unavailable or error; null when the debugger never ran.</summary>
    public string? SymbolsStatus { get; init; }

    /// <summary>The bugcheck, or null when none was reported.</summary>
    public DumpBugcheck? Bugcheck { get; init; }

    /// <summary>The debugger attribution, or null when the analysis section was not produced.</summary>
    public DumpDebuggerAttribution? Attribution { get; init; }

    /// <summary>The analysis stack, outermost-last.</summary>
    public IReadOnlyList<DumpStackFrame> Stack { get; init; } = [];

    /// <summary>The implicated module first, then modules the stack references.</summary>
    public IReadOnlyList<DumpModule> Modules { get; init; } = [];

    /// <summary>The PnP black box, or null when the debugger never ran.</summary>
    public DumpBlackbox? PnpBlackbox { get; init; }

    /// <summary>The boot/shutdown black box, or null when the debugger never ran.</summary>
    public DumpBlackbox? BsdBlackbox { get; init; }

    /// <summary>Stable warning codes describing every limitation.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>A bounded excerpt of the fixed analysis output, only when structured parsing found no bugcheck.</summary>
    public string? RawAnalysisExcerpt { get; init; }
}

/// <summary>Reads the single <c>path</c> argument of <c>system.dump_analyze</c>.</summary>
public static class DumpAnalysisArguments
{
    /// <summary>The only parameter name.</summary>
    public const string PathParameter = "path";

    /// <summary>Reads the path; every other argument is a contract violation.</summary>
    public static bool TryRead(ToolArguments arguments, out string? path, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        path = null;
        var json = arguments.ToJson();
        var unknown = json.Select(pair => pair.Key).FirstOrDefault(key => !string.Equals(key, PathParameter, StringComparison.Ordinal));
        if (unknown is not null)
        {
            error = $"system.dump_analyze accepts only '{PathParameter}'.";
            return false;
        }

        if (!arguments.TryGet<string>(PathParameter, out var value) || string.IsNullOrWhiteSpace(value))
        {
            error = $"{PathParameter} must be a non-empty string.";
            return false;
        }

        if (value.Length is < DumpAnalysisLimits.MinimumPathCharacters or > DumpAnalysisLimits.PathCharacters)
        {
            error = $"{PathParameter} must be {DumpAnalysisLimits.MinimumPathCharacters}-{DumpAnalysisLimits.PathCharacters} characters.";
            return false;
        }

        path = value;
        error = null;
        return true;
    }
}
