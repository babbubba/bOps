// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// The typed meaning of one Windows Error Reporting report, whatever carried it (a WER 1001 event or a Report.wer file).
/// </summary>
/// <param name="EventName">The report's event name exactly as the machine wrote it, or <c>null</c>.</param>
/// <param name="Kind">The crash kind (<see cref="CrashKinds"/>).</param>
/// <param name="BugcheckCode">For <c>BlueScreen</c>: canonical <c>P1</c>.</param>
/// <param name="LiveDumpCode">For <c>LiveKernelEvent</c>: canonical <c>P1</c>.</param>
/// <param name="ExceptionCode">For <c>APPCRASH</c> (<c>P7</c>) and <c>BEX</c> (<c>P8</c>): the canonical exception code.</param>
/// <param name="Application">For application kinds: <c>P1</c>, the application image name.</param>
/// <param name="Module">For application crashes: <c>P4</c>, the faulting module (or assembly, for <c>CLR20r3</c>).</param>
/// <param name="Bucket">The report bucket, when present.</param>
/// <param name="ReportId">The report GUID (WER 1001 <c>ReportId</c>, Report.wer <c>ReportIdentifier</c>), normalized, or <c>null</c>.</param>
/// <param name="DumpPath">The first <c>.dmp</c> reference among the attached files, or <c>null</c>.</param>
internal sealed record WerSignature(
    string? EventName,
    string Kind,
    string? BugcheckCode,
    string? LiveDumpCode,
    string? ExceptionCode,
    string? Application,
    string? Module,
    string? Bucket,
    string? ReportId,
    string? DumpPath);

/// <summary>The typed fields of one Application Error 1000 record.</summary>
internal sealed record ApplicationFault(string? Application, string? Module, string? ExceptionCode, int? ProcessId, string? IntegratorReportId);

/// <summary>
/// The one package-internal interpretation of Windows Error Reporting evidence, used by both <c>system.crashes</c> and
/// <c>system.stability</c> so the two tools cannot classify the same report differently (ADR-0041 §10). Named fields are read first;
/// a record whose data carries no names falls back to the documented positional layout. Unknown stays <c>null</c>; no value is
/// guessed and no message text is parsed.
/// </summary>
internal static class WindowsWerNormalizer
{
    /// <summary>The positional layout of WER 1001 event data when the record carries no field names.</summary>
    internal static readonly IReadOnlyList<string> Wer1001Layout =
    [
        "Bucket", "BucketType", "EventName", "Response", "CabId",
        "P1", "P2", "P3", "P4", "P5", "P6", "P7", "P8", "P9", "P10",
        "AttachedFiles", "StorePath", "AnalysisSymbol", "Rechecking", "ReportId", "ReportStatus", "HashedBucket", "CabGuid",
    ];

    /// <summary>The positional layout of Application Error 1000 event data when the record carries no field names.</summary>
    internal static readonly IReadOnlyList<string> ApplicationError1000Layout =
    [
        "AppName", "AppVersion", "AppTimeStamp", "ModuleName", "ModuleVersion", "ModuleTimeStamp", "ExceptionCode", "FaultingOffset",
        "ProcessId", "ProcessCreationTime", "AppPath", "ModulePath", "IntegratorReportId", "PackageFullName", "PackageRelativeAppId",
    ];

    /// <summary>The largest dump reference accepted; a longer one is dropped, never cut.</summary>
    internal const int MaximumDumpPathCharacters = 1_024;

    /// <summary>Normalizes the event data of a WER 1001 record (Application log, provider <c>Windows Error Reporting</c>).</summary>
    internal static WerSignature FromWer1001(IReadOnlyDictionary<string, string> data, bool unnamed)
    {
        ArgumentNullException.ThrowIfNull(data);
        string? Field(string name) => Read(data, unnamed, Wer1001Layout, name);
        return Classify(
            Field("EventName"),
            index => Field("P" + index.ToString(CultureInfo.InvariantCulture)),
            Field("Bucket"),
            NormalizeGuid(Field("ReportId")),
            DumpReference(Field("AttachedFiles")));
    }

    /// <summary>Normalizes the needed keys of a Report.wer file: <c>EventType</c> and <c>Sig[n].Value</c> carry the WER 1001 <c>EventName</c> and <c>P(n+1)</c>.</summary>
    internal static WerSignature FromReportWer(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Classify(
            values.GetValueOrDefault("EventType"),
            index => values.GetValueOrDefault("Sig[" + (index - 1).ToString(CultureInfo.InvariantCulture) + "].Value"),
            values.GetValueOrDefault("Response.LegacyBucketId"),
            NormalizeGuid(values.GetValueOrDefault("ReportIdentifier")),
            dumpPath: null);
    }

