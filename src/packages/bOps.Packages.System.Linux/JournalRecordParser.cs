// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Linux;

/// <summary>
/// Turns one line of <c>journalctl --output=json</c> into the normalized <see cref="SystemEventRecord"/> (ADR-0032). Only the fields
/// the tool asks journald for are read; nothing else from the record is kept. A line that is not a usable record is reported as
/// such, never guessed at.
/// </summary>
internal static class JournalRecordParser
{
    /// <summary>The <c>_TRANSPORT</c> values journald documents, which are the accepted values of the <c>channel</c> argument.</summary>
    internal static readonly IReadOnlyList<string> Transports = ["audit", "driver", "journal", "kernel", "stdout", "syslog"];

    /// <summary>The journal fields requested from journald, in addition to the ones it always emits.</summary>
    internal static readonly IReadOnlyList<string> RequestedFields =
        ["MESSAGE", "PRIORITY", "SYSLOG_IDENTIFIER", "_SYSTEMD_UNIT", "_PID", "_COMM", "MESSAGE_ID", "_TRANSPORT"];

    /// <summary>The latest instant a <see cref="DateTimeOffset"/> can hold (9999-12-31T23:59:59Z), in microseconds since the Unix epoch.</summary>
    private const long MaximumMicroseconds = 253_402_300_799_000_000L;

    /// <summary>The result of reading one line.</summary>
    internal enum LineKind
    {
        /// <summary>A usable record.</summary>
        Record,

        /// <summary>A journalctl status line such as <c>-- No entries --</c>, which carries no event.</summary>
        Status,

        /// <summary>Not JSON, or a record without a timestamp or a text message.</summary>
        Malformed,
    }

    /// <summary>Parses <paramref name="line"/>. <paramref name="record"/> is set only for <see cref="LineKind.Record"/>.</summary>
    internal static LineKind TryParse(string line, out SystemEventRecord? record)
    {
        record = null;
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal))
        {
            return LineKind.Status;
        }

        JsonObject json;
        try
        {
            if (trimmed[0] != '{' || JsonNode.Parse(trimmed) is not JsonObject parsed)
            {
                return LineKind.Malformed;
            }

            json = parsed;
        }
        catch (JsonException)
        {
            return LineKind.Malformed;
        }

        if (!TryReadTimestamp(json, out var timestamp) || Text(json, "MESSAGE") is not { } message)
        {
            return LineKind.Malformed;
        }

        var unit = Text(json, "_SYSTEMD_UNIT");
        var source = Text(json, "SYSLOG_IDENTIFIER") ?? unit ?? Text(json, "_COMM") ?? "unknown";
        record = new SystemEventRecord(
            timestamp,
            ToSeverity(Text(json, "PRIORITY")),
            source,
            Text(json, "MESSAGE_ID"),
            Text(json, "_TRANSPORT"),
            message.Length > SystemEventsLimits.MessageCharacters + 1 ? message[..(SystemEventsLimits.MessageCharacters + 1)] : message,
            int.TryParse(Text(json, "_PID"), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : null,
            Text(json, "_COMM"),
            unit);
        return LineKind.Record;
    }

    /// <summary>Maps a syslog priority (0 to 7) to a severity; an absent or unrecognized value is <see cref="SystemEventSeverity.Unknown"/>.</summary>
    internal static SystemEventSeverity ToSeverity(string? priority) => priority switch
    {
        "0" or "1" or "2" => SystemEventSeverity.Critical,
        "3" => SystemEventSeverity.Error,
        "4" => SystemEventSeverity.Warning,
        "5" or "6" => SystemEventSeverity.Information,
        "7" => SystemEventSeverity.Verbose,
        _ => SystemEventSeverity.Unknown,
    };

    private static bool TryReadTimestamp(JsonObject json, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!long.TryParse(Text(json, "__REALTIME_TIMESTAMP"), NumberStyles.None, CultureInfo.InvariantCulture, out var microseconds)
            || microseconds > MaximumMicroseconds)
        {
            return false;
        }

        timestamp = DateTimeOffset.UnixEpoch.AddTicks(microseconds * 10);
        return true;
    }

    /// <summary>
    /// A field as text. journald writes a field that occurs more than once as an array and a value that is not text as an array of
    /// bytes; the first text value is used, and binary data is treated as absent.
    /// </summary>
    private static string? Text(JsonObject json, string name)
    {
        var node = json[name];
        if (node is JsonArray array)
        {
            node = array.FirstOrDefault(item => item is JsonValue value && value.TryGetValue<string>(out _));
        }

        return node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    }
}
