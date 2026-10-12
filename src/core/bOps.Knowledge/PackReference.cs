// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace bOps.Knowledge;

/// <summary>The pack a task asks a source for: an id and an exact version. There is no "latest"; upgrades are a host decision.</summary>
public sealed partial record PackReference
{
    /// <summary>Creates a reference.</summary>
    /// <exception cref="ArgumentException">The id or the version does not have the shape of an identifier.</exception>
    public PackReference(string packId, string version)
    {
        ArgumentNullException.ThrowIfNull(packId);
        ArgumentNullException.ThrowIfNull(version);
        if (!IdShape().IsMatch(packId) || !VersionShape().IsMatch(version))
        {
            throw new ArgumentException("A pack reference is a lower-case pack id and an exact version.");
        }

        PackId = packId;
        Version = version;
    }

    /// <summary>The pack id.</summary>
    public string PackId { get; }

    /// <summary>The exact version.</summary>
    public string Version { get; }

    internal static bool IsPackId(string? value) => value is not null && IdShape().IsMatch(value);

    internal static bool IsVersion(string? value) => value is not null && VersionShape().IsMatch(value);

    [GeneratedRegex("^[a-z0-9]([a-z0-9._-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex IdShape();

    [GeneratedRegex("^(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})\\.(0|[1-9][0-9]{0,8})(-[0-9A-Za-z.-]{1,32})?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionShape();
}
