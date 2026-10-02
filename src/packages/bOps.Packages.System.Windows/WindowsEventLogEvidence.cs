// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// One Windows Event Log record reduced to what the evidence tools read: identity, provider, id, level, record time and the event data
/// (by name and in order). Built from the record's own XML inside the package; no XML ever leaves it.
/// </summary>
/// <param name="Channel">The channel the record was read from.</param>
/// <param name="RecordId">The channel's record id, the record's exact native identity within the channel.</param>
/// <param name="Provider">The provider name.</param>
/// <param name="EventId">The event id.</param>
/// <param name="Level">The native level (1 critical, 2 error, 3 warning, 4 information, 5 verbose), when present.</param>
/// <param name="TimeCreatedUtc">The record time.</param>
/// <param name="Data">The event data: by name when the provider names its values, otherwise by position (<c>"0"</c>, <c>"1"</c>, …).</param>
/// <param name="Values">The event data values in document order.</param>
internal sealed record EventLogItem(
    string Channel,
    long? RecordId,
    string Provider,
    int EventId,
    byte? Level,
    DateTimeOffset TimeCreatedUtc,
    IReadOnlyDictionary<string, string> Data,
    IReadOnlyList<string> Values)
{
    /// <summary>True when every data value is positional: the provider's manifest names none of them.</summary>
    public bool IsUnnamed => Values.Count > 0 && Data.Keys.All(key => key.All(char.IsAsciiDigit));
}

/// <summary>The outcome of one bounded Event Log read.</summary>
/// <param name="Records">The records read and accepted by the caller's filter.</param>
/// <param name="Status">The source status the read supports.</param>
/// <param name="Detail">A bounded explanation, or <c>null</c>.</param>
/// <param name="Truncated">True when the record ceiling or the time slice stopped the read before the start of the window.</param>
/// <param name="ExaminedFromUtc">The start of the window when the read reached it, the oldest record time read when it was stopped, else <c>null</c>.</param>
/// <param name="ChannelMissing">True when the channel does not exist on this machine.</param>
internal sealed record EventLogScan(
    IReadOnlyList<EventLogItem> Records,
    InventorySourceStatus Status,
    string? Detail,
    bool Truncated,
    DateTimeOffset? ExaminedFromUtc,
    bool ChannelMissing = false);

/// <summary>One bounded read: a channel, an XPath built only from package constants and validated values, and the window it covers.</summary>
internal sealed record EventLogRequest(string Channel, string XPath, DateTimeOffset WindowFromUtc, int Ceiling);

/// <summary>
/// Bounded native reads of the Windows Event Log through <see cref="EventLogReader"/> — no PowerShell, no <c>wevtutil</c>, no
/// elevation — shared by <c>system.events</c>, <c>system.crashes</c> and <c>system.stability</c>, and the temporal-coverage probe of a
/// channel (ADR-0032 HARDEN-7 amendment §5).
/// </summary>
internal static class WindowsEventLogEvidence
{
    private const int MaximumDetailCharacters = 200;

