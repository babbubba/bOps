// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime;

/// <summary>
/// A display-only mask for a secret (ADR-0029), computed once from plaintext at write time and
/// stored as non-secret metadata. Rendering a mask never requires reading plaintext back out of
/// the vault.
/// </summary>
public sealed record SecretMask(string Prefix, string Suffix, int PlaintextLength)
{
    /// <summary>
    /// Computes the display mask for <paramref name="secret"/>: the canonical
    /// <c>first six...last four</c> for length 10 or more, or a shorter, non-overlapping
    /// prefix/suffix split (revealing at most 40% of the value, always leaving at least one
    /// masked character) for anything shorter.
    /// </summary>
    public static SecretMask Compute(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        if (secret.Length >= 10)
        {
            return new SecretMask(secret[..6], secret[^4..], secret.Length);
        }

        var revealed = Math.Min(secret.Length - 1, (int)Math.Floor(secret.Length * 0.4));
        var prefixLength = (int)Math.Ceiling(revealed / 2.0);
        var suffixLength = revealed - prefixLength;

        var prefix = prefixLength == 0 ? string.Empty : secret[..prefixLength];
        var suffix = suffixLength == 0 ? string.Empty : secret[^suffixLength..];
        return new SecretMask(prefix, suffix, secret.Length);
    }
}
