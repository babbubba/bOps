// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json.Nodes;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// Formats a <see cref="DumpAnalysisReport"/> as bounded JSON (schemaVersion 1, ADR-0048 §7-§8). Every string and list is
/// cut to <see cref="DumpAnalysisLimits"/>, and when the result is still too large whole items are dropped in a fixed order:
/// the raw excerpt, then black-box excerpt lines, then non-implicated modules, then the stack tail, then warnings.
/// </summary>
public static class DumpAnalysisFormatting
{
    /// <summary>The current output schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>A fixed, runtime-authored reading note; never derived from debugger output.</summary>
    public const string Interpretation =
        "Debugger evidence, not a diagnosis. analysis fields are the debugger's automated attribution: they say where the debugger "
        + "found the failure, not that the named module caused it. Correlate with the bugcheck, stack, hardware (WHEA), PnP, "
        + "driver versions and timing before stating a cause. Missing data from a small dump or unresolved symbols is not evidence "
        + "that a component is healthy.";

    /// <summary>The stable warning added when the formatter had to drop data.</summary>
    public const string TruncatedWarning = "output-truncated";

    /// <summary>Formats <paramref name="report"/> within <see cref="DumpAnalysisLimits.OutputBytes"/>.</summary>
    public static string Format(DumpAnalysisReport report) => Format(report, DumpAnalysisLimits.OutputBytes);

    internal static string Format(DumpAnalysisReport report, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(report);
        var stack = report.Stack.Take(DumpAnalysisLimits.MaximumStackFrames).ToList();
        var modules = report.Modules.Take(DumpAnalysisLimits.MaximumModules).ToList();
        var warnings = report.Warnings.Distinct(StringComparer.Ordinal).Take(DumpAnalysisLimits.MaximumWarnings).ToList();
        var pnp = Bound(report.PnpBlackbox);
        var bsd = Bound(report.BsdBlackbox);
        var raw = Bounded(report.RawAnalysisExcerpt, DumpAnalysisLimits.RawExcerptCharacters);
        var truncated = report.Stack.Count > stack.Count || report.Modules.Count > modules.Count
            || report.Warnings.Distinct(StringComparer.Ordinal).Count() > warnings.Count
            || (report.RawAnalysisExcerpt?.Length ?? 0) > DumpAnalysisLimits.RawExcerptCharacters
            || pnp.Truncated || bsd.Truncated;

        string output;
        while (true)
        {
            output = Build(report, stack, modules, warnings, pnp.Box, bsd.Box, raw, truncated).ToJsonString();
            if (Encoding.UTF8.GetByteCount(output) <= maximumBytes)
            {
                return output;
            }

            truncated = true;
            if (raw is not null) { raw = null; continue; }
            if (pnp.Box is { Lines.Count: > 0 }) { pnp = (pnp.Box with { Lines = pnp.Box.Lines.Take(pnp.Box.Lines.Count / 2).ToArray() }, true); continue; }
            if (bsd.Box is { Lines.Count: > 0 }) { bsd = (bsd.Box with { Lines = bsd.Box.Lines.Take(bsd.Box.Lines.Count / 2).ToArray() }, true); continue; }
            var supporting = modules.FindLastIndex(module => !module.Implicated);
            if (supporting >= 0) { modules.RemoveAt(supporting); continue; }
            if (stack.Count > 0) { stack.RemoveAt(stack.Count - 1); continue; }
            if (pnp.Box is { DeviceIds.Count: > 0 }) { pnp = (pnp.Box with { DeviceIds = [] }, true); continue; }
            if (bsd.Box is { DeviceIds.Count: > 0 }) { bsd = (bsd.Box with { DeviceIds = [] }, true); continue; }
            if (warnings.Count > 1) { warnings.RemoveAt(warnings.Count - 1); continue; }
            throw new InvalidOperationException("The dump analysis envelope does not fit the output bound.");
        }
    }

