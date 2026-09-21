// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>
/// A validated request to an OS event collector: everything a native query needs, already range-checked, with the values that
/// could carry query syntax restricted to a fixed character set (see <see cref="SystemEventsArguments"/>).
/// </summary>
/// <param name="FromUtc">The start of the window, inclusive.</param>
/// <param name="ToUtc">The end of the window, inclusive.</param>
/// <param name="MinSeverity">Only events at least this severe, or <c>null</c> for no severity filter.</param>
/// <param name="Source">Exact, case-insensitive provider, syslog identifier or unit, or <c>null</c>.</param>
/// <param name="EventId">Exact event id, or <c>null</c>.</param>
/// <param name="Channel">Windows channel or Linux transport, or <c>null</c> for the platform default.</param>
/// <param name="Text">Case-insensitive substring of the message, or <c>null</c>.</param>
/// <param name="ScanCeiling">The most native records a collector may examine before it stops and reports truncation.</param>
public sealed record SystemEventQuery(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    SystemEventSeverity? MinSeverity,
    string? Source,
    string? EventId,
    string? Channel,
    string? Text,
    int ScanCeiling);
