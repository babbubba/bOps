using bOps.Abstractions;
using bOps.Packages.Providers.OpenAiCompatible;

namespace bOps.Packages.Providers.LlamaCpp;

/// <summary>
/// Contributes the <c>"LlamaCpp"</c> provider, backed by the shared OpenAI-compatible adapter.
/// Native tool-calling quality depends heavily on the loaded model/quantization — set
/// <see cref="ChatModelOptions.SupportsNativeToolCalling"/> to <c>false</c> (the prudent
/// default, plan §3.1) until you have verified the specific model you run handles it well.
/// </summary>
public sealed class LlamaCppProviderPackage(IHttpClientFactory httpClientFactory) : IModelProviderPackage
{
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedProviderIds { get; } = ["LlamaCpp"];

    /// <inheritdoc />
    public IChatModel Create(ChatModelOptions options) =>
        new OpenAiCompatibleChatModel(options, httpClientFactory.CreateClient(nameof(LlamaCppProviderPackage)));
}
