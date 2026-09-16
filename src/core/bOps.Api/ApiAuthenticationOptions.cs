// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Api;

internal sealed class ApiAuthenticationOptions
{
    public List<ApiCredentialOptions> ApiKeys { get; init; } = [];
}

internal sealed class ApiCredentialOptions
{
    public string Id { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    public SecretReference Secret { get; init; } = new("environment", string.Empty);

    /// <summary>
    /// Comma-separated role set. This is deliberately scalar: hierarchical configuration
    /// providers merge array indices, which could retain a more privileged role from a lower
    /// precedence source when an operator intended to replace the array.
    /// </summary>
    public string Roles { get; init; } = string.Empty;
}
