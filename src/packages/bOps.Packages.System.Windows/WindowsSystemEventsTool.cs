// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// Reads recent events from the Windows Event Log through <see cref="EventLogQuery"/> and <see cref="EventLogReader"/> (ADR-0032): no
/// PowerShell, no <c>wevtutil</c>, no elevation and no identity switch. Only channels the current identity can read are read; a denied
/// or missing channel is reported as a source that could not be read, never as an empty log.
/// </summary>
public sealed class WindowsSystemEventsTool : SystemEventsToolBase
{
    private const int MaximumFallbackProperties = 8;
    private const int MaximumDetailCharacters = 200;

    private static readonly string[] DefaultChannels = ["System", "Application"];
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly TimeSpan timeout;

    /// <summary>Creates the tool over the local Event Log.</summary>
    public WindowsSystemEventsTool()
        : this(TimeProvider.System, DefaultTimeout)
    {
    }

    internal WindowsSystemEventsTool(TimeProvider clock, TimeSpan timeout)
        : base("windows", clock)
    {
        this.timeout = timeout;
    }

    /// <inheritdoc />
    protected override string? ValidateEventId(string eventId)
    {
        ArgumentNullException.ThrowIfNull(eventId);
        return eventId.Length is > 0 and <= 5 && eventId.All(char.IsAsciiDigit)
            && int.Parse(eventId, NumberStyles.None, CultureInfo.InvariantCulture) <= 65_535
            ? null
            : "eventId must be a number from 0 to 65535 on Windows.";
    }

    /// <inheritdoc />
    protected override string? ValidateChannel(string channel) => null;

    /// <inheritdoc />
    protected override Task<SystemEventSnapshot> CollectAsync(SystemEventQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var events = new List<SystemEventRecord>();
        var sources = new List<InventorySourceResult>();
        var truncated = false;
        var explicitChannel = query.Channel is not null;
        var providerName = query.Source is { } source ? ResolveProvider(source) : null;
        var xpath = WindowsEventXPath.Build(query, providerName);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);

        foreach (var channel in query.Channel is { } requested ? [requested] : DefaultChannels)
        {
            ct.ThrowIfCancellationRequested();
            var name = $"windows.channel.{channel}";
            sources.Add(ReadChannel(channel, name, xpath, query, explicitChannel, events, linked, timeoutSource, ct, out var channelTruncated));
            truncated |= channelTruncated;
        }

        return Task.FromResult(new SystemEventSnapshot(events, sources, truncated));
    }

    private static InventorySourceResult ReadChannel(
        string channel,
        string sourceName,
        string xpath,
        SystemEventQuery query,
        bool explicitChannel,
        List<SystemEventRecord> events,
        CancellationTokenSource linked,
        CancellationTokenSource timeoutSource,
        CancellationToken ct,
        out bool truncated)
    {
        truncated = false;
        var scanned = 0;
        var skipped = 0;
        try
        {
            var eventQuery = new EventLogQuery(channel, PathType.LogName, xpath) { ReverseDirection = true, TolerateQueryErrors = false };
            using var reader = new EventLogReader(eventQuery);
            using var registration = linked.Token.Register(reader.CancelReading);

            while (true)
            {
                EventRecord? record;
                try
                {
                    record = reader.ReadEvent();
                }
                catch (EventLogException) when (scanned > 0)
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
                    scanned++;
                    if (TryNormalize(record, out var normalized) && SystemEventFilter.Matches(normalized!, query))
                    {
                        events.Add(normalized!);
                    }
                    else if (normalized is null)
                    {
                        skipped++;
                    }
                }

                if (scanned >= query.ScanCeiling)
                {
                    truncated = true;
                    return new(sourceName, InventorySourceStatus.Partial, "The scan ceiling was reached before the start of the window.");
                }
            }

            return skipped > 0
                ? new(sourceName, InventorySourceStatus.Partial, $"{skipped} record(s) could not be read and were skipped.")
                : new(sourceName, InventorySourceStatus.Available, null);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            truncated = true;
            return new(sourceName, InventorySourceStatus.Partial, "The read did not finish in time and was stopped.");
        }
        catch (EventLogNotFoundException)
        {
            return explicitChannel
                ? new(sourceName, InventorySourceStatus.Unavailable, "The channel does not exist on this machine.")
                : new(sourceName, InventorySourceStatus.NotApplicable, "The channel does not exist on this machine.");
        }
        catch (UnauthorizedAccessException)
        {
            return new(sourceName, InventorySourceStatus.Unavailable, "The current identity is not allowed to read this channel.");
        }
        catch (EventLogException exception)
        {
            return new(sourceName, InventorySourceStatus.Unavailable, Bounded(exception.Message));
        }
    }

    private static bool TryNormalize(EventRecord record, out SystemEventRecord? normalized)
    {
        normalized = null;
        try
        {
            if (record.TimeCreated is not { } created || string.IsNullOrWhiteSpace(record.ProviderName))
            {
                return false;
            }

            normalized = new SystemEventRecord(
                new DateTimeOffset(created.ToUniversalTime(), TimeSpan.Zero),
                ToSeverity(record.Level),
                record.ProviderName,
                record.Id.ToString(CultureInfo.InvariantCulture),
                record.LogName,
                CapMessage(Message(record)),
                record.ProcessId,
                null);
            return true;
        }
        catch (EventLogException)
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps one character more than the result can carry, so the text filter sees exactly what the result can show (as on Linux) and
    /// the formatter can still tell that the message was cut.
    /// </summary>
    internal static string CapMessage(string message) =>
        message.Length > SystemEventsLimits.MessageCharacters + 1 ? message[..(SystemEventsLimits.MessageCharacters + 1)] : message;

    /// <summary>
    /// The event's own description when its provider registers one; otherwise the event's data values, which are what the description
    /// would have been filled from. An event with neither has an empty message rather than an invented one.
    /// </summary>
    private static string Message(EventRecord record)
    {
        string? description = null;
        try
        {
            description = record.FormatDescription();
        }
        catch (EventLogException)
        {
            // No message DLL for this provider on this machine: fall back to the data below.
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        try
        {
            return string.Join(
                " | ",
                record.Properties.Take(MaximumFallbackProperties).Select(property => property.Value?.ToString()).Where(value => !string.IsNullOrEmpty(value)));
        }
        catch (EventLogException)
        {
            return string.Empty;
        }
    }

    private static SystemEventSeverity ToSeverity(byte? level) => level switch
    {
        1 => SystemEventSeverity.Critical,
        2 => SystemEventSeverity.Error,
        3 => SystemEventSeverity.Warning,
        4 => SystemEventSeverity.Information,
        5 => SystemEventSeverity.Verbose,
        _ => SystemEventSeverity.Unknown,
    };

    /// <summary>
    /// A native provider filter is case-sensitive but the tool's <c>source</c> is not, so the name is matched to the spelling the provider
    /// registered. A provider that is not registered here (or a failure to list them) leaves the filter to the shared, case-insensitive one.
    /// </summary>
    private static string? ResolveProvider(string source)
    {
        try
        {
            using var session = new EventLogSession();
            return session.GetProviderNames().FirstOrDefault(name => string.Equals(name, source, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is EventLogException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Bounded(string? detail) =>
        string.IsNullOrEmpty(detail)
            ? "The channel could not be read."
            : detail.Length > MaximumDetailCharacters ? detail[..MaximumDetailCharacters] : detail;
}
