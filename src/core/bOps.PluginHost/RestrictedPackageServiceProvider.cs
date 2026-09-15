// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace bOps.PluginHost;

/// <summary>
/// The A10 container: a plugin's entry type is constructed with <c>ActivatorUtilities</c>
/// against exactly this, never the host's own <see cref="IServiceProvider"/>. Anything not in
/// agentic/01-architecture-rules.md's rule A10 list is out of reach by design — this type only
/// changes if that list does.
/// </summary>
internal sealed class RestrictedPackageServiceProvider(
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    IConfigurationSection configurationSection,
    ICapabilityProbe capabilityProbe) : IServiceProvider
{
    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceType == typeof(ILoggerFactory))
        {
            return loggerFactory;
        }

        if (serviceType == typeof(IHttpClientFactory))
        {
            return httpClientFactory;
        }

        if (serviceType == typeof(TimeProvider))
        {
            return timeProvider;
        }

        if (serviceType == typeof(IConfigurationSection) || serviceType == typeof(IConfiguration))
        {
            return configurationSection;
        }

        if (serviceType == typeof(ICapabilityProbe))
        {
            return capabilityProbe;
        }

        return null;
    }
}
