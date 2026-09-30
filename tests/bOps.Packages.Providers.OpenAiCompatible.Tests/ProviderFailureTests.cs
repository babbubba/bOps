// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using bOps.Abstractions;
using bOps.Packages.Providers.Wire;

namespace bOps.Packages.Providers.OpenAiCompatible.Tests;

/// <summary>
/// HARDEN-2 / ADR-0039 at the provider boundary: one HTTP attempt per <see cref="OpenAiCompatibleChatModel.CompleteAsync"/>,
/// every failure classified into a provider-neutral kind, <c>Retry-After</c> parsed safely, and a reason that is
/// actionable but never carries a key, an identifier or the request.
/// </summary>
public sealed class ProviderFailureTests
{
    private const string ApiKey = "sk-or-v1-0123456789abcdef0123456789abcdef";

    private static readonly ModelRequest Request = new("system", [ChatTurn.FromUser("PROMPT-CONTENT-MARKER hello")], []);

    // The body OpenRouter returned in the incident of task 88f97dda (hardening plan §2.2), with a synthetic user id.
    private const string IncidentBody =
        """
        {"error":{"message":"Provider returned error","code":400,"metadata":{"raw":"{ \"error\": { \"code\": 400, \"message\": \"Validation: Function at index 0 has an invalid name: \\\"fs.size\\\". Only a-z, A-Z, 0-9, underscores, and dashes are allowed.\", \"type\": \"Bad Request\" } }","provider_name":"Poolside","is_byok":false,"provider_error_code":"400","previous_errors":[{"code":429,"message":"Rate limit exceeded: free-models-per-min. "},{"code":429,"message":"Rate limit exceeded: free-models-per-min. "},{"code":429,"message":"Rate limit exceeded: free-models-per-min. "},{"code":429,"message":"Rate limit exceeded: free-models-per-min. "},{"code":429,"message":"Rate limit exceeded: free-models-per-min. "},{"code":429,"message":"Rate limit exceeded: free-models-per-min. "},{"code":429,"message":"Rate limit exceeded: free-models-per-min. "}]}},"user_id":"user_2xYzSyntheticIdentifier9"}
        """;

    private const string TextReply = """{"model":"m","choices":[{"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}""";

    private static ChatModelOptions Options(TimeSpan? requestTimeout = null) =>
        new("OpenRouter", "https://openrouter.ai/api/v1", new SecretReference("environment", "OPENROUTER_API_KEY"), "openrouter/free")
        {
            ResolvedApiKey = ApiKey,
            RequestTimeout = requestTimeout,
        };

#pragma warning disable CA2000 // The handler/HttpClient pair lives as long as the test method.
    private static (OpenAiCompatibleChatModel Model, ScriptedHandler Handler, HttpClient Client) Create(
        TimeSpan? requestTimeout, params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps)
    {
        var handler = new ScriptedHandler(steps);
        var client = new HttpClient(handler);
        return (new OpenAiCompatibleChatModel(Options(requestTimeout), client), handler, client);
    }
#pragma warning restore CA2000

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond(
        HttpStatusCode status, string body, Action<HttpResponseMessage>? headers = null) =>
        (_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            headers?.Invoke(response);
            return Task.FromResult(response);
        };

    private static async Task<ModelProtocolException> FailureAsync(HttpStatusCode status, string body, Action<HttpResponseMessage>? headers = null)
    {
        var (model, handler, _) = Create(null, Respond(status, body, headers), Respond(HttpStatusCode.OK, TextReply));
        var failure = await Assert.ThrowsAsync<ModelProtocolException>(() => model.CompleteAsync(Request));
        Assert.Equal(1, handler.Calls); // ADR-0039 §3: never a hidden second attempt.
        return failure;
    }

    // H2-06, H2-07, H2-08, H2-09, H2-10, and the transient/timeout statuses.
    [Theory]
    [InlineData(401, """{"error":{"message":"No auth credentials found","code":401}}""", ModelFailureKind.Authentication)]
    [InlineData(403, """{"error":{"message":"Key is disabled","code":403}}""", ModelFailureKind.Authentication)]
    [InlineData(402, """{"error":{"message":"Insufficient credits","code":402}}""", ModelFailureKind.QuotaExceeded)]
    [InlineData(400, """{"error":{"message":"Invalid parameter: temperature","code":400}}""", ModelFailureKind.InvalidRequest)]
    [InlineData(404, """{"error":{"message":"No endpoints found that support tool use.","code":404}}""", ModelFailureKind.InvalidRequest)]
    [InlineData(413, """{"error":{"message":"Request entity too large","code":413}}""", ModelFailureKind.InvalidRequest)]
    [InlineData(413, "<html>too large</html>", ModelFailureKind.InvalidRequest)]
    [InlineData(429, """{"error":{"message":"Rate limit exceeded","code":429}}""", ModelFailureKind.RateLimited)]
    [InlineData(408, "", ModelFailureKind.Timeout)]
    [InlineData(504, "gateway timeout", ModelFailureKind.Timeout)]
    [InlineData(500, "", ModelFailureKind.Transient)]
    [InlineData(502, "bad gateway", ModelFailureKind.Transient)]
    [InlineData(503, "overloaded", ModelFailureKind.Transient)]
    [InlineData(529, """{"error":{"message":"Overloaded"}}""", ModelFailureKind.Transient)]
    [InlineData(501, "not implemented", ModelFailureKind.Unknown)]
    public async Task AnHttpFailure_IsClassified_InOneAttempt(int status, string body, ModelFailureKind expected)
    {
        var failure = await FailureAsync((HttpStatusCode)status, body);

        Assert.Equal(expected, failure.FailureKind);
        Assert.Equal(status, failure.ProviderStatusCode);
        Assert.Contains($"HTTP {status}", failure.Message, StringComparison.Ordinal);
    }