    /// <summary>
    /// Reads <paramref name="request"/> newest first until the window start, the record ceiling or the end of <paramref name="slice"/>.
    /// A record that cannot be read or parsed is skipped and makes the source partial; a stopped read keeps what it read.
    /// </summary>
    internal static EventLogScan Read(EventLogRequest request, EvidenceBudgetSlice slice, Func<EventLogItem, bool>? accept = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(slice);
        var records = new List<EventLogItem>();
        DateTimeOffset? oldestRead = null;
        var scanned = 0;
        var skipped = 0;
        try
        {
            if (slice.Token.IsCancellationRequested)
            {
                return Stopped(records, oldestRead, slice);
            }

            using var reader = new EventLogReader(new EventLogQuery(request.Channel, PathType.LogName, request.XPath) { ReverseDirection = true, TolerateQueryErrors = false });
            using var registration = slice.Token.Register(reader.CancelReading);
            while (true)
            {
                EventRecord? record;
                try
                {
                    record = reader.ReadEvent();
                }
                catch (EventLogException) when (scanned > 0 && !slice.Token.IsCancellationRequested)
                {
                    skipped++;
                    break;
                }

                if (record is null)
                {
                    break;
                }

                using (record)
                {
                    slice.Token.ThrowIfCancellationRequested();
                    scanned++;
                    if (TryParse(record, request.Channel, out var item))
                    {
                        oldestRead = oldestRead is { } known && known < item!.TimeCreatedUtc ? known : item!.TimeCreatedUtc;
                        if (accept is null || accept(item))
                        {
                            records.Add(item);
                        }
                    }
                    else
                    {
                        skipped++;
                    }
                }

                if (scanned >= request.Ceiling)
                {
                    return new(records, InventorySourceStatus.Partial, "The record ceiling was reached before the start of the window.", true, oldestRead);
                }
            }

            return skipped > 0
                ? new(records, InventorySourceStatus.Partial, $"{skipped} record(s) could not be read and were skipped.", false, request.WindowFromUtc)
                : new(records, InventorySourceStatus.Available, null, false, request.WindowFromUtc);
        }
        catch (OperationCanceledException) when (slice.TimedOut)
        {
            return Stopped(records, oldestRead, slice);
        }
        catch (EventLogNotFoundException)
        {
            return new(records, InventorySourceStatus.Unavailable, "The channel does not exist on this machine.", false, null, ChannelMissing: true);
        }
        catch (UnauthorizedAccessException)
        {
            return new(records, InventorySourceStatus.Unavailable, "The current identity is not allowed to read this channel.", false, null);
        }
        catch (EventLogException exception) when (!slice.TimedOut)
        {
            return new(records, InventorySourceStatus.Unavailable, Bounded(exception.Message), false, null);
        }
        catch (EventLogException)
        {
            return Stopped(records, oldestRead, slice);
        }
    }

    /// <summary>
    /// The coverage of one channel: the record time of its oldest live record (one forward read of one record) and its configured
    /// maximum size. Either is <c>null</c> when it cannot be read, including when <paramref name="slice"/> runs out first.
    /// </summary>
    internal static CoverageStore ProbeChannel(string channel, IReadOnlyList<string> backedSources, EvidenceBudgetSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        return new CoverageStore(
            "windows.channel." + channel,
            CoverageBasis.EventLog,
            OldestRecord(channel, slice),
            MaximumSize(channel),
            backedSources);
    }

