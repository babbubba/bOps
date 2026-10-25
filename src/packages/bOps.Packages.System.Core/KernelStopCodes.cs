// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace bOps.Packages.Sys.Core;

/// <summary>One kernel stop code: the official name and whether the code only ever identifies a live dump.</summary>
/// <param name="Name">The documented name.</param>
/// <param name="IsLiveDumpOnly">True when the code can never be a real bug check: it labels a live dump, and the system keeps running.</param>
public sealed record KernelStopCode(string Name, bool IsLiveDumpOnly);

/// <summary>
/// A deliberately tiny catalog of kernel stop codes (MAF A/B campaign, F3): only the codes the campaign's models misnamed or that decide
/// the live-dump / bugcheck / hardware distinction. Names and the live-dump-only property come from the Microsoft Learn "Bug check code
/// reference" and "Kernel live dump code reference" pages (bug-check-0x193, -0x101, -0x116, -0x50, -0x124, -0x1e, -0x7f, -0xa). A code
/// that is not here gets no entry — the catalog never guesses, and it is not a diagnostic: a name says what the code is, not what caused it.
/// </summary>
public static class KernelStopCodes
{
    private static readonly Dictionary<ulong, KernelStopCode> Codes = new()
    {
        [0xA] = new("IRQL_NOT_LESS_OR_EQUAL", false),
        [0x1E] = new("KMODE_EXCEPTION_NOT_HANDLED", false),
        [0x50] = new("PAGE_FAULT_IN_NONPAGED_AREA", false),
        [0x7F] = new("UNEXPECTED_KERNEL_MODE_TRAP", false),
        [0x101] = new("CLOCK_WATCHDOG_TIMEOUT", false),
        [0x116] = new("VIDEO_TDR_FAILURE", false),
        [0x124] = new("WHEA_UNCORRECTABLE_ERROR", false),
        [0x193] = new("VIDEO_DXGKRNL_LIVEDUMP", true),
    };

    /// <summary>How many codes the catalog holds.</summary>
    public static int Count => Codes.Count;

    /// <summary>The entry for a hexadecimal code with or without <c>0x</c> and leading zeros, or <c>null</c> when it is not catalogued.</summary>
    public static KernelStopCode? Lookup(string? code)
    {
        if (EvidenceCodes.CanonicalHex(code) is not { } canonical
            || !ulong.TryParse(canonical.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        return Codes.GetValueOrDefault(number);
    }

    /// <summary>
    /// The one-line reading of a catalogued code for a result's legend: the name, and for a live-dump-only code the plain statement that it
    /// is not a bugcheck and does not restart the system.
    /// </summary>
    public static string Describe(KernelStopCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return code.IsLiveDumpOnly
            ? $"{code.Name}: identifies a live dump, not a bugcheck; the system keeps running and does not restart because of it."
            : $"{code.Name}: a bugcheck stop code; the name says what the code is, not what caused it.";
    }
}
