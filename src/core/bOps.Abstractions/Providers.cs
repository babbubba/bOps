// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>Configuration for constructing an <see cref="IChatModel"/>, resolved to a concrete instance by provider id.</summary>
public sealed class ChatModelOptions
{
    /// <summary>Creates chat model options.</summary>
    /// <param name="Provider">The provider id to resolve.</param>
    /// <param name="BaseUrl">The provider endpoint base URL.</param>
    /// <param name="ApiKeySecret">A host-local reference to the provider credential, or <c>null</c> for a local provider.</param>
    /// <param name="Model">The specific model to request.</param>
    /// <param name="SupportsNativeToolCalling">Whether the provider/model reliably supports native tool calling.</param>
#pragma warning disable CA1054 // Configuration binding supplies URLs as strings; adapters validate and use them.
    public ChatModelOptions(
        string Provider,
        string BaseUrl,
        SecretReference? ApiKeySecret,
        string Model,
        bool SupportsNativeToolCalling = true)
#pragma warning restore CA1054
    {
        this.Provider = Provider;
        this.BaseUrl = BaseUrl;
        this.ApiKeySecret = ApiKeySecret;
        this.Model = Model;
        this.SupportsNativeToolCalling = SupportsNativeToolCalling;
    }

    /// <summary>The provider id to resolve.</summary>
    public string Provider { get; init; }

    /// <summary>The provider endpoint base URL.</summary>
#pragma warning disable CA1056 // See constructor: this is a configuration-bound value.
    public string BaseUrl { get; init; }
#pragma warning restore CA1056

    /// <summary>A host-local reference to the provider credential, or <c>null</c> for a local provider.</summary>
    public SecretReference? ApiKeySecret { get; init; }

    /// <summary>
    /// The one credential the host resolved for this configured model. It is explicitly excluded
    /// from serialization and <see cref="ToString"/>; callers must never persist or log it.
    /// </summary>
    [JsonIgnore]
    public string? ResolvedApiKey { get; init; }

    /// <summary>The specific model to request.</summary>
    public string Model { get; init; }

    /// <summary>Whether the provider/model reliably supports native tool calling.</summary>
    public bool SupportsNativeToolCalling { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{nameof(ChatModelOptions)} {{ Provider = {Provider}, BaseUrl = {BaseUrl}, ApiKeySecret = {ApiKeySecret}, " +
        $"HasResolvedApiKey = {!string.IsNullOrEmpty(ResolvedApiKey)}, Model = {Model}, SupportsNativeToolCalling = {SupportsNativeToolCalling} }}";
}

/// <summary>A package that contributes one or more LLM providers.</summary>
public interface IModelProviderPackage
{
    /// <summary>Provider ids this package can construct.</summary>
    IReadOnlyList<string> SupportedProviderIds { get; }

    /// <summary>Constructs an <see cref="IChatModel"/> for the given options.</summary>
    IChatModel Create(ChatModelOptions options);
}

/// <summary>Resolves an <see cref="IChatModel"/> by provider id among registered provider packages.</summary>
public interface IChatModelRegistry
{
    /// <summary>Registers a provider package's supported provider ids.</summary>
    void Register(PackageId package, IModelProviderPackage provider);

    /// <summary>Resolves and constructs an <see cref="IChatModel"/> for the given options.</summary>
    IChatModel Create(ChatModelOptions options);

    /// <summary>Every registered provider id, deduplicated.</summary>
    IReadOnlyList<string> RegisteredProviderIds { get; }
}

/// <summary>Thrown when a configured provider id matches no registered provider package.</summary>
public sealed class ProviderNotSupportedException : Exception
{
    /// <summary>Creates a provider-not-supported exception for the given provider id.</summary>
    public ProviderNotSupportedException(string providerId)
        : base($"Provider '{providerId}' is not recognized — no provider package registered declares it. Verify the package is installed and enabled.")
    {
        ProviderId = providerId;
    }

    /// <summary>Creates an empty exception for framework compatibility.</summary>
    public ProviderNotSupportedException()
    {
        ProviderId = string.Empty;
    }

    /// <summary>Creates an exception wrapping an underlying failure.</summary>
    public ProviderNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
        ProviderId = string.Empty;
    }

    /// <summary>The provider id that could not be resolved.</summary>
    public string ProviderId { get; }
}