    // H2-11: only explicit provider evidence makes a context overflow.
    [Theory]
    [InlineData(400, """{"error":{"message":"This model's maximum context length is 8192 tokens.","type":"invalid_request_error","code":"context_length_exceeded"}}""")]
    [InlineData(400, """{"error":{"message":"Please reduce the length of the messages: maximum context length exceeded","code":400}}""")]
    [InlineData(413, """{"error":{"message":"prompt is too long: 210000 tokens > 200000 maximum","code":413}}""")]
    [InlineData(400, """{"error":{"message":"Provider returned error","code":400,"metadata":{"raw":"{\"error\":{\"message\":\"context window exceeded\"}}","provider_name":"Upstream"}}}""")]
    public async Task AnExplicitContextLimitError_IsContextOverflow(int status, string body)
    {
        var failure = await FailureAsync((HttpStatusCode)status, body);

        Assert.Equal(ModelFailureKind.ContextOverflow, failure.FailureKind);
    }

    [Theory]
    [InlineData(429, """{"error":{"message":"You exceeded your current quota","type":"insufficient_quota","code":"insufficient_quota"}}""")]
    [InlineData(403, """{"error":{"message":"billing hard limit","code":"billing_hard_limit_reached"}}""")]
    public async Task AnExplicitQuotaCode_IsQuotaExceeded_EvenOnA429OrA403(int status, string body)
    {
        var failure = await FailureAsync((HttpStatusCode)status, body);

        Assert.Equal(ModelFailureKind.QuotaExceeded, failure.FailureKind);
    }