    /// <summary>
    /// Parses a record's own XML. Data values are read by name when present and by position otherwise; the record id and the
    /// provider come from the native record. Returns <c>false</c> for a record without a time or a provider.
    /// </summary>
    internal static bool TryParse(EventRecord record, string channel, out EventLogItem? item)
    {
        item = null;
        try
        {
            if (record.TimeCreated is not { } created || string.IsNullOrWhiteSpace(record.ProviderName))
            {
                return false;
            }

            var (data, values) = ParseData(record.ToXml());
            item = new EventLogItem(
                channel,
                record.RecordId,
                record.ProviderName,
                record.Id,
                record.Level,
                new DateTimeOffset(created.ToUniversalTime(), TimeSpan.Zero),
                data,
                values);
            return true;
        }
        catch (Exception exception) when (exception is EventLogException or XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a recorded event XML document (the shape <c>EventRecord.ToXml()</c> returns) into an <see cref="EventLogItem"/>. Used by
    /// the golden tests, which feed recorded and anonymized XML through exactly the path a live record takes.
    /// </summary>
    internal static EventLogItem ParseXml(string xml, string channel)
    {
        var document = XElement.Parse(xml);
        var system = document.Elements().First(element => element.Name.LocalName == "System");
        string Value(string name) => system.Elements().First(element => element.Name.LocalName == name).Value;
        var provider = system.Elements().First(element => element.Name.LocalName == "Provider").Attribute("Name")!.Value;
        var time = system.Elements().First(element => element.Name.LocalName == "TimeCreated").Attribute("SystemTime")!.Value;
        var level = system.Elements().FirstOrDefault(element => element.Name.LocalName == "Level")?.Value;
        var (data, values) = ParseData(xml);
        return new EventLogItem(
            channel,
            long.TryParse(Value("EventRecordID"), NumberStyles.None, CultureInfo.InvariantCulture, out var recordId) ? recordId : null,
            provider,
            int.Parse(Value("EventID"), NumberStyles.None, CultureInfo.InvariantCulture),
            byte.TryParse(level, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLevel) ? parsedLevel : null,
            DateTimeOffset.Parse(time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            data,
            values);
    }

    /// <summary>An XPath clause matching one provider and, when given, any of <paramref name="eventIds"/>.</summary>
    internal static string ProviderClause(string provider, IReadOnlyList<int> eventIds)
    {
        var clause = $"Provider[@Name='{provider}']";
        return eventIds.Count == 0
            ? clause
            : "(" + clause + " and (" + string.Join(" or ", eventIds.Select(id => "EventID=" + id.ToString(CultureInfo.InvariantCulture))) + "))";
    }

    /// <summary>The XPath of a window over one or more provider clauses, all built from package constants.</summary>
    internal static string WindowXPath(IEnumerable<string> providerClauses, DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        "*[System[(" + string.Join(" or ", providerClauses) + ") and TimeCreated[@SystemTime>='" + Time(fromUtc) + "' and @SystemTime<='" + Time(toUtc) + "']]]";

    internal static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static (Dictionary<string, string> Data, List<string> Values) ParseData(string xml)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var values = new List<string>();
        var eventData = XElement.Parse(xml).Elements().FirstOrDefault(element => element.Name.LocalName == "EventData");
        if (eventData is null)
        {
            return (data, values);
        }

        foreach (var element in eventData.Elements().Where(element => element.Name.LocalName == "Data"))
        {
            var key = element.Attribute("Name")?.Value;
            data.TryAdd(string.IsNullOrEmpty(key) ? values.Count.ToString(CultureInfo.InvariantCulture) : key, element.Value);
            values.Add(element.Value);
        }

        return (data, values);
    }

    private static DateTimeOffset? OldestRecord(string channel, EvidenceBudgetSlice slice)
    {
        if (slice.Token.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            using var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName) { ReverseDirection = false });
            using var registration = slice.Token.Register(reader.CancelReading);
            using var record = reader.ReadEvent();
            return record?.TimeCreated is { } created ? new DateTimeOffset(created.ToUniversalTime(), TimeSpan.Zero) : null;
        }
        catch (Exception exception) when (exception is EventLogException or UnauthorizedAccessException or OperationCanceledException)
        {
            return null;
        }
    }

    private static long? MaximumSize(string channel)
    {
        try
        {
            using var configuration = new EventLogConfiguration(channel);
            return configuration.MaximumSizeInBytes;
        }
        catch (Exception exception) when (exception is EventLogException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static EventLogScan Stopped(List<EventLogItem> records, DateTimeOffset? oldestRead, EvidenceBudgetSlice slice) =>
        new(records, InventorySourceStatus.Partial, $"The read did not finish within its {Seconds(slice.Length)} time bound and was stopped.", true, oldestRead);

    private static string Seconds(TimeSpan length) =>
        length.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "-second";

    internal static string Bounded(string? detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? "The channel could not be read."
            : detail.Length > MaximumDetailCharacters ? detail[..MaximumDetailCharacters] : detail;

    /// <summary>Removes control characters and surrounding space; empty or whitespace-only text is <c>null</c>.</summary>
    internal static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!trimmed.Any(char.IsControl))
        {
            return trimmed;
        }

        var builder = new StringBuilder(trimmed.Length);
        foreach (var character in trimmed.Where(character => !char.IsControl(character)))
        {
            builder.Append(character);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
