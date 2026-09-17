// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;

namespace bOps.Packages.Web.Tests;

/// <summary>
/// Replays a fixed sequence of responses and records every request sent through it — the
/// "recorded HTTP fixture" contract-test style agentic/04-testing-rules.md calls for
/// (mirrors bOps.Packages.Providers.Anthropic.Tests.StubHttpMessageHandler), extended with an
/// explicit content type since <c>web.search</c>'s JSON-disabled detection depends on it.
/// </summary>
internal sealed class StubHttpMessageHandler(params (HttpStatusCode Status, string Body, string ContentType)[] responses) : HttpMessageHandler
{
    private int _callCount;

    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);

        var (status, body, contentType) = responses[_callCount];
        _callCount++;

        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return Task.FromResult(response);
    }
}
