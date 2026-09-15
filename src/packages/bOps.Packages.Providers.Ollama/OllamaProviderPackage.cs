// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Packages.Providers.OpenAiCompatible;

namespace bOps.Packages.Providers.Ollama;

/// <summary>
/// Contributes the <c>"Ollama"</c> provider, backed by the shared OpenAI-compatible adapter.
/// Configure <see cref="ChatModelOptions.BaseUrl"/> as Ollama's OpenAI-compatible endpoint
/// (<c>http://localhost:11434/v1</c>), not its native <c>/api/chat</c> endpoint.
/// </summary>
public sealed class OllamaProviderPackage(IHttpClientFactory httpClientFactory) : IModelProviderPackage
{
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedProviderIds { get; } = ["Ollama"];

    /// <inheritdoc />
    public IChatModel Create(ChatModelOptions options) =>
        new OpenAiCompatibleChatModel(options, httpClientFactory.CreateClient(nameof(OllamaProviderPackage)));
}
