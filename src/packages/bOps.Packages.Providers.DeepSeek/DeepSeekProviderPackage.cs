using bOps.Abstractions;
using bOps.Packages.Providers.OpenAiCompatible;

namespace bOps.Packages.Providers.DeepSeek;

/// <summary>
/// Contributes the <c>"DeepSeek"</c> provider, backed by the shared OpenAI-compatible adapter
/// (ADR-0005) — DeepSeek's API speaks the same Chat Completions schema OpenRouter and Ollama
/// already use. Configure <see cref="ChatModelOptions.BaseUrl"/> as <c>https://api.deepseek.com</c>.
/// </summary>
public sealed class DeepSeekProviderPackage(IHttpClientFactory httpClientFactory) : IModelProviderPackage
{
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedProviderIds { get; } = ["DeepSeek"];

    /// <inheritdoc />
    public IChatModel Create(ChatModelOptions options) =>
        new OpenAiCompatibleChatModel(options, httpClientFactory.CreateClient(nameof(DeepSeekProviderPackage)));
}
