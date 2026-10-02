// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>One known WER report directory and the distinct source name of its root.</summary>
/// <param name="Source">The source name, for example <c>windows.wer.programdata.reportarchive</c>.</param>
/// <param name="Path">The directory.</param>
internal sealed record WerDirectory(string Source, string Path);

/// <summary>
/// Reads bounded Windows crash evidence (<c>system.crashes</c>, ADR-0032 HARDEN-7 amendment §4, ADR-0041 §6–§7): Application Error
/// 1000 and WER 1001 from the Application log, and the needed keys of Report.wer files in the known WER directories of both roots.
/// Records are typed by the one shared WER normalizer, merged only by report GUID, and dated honestly. Every source runs within a
/// slice of one call budget, so a slow source becomes partial evidence instead of exhausting the runner's tool timeout. Dump files
/// are never opened; their paths are references only.
/// </summary>
public sealed class WindowsCrashEvidenceTool : SystemCrashesToolBase
{
    internal const string ApplicationErrorSource = "windows-event-application-error";
    internal const string WerEventSource = "windows-event-wer";
    internal const int MaximumReportsPerDirectory = 512;
    internal const int MaximumEventsPerProvider = 512;
    internal const int MaximumCrashes = 2_048;

    /// <summary>The whole call: comfortably below the runner's 30-second tool timeout (review note R7).</summary>
    internal static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(20);

    /// <summary>The ADR-0032 bound of one Event Log read, now a cap within the call budget.</summary>
    internal static readonly TimeSpan EventLogReadCap = TimeSpan.FromSeconds(10);

    private const string Channel = "Application";
    // ReportQueue first: it holds the few kernel reports whose EventTime dates live dumps (H-7), and it finishes quickly, so the
    // much larger ReportArchive inherits the time it does not use.
    private static readonly string[] WerRelativeDirectories = ["ReportQueue", "ReportArchive"];

    private readonly Func<IEnumerable<WerDirectory>> knownWerDirectories;
    private readonly Func<EventLogRequest, EvidenceBudgetSlice, EventLogScan> readEvents;
    private readonly Func<IReadOnlyList<string>, EvidenceBudgetSlice, CoverageStore> probeChannel;

    public WindowsCrashEvidenceTool()
        : this(TimeProvider.System, KnownWerDirectories, null, null)
    {
    }

    internal WindowsCrashEvidenceTool(
        TimeProvider clock,
        Func<IEnumerable<WerDirectory>> knownWerDirectories,
        Func<EventLogRequest, EvidenceBudgetSlice, EventLogScan>? readEvents = null,
        Func<IReadOnlyList<string>, EvidenceBudgetSlice, CoverageStore>? probeChannel = null)
        : base("windows", clock)
    {
        this.knownWerDirectories = knownWerDirectories;
        this.readEvents = readEvents ?? ((request, slice) => WindowsEventLogEvidence.Read(request, slice));
        this.probeChannel = probeChannel ?? ((backed, slice) => WindowsEventLogEvidence.ProbeChannel(Channel, backed, slice));
    }

