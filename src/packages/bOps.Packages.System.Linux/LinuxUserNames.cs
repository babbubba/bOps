// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace bOps.Packages.Sys.Linux;

/// <summary>
/// Resolves a numeric UID to a name from the local <c>/etc/passwd</c> only (ADR-0034). It
/// deliberately does not go through NSS, LDAP or SSSD: a name lookup that leaves the machine turns
/// a read-only local observation into a network call with its own failure modes and its own
/// information disclosure. A UID with no local entry stays a number.
/// </summary>
internal static class LinuxUserNames
{
    private const string PasswdPath = "/etc/passwd";

    /// <summary>Reads the local account table. An unreadable or absent file yields no names, never an error.</summary>
    public static async Task<IReadOnlyDictionary<int, string>> ReadAsync(CancellationToken ct)
    {
        var names = new Dictionary<int, string>();
        string text;
        try
        {
            text = await File.ReadAllTextAsync(PasswdPath, ct);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return names;
        }

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(':');
            if (fields.Length > 2
                && int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
                && !string.IsNullOrWhiteSpace(fields[0]))
            {
                names.TryAdd(uid, fields[0]);
            }
        }

        return names;
    }

    /// <summary>The name for <paramref name="uid"/>, or the number itself when the machine has no local entry for it.</summary>
    public static string? Resolve(IReadOnlyDictionary<int, string> names, int? uid)
    {
        ArgumentNullException.ThrowIfNull(names);
        return uid is not { } value
            ? null
            : names.TryGetValue(value, out var name) ? name : value.ToString(CultureInfo.InvariantCulture);
    }
}
