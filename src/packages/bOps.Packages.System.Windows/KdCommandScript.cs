// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;

namespace bOps.Packages.Sys.Windows;

/// <summary>One fixed debugger section: its marker name and the single read-only command it runs.</summary>
internal sealed record KdSection(string Name, string Command);

/// <summary>
/// The only debugger input this package ever produces (ADR-0048 §6). The command list is source-controlled; nothing from the
/// model, the dump path or debugger output enters it. Each command is wrapped in begin and end markers carrying a fresh random
/// nonce, so the parser reads sections by exact marker lines, never by debugger prose, and data inside the dump cannot forge a
/// boundary.
/// </summary>
internal static class KdCommandScript
{
    internal const string Bugcheck = "BUGCHECK";
    internal const string Analyze = "ANALYZE";
    internal const string Modules = "MODULES";
    internal const string Pnp = "PNP";
    internal const string Bsd = "BSD";

    /// <summary>
    /// The fixed analysis sequence: bugcheck data, verbose automated analysis (which carries the analysis stack, the
    /// attributed module and the failure bucket), the loaded-module list with symbol state, and the PnP and boot/shutdown
    /// kernel black boxes. No memory display, search, extension loading, script or shell command exists here.
    /// </summary>
    internal static IReadOnlyList<KdSection> Sections { get; } =
    [
        new(Bugcheck, ".bugcheck"),
        new(Analyze, "!analyze -v"),
        new(Modules, "lm"),
        new(Pnp, "!blackboxpnp"),
        new(Bsd, "!blackboxbsd"),
    ];

    /// <summary>The flags placed before the target: no shell commands, and ignore the symbol-path environment variables.</summary>
    internal static IReadOnlyList<string> FixedFlags { get; } = ["-noshell", "-sins"];

    /// <summary>A new marker nonce: 16 upper-case hexadecimal characters.</summary>
    internal static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

    /// <summary>The begin marker line of <paramref name="section"/>.</summary>
    internal static string BeginMarker(string nonce, string section) => $"<<<BOPS_{nonce}_{section}_BEGIN>>>";

    /// <summary>The end marker line of <paramref name="section"/>.</summary>
    internal static string EndMarker(string nonce, string section) => $"<<<BOPS_{nonce}_{section}_END>>>";

    /// <summary>The single <c>-c</c> command string for <paramref name="nonce"/>, ending with <c>q</c>.</summary>
    internal static string Build(string nonce)
    {
        if (nonce.Length != 16 || !nonce.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The marker nonce must be 16 hexadecimal characters.", nameof(nonce));
        }

        var commands = Sections.SelectMany(section => new[]
        {
            ".echo " + BeginMarker(nonce, section.Name),
            section.Command,
            ".echo " + EndMarker(nonce, section.Name),
        });
        return string.Join(';', commands.Append("q"));
    }

    /// <summary>
    /// The complete KD argument vector. <paramref name="dumpPath"/> is a separate element and never part of the command string.
    /// </summary>
    internal static IReadOnlyList<string> Arguments(string dumpPath, string symbolPath, string nonce) =>
        [.. FixedFlags, "-y", symbolPath, "-z", dumpPath, "-c", Build(nonce)];
}