    protected override Task<CrashEvidenceSnapshot> CollectAsync(CrashesQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var budget = new EvidenceTimeBudget(CallBudget, Clock);
        var directories = knownWerDirectories().ToArray();
        var natives = new List<CrashNative>();
        var sources = new List<MaintenanceSource>();
        var stores = new List<CoverageStore>();
        var warnings = new List<string>();
        var truncated = false;
        // Time is shared among the sources that can take it: a WER directory that is not there (or cannot be listed) ends at once,
        // so it does not take a share that a populated directory needs.
        var steps = 3 + directories.Count(directory => Directory.Exists(directory.Path));

        ct.ThrowIfCancellationRequested();
        using (var slice = budget.Start(steps--, ct, EventLogReadCap))
        {
            stores.Add(probeChannel([ApplicationErrorSource, WerEventSource], slice));
        }

        foreach (var (provider, eventId, source) in new[]
                 {
                     ("Application Error", 1000, ApplicationErrorSource),
                     ("Windows Error Reporting", 1001, WerEventSource),
                 })
        {
            ct.ThrowIfCancellationRequested();
            using var slice = budget.Start(steps--, ct, EventLogReadCap);
            var request = new EventLogRequest(
                Channel,
                WindowsEventLogEvidence.WindowXPath([WindowsEventLogEvidence.ProviderClause(provider, [eventId])], query.FromUtc, query.ToUtc),
                query.FromUtc,
                MaximumEventsPerProvider);
            var scan = readEvents(request, slice);
            ct.ThrowIfCancellationRequested();
            sources.Add(new MaintenanceSource(source, scan.ChannelMissing ? InventorySourceStatus.Unavailable : scan.Status, scan.Detail) { ExaminedFromUtc = scan.ExaminedFromUtc });
            if (scan.Detail is { } detail && scan.Status != InventorySourceStatus.Available)
            {
                warnings.Add(detail);
            }

            truncated |= scan.Truncated;
            natives.AddRange(scan.Records.Select(record => eventId == 1000 ? FromApplicationError(record) : FromWer(record)));
        }

        long callBytes = 0;
        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            using var slice = budget.Start(Directory.Exists(directory.Path) ? Math.Max(1, steps--) : Math.Max(1, steps), ct);
            var result = ReadWerDirectory(directory, query.FromUtc, slice, ref callBytes);
            natives.AddRange(result.Records);
            sources.Add(result.Source);
            warnings.AddRange(result.Warnings);
            truncated |= result.Truncated;
            if (result.Source.Status != InventorySourceStatus.NotApplicable)
            {
                stores.Add(new CoverageStore(directory.Source, CoverageBasis.Directory, result.OldestItemUtc, null, [directory.Source]));
            }
        }

        var crashes = WindowsCrashCorrelator.Merge(natives)
            .Where(crash => crash.TimestampUtc >= query.FromUtc && crash.TimestampUtc <= query.ToUtc)
            .OrderByDescending(crash => crash.TimestampUtc)
            .ThenBy(crash => crash.ReportId, StringComparer.Ordinal)
            .ToList();
        if (crashes.Count > MaximumCrashes)
        {
            crashes.RemoveRange(MaximumCrashes, crashes.Count - MaximumCrashes);
            truncated = true;
            warnings.Add("The normalized crash-evidence ceiling was reached.");
        }

