// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace bOps.Api.Tests;

/// <summary>
/// ADR-0043 §13: the global exception handler keeps Development responses free of request data without turning
/// framework request errors (<c>BadHttpRequestException</c>) into 500s.
/// </summary>
public sealed class ExceptionHandlerStatusTests
{
    private const string TypedEndpoint = "/api/agents/tasks";
    private const string MalformedJson = "{distinctive-malformed-body";

    [Fact]
    public async Task BearerMalformedJson_Is400_WithoutEchoingTheKeyOrBody()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateAnonymousClient();

        using var response = await BrowserSession.SendAsync(
            client, HttpMethod.Post, TypedEndpoint, cookie: null, json: MalformedJson, bearer: $"Bearer {factory.ApiKey}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        BrowserSession.AssertAbsent(body, factory.ApiKey, "the 400 body");
        BrowserSession.AssertAbsent(body, "distinctive-malformed-body", "the 400 body");
        Assert.DoesNotContain("Bearer", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CookieMalformedJson_Is400_WithoutEchoingTheCookieKeyOrBody()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateSessionClient();
        var cookie = await BrowserSession.SignInAsync(client, factory.ApiKey);

        using var response = await BrowserSession.SendTrustedAsync(client, HttpMethod.Post, TypedEndpoint, cookie, MalformedJson);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        BrowserSession.AssertAbsent(body, cookie, "the 400 body");
        BrowserSession.AssertAbsent(body, factory.ApiKey, "the 400 body");
        BrowserSession.AssertAbsent(body, "distinctive-malformed-body", "the 400 body");
        Assert.DoesNotContain("Cookie", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BearerTextPlain_Is415()
    {
        using var factory = new TestAppFactory();
        using var client = factory.CreateClient();
        using var content = new StringContent(MalformedJson, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        using var response = await client.PostAsync(new Uri(TypedEndpoint, UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }
}
