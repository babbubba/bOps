// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>The <c>kind</c> vocabulary of <c>system.crashes</c> (ADR-0032 HARDEN-7 amendment §4).</summary>
public static class CrashKinds
{
    /// <summary>A user-mode application crash.</summary>
    public const string ApplicationCrash = "application-crash";

    /// <summary>A user-mode application hang.</summary>
    public const string ApplicationHang = "application-hang";

    /// <summary>A kernel bugcheck (blue screen).</summary>
    public const string KernelBugcheck = "kernel-bugcheck";

    /// <summary>A kernel live dump taken without stopping the machine.</summary>
    public const string KernelLiveDump = "kernel-live-dump";

    /// <summary>Another error report; its event name is kept.</summary>
    public const string Wer = "wer";

    /// <summary>A Linux core dump.</summary>
    public const string Coredump = "coredump";
}

/// <summary>
/// One normalized crash: on Windows the merge of every native record that shares a report identity (ADR-0041 §7), on Linux one core
/// dump. <see cref="TimestampUtc"/> is the primary time, of kind <see cref="TimestampKind"/>; <see cref="Source"/> is the evidence that
/// supplied it. Untrusted text: names and paths come from the machine.
/// </summary>
/// <param name="TimestampUtc">The primary time (ADR-0041 §6).</param>
/// <param name="TimestampKind">Whether <paramref name="TimestampUtc"/> is an occurrence or a reporting time.</param>
/// <param name="Source">The source that supplied <paramref name="TimestampUtc"/>.</param>
/// <param name="Kind">The crash kind (<see cref="CrashKinds"/>), or <c>null</c> when unknown.</param>
public sealed record CrashEvidence(DateTimeOffset TimestampUtc, EvidenceTimestampKind TimestampKind, string Source, string? Kind)
{
    /// <summary>The earliest WER processing time when the primary time is an occurrence time and a WER report was merged; else <c>null</c>.</summary>
    public DateTimeOffset? ReportedUtc { get; init; }

    /// <summary>The crashed or hung application (image name), when known.</summary>
    public string? Process { get; init; }

    /// <summary>The process id, when known.</summary>
    public int? Pid { get; init; }

    /// <summary>A dump reference: a path string only, never opened.</summary>
    public string? DumpPath { get; init; }

    /// <summary>The report id, else the native record or crash id of the primary source.</summary>
    public string? EventIdOrCrashId { get; init; }

    /// <summary>The report identity (lower-case GUID), or <c>null</c> when the crash has none.</summary>
    public string? ReportId { get; init; }

    /// <summary>The native error-report event name as written by the machine, when there is one.</summary>
    public string? EventName { get; init; }

    /// <summary>A bugcheck code, canonical lower-case hexadecimal with a <c>0x</c> prefix.</summary>
    public string? BugcheckCode { get; init; }

    /// <summary>A live-dump code, canonical lower-case hexadecimal with a <c>0x</c> prefix.</summary>
    public string? LiveDumpCode { get; init; }

    /// <summary>A Windows exception code, canonical lower-case hexadecimal with a <c>0x</c> prefix.</summary>
    public string? ExceptionCode { get; init; }

    /// <summary>A Linux core dump's terminating signal number, in decimal.</summary>
    public string? SignalCode { get; init; }

    /// <summary>The faulting module, when known.</summary>
    public string? FaultModule { get; init; }

    /// <summary>The error-report bucket, when known.</summary>
    public string? Bucket { get; init; }

    /// <summary>The names of the sources the crash was merged from.</summary>
    public IReadOnlyList<string> EvidenceSources { get; init; } = [];

    /// <summary>The one canonical code of this kind: bugcheck, live-dump, exception or signal code; otherwise <c>null</c>.</summary>
    public string? Code => Kind switch
    {
        CrashKinds.KernelBugcheck => BugcheckCode,
        CrashKinds.KernelLiveDump => LiveDumpCode,
        CrashKinds.ApplicationCrash or CrashKinds.ApplicationHang => ExceptionCode,
        CrashKinds.Coredump => SignalCode,
        _ => null,
    };

