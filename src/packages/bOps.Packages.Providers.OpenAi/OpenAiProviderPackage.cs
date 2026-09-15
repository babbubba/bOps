using bOps.Abstractions;
using bOps.Packages.Providers.OpenAiCompatible;

namespace bOps.Packages.Providers.OpenAi;

/// <summary>
/// Contributes the <c>"OpenAI"</c> provider, backed by the shared OpenAI-compatible adapter
/// (ADR-0005) — OpenAI's own Chat Completions API is the schema that adapter already speaks.
/// Configure <see cref="ChatModelOptions.BaseUrl"/> as <c>https://api.openai.com/v1</c>.
/// </summary>
public sealed class OpenAiProviderPackage(IHttpClientFactory httpClientFactory) : IModelProviderPackage
{
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedProviderIds { get; } = ["OpenAI"];

    /// <inheritdoc />
    public IChatModel Create(ChatModelOptions options) =>
        new OpenAiCompatibleChatModel(options, httpClientFactory.CreateClient(nameof(OpenAiProviderPackage)));
}