        return Task.FromResult(new CrashEvidenceSnapshot(crashes, sources, warnings, truncated) { Stores = stores });
    }

    /// <summary>A WER 1001 record as a native crash record: its time is when WER processed the report, so it is <c>reported</c>.</summary>
    internal static CrashNative FromWer(EventLogItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var signature = WindowsWerNormalizer.FromWer1001(record.Data, record.IsUnnamed);
        return new CrashNative(
            CrashEvidenceRole.Wer1001,
            WerEventSource,
            record.TimeCreatedUtc,
            EvidenceTimestampKind.Reported,
            NativeId(record),
            signature.ReportId is { } id ? [id] : [],
            signature.ReportId)
        {
            Kind = signature.Kind,
            EventName = signature.EventName,
            Process = signature.Application,
            FaultModule = signature.Module,
            BugcheckCode = signature.BugcheckCode,
            LiveDumpCode = signature.LiveDumpCode,
            ExceptionCode = signature.ExceptionCode,
            Bucket = signature.Bucket,
            DumpPath = signature.DumpPath,
        };
    }

    /// <summary>An Application Error 1000 record as a native crash record: its record time is the crash time, so it is <c>occurred</c>.</summary>
    internal static CrashNative FromApplicationError(EventLogItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var fault = WindowsWerNormalizer.FromApplicationError1000(record.Data, record.IsUnnamed);
        return new CrashNative(
            CrashEvidenceRole.ApplicationError,
            ApplicationErrorSource,
            record.TimeCreatedUtc,
            EvidenceTimestampKind.Occurred,
            NativeId(record),
            fault.IntegratorReportId is { } id ? [id] : [],
            null)
        {
            Kind = CrashKinds.ApplicationCrash,
            Process = fault.Application,
            Pid = fault.ProcessId,
            FaultModule = fault.Module,
            ExceptionCode = fault.ExceptionCode,
        };
    }

    /// <summary>
    /// Report.wer keys as a native crash record. <c>EventTime</c> is an occurrence time — except for a <c>BlueScreen</c> report, whose
    /// <c>EventTime</c> is written after the reboot and is therefore a reporting time (operator-decided, evidence-driven correction of
    /// ADR-0041 §6, 2026-10-02: on the operator workstation it was always 20–50 s after the Kernel-Power 41 of the next boot). Without
    /// <c>EventTime</c> the file's last-write time is a
    /// reporting time. Its identity is <c>ReportIdentifier</c> and <c>IntegratorReportIdentifier</c>.
    /// </summary>
    internal static CrashNative FromReportWer(IReadOnlyDictionary<string, string> values, string source, string reportDirectory, DateTimeOffset lastWriteUtc)
    {
        ArgumentNullException.ThrowIfNull(values);
        var signature = WindowsWerNormalizer.FromReportWer(values);
        var integrator = WindowsWerNormalizer.NormalizeGuid(values.GetValueOrDefault("IntegratorReportIdentifier"));
        var eventTime = WindowsReportWerScanner.EventTime(values);
        return new CrashNative(
            CrashEvidenceRole.ReportWer,
            source,
            eventTime ?? lastWriteUtc,
            eventTime is null || signature.Kind == CrashKinds.KernelBugcheck ? EvidenceTimestampKind.Reported : EvidenceTimestampKind.Occurred,
            reportDirectory,
            new[] { signature.ReportId, integrator }.OfType<string>().Distinct(StringComparer.Ordinal).ToArray(),
            signature.ReportId)
        {
            Kind = signature.Kind,
            EventName = signature.EventName,
            Process = signature.Application,
            FaultModule = signature.Module,
            BugcheckCode = signature.BugcheckCode,
            LiveDumpCode = signature.LiveDumpCode,
            ExceptionCode = signature.ExceptionCode,
            Bucket = signature.Bucket,
            HasEventTime = eventTime is not null,
        };
    }

    internal static IEnumerable<WerDirectory> KnownWerDirectories()
    {
        foreach (var (root, label) in new[]
                 {
                     (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "programdata"),
                     (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "localappdata"),
                 })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            foreach (var relative in WerRelativeDirectories)
            {
                yield return new WerDirectory(
                    "windows.wer." + label + "." + (relative == "ReportArchive" ? "reportarchive" : "reportqueue"),
                    Path.Combine(root, "Microsoft", "Windows", "WER", relative));
            }
        }
    }

    /// <summary>
    /// Scans one WER directory: at most <see cref="MaximumReportsPerDirectory"/> report directories, each Report.wer by the bounded key
    /// scan, within the per-call byte budget and the time slice. A report that cannot be read or lacks its needed keys within its cap is
    /// skipped and makes the source partial; it never hides the readable ones.
    /// </summary>
    internal static WerDirectoryResult ReadWerDirectory(WerDirectory directory, DateTimeOffset windowFromUtc, EvidenceBudgetSlice slice, ref long callBytes)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(slice);
        // Enumerated without an existence test first: Directory.Exists is also false for a directory this identity may not read,
        // which would turn a denied directory into a "not present" one. Only a directory that is really missing is notApplicable.
        string[] reports;
        try
        {
            reports = Directory.EnumerateDirectories(directory.Path).Order(StringComparer.OrdinalIgnoreCase).Take(MaximumReportsPerDirectory + 1).ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return new([], new(directory.Source, InventorySourceStatus.NotApplicable, "The known WER directory is not present."), [], false, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new([], new(directory.Source, InventorySourceStatus.Unavailable, "The current identity cannot read this known WER directory."), [], false, null);
        }
        catch (IOException)
        {
            return new([], new(directory.Source, InventorySourceStatus.Unavailable, "This known WER directory could not be read."), [], false, null);
        }

        var ceiling = reports.Length > MaximumReportsPerDirectory;
        var records = new List<CrashNative>();
        var warnings = new List<string>();
        int denied = 0, unreadable = 0, missingKeys = 0, reparse = 0;
        var stopped = false;
        var timedOut = false;
        DateTimeOffset? oldest = null;
        foreach (var report in reports.Take(MaximumReportsPerDirectory))
        {
            if (slice.Token.IsCancellationRequested)
            {
                if (!slice.TimedOut)
                {
                    slice.Token.ThrowIfCancellationRequested();
                }

                timedOut = true;
                break;
            }

            var allowance = (int)Math.Min(WindowsReportWerScanner.PerFileBytes, WindowsReportWerScanner.PerCallBytes - callBytes);
            if (allowance <= 0)
            {
                stopped = true;
                warnings.Add("The per-call Report.wer byte budget was reached.");
                break;
            }

            var metadata = Path.Combine(report, "Report.wer");
            try
            {
                // Existence is not tested first: without read access File.Exists and FileInfo.Exists say "absent", which would turn a
                // report this identity may not read into a silent gap. Opening it tells a missing report from a denied one.
                if (new DirectoryInfo(report).Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || new FileInfo(metadata) is { Exists: true } info && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    reparse++;
                    continue;
                }

                var scan = ScanFile(metadata, allowance);

                callBytes += scan.BytesRead;
                if (!scan.HasNeededKeys)
                {
                    if (scan.ReachedCap && allowance < WindowsReportWerScanner.PerFileBytes)
                    {
                        stopped = true;
                        warnings.Add("The per-call Report.wer byte budget was reached.");
                        break;
                    }

                    missingKeys++;
                    continue;
                }

                var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(metadata), TimeSpan.Zero);
                var native = FromReportWer(scan.Values, directory.Source, report, lastWrite);
                records.Add(native);
                oldest = oldest is { } known && known <= native.TimeUtc ? known : native.TimeUtc;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // A report directory without Report.wer (being written, or holding only attachments) is not a report.
            }
            catch (UnauthorizedAccessException)
            {
                denied++;
            }
            catch (IOException)
            {
                unreadable++;
            }
        }

        var reasons = new List<string>();
        if (denied > 0)
        {
            reasons.Add($"{denied} report(s) are not readable by this identity");
        }

        if (unreadable > 0)
        {
            reasons.Add($"{unreadable} report(s) could not be read");
        }

        if (reparse > 0)
        {
            reasons.Add($"{reparse} report(s) are reparse points and were not followed");
        }

        if (missingKeys > 0)
        {
            reasons.Add($"{missingKeys} report(s) had no EventType within the {WindowsReportWerScanner.PerFileBytes / 1024} KiB scan cap");
        }

        if (ceiling)
        {
            reasons.Add($"the {MaximumReportsPerDirectory}-report directory ceiling was reached");
        }

        if (stopped)
        {
            reasons.Add("the per-call Report.wer byte budget was reached");
        }

        if (timedOut)
        {
            reasons.Add("the time bound stopped the scan");
        }

        warnings.AddRange(reasons.Select(reason => "WER reports: " + reason + "."));
        var status = reasons.Count > 0 ? InventorySourceStatus.Partial : InventorySourceStatus.Available;
        var detail = reasons.Count > 0 ? string.Join("; ", reasons) + "." : null;
        var complete = !ceiling && !stopped && !timedOut;
        return new(
            records,
            new(directory.Source, status, detail) { ExaminedFromUtc = complete ? windowFromUtc : null },
            warnings,
            ceiling || stopped || timedOut,
            oldest);
    }

    private static ReportWerScan ScanFile(string path, int allowance)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return WindowsReportWerScanner.Scan(stream, allowance);
    }

    private static string? NativeId(EventLogItem record) =>
        record.RecordId is { } id ? record.Channel + "/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    internal sealed record WerDirectoryResult(
        IReadOnlyList<CrashNative> Records,
        MaintenanceSource Source,
        IReadOnlyList<string> Warnings,
        bool Truncated,
        DateTimeOffset? OldestItemUtc);
}
