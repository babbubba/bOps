// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>
/// One operating-system event in the normalized shape shared by Windows and Linux. Untrusted text: the message and the
/// names can be written by any identity that can log.
/// </summary>
/// <param name="TimestampUtc">When the event was recorded, in UTC.</param>
/// <param name="Severity">The normalized severity.</param>
/// <param name="Source">The Windows provider, or the Linux syslog identifier (the unit when there is no identifier).</param>
/// <param name="EventId">The Windows event id, or the Linux <c>MESSAGE_ID</c>, when present.</param>
/// <param name="Channel">The Windows channel, or the Linux journal transport, when meaningful.</param>
/// <param name="Message">The event's message.</param>
/// <param name="ProcessId">The process that wrote it, when the platform records one.</param>
/// <param name="ProcessName">The process name, when the platform records one.</param>
/// <param name="Unit">The systemd unit (Linux), when present.</param>
public sealed record SystemEventRecord(
    DateTimeOffset TimestampUtc,
    SystemEventSeverity Severity,
    string Source,
    string? EventId,
    string? Channel,
    string Message,
    int? ProcessId,
    string? ProcessName,
    string? Unit = null);
