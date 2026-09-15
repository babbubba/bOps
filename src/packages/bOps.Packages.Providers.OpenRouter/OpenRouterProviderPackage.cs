// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Packages.Providers.OpenAiCompatible;

namespace bOps.Packages.Providers.OpenRouter;

/// <summary>Contributes the <c>"OpenRouter"</c> provider, backed by the shared OpenAI-compatible adapter.</summary>
public sealed class OpenRouterProviderPackage(IHttpClientFactory httpClientFactory) : IModelProviderPackage
{
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedProviderIds { get; } = ["OpenRouter"];

    /// <inheritdoc />
    public IChatModel Create(ChatModelOptions options) =>
        new OpenAiCompatibleChatModel(options, httpClientFactory.CreateClient(nameof(OpenRouterProviderPackage)));
}