    /// <summary>The non-null parts of the event name, code and faulting module, joined with <c>"; "</c>; <c>null</c> when there are none.</summary>
    public string? Summary
    {
        get
        {
            var parts = new[] { EventName, Code, FaultModule }.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray();
            return parts.Length == 0 ? null : string.Join("; ", parts);
        }
    }
}

/// <summary>Normalized crashes plus the explicit status of every source and the stores behind them.</summary>
/// <param name="Items">The crashes found, in any order.</param>
/// <param name="Sources">One entry per source, so a gap is never mistaken for an empty source.</param>
/// <param name="Warnings">Bounded, non-identifying explanations.</param>
/// <param name="CollectionTruncated">True when a ceiling, a time bound or the read budget cut collection.</param>
public sealed record CrashEvidenceSnapshot(
    IReadOnlyList<CrashEvidence> Items,
    IReadOnlyList<MaintenanceSource> Sources,
    IReadOnlyList<string>? Warnings = null,
    bool CollectionTruncated = false)
{
    /// <summary>The history-bearing stores behind <see cref="Sources"/>.</summary>
    public IReadOnlyList<CoverageStore> Stores { get; init; } = [];
}

/// <summary>A validated <c>system.crashes</c> request.</summary>
/// <param name="Mode">Aggregate (the default) or raw.</param>
/// <param name="FromUtc">Start of the window, inclusive.</param>
/// <param name="ToUtc">End of the window, inclusive.</param>
/// <param name="Limit">Groups (aggregate) or rows (raw) returned.</param>
public sealed record CrashesQuery(EvidenceMode Mode, DateTimeOffset FromUtc, DateTimeOffset ToUtc, int Limit);

/// <summary>Reads <c>system.crashes</c> arguments (ADR-0032 HARDEN-7 amendment §2, §4). Invalid values are rejected, never clamped.</summary>
public static class SystemCrashesArguments
{
    /// <summary>The accepted <c>mode</c> values; <c>aggregate</c>, the default, first.</summary>
    public static readonly IReadOnlyList<string> ModeNames = ["aggregate", "raw"];

    private static readonly HorizonRules Horizon = new(
        "sinceMinutes",
        "sinceDays",
        EvidenceMode.Aggregate,
        SystemMaintenanceLimits.DefaultSinceMinutes,
        SystemMaintenanceLimits.MaximumSinceMinutes,
        SystemMaintenanceLimits.MaximumCrashSinceDays);

    /// <summary>Validates <paramref name="arguments"/> for a window that ends at <paramref name="now"/>.</summary>
    public static bool TryRead(ToolArguments arguments, DateTimeOffset now, out CrashesQuery? query, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        query = null;
        var json = arguments.ToJson();
        if (!EvidenceModes.TryReadHorizon(json, arguments, Horizon, out var mode, out var window, out error))
        {
            return false;
        }

        var limit = SystemMaintenanceLimits.DefaultCrashes;
        if (json.ContainsKey("limit") && json["limit"] is not null)
        {
            if (!arguments.TryGet<int>("limit", out limit))
            {
                error = "limit must be an integer.";
                return false;
            }

            if (limit < 1 || limit > SystemMaintenanceLimits.MaximumCrashes)
            {
                error = $"limit must be between 1 and {SystemMaintenanceLimits.MaximumCrashes}.";
                return false;
            }
        }

        query = new CrashesQuery(mode, now.Subtract(window).ToUniversalTime(), now.ToUniversalTime(), limit);
        return true;
    }
}

/// <summary>
/// The bounded, deterministic JSON of <c>system.crashes</c>, schema 2 (ADR-0032 HARDEN-7 amendment §4): crash groups by default, crash
/// rows with <c>mode: raw</c>; a fixed 65,536-byte budget in both modes; temporal coverage; <c>complete</c> only when every source was
/// read, nothing was cut and every log reaches the start of the request.
/// </summary>
public static class SystemCrashFormatting
{
    /// <summary>The result schema version.</summary>
    public const int SchemaVersion = 2;

