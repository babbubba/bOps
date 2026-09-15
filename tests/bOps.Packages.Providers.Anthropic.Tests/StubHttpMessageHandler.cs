// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;

namespace bOps.Packages.Providers.Anthropic.Tests;

/// <summary>
/// Replays a fixed sequence of responses and records every request sent through it — the
/// "recorded HTTP fixture" contract-test style agentic/04-testing-rules.md calls for on provider
/// packages, without a live call.
/// </summary>
internal sealed class StubHttpMessageHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
{
    private int _callCount;

    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> RequestBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));

        var (status, body) = responses[_callCount];
        _callCount++;

        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}
