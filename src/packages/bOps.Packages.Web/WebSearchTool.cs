// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Web;

/// <summary>
/// Searches the web through one operator-configured SearXNG instance's JSON API. Read-risk. No
/// API key, no HTML-scraping fallback — a JSON-disabled instance is reported as an actionable
/// failure (ADR-0028).
/// </summary>
public sealed class WebSearchTool(SearxngClient client) : ITool
{
    private const int MaxQueryLength = 512;

    public ToolManifest Manifest { get; } = new()
    {
        Name = "web.search",
        Description =
            "Searches the web through an operator-configured SearXNG instance's JSON API. No API key. " +
            "Never falls back to scraping an HTML search page.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [WebCapabilities.Searxng],
        Parameters =
        [
            new ToolParameter("query", ToolParameterType.String, "The search query."),
            new ToolParameter(
                "category", ToolParameterType.Enum, "Restricts results to one result category.",
                Required: false, AllowedValues: ["general", "images", "videos", "news", "map", "science", "it"]),
            new ToolParameter("language", ToolParameterType.String, "ISO language code, e.g. 'en' or 'en-US'.", Required: false),
            new ToolParameter(
                "safeSearch", ToolParameterType.Integer,
                "0 (off), 1 (moderate) or 2 (strict). Defaults to the instance's own setting.", Required: false),
            new ToolParameter(
                "timeRange", ToolParameterType.Enum, "Restricts results to a recency window.",
                Required: false, AllowedValues: ["day", "week", "month", "year"]),
            new ToolParameter("page", ToolParameterType.Integer, "1-based result page. Defaults to 1.", Required: false),
        ],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var queryText = arguments.GetRequired<string>("query").Trim();
        if (queryText.Length == 0)
        {
            return ToolCallResult.Failure("'query' must not be empty.");
        }

        if (queryText.Length > MaxQueryLength)
        {
            return ToolCallResult.Failure($"'query' must be at most {MaxQueryLength} characters.");
        }

        arguments.TryGet<string>("category", out var category);
        arguments.TryGet<string>("language", out var language);
        arguments.TryGet<string>("timeRange", out var timeRange);

        arguments.TryGet<int>("safeSearch", out var safeSearch);
        if (arguments.ContainsKey("safeSearch") && safeSearch is < 0 or > 2)
        {
            return ToolCallResult.Failure("'safeSearch' must be 0, 1 or 2.");
        }

        arguments.TryGet<int>("page", out var page);
        if (arguments.ContainsKey("page") && page is < 1 or > 20)
        {
            return ToolCallResult.Failure("'page' must be between 1 and 20.");
        }

        var query = new SearxngQuery(
            queryText,
            category,
            language,
            arguments.ContainsKey("safeSearch") ? safeSearch : null,
            timeRange,
            arguments.ContainsKey("page") ? page : null);

        var outcome = await client.SearchAsync(query, ct);
        return outcome.Success
            ? ToolCallResult.Success(ToJson(queryText, outcome.Results))
            : ToolCallResult.Failure(outcome.FailureReason!);
    }

    private static string ToJson(string query, IReadOnlyList<SearxngResult> results)
    {
        var array = new JsonArray();
        foreach (var result in results)
        {
            array.Add(new JsonObject
            {
                ["title"] = result.Title,
                ["url"] = result.Url.ToString(),
                ["snippet"] = result.Snippet,
                ["engines"] = new JsonArray(result.Engines.Select(engine => (JsonNode)engine).ToArray()),
                ["publishedDate"] = result.PublishedDate,
            });
        }

        var json = new JsonObject
        {
            ["query"] = query,
            ["resultCount"] = results.Count,
            ["results"] = array,
        };
        return json.ToJsonString();
    }
}
