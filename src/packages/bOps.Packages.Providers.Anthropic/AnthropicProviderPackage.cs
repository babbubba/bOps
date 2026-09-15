// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Providers.Anthropic;

/// <summary>
/// Contributes the <c>"Anthropic"</c> provider, the one native (non-OpenAI-compatible) adapter
/// (ADR-0005). Configure <see cref="ChatModelOptions.BaseUrl"/> as <c>https://api.anthropic.com</c>.
/// </summary>
public sealed class AnthropicProviderPackage(IHttpClientFactory httpClientFactory) : IModelProviderPackage
{
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedProviderIds { get; } = ["Anthropic"];

    /// <inheritdoc />
    public IChatModel Create(ChatModelOptions options) =>
        new AnthropicChatModel(options, httpClientFactory.CreateClient(nameof(AnthropicProviderPackage)));
}