    [Fact]
    public async Task RetryAfterInSeconds_IsParsedForTheRuntime()
    {
        var failure = await FailureAsync(HttpStatusCode.TooManyRequests, "{}",
            response => response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2)));

        Assert.Equal(ModelFailureKind.RateLimited, failure.FailureKind);
        Assert.Equal(TimeSpan.FromSeconds(2), failure.RetryAfter);
    }

    [Fact]
    public async Task RetryAfterAsAnHttpDate_IsRelativeToTheResponseDate_AndNeverNegative()
    {
        var date = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);
        var later = await FailureAsync(HttpStatusCode.ServiceUnavailable, "", response =>
        {
            response.Headers.Date = date;
            response.Headers.RetryAfter = new RetryConditionHeaderValue(date.AddSeconds(7));
        });
        var earlier = await FailureAsync(HttpStatusCode.ServiceUnavailable, "", response =>
        {
            response.Headers.Date = date;
            response.Headers.RetryAfter = new RetryConditionHeaderValue(date.AddSeconds(-30));
        });

        Assert.Equal(TimeSpan.FromSeconds(7), later.RetryAfter);
        Assert.Equal(TimeSpan.Zero, earlier.RetryAfter);
    }

    [Fact]
    public async Task AnUnparseableRetryAfter_IsIgnored()
    {
        var failure = await FailureAsync(HttpStatusCode.TooManyRequests, "{}",
            response => response.Headers.TryAddWithoutValidation("Retry-After", "soon, maybe 99999999999999999999"));

        Assert.Null(failure.RetryAfter);
    }

    // H2-12
    [Theory]
    [InlineData("this is not json")]
    [InlineData("""{"model":"m","choices":[]}""")]
    [InlineData("""{"choices":[{"message":{"role":"assistant","content":"a"}}],"choices":[]}""")]
    public async Task AMalformedSuccessReply_IsMalformedResponse(string body)
    {
        var failure = await FailureAsync(HttpStatusCode.OK, body);

        Assert.Equal(ModelFailureKind.MalformedResponse, failure.FailureKind);
        Assert.Equal(200, failure.ProviderStatusCode);
    }

    [Fact]
    public async Task ASuccessStatusCarryingAnEmbeddedError_IsClassifiedByTheEmbeddedStatus()
    {
        var failure = await FailureAsync(HttpStatusCode.OK, """{"error":{"message":"Rate limit exceeded upstream","code":429}}""");

        Assert.Equal(ModelFailureKind.RateLimited, failure.FailureKind);
        Assert.Equal(429, failure.ProviderStatusCode);
    }

    // H2-13
    [Fact]
    public async Task AConnectionFailure_IsUnreachable_InOneAttempt()
    {
        var (model, handler, _) = Create(null,
            (_, _) => throw new HttpRequestException("No such host is known. (openrouter.invalid:443)"),
            Respond(HttpStatusCode.OK, TextReply));

        var failure = await Assert.ThrowsAsync<ModelProtocolException>(() => model.CompleteAsync(Request));

        Assert.Equal(ModelFailureKind.Unreachable, failure.FailureKind);
        Assert.Null(failure.ProviderStatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TheTransportTimeout_IsATimeoutFailure_NotACancellation()
    {
        var (model, handler, client) = Create(TimeSpan.FromMilliseconds(100), async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        });

        var failure = await Assert.ThrowsAsync<ModelProtocolException>(() => model.CompleteAsync(Request));

        Assert.Equal(TimeSpan.FromMilliseconds(100), client.Timeout);
        Assert.Equal(ModelFailureKind.Timeout, failure.FailureKind);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TheCallersCancellation_PropagatesAsCancellation()
    {
        using var cts = new CancellationTokenSource();
        var (model, _, _) = Create(null, async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.CompleteAsync(Request, cts.Token));
    }

    [Fact]
    public void TheExplicitTransportTimeout_ReplacesTheImplicitDefault()
    {
        var (_, _, client) = Create(null);

        Assert.Equal(ChatModelOptions.DefaultRequestTimeout, client.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(150), client.Timeout);
    }

    // H2-19 / packet acceptance: the incident body yields an actionable reason without the user id.
    [Fact]
    public async Task TheIncidentBody_YieldsTheUpstreamReasonAndProvider_WithoutTheUserIdOrTheKey()
    {
        var failure = await FailureAsync(HttpStatusCode.BadRequest, IncidentBody);

        Assert.Equal(ModelFailureKind.InvalidRequest, failure.FailureKind);
        Assert.Contains("Function at index 0 has an invalid name", failure.Message, StringComparison.Ordinal);
        Assert.Contains("fs.size", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Poolside", failure.Message, StringComparison.Ordinal);
        Assert.Contains("429×7", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("user_2xYzSyntheticIdentifier9", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("user_id", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PROMPT-CONTENT-MARKER", failure.Message, StringComparison.Ordinal);
        Assert.True(failure.Message.Length <= ProviderFailures.MaxReasonCharacters);
    }

    [Fact]
    public async Task KeyShapedTextInAProviderMessage_IsRedacted_AndTheReasonIsBounded()
    {
        var body = "{\"error\":{\"message\":\"Invalid key " + ApiKey +
                   "; header Authorization: Bearer abc.def.ghi; api_key=plainsecret; contact ops@example.com; " +
                   "ref 7a6b5c4d3e2f1a0b9c8d7e6f5a4b3c2d1e0f " + new string('z', 1500) + "\",\"code\":401}}";

        var failure = await FailureAsync(HttpStatusCode.Unauthorized, body);

        Assert.Equal(ModelFailureKind.Authentication, failure.FailureKind);
        Assert.Contains("Invalid key", failure.Message, StringComparison.Ordinal);
        foreach (var secret in new[] { ApiKey, "abc.def.ghi", "plainsecret", "ops@example.com", "7a6b5c4d3e2f1a0b9c8d7e6f5a4b3c2d1e0f" })
        {
            Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
        }

        Assert.True(failure.Message.Length <= ProviderFailures.MaxReasonCharacters);
        Assert.EndsWith("…", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADuplicateKeyErrorBody_ContributesNothing_ButTheFailureIsStillClassified()
    {
        var failure = await FailureAsync(HttpStatusCode.BadRequest, """{"error":{"message":"a","message":"b"}}""");

        Assert.Equal(ModelFailureKind.InvalidRequest, failure.FailureKind);
        Assert.Equal("Provider 'OpenRouter' returned HTTP 400.", failure.Message);
    }

    [Fact]
    public async Task TheFallbackStrategy_KeepsItsOneCorrectiveReask_ButNeverRetriesATransportFailure()
    {
        using var handler = new ScriptedHandler(
            Respond(HttpStatusCode.OK, """{"model":"m","choices":[{"message":{"role":"assistant","content":"not json"}}]}"""),
            Respond(HttpStatusCode.ServiceUnavailable, "overloaded"),
            Respond(HttpStatusCode.OK, TextReply));
        using var client = new HttpClient(handler, disposeHandler: false);
        var model = new OpenAiCompatibleChatModel(
            new ChatModelOptions("OpenRouter", "https://openrouter.ai/api/v1", null, "m", SupportsNativeToolCalling: false), client);

        var failure = await Assert.ThrowsAsync<ModelProtocolException>(() => model.CompleteAsync(Request));

        Assert.Equal(ModelFailureKind.Transient, failure.FailureKind);
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>Runs one scripted behaviour per HTTP request and counts them.</summary>
    private sealed class ScriptedHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            return steps[index](request, cancellationToken);
        }
    }
}
