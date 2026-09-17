// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace bOps.Packages.Web;

/// <summary>Bounded search inputs, validated/clamped by the caller before <see cref="SearxngClient.SearchAsync"/> (ADR-0028).</summary>
public sealed record SearxngQuery(string Query, string? Category, string? Language, int? SafeSearch, string? TimeRange, int? Page);

/// <summary>One normalized SearXNG result. Only fields the instance actually supplied are populated — nothing is invented.</summary>
public sealed record SearxngResult(string Title, Uri Url, string? Snippet, IReadOnlyList<string> Engines, string? PublishedDate);

/// <summary>
/// The outcome of a <c>web.search</c> call. JSON-disabled instances, non-JSON responses, transport
/// failures and non-success statuses are all actionable failures, never a silent empty result and
/// never an HTML-scraping fallback (ADR-0028, D-023 context; task V1.1-E).
/// </summary>
public sealed class SearxngSearchOutcome
{
    private SearxngSearchOutcome(bool success, IReadOnlyList<SearxngResult> results, string? failureReason)
    {
        Success = success;
        Results = results;
        FailureReason = failureReason;
    }

    public bool Success { get; }

    public IReadOnlyList<SearxngResult> Results { get; }

    public string? FailureReason { get; }

    public static SearxngSearchOutcome Ok(IReadOnlyList<SearxngResult> results) => new(true, results, null);

    public static SearxngSearchOutcome Failed(string reason) => new(false, [], reason);
}

/// <summary>
/// Queries one operator-configured SearXNG instance's JSON search API
/// (<see href="https://docs.searxng.org/dev/search_api.html"/>). The endpoint is operator-trusted
/// configuration, not model input, so this client does not route through
/// <see cref="SsrfSafeConnectCallback"/> — see ADR-0028.
/// </summary>
public sealed class SearxngClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly WebSearchOptions _options;

    /// <param name="handler">Injectable for recorded-contract tests (<see cref="StubHttpMessageHandler"/>-style); production omits it and gets a plain <see cref="HttpClient"/>.</param>
    public SearxngClient(WebSearchOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _httpClient.Timeout = options.Timeout;
    }

    public async Task<SearxngSearchOutcome> SearchAsync(SearxngQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!_options.IsConfigured)
        {
            return SearxngSearchOutcome.Failed("Web:Search:BaseUrl is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(query));
        request.Headers.UserAgent.ParseAdd("bOps-web-search/1.1");
        request.Headers.Accept.ParseAdd("application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            return SearxngSearchOutcome.Failed($"Could not reach the configured SearXNG instance: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return SearxngSearchOutcome.Failed($"SearXNG returned HTTP {(int)response.StatusCode}.");
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is null || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return SearxngSearchOutcome.Failed(
                    $"SearXNG JSON output does not appear to be enabled on this instance (received " +
                    $"Content-Type '{contentType ?? "(none)"}'). Enable 'json' under 'search.formats' in the " +
                    "instance's settings.yml.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var (bytes, _) = await BoundedReader.ReadAsync(stream, _options.MaxResponseBytes, ct);

            SearxngResponseDto? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(bytes, WebJsonContext.Default.SearxngResponseDto);
            }
            catch (JsonException ex)
            {
                return SearxngSearchOutcome.Failed($"SearXNG response could not be parsed as JSON: {ex.Message}");
            }

            var results = (parsed?.Results ?? [])
                .Where(r => !string.IsNullOrWhiteSpace(r.Title) && Uri.TryCreate(r.Url, UriKind.Absolute, out _))
                .DistinctBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
                .Take(_options.MaxResults)
                .Select(r => new SearxngResult(
                    r.Title!,
                    new Uri(r.Url!, UriKind.Absolute),
                    Truncate(r.Content, _options.MaxResultTextBytes),
                    r.Engines is { Count: > 0 } engines ? engines : r.Engine is null ? [] : [r.Engine],
                    r.PublishedDate))
                .ToList();

            return SearxngSearchOutcome.Ok(results);
        }
    }

    private Uri BuildUri(SearxngQuery query)
    {
        var parameters = new List<string> { $"q={Uri.EscapeDataString(query.Query)}", "format=json" };

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            parameters.Add($"categories={Uri.EscapeDataString(query.Category)}");
        }

        if (!string.IsNullOrWhiteSpace(query.Language))
        {
            parameters.Add($"language={Uri.EscapeDataString(query.Language)}");
        }

        if (query.SafeSearch is { } safeSearch)
        {
            parameters.Add($"safesearch={safeSearch}");
        }

        if (!string.IsNullOrWhiteSpace(query.TimeRange))
        {
            parameters.Add($"time_range={Uri.EscapeDataString(query.TimeRange)}");
        }

        if (query.Page is { } page)
        {
            parameters.Add($"pageno={page}");
        }

        return new Uri($"{_options.BaseUrl.TrimEnd('/')}/search?{string.Join('&', parameters)}");
    }

    private static string? Truncate(string? text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        var result = text;
        while (result.Length > 0 && Encoding.UTF8.GetByteCount(result) > maxBytes)
        {
            result = result[..^1];
        }

        return result;
    }

    public void Dispose() => _httpClient.Dispose();
}

internal sealed class SearxngResponseDto
{
    [JsonPropertyName("results")]
    public List<SearxngResultDto>? Results { get; set; }
}

internal sealed class SearxngResultDto
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("engine")]
    public string? Engine { get; set; }

    [JsonPropertyName("engines")]
    public List<string>? Engines { get; set; }

    [JsonPropertyName("publishedDate")]
    public string? PublishedDate { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SearxngResponseDto))]
internal sealed partial class WebJsonContext : JsonSerializerContext;
