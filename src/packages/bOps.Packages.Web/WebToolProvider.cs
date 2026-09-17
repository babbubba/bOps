// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Packages.Web;

/// <summary>Contributes <c>web.search</c> and <c>web.fetch</c>, owning the one <see cref="HttpClient"/> each depends on (ADR-0028).</summary>
public sealed class WebToolProvider : IToolProvider, IDisposable
{
    private readonly WebFetchService _fetchService;
    private readonly SearxngClient _searxngClient;

    public WebToolProvider(WebFetchOptions fetchOptions, WebSearchOptions searchOptions, IDnsResolver? dnsResolver = null)
    {
        ArgumentNullException.ThrowIfNull(fetchOptions);
        ArgumentNullException.ThrowIfNull(searchOptions);
        _fetchService = new WebFetchService(fetchOptions, dnsResolver);
        _searxngClient = new SearxngClient(searchOptions);
    }

    public IEnumerable<ITool> GetTools() =>
    [
        new WebSearchTool(_searxngClient),
        new WebFetchTool(_fetchService),
    ];

    public void Dispose()
    {
        _fetchService.Dispose();
        _searxngClient.Dispose();
    }
}
