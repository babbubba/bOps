// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;

namespace bOps.Packages.Web;

/// <summary>The bounded, attributed result of one <c>web.fetch</c> call (ADR-0028). Untrusted external data under S5.</summary>
public sealed record WebFetchResult(
    Uri RequestedUrl,
    Uri FinalUrl,
    int StatusCode,
    string? ContentType,
    string? Charset,
    DateTimeOffset RetrievedAtUtc,
    bool Truncated,
    long ByteLength,
    string? Text);

/// <summary>An expected, actionable <c>web.fetch</c> failure: too many redirects, a rejected scheme downgrade, or a malformed redirect.</summary>
public sealed class WebFetchException : Exception
{
    public WebFetchException(string message)
        : base(message)
    {
    }

    public WebFetchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WebFetchException()
    {
    }
}

/// <summary>
/// Fetches one HTTP/HTTPS resource under the SSRF/DNS-rebinding/redirect/decoding controls in
/// ADR-0028. Owns one <see cref="HttpClient"/> built on <see cref="SsrfSafeConnectCallback"/>;
/// redirects are followed by <see cref="FetchAsync"/> itself (<c>AllowAutoRedirect</c> is off) so
/// every hop is independently bounded and revalidated.
/// </summary>
public sealed class WebFetchService : IDisposable
{
    private static readonly HashSet<string> AllowedCharsets = new(StringComparer.OrdinalIgnoreCase)
    {
        "utf-8", "utf8", "us-ascii", "ascii", "iso-8859-1", "latin1",
    };

    private readonly HttpClient _httpClient;
    private readonly WebFetchOptions _options;
    private readonly TimeProvider _timeProvider;

    public WebFetchService(WebFetchOptions options, IDnsResolver? dnsResolver = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var policy = new IpAddressPolicy(options.AllowedAddresses, options.AllowedNetworks);
        var connectCallback = new SsrfSafeConnectCallback(dnsResolver ?? new SystemDnsResolver(), policy);

        // Ownership transfers to HttpClient(handler, disposeHandler: true), which disposes it;
        // CA2000 cannot model that hand-off across a constructor argument.
#pragma warning disable CA2000
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = options.ConnectTimeout,
            ConnectCallback = connectCallback.ConnectAsync,
        };
#pragma warning restore CA2000

        _httpClient = new HttpClient(handler, disposeHandler: true);
    }

    public async Task<WebFetchResult> FetchAsync(Uri requestedUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requestedUrl);
        if (requestedUrl.Scheme is not ("http" or "https"))
        {
            throw new WebFetchException($"Only http/https URLs are supported, not '{requestedUrl.Scheme}'.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.OverallTimeout);
        var linkedCt = timeoutCts.Token;

        var currentUrl = requestedUrl;
        for (var redirectCount = 0; ; redirectCount++)
        {
            // Redeclared each loop iteration; CA2000 cannot prove disposal across loop re-entry,
            // but the using statement disposes both on every iteration and on every return path.
#pragma warning disable CA2000
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
            request.Headers.UserAgent.ParseAdd("bOps-web-fetch/1.1");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCt);
#pragma warning restore CA2000

            if (!IsRedirect(response.StatusCode))
            {
                return await BuildResultAsync(requestedUrl, currentUrl, response, linkedCt);
            }

            var location = response.Headers.Location
                ?? throw new WebFetchException($"'{currentUrl}' returned {(int)response.StatusCode} without a Location header.");
            var nextUrl = location.IsAbsoluteUri ? location : new Uri(currentUrl, location);

            if (nextUrl.Scheme is not ("http" or "https"))
            {
                throw new WebFetchException($"Redirect from '{currentUrl}' to unsupported scheme '{nextUrl.Scheme}' is rejected.");
            }

            if (!_options.AllowSchemeDowngradeOnRedirect && IsSchemeDowngrade(currentUrl, nextUrl))
            {
                throw new WebFetchException($"Redirect from https to http is rejected by policy ({currentUrl} -> {nextUrl}).");
            }

            if (redirectCount >= _options.MaxRedirects)
            {
                throw new WebFetchException($"'{requestedUrl}' exceeded the maximum of {_options.MaxRedirects} redirects.");
            }

            currentUrl = nextUrl;
        }
    }

    private async Task<WebFetchResult> BuildResultAsync(Uri requestedUrl, Uri finalUrl, HttpResponseMessage response, CancellationToken ct)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var charset = response.Content.Headers.ContentType?.CharSet;
        var retrievedAt = _timeProvider.GetUtcNow();
        var contentEncoding = response.Content.Headers.ContentEncoding.FirstOrDefault();

        var allowed = mediaType is not null
            && _options.AllowedContentTypes.Any(t => string.Equals(t, mediaType, StringComparison.OrdinalIgnoreCase));

        await using var rawStream = await response.Content.ReadAsStreamAsync(ct);
        await using var contentStream = WrapDecompression(rawStream, contentEncoding);
        var maxBytes = IsCompressed(contentEncoding) ? _options.MaxDecompressedBytes : _options.MaxResponseBytes;
        var (bytes, truncated) = await BoundedReader.ReadAsync(contentStream, maxBytes, ct);

        var text = allowed ? ResolveEncoding(charset).GetString(bytes) : null;

        return new WebFetchResult(
            requestedUrl,
            finalUrl,
            (int)response.StatusCode,
            mediaType,
            allowed ? charset : null,
            retrievedAt,
            truncated,
            bytes.LongLength,
            text);
    }

    /// <summary>Extracted so the scheme-downgrade rule is unit-testable without a real TLS connection.</summary>
    internal static bool IsSchemeDowngrade(Uri currentUrl, Uri nextUrl) =>
        currentUrl.Scheme == "https" && nextUrl.Scheme == "http";

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsCompressed(string? contentEncoding) =>
        contentEncoding is "gzip" or "deflate" or "br";

    private static Stream WrapDecompression(Stream raw, string? contentEncoding) => contentEncoding switch
    {
        "gzip" => new GZipStream(raw, CompressionMode.Decompress),
        "deflate" => new DeflateStream(raw, CompressionMode.Decompress),
        "br" => new BrotliStream(raw, CompressionMode.Decompress),
        _ => raw,
    };

    private static Encoding ResolveEncoding(string? charset)
    {
        var normalized = charset?.Trim('"', ' ');
        if (normalized is null || !AllowedCharsets.Contains(normalized))
        {
            return Encoding.UTF8;
        }

        return normalized.ToLowerInvariant() switch
        {
            "us-ascii" or "ascii" => Encoding.ASCII,
            "iso-8859-1" or "latin1" => Encoding.Latin1,
            _ => Encoding.UTF8,
        };
    }

    public void Dispose() => _httpClient.Dispose();
}
