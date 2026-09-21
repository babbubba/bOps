// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace bOps.Packages.Docker;

/// <summary>Validation for the names the Docker tools accept beyond image references (ADR-0033).</summary>
public static partial class DockerNames
{
    /// <summary>The longest volume name accepted.</summary>
    public const int MaximumVolumeNameLength = 128;

    /// <summary>Checks a volume name: 2 to 128 characters of letters, digits, <c>_</c>, <c>.</c> and <c>-</c>, starting with a letter or digit.</summary>
    public static bool TryValidateVolumeName(string? value, out string? error)
    {
        if (string.IsNullOrEmpty(value))
        {
            error = "A volume name must not be empty.";
            return false;
        }

        if (value.Length > MaximumVolumeNameLength)
        {
            error = $"A volume name is at most {MaximumVolumeNameLength} characters.";
            return false;
        }

        if (!VolumeNamePattern().IsMatch(value))
        {
            error = "A volume name is 2 or more letters, digits, '_', '.' or '-', starting with a letter or digit.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Checks a platform such as <c>linux/amd64</c> or <c>linux/arm/v7</c>.</summary>
    public static bool TryValidatePlatform(string? value, out string? error)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 32 || !PlatformPattern().IsMatch(value))
        {
            error = "A platform is os/architecture or os/architecture/variant, for example linux/amd64.";
            return false;
        }

        error = null;
        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex VolumeNamePattern();

    [GeneratedRegex("^[a-z0-9]+/[a-z0-9]+(?:/[a-z0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PlatformPattern();
}
