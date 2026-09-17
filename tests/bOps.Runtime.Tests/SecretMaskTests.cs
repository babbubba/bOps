// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Runtime.Tests;

public sealed class SecretMaskTests
{
    [Theory]
    [InlineData("sk-abcdefghijklmnopqrstuvwxyz", "sk-abc", "wxyz")]
    [InlineData("0123456789", "012345", "6789")]
    public void Compute_UsesTheCanonicalSixAndFourFormat_ForLengthTenOrMore(string secret, string expectedPrefix, string expectedSuffix)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var mask = SecretMask.Compute(secret);

        Assert.Equal(expectedPrefix, mask.Prefix);
        Assert.Equal(expectedSuffix, mask.Suffix);
        Assert.Equal(secret.Length, mask.PlaintextLength);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    [InlineData("abcd")]
    [InlineData("abcde")]
    [InlineData("abcdef")]
    [InlineData("abcdefg")]
    [InlineData("abcdefgh")]
    [InlineData("abcdefghi")]
    public void Compute_NeverLetsPrefixAndSuffixOverlap_ForShortSecrets(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var mask = SecretMask.Compute(secret);

        Assert.True(mask.Prefix.Length + mask.Suffix.Length < secret.Length);
        Assert.Equal(secret[..mask.Prefix.Length], mask.Prefix);
        Assert.Equal(mask.Suffix.Length == 0 ? string.Empty : secret[^mask.Suffix.Length..], mask.Suffix);
    }

    [Fact]
    public void Compute_FullyMasksASingleCharacterSecret()
    {
        var mask = SecretMask.Compute("x");

        Assert.Equal(string.Empty, mask.Prefix);
        Assert.Equal(string.Empty, mask.Suffix);
        Assert.Equal(1, mask.PlaintextLength);
    }

    [Fact]
    public void Compute_RejectsAnEmptySecret()
    {
        Assert.Throws<ArgumentException>(() => SecretMask.Compute(string.Empty));
    }
}
