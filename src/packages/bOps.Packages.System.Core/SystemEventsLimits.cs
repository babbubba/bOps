// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Sys.Core;

/// <summary>Finite limits for <c>system.events</c> (ADR-0032).</summary>
public static class SystemEventsLimits
{
    /// <summary>Default look-back window, in minutes.</summary>
    public const int DefaultWindowMinutes = 60;

    /// <summary>Largest look-back window a caller may request: seven days.</summary>
    public const int MaximumWindowMinutes = 10_080;

    /// <summary>Default number of events returned.</summary>
    public const int DefaultEvents = 50;

    /// <summary>Largest number of events a caller may request.</summary>
    public const int MaximumEvents = 500;

    /// <summary>Most native records a collector examines for one call.</summary>
    public const int ScanCeiling = 10_000;

    /// <summary>Longest message returned for one event; longer ones are cut and flagged.</summary>
    public const int MessageCharacters = 2_000;

    /// <summary>Longest <c>source</c> value.</summary>
    public const int SourceCharacters = 128;

    /// <summary>Longest <c>eventId</c> value.</summary>
    public const int EventIdCharacters = 64;

    /// <summary>Longest <c>channel</c> value.</summary>
    public const int ChannelCharacters = 256;

    /// <summary>Longest <c>text</c> filter.</summary>
    public const int TextCharacters = 256;

    /// <summary>Default maximum UTF-8 output size.</summary>
    public const int DefaultOutputBytes = 32_768;

    /// <summary>Smallest supported UTF-8 output budget.</summary>
    public const int MinimumOutputBytes = 4_096;

    /// <summary>Largest supported UTF-8 output budget.</summary>
    public const int MaximumOutputBytes = 65_536;
}
