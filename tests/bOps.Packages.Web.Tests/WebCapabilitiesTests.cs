// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Packages.Web.Tests;

public sealed class WebCapabilitiesTests
{
    [Fact]
    public async Task IsSearxngConfiguredAsync_IsFalse_WhenBaseUrlIsEmpty()
    {
        var options = new WebSearchOptions { BaseUrl = string.Empty };

        Assert.False(options.IsConfigured);
        Assert.False(await WebCapabilities.IsSearxngConfiguredAsync(options, CancellationToken.None));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://searxng.internal/")]
    public async Task IsSearxngConfiguredAsync_IsFalse_ForANonHttpBaseUrl(string value)
    {
        var options = new WebSearchOptions { BaseUrl = value };

        Assert.False(options.IsConfigured);
        Assert.False(await WebCapabilities.IsSearxngConfiguredAsync(options, CancellationToken.None));
    }

    [Fact]
    public async Task IsSearxngConfiguredAsync_IsTrue_ForAWellFormedAbsoluteHttpUrl()
    {
        var options = new WebSearchOptions { BaseUrl = "http://searxng.internal:8080" };

        Assert.True(options.IsConfigured);
        Assert.True(await WebCapabilities.IsSearxngConfiguredAsync(options, CancellationToken.None));
    }

    [Fact]
    public void WebToolProvider_AlwaysContributesBothTools_RegardlessOfSearchConfiguration()
    {
        using var provider = new WebToolProvider(new WebFetchOptions(), new WebSearchOptions());

        var names = provider.GetTools().Select(tool => tool.Manifest.Name).ToList();

        Assert.Contains("web.fetch", names);
        Assert.Contains("web.search", names);
        Assert.Empty(provider.GetTools().Single(t => t.Manifest.Name == "web.fetch").Manifest.Requires);
        Assert.Equal([WebCapabilities.Searxng], provider.GetTools().Single(t => t.Manifest.Name == "web.search").Manifest.Requires);
    }
}
