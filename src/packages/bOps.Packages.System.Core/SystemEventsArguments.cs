// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>
/// Reads and validates the arguments of <c>system.events</c> (ADR-0032). An out-of-range or malformed value is rejected, never
/// clamped, and a value that could carry query syntax is limited to a fixed character set before it reaches any collector.
/// </summary>
public static partial class SystemEventsArguments
{
    /// <summary>The accepted <c>minSeverity</c> values, most severe first.</summary>
    public static readonly IReadOnlyList<string> SeverityNames = ["critical", "error", "warning", "information", "verbose"];

    /// <summary>
    /// Validates <paramref name="arguments"/> and builds the collector query for a window that ends at <paramref name="now"/>.
    /// Returns <c>false</c> with an explanation when anything is out of range or malformed.
    /// </summary>
    public static bool TryRead(
        ToolArguments arguments,
        DateTimeOffset now,
        out SystemEventQuery? query,
        out int limit,
        out int maxOutputBytes,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        query = null;
        limit = SystemEventsLimits.DefaultEvents;
        maxOutputBytes = SystemEventsLimits.DefaultOutputBytes;
        var json = arguments.ToJson();

        if (!TryInteger(json, arguments, "windowMinutes", SystemEventsLimits.DefaultWindowMinutes, 1, SystemEventsLimits.MaximumWindowMinutes, out var windowMinutes, out error)
            || !TryInteger(json, arguments, "limit", SystemEventsLimits.DefaultEvents, 1, SystemEventsLimits.MaximumEvents, out limit, out error)
            || !TryInteger(json, arguments, "maxOutputBytes", SystemEventsLimits.DefaultOutputBytes, SystemEventsLimits.MinimumOutputBytes, SystemEventsLimits.MaximumOutputBytes, out maxOutputBytes, out error)
            || !TrySeverity(json, arguments, out var minSeverity, out error)
            || !TryName(json, arguments, "source", SystemEventsLimits.SourceCharacters, out var source, out error)
            || !TryName(json, arguments, "eventId", SystemEventsLimits.EventIdCharacters, out var eventId, out error)
            || !TryName(json, arguments, "channel", SystemEventsLimits.ChannelCharacters, out var channel, out error)
            || !TryText(json, arguments, out var text, out error))
        {
            return false;
        }

        query = new SystemEventQuery(
            now.AddMinutes(-windowMinutes).ToUniversalTime(),
            now.ToUniversalTime(),
            minSeverity,
            source,
            eventId,
            channel,
            text,
            SystemEventsLimits.ScanCeiling);
        error = null;
        return true;
    }

    private static bool TryInteger(
        JsonObject json, ToolArguments arguments, string name, int fallback, int minimum, int maximum, out int value, out string? error)
    {
        value = fallback;
        error = null;
        if (!json.ContainsKey(name) || json[name] is null)
        {
            return true;
        }

        if (!arguments.TryGet<int>(name, out value))
        {
            value = fallback;
            error = $"{name} must be an integer.";
            return false;
        }

        if (value < minimum || value > maximum)
        {
            error = $"{name} must be between {minimum} and {maximum}.";
            return false;
        }

        return true;
    }

    private static bool TrySeverity(JsonObject json, ToolArguments arguments, out SystemEventSeverity? severity, out string? error)
    {
        severity = null;
        error = null;
        if (!json.ContainsKey("minSeverity") || json["minSeverity"] is null)
        {
            return true;
        }

        if (!arguments.TryGet<string>("minSeverity", out var name))
        {
            error = "minSeverity must be a string.";
            return false;
        }

        var index = SeverityNames.ToList().FindIndex(candidate => string.Equals(candidate, name, StringComparison.Ordinal));
        if (index < 0)
        {
            error = $"minSeverity must be one of: {string.Join(", ", SeverityNames)}.";
            return false;
        }

        severity = (SystemEventSeverity)index;
        return true;
    }

    private static bool TryName(JsonObject json, ToolArguments arguments, string name, int maximum, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (!json.ContainsKey(name) || json[name] is null)
        {
            return true;
        }

        if (!arguments.TryGet<string>(name, out var raw))
        {
            error = $"{name} must be a string.";
            return false;
        }

        if (string.IsNullOrEmpty(raw) || raw.Length > maximum)
        {
            error = $"{name} must be 1 to {maximum} characters.";
            return false;
        }

        if (!NamePattern().IsMatch(raw) || string.IsNullOrWhiteSpace(raw))
        {
            error = $"{name} may contain only letters, digits and the characters space _ . @ : / ( ) -.";
            return false;
        }

        value = raw;
        return true;
    }

    private static bool TryText(JsonObject json, ToolArguments arguments, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (!json.ContainsKey("text") || json["text"] is null)
        {
            return true;
        }

        if (!arguments.TryGet<string>("text", out var raw))
        {
            error = "text must be a string.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(raw) || raw.Length > SystemEventsLimits.TextCharacters)
        {
            error = $"text must be 1 to {SystemEventsLimits.TextCharacters} characters and not blank.";
            return false;
        }

        if (raw.Any(char.IsControl))
        {
            error = "text must not contain control characters.";
            return false;
        }

        value = raw;
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z0-9 _.@:/()\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
