namespace bOps.Abstractions;

/// <summary>Configuration for constructing an <see cref="IChatModel"/>, resolved to a concrete instance by provider id.</summary>
public sealed record ChatModelOptions
{
    /// <summary>Creates chat model options.</summary>
    /// <param name="Provider">The provider id to resolve, e.g. <c>"OpenRouter"</c>, <c>"Ollama"</c>, <c>"LlamaCpp"</c>.</param>
    /// <param name="BaseUrl">The base URL of the provider's OpenAI-compatible (or native) endpoint.</param>
    /// <param name="ApiKey"><c>null</c> for local providers such as Ollama or llama.cpp.</param>
    /// <param name="Model">The specific model to request from the provider.</param>
    /// <param name="SupportsNativeToolCalling">
    /// When false, the adapter must not use the provider's native tool-calling — it falls back
    /// to the JSON-schema-in-prompt strategy described in the plan (§3.1.1), because the model
    /// or provider's native tool-calling is unreliable.
    /// </param>
    public ChatModelOptions(string Provider, string BaseUrl, string? ApiKey, string Model, bool SupportsNativeToolCalling = true)
    {
        this.Provider = Provider;
        this.BaseUrl = BaseUrl;
        this.ApiKey = ApiKey;
        this.Model = Model;
        this.SupportsNativeToolCalling = SupportsNativeToolCalling;
    }

    /// <summary>The provider id to resolve, e.g. <c>"OpenRouter"</c>, <c>"Ollama"</c>, <c>"LlamaCpp"</c>.</summary>
    public string Provider { get; init; }

    /// <summary>The base URL of the provider's OpenAI-compatible (or native) endpoint.</summary>
    public string BaseUrl { get; init; }

    /// <summary><c>null</c> for local providers such as Ollama or llama.cpp.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The specific model to request from the provider.</summary>
    public string Model { get; init; }

    /// <summary>
    /// When false, the adapter must not use the provider's native tool-calling — it falls back
    /// to the JSON-schema-in-prompt strategy, because the model or provider's native
    /// tool-calling is unreliable.
    /// </summary>
    public bool SupportsNativeToolCalling { get; init; }
}

/// <summary>
/// A package implements this to contribute one or more LLM providers. There is no hardcoded
/// switch over provider names anywhere in the runtime — a provider is a package exactly as a
/// tool category is (agentic/00-project-spec.md, principle 7).
/// </summary>
public interface IModelProviderPackage
{
    /// <summary>Provider ids this package can construct, e.g. <c>["OpenRouter"]</c>.</summary>
    IReadOnlyList<string> SupportedProviderIds { get; }

    /// <summary>Constructs an <see cref="IChatModel"/> for the given options.</summary>
    /// <param name="options">Must have a <see cref="ChatModelOptions.Provider"/> present in <see cref="SupportedProviderIds"/>.</param>
    IChatModel Create(ChatModelOptions options);
}

/// <summary>Resolves an <see cref="IChatModel"/> by provider id among the registered provider packages.</summary>
public interface IChatModelRegistry
{
    /// <summary>Registers a provider package's supported provider ids.</summary>
    /// <param name="package">The package contributing the provider.</param>
    /// <param name="provider">The provider package to register.</param>
    void Register(PackageId package, IModelProviderPackage provider);

    /// <summary>Resolves and constructs an <see cref="IChatModel"/> for the given options.</summary>
    /// <param name="options">The provider and model to resolve.</param>
    /// <exception cref="ProviderNotSupportedException">No registered package declares <see cref="ChatModelOptions.Provider"/>.</exception>
    IChatModel Create(ChatModelOptions options);
}

/// <summary>Thrown when <see cref="ChatModelOptions.Provider"/> matches no registered provider package.</summary>
public sealed class ProviderNotSupportedException(string providerId)
    : Exception($"Provider '{providerId}' is not recognized — no provider package registered declares it. " +
                "Verify the package is installed and enabled.")
{
    /// <summary>The provider id that could not be resolved.</summary>
    public string ProviderId { get; } = providerId;
}