    /// <summary>The most source names one group lists.</summary>
    public const int MaximumGroupSources = 8;

    /// <summary>Formats <paramref name="snapshot"/> for <paramref name="query"/>.</summary>
    public static string Format(CrashEvidenceSnapshot snapshot, CrashesQuery query, TimeSpan? catalogAge = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);
        var inWindow = snapshot.Items
            .Where(item => item.TimestampUtc >= query.FromUtc && item.TimestampUtc <= query.ToUtc)
            .OrderByDescending(item => item.TimestampUtc)
            .ThenBy(item => item.Process, KeyComparer.Instance)
            .ThenBy(item => item.Source, KeyComparer.Instance)
            .ThenBy(item => item.ReportId, KeyComparer.Instance)
            .ToArray();
        var status = Status(snapshot.Sources);
        var statuses = new Dictionary<string, InventorySourceStatus>(StringComparer.Ordinal);
        foreach (var source in snapshot.Sources)
        {
            statuses.TryAdd(source.Name, source.Status);
        }

        var stores = TemporalCoverage.Listed(snapshot.Stores, statuses);
        var coverage = TemporalCoverage.ToJson(stores, query.FromUtc, query.ToUtc);
        var coverageState = coverage["state"]!.GetValue<string>();
        var warnings = (snapshot.Warnings ?? [])
            .Select(warning => SystemInventoryFormatting.Bounded(warning, SystemMaintenanceLimits.WarningCharacters))
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(SystemMaintenanceLimits.MaximumWarnings)
            .ToArray();

        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["mode"] = EvidenceModes.ToWireValue(query.Mode),
            ["status"] = status,
            ["complete"] = false,
            ["truncated"] = false,
            ["window"] = new JsonObject { ["fromUtc"] = EvidenceTime.Format(query.FromUtc), ["toUtc"] = EvidenceTime.Format(query.ToUtc) },
            ["coverage"] = coverage,
            ["observedItems"] = inWindow.Length,
        };

        JsonArray entries;
        string returnedName;
        bool cut;
        if (query.Mode == EvidenceMode.Aggregate)
        {
            var groups = Aggregate(inWindow);
            var selected = groups.Take(query.Limit).ToArray();
            root["catalogAge"] = catalogAge?.TotalSeconds;
            root["sources"] = Sources(snapshot.Sources);
            root["warnings"] = new JsonArray(warnings.Select(warning => (JsonNode?)JsonValue.Create(warning)).ToArray());
            root["observedGroups"] = groups.Count;
            root["returnedGroups"] = selected.Length;
            root["groups"] = entries = new JsonArray(selected.Select(group => (JsonNode)group).ToArray());
            returnedName = "returnedGroups";
            cut = selected.Length < groups.Count;
        }
        else
        {
            var selected = inWindow.Take(query.Limit).Select(Row).ToArray();
            root["returnedItems"] = selected.Length;
            root["catalogAge"] = catalogAge?.TotalSeconds;
            root["sources"] = Sources(snapshot.Sources);
            root["warnings"] = new JsonArray(warnings.Select(warning => (JsonNode?)JsonValue.Create(warning)).ToArray());
            root["items"] = entries = new JsonArray(selected.Select(row => (JsonNode)row).ToArray());
            returnedName = "returnedItems";
            cut = selected.Length < inWindow.Length;
        }

        var truncated = snapshot.CollectionTruncated || cut;
        SystemEventFormatting.SetCompleteness(root, status, truncated, coverageState);
        var output = root.ToJsonString();
        while (Encoding.UTF8.GetByteCount(output) > SystemMaintenanceLimits.CrashOutputBytes && entries.Count > 0)
        {
            entries.RemoveAt(entries.Count - 1);
            root[returnedName] = entries.Count;
            SystemEventFormatting.SetCompleteness(root, status, truncated: true, coverageState);
            output = root.ToJsonString();
        }

        if (Encoding.UTF8.GetByteCount(output) > SystemMaintenanceLimits.CrashOutputBytes)
        {
            throw new InvalidOperationException("Crash evidence metadata exceeds the fixed output budget.");
        }

        return output;
    }

    /// <summary>The raw row: every schema-1 field, in the schema-1 order, followed by the schema-2 fields.</summary>
    internal static JsonObject Row(CrashEvidence item) => new()
    {
        ["timestampUtc"] = EvidenceTime.Format(item.TimestampUtc),
        ["process"] = SystemInventoryFormatting.Bounded(item.Process, 512),
        ["pid"] = item.Pid,
        ["kind"] = SystemInventoryFormatting.Bounded(item.Kind, 128),
        ["dumpPath"] = SystemInventoryFormatting.Bounded(item.DumpPath, SystemMaintenanceLimits.PathCharacters),
        ["eventIdOrCrashId"] = SystemInventoryFormatting.Bounded(item.EventIdOrCrashId, 256),
        ["summary"] = SystemInventoryFormatting.Bounded(item.Summary, SystemMaintenanceLimits.TextCharacters),
        ["source"] = SystemInventoryFormatting.Bounded(item.Source, SystemMaintenanceLimits.SourceCharacters),
        ["timestampKind"] = TemporalCoverage.ToWireValue(item.TimestampKind),
        ["reportedUtc"] = EvidenceTime.Format(item.ReportedUtc),
        ["reportId"] = SystemInventoryFormatting.Bounded(item.ReportId, 64),
        ["eventName"] = SystemInventoryFormatting.Bounded(item.EventName, 128),
        ["code"] = SystemInventoryFormatting.Bounded(item.Code, 32),
        ["bugcheckCode"] = SystemInventoryFormatting.Bounded(item.BugcheckCode, 32),
        ["liveDumpCode"] = SystemInventoryFormatting.Bounded(item.LiveDumpCode, 32),
        ["exceptionCode"] = SystemInventoryFormatting.Bounded(item.ExceptionCode, 32),
        ["faultModule"] = SystemInventoryFormatting.Bounded(item.FaultModule, 512),
        ["bucket"] = SystemInventoryFormatting.Bounded(item.Bucket, 256),
        ["evidenceSources"] = SourceNames(item.EvidenceSources),
    };

    /// <summary>
    /// Groups crashes by (kind, event name — only for kind <c>wer</c> —, code, application, module, timestamp kind). Application and
    /// module compare case-insensitively and the group shows the ordinally smallest spelling of its members.
    /// </summary>
    public static IReadOnlyList<JsonObject> Aggregate(IReadOnlyList<CrashEvidence> items) =>
        items
            .GroupBy(item => (
                item.Kind,
                EventName: item.Kind == CrashKinds.Wer ? item.EventName : null,
                item.Code,
                Application: item.Process?.ToUpperInvariant(),
                Module: item.FaultModule?.ToUpperInvariant(),
                item.TimestampKind))
            .Select(group =>
            {
                var members = group.ToArray();
                return new
                {
                    group.Key.Kind,
                    group.Key.EventName,
                    group.Key.Code,
                    Application = members.Select(member => member.Process).Where(value => value is not null).Min(StringComparer.Ordinal),
                    Module = members.Select(member => member.FaultModule).Where(value => value is not null).Min(StringComparer.Ordinal),
                    group.Key.TimestampKind,
                    Members = members,
                    First = members.Min(member => member.TimestampUtc),
                    Last = members.Max(member => member.TimestampUtc),
                };
            })
            .OrderByDescending(group => group.Members.Length)
            .ThenByDescending(group => group.Last)
            .ThenBy(group => group.Kind, KeyComparer.Instance)
            .ThenBy(group => group.EventName, KeyComparer.Instance)
            .ThenBy(group => group.Code, KeyComparer.Instance)
            .ThenBy(group => group.Application, KeyComparer.Instance)
            .ThenBy(group => group.Module, KeyComparer.Instance)
            .ThenBy(group => TemporalCoverage.ToWireValue(group.TimestampKind), StringComparer.Ordinal)
            .Select(group => new JsonObject
            {
                ["kind"] = SystemInventoryFormatting.Bounded(group.Kind, 128),
                ["eventName"] = SystemInventoryFormatting.Bounded(group.EventName, 128),
                ["code"] = SystemInventoryFormatting.Bounded(group.Code, 32),
                ["application"] = SystemInventoryFormatting.Bounded(group.Application, 512),
                ["module"] = SystemInventoryFormatting.Bounded(group.Module, 512),
                ["timestampKind"] = TemporalCoverage.ToWireValue(group.TimestampKind),
                ["count"] = group.Members.Length,
                ["uncorrelatedCount"] = group.Members.Count(member => member.ReportId is null),
                ["dumpReferenceCount"] = group.Members.Count(member => member.DumpPath is not null),
                ["firstSeenUtc"] = EvidenceTime.Format(group.First),
                ["lastSeenUtc"] = EvidenceTime.Format(group.Last),
                ["evidenceSources"] = SourceNames(group.Members.SelectMany(member => member.EvidenceSources.Count > 0 ? member.EvidenceSources : [member.Source])),
            })
            .ToArray();

    internal static string Status(IReadOnlyList<MaintenanceSource> sources)
    {
        var applicable = sources.Where(source => source.Status != InventorySourceStatus.NotApplicable).ToArray();
        if (applicable.Length == 0 || applicable.All(source => source.Status is InventorySourceStatus.Unavailable or InventorySourceStatus.Unsupported))
        {
            return "unavailable";
        }

        return applicable.All(source => source.Status == InventorySourceStatus.Available) ? "complete" : "partial";
    }

    private static JsonArray Sources(IReadOnlyList<MaintenanceSource> sources) =>
        new(sources
            .OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Name, StringComparer.Ordinal)
            .Select(source => (JsonNode)new JsonObject
            {
                ["name"] = SystemInventoryFormatting.Bounded(source.Name, SystemMaintenanceLimits.SourceCharacters),
                ["status"] = SystemInventoryFormatting.ToWireValue(source.Status),
                ["detail"] = SystemInventoryFormatting.Bounded(source.Detail, SystemMaintenanceLimits.WarningCharacters),
                ["examinedFromUtc"] = EvidenceTime.Format(source.ExaminedFromUtc),
            })
            .ToArray());

    private static JsonArray SourceNames(IEnumerable<string> names) =>
        new(names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(MaximumGroupSources)
            .Select(name => (JsonNode?)JsonValue.Create(SystemInventoryFormatting.Bounded(name, SystemMaintenanceLimits.SourceCharacters)))
            .ToArray());
}

/// <summary>Canonical text of native error codes, shared by every collector so one code has one spelling (ADR-0032 HARDEN-7 amendment §4).</summary>
public static class EvidenceCodes
{
    /// <summary>
    /// Lower-case hexadecimal with a <c>0x</c> prefix and no leading zeros (<c>0x50</c>, <c>0x1a1</c>, <c>0xc0000005</c>, <c>0x0</c>)
    /// from a hexadecimal text with or without the prefix; <c>null</c> when it is not a 64-bit hexadecimal number.
    /// </summary>
    public static string? CanonicalHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = value.Trim();
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        return digits.Length is > 0 and <= 64
            && digits.All(char.IsAsciiHexDigit)
            && ulong.TryParse(digits.TrimStart('0') is { Length: > 0 } significant ? significant : "0", NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number)
            ? FromNumber(number)
            : null;
    }

    /// <summary>The canonical hexadecimal text of <paramref name="number"/>.</summary>
    public static string FromNumber(ulong number) => "0x" + number.ToString("x", CultureInfo.InvariantCulture);
}
