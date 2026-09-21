// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>
/// The one definition of which events a <see cref="SystemEventQuery"/> selects. Collectors use it for whatever their native
/// query cannot express, and the tool applies it again to what they return, so both platforms select the same events.
/// </summary>
public static class SystemEventFilter
{
    /// <summary>True when <paramref name="record"/> is inside the window and satisfies every filter of <paramref name="query"/>.</summary>
    public static bool Matches(SystemEventRecord record, SystemEventQuery query)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(query);

        if (record.TimestampUtc < query.FromUtc || record.TimestampUtc > query.ToUtc)
        {
            return false;
        }

        if (query.MinSeverity is { } minimum
            && (record.Severity == SystemEventSeverity.Unknown || record.Severity > minimum))
        {
            return false;
        }

        if (query.Source is { } source
            && !Equal(record.Source, source)
            && !Equal(record.Unit, source))
        {
            return false;
        }

        if (query.EventId is { } eventId && !Equal(record.EventId, eventId))
        {
            return false;
        }

        if (query.Channel is { } channel && !Equal(record.Channel, channel))
        {
            return false;
        }

        return query.Text is not { } text
            || record.Message.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Equal(string? left, string right) =>
        left is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
