// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace bOps.Api;

/// <summary>
/// The validated <c>ReverseProxy</c> configuration section (F-26, BOPS-0120). Forwarded headers are never trusted by default: with no
/// <c>KnownProxies</c> and no <c>KnownNetworks</c> the host ignores <c>X-Forwarded-*</c> completely. When a TLS reverse proxy is listed,
/// only <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> from that address are honoured (one hop), which lets the per-address
/// rate limiter see the browser instead of the proxy. The browser-session origin check never reads forwarded headers (ADR-0043 §9).
/// Both keys are one comma-separated string, like <c>BrowserSession:Origins</c>; a catch-all network is refused.
/// </summary>
internal sealed class ReverseProxySettings
{
    public const string SectionName = "ReverseProxy";

    private ReverseProxySettings(IReadOnlyList<IPAddress> proxies, IReadOnlyList<IPNetwork> networks)
    {
        KnownProxies = proxies;
        KnownNetworks = networks;
    }

    public IReadOnlyList<IPAddress> KnownProxies { get; }

    public IReadOnlyList<IPNetwork> KnownNetworks { get; }

    public bool Enabled => KnownProxies.Count > 0 || KnownNetworks.Count > 0;

    public static ReverseProxySettings Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        foreach (var key in new[] { "KnownProxies", "KnownNetworks" })
        {
            if (section.GetSection(key).GetChildren().Any())
            {
                throw Invalid(key, "must be one comma-separated string, not an array");
            }
        }

        var proxies = new List<IPAddress>();
        foreach (var entry in Split(section["KnownProxies"]))
        {
            proxies.Add(IPAddress.TryParse(entry, out var address)
                ? address
                : throw Invalid("KnownProxies", $"entry '{entry}' is not an IP address"));
        }

        var networks = new List<IPNetwork>();
        foreach (var entry in Split(section["KnownNetworks"]))
        {
            if (!IPNetwork.TryParse(entry, out var network))
            {
                throw Invalid("KnownNetworks", $"entry '{entry}' is not a CIDR network such as 10.0.0.0/24");
            }

            if (network.PrefixLength == 0)
            {
                throw Invalid("KnownNetworks", $"entry '{entry}' would trust every address");
            }

            networks.Add(network);
        }

        return new ReverseProxySettings(proxies, networks);
    }

    public ForwardedHeadersOptions ToOptions()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in KnownProxies)
        {
            options.KnownProxies.Add(proxy);
        }

        foreach (var network in KnownNetworks)
        {
            options.KnownIPNetworks.Add(network);
        }

        return options;
    }

    private static string[] Split(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static InvalidOperationException Invalid(string key, string reason) =>
        new($"'{SectionName}:{key}' {reason}.");
}
