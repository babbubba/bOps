// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Api;

/// <summary>
/// Host settings for operational system messages (<c>SystemMessages</c> configuration section, ADR-0049): the node-local SQLite file and
/// the time-based retention. Out-of-range values fail start-up instead of silently disabling or over-keeping.
/// </summary>
internal sealed class SystemMessageOptions
{
    internal const string SectionName = "SystemMessages";

    internal const int DefaultRetentionDays = 90;

    internal const int MaxRetentionDays = 3650;

    internal const int DefaultRetentionIntervalMinutes = 360;

    internal const int MinRetentionIntervalMinutes = 5;

    internal const int MaxRetentionIntervalMinutes = 10080;

    /// <summary>The SQLite file holding messages and prerequisite state.</summary>
    public string FilePath { get; set; } = "system-messages.db";

    /// <summary>How many days a message is kept; the default is 90.</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>Minutes between two retention cleanups after the one at start-up.</summary>
    public int RetentionIntervalMinutes { get; set; } = DefaultRetentionIntervalMinutes;

    public TimeSpan Retention => TimeSpan.FromDays(RetentionDays);

    public TimeSpan RetentionInterval => TimeSpan.FromMinutes(RetentionIntervalMinutes);

    public SystemMessageOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            throw new InvalidOperationException($"'{SectionName}:FilePath' must not be blank.");
        }

        if (RetentionDays is < 1 or > MaxRetentionDays)
        {
            throw new InvalidOperationException($"'{SectionName}:RetentionDays' must be 1–{MaxRetentionDays} (was {RetentionDays}).");
        }

        if (RetentionIntervalMinutes is < MinRetentionIntervalMinutes or > MaxRetentionIntervalMinutes)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:RetentionIntervalMinutes' must be {MinRetentionIntervalMinutes}–{MaxRetentionIntervalMinutes} (was {RetentionIntervalMinutes}).");
        }

        return this;
    }
}
