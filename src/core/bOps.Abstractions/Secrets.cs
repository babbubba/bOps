// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>
/// A host-local reference to a secret. The reference is safe to persist; the resolved value is
/// not. <see cref="Provider"/> selects a configured resolver and <see cref="Name"/> is opaque to
/// every other component.
/// </summary>
/// <param name="Provider">The secret-provider id, for example <c>environment</c>.</param>
/// <param name="Name">The provider-specific secret name, never the secret value.</param>
public sealed record SecretReference(string Provider, string Name);

/// <summary>
/// Resolves host-local secret references. Implementations must never log a resolved value or put
/// it in an exception message. Packages do not receive this service through the restricted plugin
/// activation container (ADR-0022).
/// </summary>
public interface ISecretProvider
{
    /// <summary>Resolves <paramref name="reference"/>, or returns <c>null</c> when it does not exist.</summary>
    /// <param name="reference">The provider/name pair to resolve.</param>
    string? GetSecret(SecretReference reference);
}
