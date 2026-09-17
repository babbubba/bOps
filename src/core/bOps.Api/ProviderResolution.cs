// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.Runtime;

namespace bOps.Api;

/// <summary>
/// Resolves the effective active provider and its <see cref="ChatModelOptions"/> (ADR-0029).
/// Shared by <c>Program.cs</c> (startup composition) and <see cref="SettingsEndpoints"/> (the
/// administrator-facing view), so both agree on exactly one precedence rule.
/// </summary>
internal static class ProviderResolution
{
    /// <summary>Where the effective active provider id came from.</summary>
    internal enum Source
    {
        /// <summary>The real <c>ModelProvider__Provider</c> environment variable is set; Settings was not consulted.</summary>
        EnvironmentOverride,

        /// <summary>The administrator selected this provider through Settings.</summary>
        Settings,

        /// <summary>Neither of the above; the <c>appsettings.json</c> default applies.</summary>
        Default,
    }

    /// <summary>
    /// Resolves which provider id is effective right now. A real <c>ModelProvider__Provider</c>
    /// environment variable — checked directly, not merely read back from the already-merged
    /// <see cref="IConfiguration"/> value, which cannot distinguish an explicit override from the
    /// shipped default — always wins outright over Settings.
    /// </summary>
    internal static (string ProviderId, Source Source) ResolveActiveProvider(IConfiguration configuration, SettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(settingsStore);

        var defaultProviderId = configuration["ModelProvider:Provider"] ?? string.Empty;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ModelProvider__Provider")))
        {
            return (defaultProviderId, Source.EnvironmentOverride);
        }

        var stored = settingsStore.ActiveProviderId;
        return !string.IsNullOrWhiteSpace(stored) ? (stored, Source.Settings) : (defaultProviderId, Source.Default);
    }

    /// <summary>
    /// Resolves the <see cref="ChatModelOptions"/> the host should actually construct its
    /// <see cref="IChatModel"/> from: the effective provider id above, its non-secret profile
    /// (falling back field-by-field to the <c>appsettings.json</c> <c>ModelProvider</c> block for
    /// anything the profile has not set), and its API key (an explicit environment override wins
    /// outright; otherwise the vault, keyed by provider id).
    /// </summary>
    internal static ChatModelOptions ResolveEffectiveModelOptions(
        IConfiguration configuration, SettingsStore settingsStore, ISecretProvider environmentSecretProvider, VaultSecretProvider? vaultSecretProvider)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(environmentSecretProvider);

        var configured = configuration.GetSection("ModelProvider").Get<ChatModelOptions>()
            ?? throw new InvalidOperationException("Missing 'ModelProvider' configuration section.");
        var (providerId, source) = ResolveActiveProvider(configuration, settingsStore);
        var profile = source == Source.EnvironmentOverride ? null : settingsStore.FindProviderProfile(providerId);

        var resolvedKey = configured.ApiKeySecret is not null ? environmentSecretProvider.GetSecret(configured.ApiKeySecret) : null;
        if (string.IsNullOrEmpty(resolvedKey) && vaultSecretProvider is not null)
        {
            resolvedKey = vaultSecretProvider.GetSecret(new SecretReference("vault", providerId));
        }

        return new ChatModelOptions(
            providerId,
            profile?.BaseUrl ?? configured.BaseUrl,
            configured.ApiKeySecret,
            profile?.Model ?? configured.Model,
            profile?.SupportsNativeToolCalling ?? configured.SupportsNativeToolCalling)
        {
            ResolvedApiKey = resolvedKey,
        };
    }
}
