// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>
/// Builds the <c>journalctl</c> argument list for a <see cref="SystemEventQuery"/> (ADR-0032). Every switch is fixed here and every
/// value is its own list item for <c>ProcessStartInfo.ArgumentList</c>: nothing is ever composed into a command string, nothing the
/// model supplied becomes a switch, and the only values that come from the query are a time, a priority number and two
/// <c>FIELD=value</c> matches whose values have already been restricted to a fixed character set.
/// </summary>
internal static class JournalctlArguments
{
    /// <summary>The argument list, in order.</summary>
    internal static IReadOnlyList<string> Build(SystemEventQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var arguments = new List<string>
        {
            "--no-pager",
            "--output=json",
            "--reverse",
            "--utc",
            "--since=" + Time(query.FromUtc),
            "--until=" + Time(query.ToUtc),
            "--lines=" + query.ScanCeiling.ToString(CultureInfo.InvariantCulture),
            "--output-fields=" + string.Join(',', JournalRecordParser.RequestedFields),
        };

        if (Priority(query.MinSeverity) is { } priority)
        {
            arguments.Add("--priority=" + priority.ToString(CultureInfo.InvariantCulture));
        }

        // Matches on different fields are ANDed by journald, which is what these two need. Source and text are not journald matches:
        // "identifier or unit" would need a disjunction that does not compose with them, so the shared filter applies them.
        if (query.Channel is { } channel)
        {
            arguments.Add("_TRANSPORT=" + channel.ToLowerInvariant());
        }

        if (query.EventId is { } eventId)
        {
            arguments.Add("MESSAGE_ID=" + eventId.ToLowerInvariant());
        }

        return arguments;
    }

    private static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    /// <summary><c>--priority=N</c> keeps priorities 0 to N, so "at least this severe" is the highest number still wanted.</summary>
    private static int? Priority(SystemEventSeverity? minimum) => minimum switch
    {
        SystemEventSeverity.Critical => 2,
        SystemEventSeverity.Error => 3,
        SystemEventSeverity.Warning => 4,
        SystemEventSeverity.Information => 6,
        _ => null,
    };
}
