// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// Host settings for prerequisite readiness (<c>Prerequisites</c> configuration section, ADR-0049). Bounded: an out-of-range value
/// fails host start-up with a clear message instead of silently running unbounded.
/// </summary>
public sealed class PrerequisiteOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Prerequisites";

    /// <summary>The default refresh period of the long-running host.</summary>
    public const int DefaultRefreshIntervalSeconds = 30;

    /// <summary>The shortest refresh period.</summary>
    public const int MinRefreshIntervalSeconds = 5;

    /// <summary>The longest refresh period (one hour).</summary>
    public const int MaxRefreshIntervalSeconds = 3600;

    /// <summary>The most checks one refresh runs at once, 1 to <see cref="PrerequisiteRegistry.MaxConcurrencyLimit"/>.</summary>
    public int MaxConcurrency { get; set; } = PrerequisiteRegistry.DefaultMaxConcurrency;

    /// <summary>Seconds between two refresh cycles of the long-running host, <see cref="MinRefreshIntervalSeconds"/> to <see cref="MaxRefreshIntervalSeconds"/>.</summary>
    public int RefreshIntervalSeconds { get; set; } = DefaultRefreshIntervalSeconds;

    /// <summary>The refresh period as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(RefreshIntervalSeconds);

    /// <summary>Throws <see cref="InvalidOperationException"/> when a setting is outside its bounds; returns this instance otherwise.</summary>
    public PrerequisiteOptions Validate()
    {
        if (MaxConcurrency is < 1 or > PrerequisiteRegistry.MaxConcurrencyLimit)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:MaxConcurrency' must be 1–{PrerequisiteRegistry.MaxConcurrencyLimit} (was {MaxConcurrency}).");
        }

        if (RefreshIntervalSeconds is < MinRefreshIntervalSeconds or > MaxRefreshIntervalSeconds)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:RefreshIntervalSeconds' must be {MinRefreshIntervalSeconds}–{MaxRefreshIntervalSeconds} (was {RefreshIntervalSeconds}).");
        }

        return this;
    }
}
