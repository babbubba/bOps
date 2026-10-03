// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using bOps.Packages.Sys.Core;

namespace bOps.Packages.Sys.Windows;

/// <summary>
/// Builds the XPath of a Windows Event Log query for a <see cref="SystemEventQuery"/> (ADR-0032). The window, level, provider and event
/// id are filtered natively; the message text and everything else the native language cannot say is left to the shared filter. Every
/// value has been restricted to a fixed character set before it gets here (no quote, bracket or operator can occur), and the event
/// id is re-checked to be a number, so the text of a query is fixed apart from those values.
/// </summary>
internal static class WindowsEventXPath
{
    /// <summary>
    /// Builds the XPath. <paramref name="providerName"/> is the provider's registered spelling, or <c>null</c> to filter by provider
    /// afterwards. <paramref name="excludedProviders"/> are the names to leave out natively, one <c>@Name!=</c> term each, already
    /// resolved to the registered spelling or, when none is registered, the validated entry itself (ADR-0032 HARDEN-9 amendment §2).
    /// </summary>
    internal static string Build(SystemEventQuery query, string? providerName, IReadOnlyList<string>? excludedProviders = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var conditions = new List<string>
        {
            $"TimeCreated[@SystemTime>='{Time(query.FromUtc)}' and @SystemTime<='{Time(query.ToUtc)}']",
        };

        if (Levels(query.MinSeverity) is { } levels)
        {
            conditions.Add("(" + string.Join(" or ", levels.Select(level => "Level=" + level.ToString(CultureInfo.InvariantCulture))) + ")");
        }

        if (providerName is not null)
        {
            conditions.Add($"Provider[@Name='{Checked(providerName)}']");
        }

        if (excludedProviders is { Count: > 0 })
        {
            // One predicate, one != term per name, joined with and. not() is outside the Event Log XPath subset.
            conditions.Add("Provider[" + string.Join(" and ", excludedProviders.Select(name => $"@Name!='{CheckedExcluded(name)}'")) + "]");
        }

        if (query.EventId is { } eventId)
        {
            conditions.Add("EventID=" + Number(eventId).ToString(CultureInfo.InvariantCulture));
        }

        return "*[System[" + string.Join(" and ", conditions) + "]]";
    }

    /// <summary>The Windows levels (1 critical, 2 error, 3 warning, 4 information, 5 verbose) at least as severe as <paramref name="minimum"/>.</summary>
    internal static IReadOnlyList<int>? Levels(SystemEventSeverity? minimum) => minimum switch
    {
        null => null,
        SystemEventSeverity.Critical => [1],
        SystemEventSeverity.Error => [1, 2],
        SystemEventSeverity.Warning => [1, 2, 3],
        SystemEventSeverity.Information => [1, 2, 3, 4],
        SystemEventSeverity.Verbose => [1, 2, 3, 4, 5],
        _ => null,
    };

    private static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static int Number(string eventId) =>
        eventId.Length is > 0 and <= 5 && eventId.All(char.IsAsciiDigit)
            && int.Parse(eventId, NumberStyles.None, CultureInfo.InvariantCulture) is <= 65_535 and var number
            ? number
            : throw new ArgumentException("The event id is not a number from 0 to 65535.", nameof(eventId));

    /// <summary>Whether <paramref name="value"/> is made only of the characters a provider name may carry into a query (the <c>source</c> character set).</summary>
    internal static bool IsSafeProviderName(string value) =>
        value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || " _.@:/()-".Contains(character, StringComparison.Ordinal));

    /// <summary>An excluded name must be a non-empty name of the <c>source</c> character set: an empty one would exclude nothing and hide a bug.</summary>
    private static string CheckedExcluded(string value) =>
        value.Length > 0 ? Checked(value) : throw new ArgumentException("An excluded provider name cannot be empty.", nameof(value));

    /// <summary>A last defence: a value that could end a string literal or change the query is refused, not escaped.</summary>
    private static string Checked(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || " _.@:/()-".Contains(character, StringComparison.Ordinal)))
            {
                throw new ArgumentException("The provider name contains a character that is not allowed in a query.", nameof(value));
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
