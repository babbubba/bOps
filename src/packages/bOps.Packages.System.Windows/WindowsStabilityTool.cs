// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>One fixed Event Log source of <c>system.stability</c>: its name, channel and provider/event-id tuples, all package constants.</summary>
internal sealed record StabilityEventSource(string Name, string Channel, IReadOnlyList<(string Provider, int[] EventIds)> Tuples);

/// <summary>
/// Collects typed machine-stability evidence on Windows (ADR-0041 §3.1): fixed System-log tuples (Kernel-Power 41 and EventLog 6008,
/// the System-log bugcheck record, WHEA-Logger, Display 4101, disk/stornvme/storahci), the LiveKernelEvent display codes of WER 1001
/// through the one shared WER normalizer, boot context (6005/6006) and the minidump directory (names, sizes and times only — never
/// opened). The XPath of every source is built only from these constants and the window; nothing comes from the caller. Every read,
/// coverage probes included, runs within its share of one 20-second call budget.
/// </summary>
public sealed partial class WindowsStabilityTool : SystemStabilityToolBase
{
    internal const string UnexpectedShutdownSource = "windows.stability.unexpectedShutdown";
    internal const string KernelCrashSource = "windows.stability.kernelCrash";
    internal const string HardwareErrorSource = "windows.stability.hardwareError";
    internal const string DisplayFaultSystemSource = "windows.stability.displayFault.system";
    internal const string DisplayFaultWerSource = "windows.stability.displayFault.wer";
    internal const string StorageErrorSource = "windows.stability.storageError";
    internal const string BootContextSource = "windows.stability.bootContext";
    internal const string MinidumpSource = "windows.minidump";

    internal const string KernelPower = "Microsoft-Windows-Kernel-Power";
    internal const string EventLogProvider = "EventLog";
    internal const string SystemErrorReporting = "Microsoft-Windows-WER-SystemErrorReporting";
    internal const string WheaLogger = "Microsoft-Windows-WHEA-Logger";
    internal const string DisplayProvider = "Display";
    internal const string WerProvider = "Windows Error Reporting";

    /// <summary>The LiveKernelEvent codes that are display evidence: 0x117 and 0x141 (timeout detection and recovery) and 0x193 (display-kernel live dump). Never 0x1a1.</summary>
    internal static readonly IReadOnlyList<string> DisplayLiveDumpCodes = ["0x117", "0x141", "0x193"];

    internal static readonly IReadOnlyList<StabilityEventSource> EventSources =
    [
        new(UnexpectedShutdownSource, "System", [(KernelPower, [41]), (EventLogProvider, [6008])]),
        new(KernelCrashSource, "System", [(SystemErrorReporting, [1001])]),
        new(HardwareErrorSource, "System", [(WheaLogger, [])]),
        new(DisplayFaultSystemSource, "System", [(DisplayProvider, [4101])]),
        new(DisplayFaultWerSource, "Application", [(WerProvider, [1001])]),
        new(StorageErrorSource, "System", [("disk", [7, 11, 51, 153]), ("stornvme", [129]), ("storahci", [129])]),
        new(BootContextSource, "System", [(EventLogProvider, [6005, 6006])]),
    ];

    private const string KernelFaultDetail = "Non-display LiveKernelEvent reports (for example 0x1a1) are listed by system.crashes as kernel-live-dump.";
    private const string MemoryExhaustionDetail = "Windows low-memory diagnostics are not collected by this version; use system.events.";
    private const string MinidumpMissingDetail = "The minidump directory does not exist.";
    private const string MinidumpDeniedDetail = "The minidump directory is not readable by this identity; it usually requires elevation.";

    /// <summary>The most one coverage probe may take of the call budget.</summary>
    private static readonly TimeSpan ProbeCap = TimeSpan.FromSeconds(2);

    private readonly Func<string> minidumpDirectory;
    private readonly Func<EventLogRequest, EvidenceBudgetSlice, EventLogScan> readEvents;
    private readonly Func<string, IReadOnlyList<string>, EvidenceBudgetSlice, CoverageStore> probeChannel;
    private readonly Func<string, DateTimeOffset, EvidenceBudgetSlice, MinidumpRead> readMinidumps;

    /// <summary>Creates the tool over the local Event Log and <c>%SystemRoot%\Minidump</c>.</summary>
    public WindowsStabilityTool()
        : this(TimeProvider.System, DefaultMinidumpDirectory, null, null, null)
    {
    }

