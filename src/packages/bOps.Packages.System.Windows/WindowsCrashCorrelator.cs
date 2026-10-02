// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>Which kind of native evidence a crash record came from; fixes the precedence of ADR-0041 §6 and §7.</summary>
internal enum CrashEvidenceRole
{
    /// <summary>A Report.wer file.</summary>
    ReportWer,

    /// <summary>An Application Error 1000 record.</summary>
    ApplicationError,

    /// <summary>A WER 1001 record (Application log).</summary>
    Wer1001,
}

/// <summary>One native crash record before merging.</summary>
/// <param name="Role">The kind of evidence.</param>
/// <param name="Source">The result source it was read from.</param>
/// <param name="TimeUtc">Its time: Report.wer <c>EventTime</c> or file time, the 1000 record time, or the 1001 record time.</param>
/// <param name="TimestampKind">Whether <paramref name="TimeUtc"/> is an occurrence time.</param>
/// <param name="NativeId">Its exact native identity (channel and record id, or the report directory), for deduplication only.</param>
/// <param name="Identities">The report GUIDs it carries, normalized.</param>
/// <param name="PrimaryReportId">WER 1001 <c>ReportId</c> or Report.wer <c>ReportIdentifier</c>, normalized.</param>
internal sealed record CrashNative(
    CrashEvidenceRole Role,
    string Source,
    DateTimeOffset TimeUtc,
    EvidenceTimestampKind TimestampKind,
    string? NativeId,
    IReadOnlyList<string> Identities,
    string? PrimaryReportId)
{
    public string? Kind { get; init; }

    public string? EventName { get; init; }

    public string? Process { get; init; }

    public int? Pid { get; init; }

    public string? FaultModule { get; init; }

    public string? BugcheckCode { get; init; }

    public string? LiveDumpCode { get; init; }

    public string? ExceptionCode { get; init; }

    public string? Bucket { get; init; }

    public string? DumpPath { get; init; }

    /// <summary>True for a Report.wer record dated by its <c>EventTime</c> (whatever its timestamp kind), false when dated by its file time.</summary>
    public bool HasEventTime { get; init; }
}

/// <summary>
/// Merges native crash records into one record per crash (ADR-0041 §7). Records are merged only when their report-GUID sets
/// intersect, transitively; a record without a usable GUID is never merged. Nothing is ever correlated by time proximity, process name,
/// module, code, bucket or message. Within a call, records with the same native identity are one record.
/// </summary>
internal static class WindowsCrashCorrelator
{
    private static readonly CrashEvidenceRole[] GeneralOrder = [CrashEvidenceRole.ReportWer, CrashEvidenceRole.ApplicationError, CrashEvidenceRole.Wer1001];
    private static readonly CrashEvidenceRole[] FaultOrder = [CrashEvidenceRole.ApplicationError, CrashEvidenceRole.ReportWer, CrashEvidenceRole.Wer1001];

    /// <summary>Merges <paramref name="records"/> into crashes.</summary>
    internal static IReadOnlyList<CrashEvidence> Merge(IEnumerable<CrashNative> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var unique = records
            .GroupBy(record => record.NativeId is null ? null : record.Role + "|" + record.NativeId, StringComparer.Ordinal)
            .SelectMany(group => group.Key is null ? group : group.Take(1))
            .OrderBy(record => record.TimeUtc)
            .ThenBy(record => record.Source, StringComparer.Ordinal)
            .ThenBy(record => record.NativeId, StringComparer.Ordinal)
            .ToArray();

        // Union-find over the records, joined through every GUID they share.
        var parent = Enumerable.Range(0, unique.Length).ToArray();
        int Find(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }

            return index;
        }

        var firstByIdentity = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < unique.Length; index++)
        {
            foreach (var identity in unique[index].Identities)
            {
                if (firstByIdentity.TryGetValue(identity, out var other))
                {
                    parent[Find(index)] = Find(other);
                }
                else
                {
                    firstByIdentity[identity] = index;
                }
            }
        }

        return Enumerable.Range(0, unique.Length)
            .GroupBy(Find)
            .Select(component => Build(component.Select(index => unique[index]).ToArray()))
            .ToArray();
    }

    private static CrashEvidence Build(CrashNative[] members)
    {
        // Primary time (ADR-0041 §6): the Report.wer EventTime, then the Application Error 1000 time, then the earliest WER 1001 time,
        // then a Report.wer file time. Each keeps its own kind: a BlueScreen EventTime is reported (the evidence-driven correction of
        // 2026-10-02), and nothing else is promoted to an occurrence in its place. Members are already in time order.
        var primary = members.FirstOrDefault(member => member.Role == CrashEvidenceRole.ReportWer && member.HasEventTime)
            ?? members.FirstOrDefault(member => member.Role == CrashEvidenceRole.ApplicationError)
            ?? members.FirstOrDefault(member => member.Role == CrashEvidenceRole.Wer1001)
            ?? members[0];
        var firstWer = members.FirstOrDefault(member => member.Role == CrashEvidenceRole.Wer1001);
        var reportId = members.Where(member => member.Role == CrashEvidenceRole.Wer1001).Select(member => member.PrimaryReportId).FirstOrDefault(id => id is not null)
            ?? members.Where(member => member.Role == CrashEvidenceRole.ReportWer).Select(member => member.PrimaryReportId).FirstOrDefault(id => id is not null)
            ?? members.SelectMany(member => member.Identities).Order(StringComparer.Ordinal).FirstOrDefault();

        return new CrashEvidence(primary.TimeUtc, primary.TimestampKind, primary.Source, First(members, GeneralOrder, member => member.Kind))
        {
            ReportedUtc = primary.TimestampKind == EvidenceTimestampKind.Occurred && firstWer is not null ? firstWer.TimeUtc : null,
            Process = First(members, FaultOrder, member => member.Process),
            Pid = members.Where(member => member.Role == CrashEvidenceRole.ApplicationError).Select(member => member.Pid).FirstOrDefault(pid => pid is not null),
            FaultModule = First(members, FaultOrder, member => member.FaultModule),
            ExceptionCode = First(members, FaultOrder, member => member.ExceptionCode),
            EventName = First(members, GeneralOrder, member => member.EventName),
            BugcheckCode = First(members, GeneralOrder, member => member.BugcheckCode),
            LiveDumpCode = First(members, GeneralOrder, member => member.LiveDumpCode),
            Bucket = First(members, GeneralOrder, member => member.Bucket),
            DumpPath = First(members, GeneralOrder, member => member.DumpPath),
            ReportId = reportId,
            EventIdOrCrashId = reportId ?? primary.NativeId,
            EvidenceSources = members.Select(member => member.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        };
    }

    private static string? First(CrashNative[] members, CrashEvidenceRole[] order, Func<CrashNative, string?> field) =>
        order.SelectMany(role => members.Where(member => member.Role == role)).Select(field).FirstOrDefault(value => value is not null);
}
