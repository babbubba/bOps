// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Sys.Core;

/// <summary>The output shape of <c>system.events</c> and <c>system.crashes</c> (ADR-0032 HARDEN-7 amendment §1).</summary>
public enum EvidenceMode
{
    /// <summary>One entry per record.</summary>
    Raw,

    /// <summary>One entry per distinct aggregation key, with a count and first/last times.</summary>
    Aggregate,
}

/// <summary>Wire names of <see cref="EvidenceMode"/> and the shared look-back reader with the two cross-field rules.</summary>
public static class EvidenceModes
{
    /// <summary>The wire name of a mode.</summary>
    public static string ToWireValue(EvidenceMode mode) => mode switch
    {
        EvidenceMode.Raw => "raw",
        EvidenceMode.Aggregate => "aggregate",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown evidence mode."),
    };

    /// <summary>
    /// Reads <c>mode</c> and the look-back window of a tool that has a minute argument valid in both modes and a day argument valid only
    /// in aggregate mode (ADR-0032 HARDEN-7 amendment §2). The cross-field rules are checked in a fixed order and only the first one
    /// that fails is reported: (1) the minute and the day argument together; (2) the day argument in raw mode — the explicit
    /// <c>mode: raw</c> or a raw default; (3) everything else. The mode is resolved before rule 2 (the explicit value, else
    /// <paramref name="defaultMode"/>), so an unrecognised mode value is reported there. Nothing is clamped and the mode is never
    /// switched on the caller's behalf.
    /// </summary>
    internal static bool TryReadHorizon(
        JsonObject json,
        ToolArguments arguments,
        HorizonRules rules,
        out EvidenceMode mode,
        out TimeSpan window,
        out string? error)
    {
        mode = rules.DefaultMode;
        window = TimeSpan.FromMinutes(rules.DefaultMinutes);
        error = null;
        var hasMinutes = Present(json, rules.MinuteName);
        var hasDays = Present(json, rules.DayName);

        // Rule 1.
        if (hasMinutes && hasDays)
        {
            error = $"Use either {rules.MinuteName} or {rules.DayName}, not both.";
            return false;
        }

        if (Present(json, "mode"))
        {
            if (!arguments.TryGet<string>("mode", out var name) || name is not ("raw" or "aggregate"))
            {
                error = "mode must be one of: raw, aggregate.";
                return false;
            }

            mode = name == "raw" ? EvidenceMode.Raw : EvidenceMode.Aggregate;
        }

        // Rule 2.
        if (hasDays && mode == EvidenceMode.Raw)
        {
            error = $"{rules.DayName} applies only to mode aggregate; raw mode is limited to {rules.MinuteName} up to {rules.MaximumMinutes} (7 days).";
            return false;
        }

        // Rule 3: the per-argument checks.
        if (hasDays)
        {
            if (!TryInteger(arguments, rules.DayName, 1, rules.MaximumDays, out var days, out error))
            {
                return false;
            }

            window = TimeSpan.FromMinutes(days * 1_440d);
            return true;
        }

        if (hasMinutes)
        {
            if (!TryInteger(arguments, rules.MinuteName, 1, rules.MaximumMinutes, out var minutes, out error))
            {
                return false;
            }

            window = TimeSpan.FromMinutes(minutes);
        }

        return true;
    }

    private static bool Present(JsonObject json, string name) => json.ContainsKey(name) && json[name] is not null;

    private static bool TryInteger(ToolArguments arguments, string name, int minimum, int maximum, out int value, out string? error)
    {
        error = null;
        if (!arguments.TryGet<int>(name, out value))
        {
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
}

/// <summary>The argument names, bounds and default mode a tool's look-back reader applies.</summary>
internal sealed record HorizonRules(
    string MinuteName,
    string DayName,
    EvidenceMode DefaultMode,
    int DefaultMinutes,
    int MaximumMinutes,
    int MaximumDays);