    /// <summary>Normalizes the event data of an Application Error 1000 record.</summary>
    internal static ApplicationFault FromApplicationError1000(IReadOnlyDictionary<string, string> data, bool unnamed)
    {
        ArgumentNullException.ThrowIfNull(data);
        string? Field(string name) => Read(data, unnamed, ApplicationError1000Layout, name);
        return new ApplicationFault(
            WindowsEventLogEvidence.Clean(Field("AppName")),
            WindowsEventLogEvidence.Clean(Field("ModuleName")),
            EvidenceCodes.CanonicalHex(Field("ExceptionCode")),
            HexProcessId(Field("ProcessId")),
            NormalizeGuid(Field("IntegratorReportId")));
    }

    /// <summary>
    /// Maps an event name and its P-fields to a typed signature: <c>BlueScreen</c> → kernel-bugcheck with <c>P1</c> as the bugcheck
    /// code; <c>LiveKernelEvent</c> → kernel-live-dump with <c>P1</c> as the live-dump code; <c>APPCRASH</c>, <c>BEX</c>,
    /// <c>CLR20R3</c> → application-crash; <c>AppHangB1</c>, <c>APPHANG</c> → application-hang; anything else → <c>wer</c> with the
    /// event name kept. Event names compare case-insensitively.
    /// </summary>
    internal static WerSignature Classify(string? eventName, Func<int, string?> parameter, string? bucket, string? reportId, string? dumpPath)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var name = WindowsEventLogEvidence.Clean(eventName);
        var cleanBucket = WindowsEventLogEvidence.Clean(bucket);
        string? P(int index) => WindowsEventLogEvidence.Clean(parameter(index));
        return name?.ToUpperInvariant() switch
        {
            "BLUESCREEN" => new(name, CrashKinds.KernelBugcheck, EvidenceCodes.CanonicalHex(P(1)), null, null, null, null, cleanBucket, reportId, dumpPath),
            "LIVEKERNELEVENT" => new(name, CrashKinds.KernelLiveDump, null, EvidenceCodes.CanonicalHex(P(1)), null, null, null, cleanBucket, reportId, dumpPath),
            "APPCRASH" => new(name, CrashKinds.ApplicationCrash, null, null, EvidenceCodes.CanonicalHex(P(7)), P(1), P(4), cleanBucket, reportId, dumpPath),
            "BEX" => new(name, CrashKinds.ApplicationCrash, null, null, EvidenceCodes.CanonicalHex(P(8)), P(1), P(4), cleanBucket, reportId, dumpPath),
            "CLR20R3" => new(name, CrashKinds.ApplicationCrash, null, null, null, P(1), P(4), cleanBucket, reportId, dumpPath),
            "APPHANGB1" or "APPHANG" => new(name, CrashKinds.ApplicationHang, null, null, null, P(1), null, cleanBucket, reportId, dumpPath),
            _ => new(name, CrashKinds.Wer, null, null, null, null, null, cleanBucket, reportId, dumpPath),
        };
    }

    /// <summary>A report GUID in lower-case <c>D</c> format; <c>null</c> when it is not a GUID or is the all-zero GUID (ADR-0041 §7).</summary>
    internal static string? NormalizeGuid(string? value) =>
        Guid.TryParse(value?.Trim(), out var guid) && guid != Guid.Empty ? guid.ToString("D") : null;

    /// <summary>
    /// The first <c>.dmp</c> reference among WER <c>AttachedFiles</c> (one path per line), as a path string only (review note R11): the
    /// <c>\\?\</c> prefix is removed; only a drive-absolute path (<c>X:\…</c>) of at most <see cref="MaximumDumpPathCharacters"/>
    /// characters without control characters is accepted — anything else is skipped, never repaired or cut. The file is never
    /// opened. The path may contain a user-profile directory; it is reported as written, in raw mode only, and is never audited.
    /// </summary>
    internal static string? DumpReference(string? attachedFiles)
    {
        if (string.IsNullOrWhiteSpace(attachedFiles))
        {
            return null;
        }

        foreach (var line in attachedFiles.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = line.StartsWith(@"\\?\", StringComparison.Ordinal) ? line[4..] : line;
            if (path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)
                && path.Length is >= 8 and <= MaximumDumpPathCharacters
                && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
                && !path.Any(char.IsControl))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Application Error 1000 writes <c>ProcessId</c> as <c>0x</c>-prefixed hexadecimal. Only that form is parsed; a value without the
    /// prefix is ambiguous and stays <c>null</c>.
    /// </summary>
    internal static int? HexProcessId(string? value)
    {
        var text = value?.Trim();
        return text is { Length: > 2 and <= 10 }
            && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var pid)
            && pid > 0
                ? pid
                : null;
    }

    /// <summary>A field by name, or — only when the record carries no names at all — by its index in <paramref name="layout"/>.</summary>
    private static string? Read(IReadOnlyDictionary<string, string> data, bool unnamed, IReadOnlyList<string> layout, string name)
    {
        if (!unnamed)
        {
            return data.GetValueOrDefault(name);
        }

        for (var index = 0; index < layout.Count; index++)
        {
            if (string.Equals(layout[index], name, StringComparison.Ordinal))
            {
                return data.GetValueOrDefault(index.ToString(CultureInfo.InvariantCulture));
            }
        }

        return null;
    }
}