    internal WindowsStabilityTool(
        TimeProvider clock,
        Func<string> minidumpDirectory,
        Func<EventLogRequest, EvidenceBudgetSlice, EventLogScan>? readEvents = null,
        Func<string, IReadOnlyList<string>, EvidenceBudgetSlice, CoverageStore>? probeChannel = null,
        Func<string, DateTimeOffset, EvidenceBudgetSlice, MinidumpRead>? readMinidumps = null)
        : base("windows", clock)
    {
        this.minidumpDirectory = minidumpDirectory;
        this.readEvents = readEvents ?? ((request, slice) => WindowsEventLogEvidence.Read(request, slice));
        this.probeChannel = probeChannel ?? WindowsEventLogEvidence.ProbeChannel;
        this.readMinidumps = readMinidumps ?? ReadMinidumps;
    }

    /// <summary>The fixed category plan of this platform (ADR-0041 §3.1).</summary>
    internal static IReadOnlyList<StabilityCategoryPlan> Categories { get; } =
    [
        new(StabilityCategory.UnexpectedShutdown, CategoryApplicability.Applicable, null, [UnexpectedShutdownSource]),
        new(StabilityCategory.KernelCrash, CategoryApplicability.Applicable, null, [KernelCrashSource]),
        new(StabilityCategory.KernelFault, CategoryApplicability.NotCollected, KernelFaultDetail, []),
        new(StabilityCategory.HardwareError, CategoryApplicability.Applicable, null, [HardwareErrorSource]),
        new(StabilityCategory.DisplayFault, CategoryApplicability.Applicable, null, [DisplayFaultSystemSource, DisplayFaultWerSource]),
        new(StabilityCategory.StorageError, CategoryApplicability.Applicable, null, [StorageErrorSource]),
        new(StabilityCategory.MemoryExhaustion, CategoryApplicability.NotCollected, MemoryExhaustionDetail, []),
        new(StabilityCategory.Minidump, CategoryApplicability.Applicable, null, [MinidumpSource]),
    ];

    /// <inheritdoc />
    protected override Task<StabilitySnapshot> CollectAsync(StabilityQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var budget = new EvidenceTimeBudget(StabilityLimits.CallTimeout, Clock);
        var steps = 2 + EventSources.Count + 1;
        var stores = new List<CoverageStore>();
        foreach (var (channel, backed) in new (string, string[])[]
                 {
                     ("System", EventSources.Where(source => source.Channel == "System").Select(source => source.Name).ToArray()),
                     ("Application", [DisplayFaultWerSource]),
                 })
        {
            ct.ThrowIfCancellationRequested();
            using var slice = budget.Start(steps--, ct, ProbeCap);
            stores.Add(probeChannel(channel, backed, slice));
        }

        var evidence = new List<StabilityEvidence>();
        var sources = new List<InventorySourceResult>();
        var truncated = false;
        int boots = 0, cleanShutdowns = 0;
        foreach (var source in EventSources)
        {
            ct.ThrowIfCancellationRequested();
            using var slice = budget.Start(steps--, ct);
            var request = new EventLogRequest(
                source.Channel,
                WindowsEventLogEvidence.WindowXPath(source.Tuples.Select(tuple => WindowsEventLogEvidence.ProviderClause(tuple.Provider, tuple.EventIds)), query.FromUtc, query.ToUtc),
                query.FromUtc,
                StabilityLimits.EventLogRecordCeiling);
            var scan = readEvents(request, slice);
            ct.ThrowIfCancellationRequested();
            sources.Add(new InventorySourceResult(source.Name, scan.ChannelMissing ? InventorySourceStatus.Unavailable : scan.Status, scan.Detail) { ExaminedFromUtc = scan.ExaminedFromUtc });
            truncated |= scan.Truncated;
            if (source.Name == BootContextSource)
            {
                boots += scan.Records.Count(record => record.EventId == 6005);
                cleanShutdowns += scan.Records.Count(record => record.EventId == 6006);
                continue;
            }

            var seenReports = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in scan.Records)
            {
                if (Classify(source.Name, record) is not { } classified)
                {
                    continue;
                }

                // WER 1001 LiveKernelEvent records of one report (a re-processing burst) count once (ADR-0041 §7).
                if (classified.ReportId is { } reportId && !seenReports.Add(reportId))
                {
                    continue;
                }

                evidence.Add(classified.Evidence);
            }
        }

