// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime.Tests;

public sealed class EnvironmentSecretProviderTests
{
    [Fact]
    public void GetSecret_ResolvesTheExactEnvironmentVariable()
    {
        var variableName = $"BOPS_TEST_SECRET_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, "expected-secret");
        try
        {
            var provider = new EnvironmentSecretProvider();

            var value = provider.GetSecret(new SecretReference("environment", variableName));

            Assert.Equal("expected-secret", value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public void GetSecret_RejectsUnknownProviders_WithoutIncludingTheSecretName()
    {
        var provider = new EnvironmentSecretProvider();

        var exception = Assert.Throws<SecretProviderNotSupportedException>(() =>
            provider.GetSecret(new SecretReference("vault", "sensitive-name")));

        Assert.DoesNotContain("sensitive-name", exception.Message, StringComparison.Ordinal);
    }
}
