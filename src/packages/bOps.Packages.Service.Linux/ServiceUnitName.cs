// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace bOps.Packages.Service.Linux;

/// <summary>
/// Validates a systemd unit name before it ever reaches a <c>systemctl</c> subprocess (ADR-0021),
/// on top of <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> already making shell
/// injection structurally impossible. Shared by every Linux service tool that takes a
/// <c>name</c> argument.
/// </summary>
internal static partial class ServiceUnitName
{
    /// <summary>
    /// A conservative systemd unit-name character set (letters, digits, and <c>: _ . - @</c>),
    /// with an optional <c>.service</c> suffix that <c>systemctl</c> itself would otherwise
    /// append.
    /// </summary>
    public static bool IsPlausible(string name) => UnitNamePattern().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9:_.@-]+$")]
    private static partial Regex UnitNamePattern();
}
