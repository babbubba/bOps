// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using bOps.Abstractions;

namespace bOps.Packages.Providers.Anthropic.Tests;

/// <summary>
/// HARDEN-2 / ADR-0039 for the native Anthropic adapter: one HTTP attempt per call, Anthropic error bodies classified into
/// provider-neutral kinds, <c>Retry-After</c> parsed, and a safe reason that never carries the key.
/// </summary>
public sealed class AnthropicProviderFailureTests
{
    private const string ApiKey = "sk-ant-api03-0123456789abcdefghijklmnop";

    private static readonly ModelRequest Request = new("system", [ChatTurn.FromUser("hello")], []);

    private static string Error(string type, string message) =>
        $$$"""{"type":"error","error":{"type":"{{{type}}}","message":"{{{message}}}"}}""";

    private static async Task<(ModelProtocolException Failure, int Requests)> FailureAsync(
        HttpStatusCode status, string body, Action<HttpResponseMessage>? headers = null)
    {
        using var handler = new CountingHandler(status, body, headers);
        using var client = new HttpClient(handler, disposeHandler: false);
        var model = new AnthropicChatModel(
            new ChatModelOptions("Anthropic", "https://api.anthropic.com", new SecretReference("environment", "K"), "claude-test")
            {
                ResolvedApiKey = ApiKey,
            },
            client);

        var failure = await Assert.ThrowsAsync<ModelProtocolException>(() => model.CompleteAsync(Request));
        return (failure, handler.Calls);
    }

    [Theory]
    [InlineData(401, "authentication_error", "invalid x-api-key", ModelFailureKind.Authentication)]
    [InlineData(403, "permission_error", "Your API key does not have permission", ModelFailureKind.Authentication)]
    [InlineData(400, "billing_error", "Your credit balance is too low", ModelFailureKind.QuotaExceeded)]
    [InlineData(400, "invalid_request_error", "messages: roles must alternate", ModelFailureKind.InvalidRequest)]
    [InlineData(400, "invalid_request_error", "prompt is too long: 210000 tokens > 200000 maximum", ModelFailureKind.ContextOverflow)]
    [InlineData(413, "request_too_large", "Request exceeds the maximum allowed number of bytes", ModelFailureKind.InvalidRequest)]
    [InlineData(429, "rate_limit_error", "Number of request tokens has exceeded your per-minute rate limit", ModelFailureKind.RateLimited)]
    [InlineData(500, "api_error", "Internal server error", ModelFailureKind.Transient)]
    [InlineData(529, "overloaded_error", "Overloaded", ModelFailureKind.Transient)]
    public async Task AnAnthropicError_IsClassified_InOneAttempt(int status, string type, string message, ModelFailureKind expected)
    {
        var (failure, requests) = await FailureAsync((HttpStatusCode)status, Error(type, message));

        Assert.Equal(expected, failure.FailureKind);
        Assert.Equal(status, failure.ProviderStatusCode);
        Assert.Equal(1, requests);
        Assert.Contains(message, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryAfter_IsParsedForTheRuntime()
    {
        var (failure, _) = await FailureAsync(HttpStatusCode.TooManyRequests, Error("rate_limit_error", "slow down"),
            response => response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3)));

        Assert.Equal(TimeSpan.FromSeconds(3), failure.RetryAfter);
    }

    [Fact]
    public async Task AMalformedSuccessReply_IsMalformedResponse()
    {
        var (failure, _) = await FailureAsync(HttpStatusCode.OK, "not json");

        Assert.Equal(ModelFailureKind.MalformedResponse, failure.FailureKind);
    }

    private sealed class CountingHandler(HttpStatusCode status, string body, Action<HttpResponseMessage>? headers) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            headers?.Invoke(response);
            return Task.FromResult(response);
        }
    }
}
