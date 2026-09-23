// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// Reads bounded Windows crash evidence from official WER metadata directories and the two
/// relevant Application Event Log providers. Dump files are deliberately never opened.
/// </summary>
public sealed class WindowsCrashEvidenceTool : SystemCrashesToolBase
{
    private const int MaximumReportsPerDirectory = 512;
    private const int MaximumEventsPerProvider = 512;
    private const int MaximumMetadataBytes = 32 * 1024;
    private const int MaximumRows = 2_048;
    private static readonly TimeSpan EventLogTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] WerRelativeDirectories = ["Microsoft\\Windows\\WER\\ReportArchive", "Microsoft\\Windows\\WER\\ReportQueue"];
    private readonly TimeProvider clock;
    private readonly Func<IEnumerable<string>> knownWerDirectories;

    public WindowsCrashEvidenceTool() : this(TimeProvider.System, KnownWerDirectories) { }
    internal WindowsCrashEvidenceTool(TimeProvider clock) : this(clock, KnownWerDirectories) { }
    internal WindowsCrashEvidenceTool(TimeProvider clock, Func<IEnumerable<string>> knownWerDirectories) : base("windows")
    {
        this.clock = clock;
        this.knownWerDirectories = knownWerDirectories;
    }

    protected override Task<MaintenanceSnapshot<CrashRecord>> CollectAsync(MaintenanceArguments arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var since = clock.GetUtcNow().AddMinutes(-arguments.SinceMinutes!.Value);
        var rows = new List<CrashRecord>();
        var sources = new List<MaintenanceSource>();
        var warnings = new List<string>();
        var truncated = false;

        foreach (var directory in knownWerDirectories())
        {
            ct.ThrowIfCancellationRequested();
            var result = ReadWerDirectory(directory, since, ct);
            rows.AddRange(result.Rows);
            sources.Add(result.Source);
            warnings.AddRange(result.Warnings);
            truncated |= result.Truncated;
        }

        foreach (var provider in new[] { new CrashEventProvider("Application Error", 1000, "windows-event-application-error"), new CrashEventProvider("Windows Error Reporting", 1001, "windows-event-wer") })
        {
            ct.ThrowIfCancellationRequested();
            var result = ReadEvents(provider, since, ct);
            rows.AddRange(result.Rows);
            sources.Add(result.Source);
            warnings.AddRange(result.Warnings);
            truncated |= result.Truncated;
        }

        var deduplicatedWer = rows
            .GroupBy(row => row.Source == "windows-wer-report" && !string.IsNullOrWhiteSpace(row.EventIdOrCrashId)
                ? "wer:" + row.EventIdOrCrashId : Guid.NewGuid().ToString("N"), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(row => row.TimestampUtc).ThenBy(row => row.Process, StringComparer.Ordinal).First())
            .OrderByDescending(row => row.TimestampUtc).ThenBy(row => row.Process, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Process, StringComparer.Ordinal).ThenBy(row => row.Source, StringComparer.Ordinal).ToList();
        if (deduplicatedWer.Count > MaximumRows)
        {
            deduplicatedWer.RemoveRange(MaximumRows, deduplicatedWer.Count - MaximumRows);
            truncated = true;
            warnings.Add("The normalized crash-evidence ceiling was reached.");
        }

        return Task.FromResult(new MaintenanceSnapshot<CrashRecord>(deduplicatedWer, sources, warnings, truncated));
    }

    private static IEnumerable<string> KnownWerDirectories()
    {
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
        return roots.Where(root => !string.IsNullOrWhiteSpace(root)).SelectMany(root => WerRelativeDirectories.Select(relative => Path.Combine(root, relative))).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static CrashSourceResult ReadWerDirectory(string directory, DateTimeOffset since, CancellationToken ct)
    {
        var sourceName = "windows.wer." + Path.GetFileName(directory).ToLowerInvariant();
        if (!Directory.Exists(directory)) return new([], new(sourceName, InventorySourceStatus.NotApplicable, "The known WER directory is not present."), [], false);
        try
        {
            var reports = Directory.EnumerateDirectories(directory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(MaximumReportsPerDirectory + 1).ToArray();
            var truncated = reports.Length > MaximumReportsPerDirectory;
            var rows = new List<CrashRecord>();
            var warnings = new List<string>();
            foreach (var report in reports.Take(MaximumReportsPerDirectory))
            {
                ct.ThrowIfCancellationRequested();
                var metadata = Path.Combine(report, "Report.wer");
                if (!File.Exists(metadata)) continue;
                try
                {
                    var parsed = WindowsWerReport.Parse(ReadBoundedText(metadata));
                    var row = parsed.ToCrashRecord(File.GetLastWriteTimeUtc(metadata));
                    if (row.TimestampUtc >= since) rows.Add(row);
                }
                catch (InvalidDataException exception)
                {
                    warnings.Add("WER report metadata was skipped: " + Bounded(exception.Message));
                }
                catch (UnauthorizedAccessException)
                {
                    return new(rows, new(sourceName, InventorySourceStatus.Unavailable, "The current identity cannot read a WER report."), warnings, truncated);
                }
                catch (IOException)
                {
                    return new(rows, new(sourceName, InventorySourceStatus.Unavailable, "A WER report could not be read."), warnings, truncated);
                }
            }
            if (truncated) warnings.Add("The WER report-directory ceiling was reached.");
            var status = warnings.Count > 0 || truncated ? InventorySourceStatus.Partial : InventorySourceStatus.Available;
            return new(rows, new(sourceName, status, status == InventorySourceStatus.Partial ? "One or more WER reports were unavailable, malformed, or beyond bounds." : null), warnings, truncated);
        }
        catch (UnauthorizedAccessException)
        {
            return new([], new(sourceName, InventorySourceStatus.Unavailable, "The current identity cannot read this known WER directory."), [], false);
        }
        catch (IOException)
        {
            return new([], new(sourceName, InventorySourceStatus.Unavailable, "This known WER directory could not be read."), [], false);
        }
    }

    private static CrashSourceResult ReadEvents(CrashEventProvider provider, DateTimeOffset since, CancellationToken ct)
    {
        var sourceName = provider.Source;
        var rows = new List<CrashRecord>();
        try
        {
            var start = since.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            var xpath = "*[System[TimeCreated[@SystemTime>='" + start + "'] and Provider[@Name='" + provider.Name + "'] and EventID=" + provider.EventId.ToString(CultureInfo.InvariantCulture) + "]]";
            using var timeout = new CancellationTokenSource(EventLogTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, xpath) { ReverseDirection = true, TolerateQueryErrors = false });
            using var registration = linked.Token.Register(reader.CancelReading);
            var count = 0;
            while (true)
            {
                EventRecord? record;
                try { record = reader.ReadEvent(); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    return new(rows, new(sourceName, InventorySourceStatus.Partial, "The Event Log read did not finish in time."), ["The Event Log read did not finish in time."], true);
                }
                if (record is null) break;
                using (record)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (++count > MaximumEventsPerProvider) return new(rows, new(sourceName, InventorySourceStatus.Partial, "The Event Log record ceiling was reached."), ["The Event Log record ceiling was reached."], true);
                    if (TryReadEventEvidence(record, out var evidence) && WindowsCrashEventNormalizer.TryNormalize(evidence!, out var row)) rows.Add(row!);
                }
            }
            return new(rows, new(sourceName, InventorySourceStatus.Available), [], false);
        }
        catch (UnauthorizedAccessException)
        {
            return new(rows, new(sourceName, InventorySourceStatus.Unavailable, "The current identity cannot read the Application Event Log."), [], false);
        }
        catch (EventLogException exception)
        {
            return new(rows, new(sourceName, InventorySourceStatus.Unavailable, Bounded(exception.Message)), [], false);
        }
    }

    private static bool TryReadEventEvidence(EventRecord record, out WindowsCrashEventEvidence? evidence)
    {
        evidence = null;
        try
        {
            if (record.TimeCreated is not { } timestamp || string.IsNullOrWhiteSpace(record.ProviderName)) return false;
            var data = XElement.Parse(record.ToXml()).Descendants().Where(x => x.Name.LocalName == "Data")
                .Select((x, index) => new KeyValuePair<string, string>(x.Attribute("Name")?.Value ?? index.ToString(CultureInfo.InvariantCulture), x.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            evidence = new(record.ProviderName, record.Id, new DateTimeOffset(timestamp.ToUniversalTime(), TimeSpan.Zero), record.RecordId?.ToString(CultureInfo.InvariantCulture), data);
            return true;
        }
        catch (Exception exception) when (exception is EventLogException or System.Xml.XmlException)
        {
            return false;
        }
    }

    private static string ReadBoundedText(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumMetadataBytes) throw new InvalidDataException("The report metadata exceeds the byte limit.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[MaximumMetadataBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > MaximumMetadataBytes) throw new InvalidDataException("The report metadata exceeds the byte limit.");
        var payload = bytes.AsSpan(0, count);
        Encoding encoding = new UTF8Encoding(false, true);
        if (payload.StartsWith(Encoding.Unicode.GetPreamble())) { payload = payload[2..]; encoding = new UnicodeEncoding(false, true, true); }
        else if (payload.StartsWith(Encoding.BigEndianUnicode.GetPreamble())) { payload = payload[2..]; encoding = new UnicodeEncoding(true, true, true); }
        else if (payload.StartsWith(Encoding.UTF8.GetPreamble())) payload = payload[3..];
        try { return encoding.GetString(payload); }
        catch (DecoderFallbackException) { throw new InvalidDataException("The report metadata has an invalid text encoding."); }
    }

    private static string Bounded(string? value) => string.IsNullOrWhiteSpace(value) ? "The Event Log query could not be read." : value.Length <= 200 ? value : value[..200];
    private sealed record CrashEventProvider(string Name, int EventId, string Source);
    private sealed record CrashSourceResult(IReadOnlyList<CrashRecord> Rows, MaintenanceSource Source, IReadOnlyList<string> Warnings, bool Truncated);
}

/// <summary>Strict, bounded parser for the key/value fields bOps consumes from an official Report.wer file.</summary>
internal sealed record WindowsWerReport(IReadOnlyDictionary<string, string> Values)
{
    public static WindowsWerReport Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            if (key.Length is 0 or > 128) continue;
            var value = line[(separator + 1)..].Trim();
            if (value.Length > 2_048) value = value[..2_048];
            values.TryAdd(key, value);
        }
        if (values.Count == 0) throw new InvalidDataException("The report has no key/value metadata.");
        return new(values);
    }

    public CrashRecord ToCrashRecord(DateTime lastWriteUtc)
    {
        var timestamp = TryFileTime("EventTime") ?? new DateTimeOffset(DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc));
        var process = First("AppName", "NsAppName", "ProcessName");
        var pid = TryPid(First("AppPid", "Pid", "ProcessId"));
        var type = First("EventType")?.ToUpperInvariant();
        var kind = type switch { "APPCRASH" or "BEX" or "CLR20R3" => "application-crash", "APPHANG" => "application-hang", null => "wer", _ => "wer" };
        var id = First("ReportIdentifier", "CabId", "ReportId", "Response.BucketId");
        var dump = First("DumpPath", "DumpFile", "MinidumpPath");
        var module = First("FaultModuleName", "FaultingModule", "Sig[3]");
        var summary = string.Join("; ", new[] { type, module }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new(timestamp, process, pid, kind, dump, id, string.IsNullOrWhiteSpace(summary) ? null : summary, "windows-wer-report");
    }

    private string? First(params string[] names) => names.Select(name => Values.GetValueOrDefault(name)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    private DateTimeOffset? TryFileTime(string name) => long.TryParse(Values.GetValueOrDefault(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? DateTimeOffset.FromFileTime(value) : null;
    private static int? TryPid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var style = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;
        var text = style == NumberStyles.AllowHexSpecifier ? value[2..] : value;
        return int.TryParse(text, style, CultureInfo.InvariantCulture, out var pid) && pid > 0 ? pid : null;
    }
}

internal sealed record WindowsCrashEventEvidence(string Provider, int EventId, DateTimeOffset TimestampUtc, string? RecordId, IReadOnlyDictionary<string, string> Data);

internal static class WindowsCrashEventNormalizer
{
    public static bool TryNormalize(WindowsCrashEventEvidence evidence, out CrashRecord? row)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        row = null;
        var isApplicationError = evidence.Provider.Equals("Application Error", StringComparison.OrdinalIgnoreCase) && evidence.EventId == 1000;
        var isWer = evidence.Provider.Equals("Windows Error Reporting", StringComparison.OrdinalIgnoreCase) && evidence.EventId == 1001;
        if (!isApplicationError && !isWer) return false;
        var process = First(evidence.Data, "AppName", "FaultingApplicationName", "0");
        var pid = ParsePid(First(evidence.Data, "ProcessId", "FaultingProcessId", "6"));
        var crashId = First(evidence.Data, "ReportId", "ReportIdentifier", "CabId") ?? evidence.RecordId;
        var eventType = First(evidence.Data, "EventType");
        var kind = isApplicationError ? "application-crash" : eventType?.Equals("APPHANG", StringComparison.OrdinalIgnoreCase) == true ? "application-hang" : "wer";
        var summary = First(evidence.Data, "FaultingModuleName", "FaultModuleName", "P1", "1");
        row = new(evidence.TimestampUtc.ToUniversalTime(), process, pid, kind, First(evidence.Data, "DumpPath", "DumpFile"), crashId, summary, isApplicationError ? "windows-event-application-error" : "windows-event-wer");
        return true;
    }

    private static string? First(IReadOnlyDictionary<string, string> values, params string[] names) => names.Select(name => values.GetValueOrDefault(name)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    private static int? ParsePid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var style = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;
        return int.TryParse(style == NumberStyles.AllowHexSpecifier ? value[2..] : value, style, CultureInfo.InvariantCulture, out var pid) && pid > 0 ? pid : null;
    }
}
