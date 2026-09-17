// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Web;

/// <summary>
/// Fetches one HTTP/HTTPS URL under strict SSRF/DNS-rebinding/redirect/size/content-type controls
/// (ADR-0028). Read-risk: it mutates nothing on the managed node. Accepts no headers, credentials,
/// cookies or request body — only a URL.
/// </summary>
public sealed class WebFetchTool(WebFetchService fetchService) : ITool
{
    public ToolManifest Manifest { get; } = new()
    {
        Name = "web.fetch",
        Description =
            "Fetches one HTTP/HTTPS URL and returns bounded text. Denies loopback/private/link-local/metadata " +
            "network destinations by default, caps redirects/response size/decompressed size, and only decodes " +
            "an allowlisted set of textual content types.",
        Risk = RiskLevel.Read,
        Platforms = ["windows", "linux"],
        Requires = [],
        Parameters = [new ToolParameter("url", ToolParameterType.String, "The absolute http:// or https:// URL to fetch.")],
    };

    public async Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var url = arguments.GetRequired<string>("url");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return ToolCallResult.Failure($"'{url}' is not an absolute http:// or https:// URL.");
        }

        try
        {
            var result = await fetchService.FetchAsync(uri, ct);
            return ToolCallResult.Success(ToJson(result));
        }
        catch (Exception ex) when (ex is WebFetchException or WebFetchDestinationDeniedException or HttpRequestException)
        {
            return ToolCallResult.Failure(FindDeniedException(ex)?.Message ?? ex.Message);
        }
    }

    /// <summary>
    /// SocketsHttpHandler wraps any exception ConnectCallback throws in HttpRequestException — and,
    /// observed under connection-retry conditions, sometimes wraps that again — which would
    /// otherwise hide SsrfSafeConnectCallback's actionable denial message behind a generic
    /// transport-failure one.
    /// </summary>
    private static WebFetchDestinationDeniedException? FindDeniedException(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is WebFetchDestinationDeniedException denied)
            {
                return denied;
            }
        }

        return null;
    }

    private static string ToJson(WebFetchResult result)
    {
        var json = new JsonObject
        {
            ["requestedUrl"] = result.RequestedUrl.ToString(),
            ["finalUrl"] = result.FinalUrl.ToString(),
            ["statusCode"] = result.StatusCode,
            ["contentType"] = result.ContentType,
            ["charset"] = result.Charset,
            ["retrievedAtUtc"] = result.RetrievedAtUtc,
            ["truncated"] = result.Truncated,
            ["byteLength"] = result.ByteLength,
            ["text"] = result.Text,
        };
        return json.ToJsonString();
    }
}
