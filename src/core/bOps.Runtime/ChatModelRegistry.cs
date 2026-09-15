// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// Resolves an <see cref="IChatModel"/> by provider id among the registered provider packages —
/// there is no hardcoded switch over provider names anywhere in the runtime
/// (agentic/00-project-spec.md, principle 7).
/// </summary>
public sealed class ChatModelRegistry : IChatModelRegistry
{
    private readonly ConcurrentDictionary<string, IModelProviderPackage> _byProviderId = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void Register(PackageId package, IModelProviderPackage provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        foreach (var providerId in provider.SupportedProviderIds)
        {
            _byProviderId[providerId] = provider;
        }
    }

    /// <inheritdoc />
    public IChatModel Create(ChatModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return _byProviderId.TryGetValue(options.Provider, out var provider)
            ? provider.Create(options)
            : throw new ProviderNotSupportedException(options.Provider);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> RegisteredProviderIds => [.. _byProviderId.Keys];
}
