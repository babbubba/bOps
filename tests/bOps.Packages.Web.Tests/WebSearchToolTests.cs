// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Web.Tests;

public sealed class WebSearchToolTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    private static WebSearchOptions Options(int maxResults = 10, int maxResultTextBytes = 1024) => new()
    {
        BaseUrl = "http://searxng.internal.test",
        MaxResults = maxResults,
        MaxResultTextBytes = maxResultTextBytes,
        Timeout = TimeSpan.FromSeconds(5),
    };

    private (WebSearchTool Tool, StubHttpMessageHandler Handler) CreateTool(
        WebSearchOptions? options = null, params (HttpStatusCode, string, string)[] responses)
    {
        var handler = new StubHttpMessageHandler(responses);
        var client = new SearxngClient(options ?? Options(), handler);
        _disposables.Add(handler);
        _disposables.Add(client);
        return (new WebSearchTool(client), handler);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private static ToolArguments Args(object payload) =>
        ToolArguments.FromJson((JsonObject)JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(payload))!);

    [Fact]
    public async Task ExecuteAsync_ReturnsNormalizedResults_ForASuccessfulJsonResponse()
    {
        const string body = """
            {"results": [
                {"title": "Example", "url": "https://example.com/a", "content": "snippet", "engine": "duckduckgo"},
                {"title": "Second", "url": "https://example.com/b", "engines": ["google", "bing"], "publishedDate": "2026-01-01"}
            ]}
            """;
        var (tool, handler) = CreateTool(responses: (HttpStatusCode.OK, body, "application/json; charset=utf-8"));

        var result = await tool.ExecuteAsync(Args(new { query = "bOps" }));

        Assert.True(result.Succeeded);
        var json = JsonNode.Parse(result.Output!)!;
        Assert.Equal(2, json["resultCount"]!.GetValue<int>());
        Assert.Equal("Example", json["results"]![0]!["title"]!.GetValue<string>());
        Assert.Equal("https://example.com/a", json["results"]![0]!["url"]!.GetValue<string>());
        Assert.Contains("format=json", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsZeroResults_ForAnEmptyResultsArray()
    {
        var (tool, _) = CreateTool(responses: (HttpStatusCode.OK, """{"results": []}""", "application/json"));

        var result = await tool.ExecuteAsync(Args(new { query = "nothing found" }));

        Assert.True(result.Succeeded);
        Assert.Equal(0, JsonNode.Parse(result.Output!)!["resultCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task ExecuteAsync_Fails_ForMalformedJson()
    {
        var (tool, _) = CreateTool(responses: (HttpStatusCode.OK, "{not json", "application/json"));

        var result = await tool.ExecuteAsync(Args(new { query = "x" }));

        Assert.False(result.Succeeded);
        Assert.Contains("could not be parsed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Fails_WhenTheInstanceReturnsHtmlInsteadOfJson()
    {
        var (tool, _) = CreateTool(responses: (HttpStatusCode.OK, "<html>search results</html>", "text/html"));

        var result = await tool.ExecuteAsync(Args(new { query = "x" }));

        Assert.False(result.Succeeded);
        Assert.Contains("JSON output does not appear to be enabled", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_Fails_ForANonSuccessStatusCode()
    {
        var (tool, _) = CreateTool(responses: (HttpStatusCode.InternalServerError, "oops", "text/plain"));

        var result = await tool.ExecuteAsync(Args(new { query = "x" }));

        Assert.False(result.Succeeded);
        Assert.Contains("500", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_LimitsResults_ToMaxResults()
    {
        const string body = """
            {"results": [
                {"title": "A", "url": "https://example.com/1"},
                {"title": "B", "url": "https://example.com/2"},
                {"title": "C", "url": "https://example.com/3"}
            ]}
            """;
        var (tool, _) = CreateTool(Options(maxResults: 2), (HttpStatusCode.OK, body, "application/json"));

        var result = await tool.ExecuteAsync(Args(new { query = "x" }));

        Assert.Equal(2, JsonNode.Parse(result.Output!)!["resultCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task ExecuteAsync_DeduplicatesResultsWithTheSameUrl()
    {
        const string body = """
            {"results": [
                {"title": "A", "url": "https://example.com/1"},
                {"title": "A duplicate", "url": "https://example.com/1"}
            ]}
            """;
        var (tool, _) = CreateTool(responses: (HttpStatusCode.OK, body, "application/json"));

        var result = await tool.ExecuteAsync(Args(new { query = "x" }));

        Assert.Equal(1, JsonNode.Parse(result.Output!)!["resultCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task ExecuteAsync_TruncatesASnippetLongerThanMaxResultTextBytes()
    {
        var longSnippet = new string('a', 2000);
        var body = $$"""{"results": [{"title": "A", "url": "https://example.com/1", "content": "{{longSnippet}}"}]}""";
        var (tool, _) = CreateTool(Options(maxResultTextBytes: 100), (HttpStatusCode.OK, body, "application/json"));

        var result = await tool.ExecuteAsync(Args(new { query = "x" }));

        var snippet = JsonNode.Parse(result.Output!)!["results"]![0]!["snippet"]!.GetValue<string>();
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(snippet) <= 100);
    }

    [Fact]
    public async Task ExecuteAsync_Fails_ForAnEmptyQuery()
    {
        var (tool, _) = CreateTool(responses: (HttpStatusCode.OK, "{}", "application/json"));

        var result = await tool.ExecuteAsync(Args(new { query = "   " }));

        Assert.False(result.Succeeded);
        Assert.Contains("empty", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public async Task ExecuteAsync_Fails_ForAnOutOfRangeSafeSearchValue(int safeSearch)
    {
        var (tool, _) = CreateTool(responses: (HttpStatusCode.OK, "{}", "application/json"));

        var result = await tool.ExecuteAsync(Args(new { query = "x", safeSearch }));

        Assert.False(result.Succeeded);
        Assert.Contains("safeSearch", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellation_WhenTheRequestExceedsTheConfiguredTimeout()
    {
        var handler = new DelayingHandler(TimeSpan.FromMilliseconds(500));
        var options = Options();
        options.Timeout = TimeSpan.FromMilliseconds(20);
        var client = new SearxngClient(options, handler);
        _disposables.Add(handler);
        _disposables.Add(client);
        var tool = new WebSearchTool(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(Args(new { query = "x" })));
    }

    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