        ct.ThrowIfCancellationRequested();
        var directory = minidumpDirectory();
        using (var slice = budget.Start(steps, ct))
        {
            var inventory = readMinidumps(directory, query.FromUtc, slice);
            sources.Add(inventory.Source);
            truncated |= inventory.Truncated;
            if (inventory.Source.Status != InventorySourceStatus.NotApplicable)
            {
                stores.Add(new CoverageStore(MinidumpSource, CoverageBasis.Directory, inventory.Files.Count == 0 ? null : inventory.Files.Min(file => file.FileTimeUtc), null, [MinidumpSource]));
            }

            return Task.FromResult(new StabilitySnapshot(
                evidence,
                sources,
                Categories,
                new StabilityContext(CategoryApplicability.Applicable, BootContextSource, boots, cleanShutdowns),
                new MinidumpInventory(CategoryApplicability.Applicable, MinidumpSource, directory, inventory.Files),
                truncated)
            { Stores = stores });
        }
    }

    /// <summary>
    /// Types one record of a stability source (ADR-0041 §3.1). Returns <c>null</c> for a record that is not evidence of the source's
    /// category (for example a WER 1001 that is not a display live dump). Values that fail their pattern are <c>null</c>, never guessed.
    /// </summary>
    internal static ClassifiedStability? Classify(string sourceName, EventLogItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        switch (sourceName)
        {
            case UnexpectedShutdownSource when Is(record, KernelPower, 41):
                return new(new(
                    StabilityCategory.UnexpectedShutdown, KernelPower, "41",
                    ulong.TryParse(Field(record, "BugcheckCode", 0), NumberStyles.None, CultureInfo.InvariantCulture, out var bugcheck) ? EvidenceCodes.FromNumber(bugcheck) : null,
                    ulong.TryParse(Field(record, "PowerButtonTimestamp", 6), NumberStyles.None, CultureInfo.InvariantCulture, out var pressed) && pressed != 0 ? "powerButton" : null,
                    null, EvidenceTimestampKind.Reported, record.TimeCreatedUtc), null);
            case UnexpectedShutdownSource when Is(record, EventLogProvider, 6008):
                return new(new(StabilityCategory.UnexpectedShutdown, EventLogProvider, "6008", null, null, null, EvidenceTimestampKind.Reported, record.TimeCreatedUtc), null);
            case KernelCrashSource when Is(record, SystemErrorReporting, 1001):
                var parameter = Field(record, "param1", 0);
                var token = parameter is null ? null : LeadingHexToken().Match(parameter);
                return new(new(
                    StabilityCategory.KernelCrash, SystemErrorReporting, "1001",
                    token is { Success: true } ? EvidenceCodes.CanonicalHex(token.Groups[1].Value) : null,
                    null, null, EvidenceTimestampKind.Reported, record.TimeCreatedUtc), null);
            case HardwareErrorSource when Is(record, WheaLogger, null):
                // Conservative (review note R9): corrected only for a warning-level record, uncorrected only for an error or critical one;
                // any other level, informational included, is counted as unknown. A fatal error is logged after the restart.
                var severity = record.Level switch
                {
                    3 => StabilitySeverityClasses.Corrected,
                    1 or 2 => StabilitySeverityClasses.Uncorrected,
                    _ => StabilitySeverityClasses.Unknown,
                };
                return new(new(
                    StabilityCategory.HardwareError, WheaLogger, record.EventId.ToString(CultureInfo.InvariantCulture), null, null, severity,
                    severity == StabilitySeverityClasses.Corrected ? EvidenceTimestampKind.Occurred : EvidenceTimestampKind.Reported,
                    record.TimeCreatedUtc), null);
            case DisplayFaultSystemSource when Is(record, DisplayProvider, 4101):
                return new(new(
                    StabilityCategory.DisplayFault, DisplayProvider, "4101", null,
                    Matching(record.Values.Count > 0 ? record.Values[0] : null, DriverPattern()), null, EvidenceTimestampKind.Occurred, record.TimeCreatedUtc), null);
            case DisplayFaultWerSource when Is(record, WerProvider, 1001):
                var signature = WindowsWerNormalizer.FromWer1001(record.Data, record.IsUnnamed);
                return signature.Kind == CrashKinds.KernelLiveDump && signature.LiveDumpCode is { } code && DisplayLiveDumpCodes.Contains(code, StringComparer.Ordinal)
                    ? new(new(StabilityCategory.DisplayFault, WerProvider, "1001", code, null, null, EvidenceTimestampKind.Reported, record.TimeCreatedUtc), signature.ReportId)
                    : null;
            case StorageErrorSource when IsStorage(record, out var provider):
                return new(new(
                    StabilityCategory.StorageError, provider, record.EventId.ToString(CultureInfo.InvariantCulture), null,
                    Matching(record.Values.Count > 0 ? record.Values[0] : null, DevicePattern()), null, EvidenceTimestampKind.Occurred, record.TimeCreatedUtc), null);
            default:
                return null;
        }
    }

    /// <summary>
    /// Enumerates the top level of the minidump directory for names, sizes and last-write times only: a file is never opened,
    /// reparse points are not followed, and the enumeration stops after <see cref="StabilityLimits.MinidumpEntryCeiling"/> entries.
    /// Entries that are not <c>.dmp</c> files with a plain name are skipped and make the source partial. A missing directory is
    /// <c>notApplicable</c>; an unreadable one is <c>unavailable</c>, never empty.
    /// </summary>
    internal static MinidumpRead ReadMinidumps(string directory, DateTimeOffset windowFromUtc, EvidenceBudgetSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        var files = new List<MinidumpFile>();
        try
        {
            var info = new DirectoryInfo(directory);

            var entries = 0;
            var skipped = 0;
            foreach (var entry in info.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 }))
            {
                if (slice.Token.IsCancellationRequested)
                {
                    if (!slice.TimedOut)
                    {
                        slice.Token.ThrowIfCancellationRequested();
                    }

                    return new(files, new(MinidumpSource, InventorySourceStatus.Partial, "The enumeration did not finish within its share of the time bound."), true);
                }

                if (++entries > StabilityLimits.MinidumpEntryCeiling)
                {
                    return new(files, new(MinidumpSource, InventorySourceStatus.Partial, $"The {StabilityLimits.MinidumpEntryCeiling}-entry enumeration ceiling was reached."), true);
                }

                if (entry is not FileInfo file
                    || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || !file.Name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)
                    || !FileNamePattern().IsMatch(file.Name))
                {
                    skipped++;
                    continue;
                }

                files.Add(new MinidumpFile(file.Name, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), FileNameLocalDate(file.Name)));
            }

            return skipped > 0
                ? new(files, new(MinidumpSource, InventorySourceStatus.Partial, $"{skipped} entr{(skipped == 1 ? "y is" : "ies are")} not a plain .dmp file and {(skipped == 1 ? "was" : "were")} skipped.") { ExaminedFromUtc = windowFromUtc }, false)
                : new(files, new(MinidumpSource, InventorySourceStatus.Available, null) { ExaminedFromUtc = windowFromUtc }, false);
        }
        catch (DirectoryNotFoundException)
        {
            // Only a directory that is really missing is notApplicable; an unreadable one throws UnauthorizedAccessException below.
            return new([], new(MinidumpSource, InventorySourceStatus.NotApplicable, MinidumpMissingDetail), false);
        }
        catch (UnauthorizedAccessException)
        {
            return new([], new(MinidumpSource, InventorySourceStatus.Unavailable, MinidumpDeniedDetail), false);
        }
        catch (Exception exception) when (exception is IOException or System.Security.SecurityException)
        {
            return new([], new(MinidumpSource, InventorySourceStatus.Unavailable, "The minidump directory could not be read."), false);
        }
    }

    /// <summary>
    /// The date a minidump name of the form <c>MMDDYY-&lt;digits&gt;-&lt;digits&gt;.dmp</c> carries, as <c>20YY-MM-DD</c> when valid:
    /// descriptive file metadata in the machine's local calendar, never an occurrence time and never used for the window, buckets,
    /// order or first/last times (ADR-0041 §6).
    /// </summary>
    internal static string? FileNameLocalDate(string name)
    {
        var match = MinidumpNamePattern().Match(name);
        if (!match.Success)
        {
            return null;
        }

        var month = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var year = 2000 + int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
    }

    private static string DefaultMinidumpDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");

    private static bool Is(EventLogItem record, string provider, int? eventId) =>
        string.Equals(record.Provider, provider, StringComparison.OrdinalIgnoreCase) && (eventId is null || record.EventId == eventId);

    private static bool IsStorage(EventLogItem record, out string provider)
    {
        foreach (var (name, ids) in EventSources.Single(source => source.Name == StorageErrorSource).Tuples)
        {
            if (string.Equals(record.Provider, name, StringComparison.OrdinalIgnoreCase) && ids.Contains(record.EventId))
            {
                provider = name;
                return true;
            }
        }

        provider = string.Empty;
        return false;
    }

    /// <summary>A named field, or — only when the record carries no names — the value at <paramref name="index"/>.</summary>
    private static string? Field(EventLogItem record, string name, int index) =>
        record.IsUnnamed
            ? (index < record.Values.Count ? record.Values[index] : null)
            : record.Data.GetValueOrDefault(name);

    private static string? Matching(string? value, Regex pattern)
    {
        var trimmed = value?.Trim();
        return trimmed is not null && pattern.IsMatch(trimmed) ? trimmed : null;
    }

    [GeneratedRegex(@"^\s*(0x[0-9A-Fa-f]{1,16})\b", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingHexToken();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DriverPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.\\-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex DevicePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"^(\d{2})(\d{2})(\d{2})-\d+-\d+\.dmp$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MinidumpNamePattern();

    /// <summary>A typed record, and the report GUID it may be deduplicated by.</summary>
    internal sealed record ClassifiedStability(StabilityEvidence Evidence, string? ReportId);

    /// <summary>The outcome of the minidump enumeration.</summary>
    internal sealed record MinidumpRead(IReadOnlyList<MinidumpFile> Files, InventorySourceResult Source, bool Truncated);
}