    private static JsonObject Build(
        DumpAnalysisReport report,
        IReadOnlyList<DumpStackFrame> stack,
        IReadOnlyList<DumpModule> modules,
        IReadOnlyList<string> warnings,
        DumpBlackbox? pnp,
        DumpBlackbox? bsd,
        string? raw,
        bool truncated)
    {
        var status = truncated && report.Status == DumpAnalysisStatus.Complete ? DumpAnalysisStatus.Partial : report.Status;
        var allWarnings = truncated && !warnings.Contains(TruncatedWarning, StringComparer.Ordinal)
            ? warnings.Append(TruncatedWarning).ToArray()
            : warnings.ToArray();
        return new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["status"] = StatusName(status),
            ["complete"] = status == DumpAnalysisStatus.Complete,
            ["truncated"] = truncated,
            ["failure"] = report.Failure == DumpAnalysisFailure.None ? null : FailureName(report.Failure),
            ["dump"] = report.Dump is null ? null : new JsonObject
            {
                ["path"] = Bounded(report.Dump.Path, DumpAnalysisLimits.PathCharacters),
                ["fileName"] = Bounded(report.Dump.FileName, DumpAnalysisLimits.PathCharacters),
                ["sizeBytes"] = report.Dump.SizeBytes,
                ["lastWriteUtc"] = EvidenceTime.Format(report.Dump.LastWriteUtc),
                ["kind"] = report.Dump.Kind,
            },
            ["debugger"] = report.Debugger is null ? null : new JsonObject
            {
                ["engine"] = report.Debugger.Engine,
                ["version"] = Bounded(report.Debugger.Version, 64),
                ["dumpCheckUsed"] = report.Debugger.DumpCheckUsed,
                ["dumpCheckPassed"] = report.Debugger.DumpCheckPassed,
            },
            ["symbols"] = report.SymbolsStatus is null ? null : new JsonObject { ["status"] = report.SymbolsStatus },
            ["bugcheck"] = report.Bugcheck is null ? null : new JsonObject
            {
                ["code"] = Bounded(report.Bugcheck.Code, 32),
                ["name"] = Bounded(report.Bugcheck.Name, DumpAnalysisLimits.FieldCharacters),
                ["parameters"] = new JsonArray(report.Bugcheck.Parameters.Take(4)
                    .Select(parameter => (JsonNode?)JsonValue.Create(Bounded(parameter, 32))).ToArray()),
            },
            ["analysis"] = report.Attribution is null ? null : new JsonObject
            {
                ["qualifier"] = "debugger-attribution",
                ["processName"] = Text(report.Attribution.ProcessName),
                ["moduleName"] = Text(report.Attribution.ModuleName),
                ["imageName"] = Text(report.Attribution.ImageName),
                ["symbolName"] = Text(report.Attribution.SymbolName),
                ["exceptionCode"] = Text(report.Attribution.ExceptionCode),
                ["failureBucketId"] = Text(report.Attribution.FailureBucketId),
                ["failureIdHash"] = Text(report.Attribution.FailureIdHash),
                ["bucketId"] = Text(report.Attribution.BucketId),
            },
            ["stack"] = new JsonArray(stack.Select(frame => (JsonNode)new JsonObject
            {
                ["index"] = frame.Index,
                ["module"] = Field(frame.Module),
                ["symbol"] = Field(frame.Symbol),
                ["offset"] = Bounded(frame.Offset, 32),
                ["address"] = Bounded(frame.Address, 32),
            }).ToArray()),
            ["modules"] = new JsonArray(modules.Select(module => (JsonNode)new JsonObject
            {
                ["name"] = Field(module.Name),
                ["start"] = Bounded(module.Start, 32),
                ["end"] = Bounded(module.End, 32),
                ["symbolState"] = Bounded(module.SymbolState, 64),
                ["implicated"] = module.Implicated,
            }).ToArray()),
            ["blackbox"] = pnp is null && bsd is null ? null : new JsonObject
            {
                ["pnp"] = Blackbox(pnp),
                ["bsd"] = Blackbox(bsd),
            },
            ["warnings"] = new JsonArray(allWarnings.Select(warning => (JsonNode?)JsonValue.Create(Bounded(warning, 128))).ToArray()),
            ["rawAnalysisExcerpt"] = raw,
            ["interpretation"] = Interpretation,
        };
    }

    private static JsonObject? Blackbox(DumpBlackbox? box) => box is null ? null : new JsonObject
    {
        ["available"] = box.Available,
        ["deviceIds"] = new JsonArray(box.DeviceIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        ["lines"] = new JsonArray(box.Lines.Select(line => (JsonNode?)JsonValue.Create(line)).ToArray()),
    };

    private static (DumpBlackbox? Box, bool Truncated) Bound(DumpBlackbox? box)
    {
        if (box is null)
        {
            return (null, false);
        }

        var ids = box.DeviceIds.Distinct(StringComparer.OrdinalIgnoreCase).Take(DumpAnalysisLimits.MaximumDeviceIds)
            .Select(id => Bounded(id, DumpAnalysisLimits.BlackboxLineCharacters)!).ToArray();
        var lines = box.Lines.Take(DumpAnalysisLimits.MaximumBlackboxLines)
            .Select(line => Bounded(line, DumpAnalysisLimits.BlackboxLineCharacters)!).ToArray();
        var truncated = box.DeviceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() > ids.Length
            || box.Lines.Count > lines.Length
            || box.Lines.Any(line => line.Length > DumpAnalysisLimits.BlackboxLineCharacters);
        return (new DumpBlackbox(box.Available, ids, lines), truncated);
    }

    private static string? Text(string? value) => Bounded(value, DumpAnalysisLimits.TextCharacters);

    private static string? Field(string? value) => Bounded(value, DumpAnalysisLimits.FieldCharacters);

    private static string? Bounded(string? value, int maximumCharacters) =>
        value is null || value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    /// <summary>The wire name of <paramref name="status"/>.</summary>
    public static string StatusName(DumpAnalysisStatus status) => status switch
    {
        DumpAnalysisStatus.Complete => "complete",
        DumpAnalysisStatus.Partial => "partial",
        DumpAnalysisStatus.Unavailable => "unavailable",
        DumpAnalysisStatus.Invalid => "invalid",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>The wire name of <paramref name="failure"/>.</summary>
    public static string FailureName(DumpAnalysisFailure failure) => failure switch
    {
        DumpAnalysisFailure.None => "none",
        DumpAnalysisFailure.NotFound => "not-found",
        DumpAnalysisFailure.AccessDenied => "access-denied",
        DumpAnalysisFailure.InvalidPath => "invalid-path",
        DumpAnalysisFailure.DebuggerUnavailable => "debugger-unavailable",
        DumpAnalysisFailure.InvalidDump => "invalid-dump",
        DumpAnalysisFailure.Timeout => "timeout",
        DumpAnalysisFailure.DebuggerFailure => "debugger-failure",
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };
}
