// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using bOps.Abstractions;

namespace bOps.Packages.Providers.Wire;

/// <summary>
/// The safe, bounded facts one adapter extracted from a provider's error body (ADR-0039 §6): an error code or type when the
/// provider gave one, the provider's own message, and a <see cref="Summary"/> fit for an operator. Each adapter builds this
/// from its own body format; nothing else about the body leaves the adapter.
/// </summary>
/// <param name="Code">The provider's structured error code or type, for classification only.</param>
/// <param name="SemanticMessage">The provider's message text (including any nested upstream message), for classification only.</param>
/// <param name="Summary">What the operator is shown, before redaction and bounding.</param>
internal sealed record ProviderError(string? Code, string? SemanticMessage, string? Summary)
{
    public static ProviderError None { get; } = new(null, null, null);
}

/// <summary>
/// How every first-party adapter turns a failed provider exchange into a classified <see cref="ModelProtocolException"/>
/// (ADR-0039): one attempt per call, a provider-neutral <see cref="ModelFailureKind"/>, a bounded <c>Retry-After</c>, and a
/// reason that is redacted and bounded before it leaves the adapter. Owned by the OpenAI-compatible package and linked, as
/// this one file, into the Anthropic package, exactly like <see cref="ToolWireNames"/> and <see cref="StrictJson"/>. Internal:
/// the sanitizing algorithm is not a public contract.
/// </summary>
internal static partial class ProviderFailures
{
    /// <summary>The longest safe reason kept, in characters.</summary>
    public const int MaxReasonCharacters = 500;

    private const string Redacted = "[redacted]";

    private static readonly string[] QuotaCodes = ["insufficient_quota", "quota_exceeded", "billing_hard_limit_reached", "billing_error"];

    private static readonly string[] ContextCodes = ["context_length_exceeded"];

    /// <summary>
    /// The provider-neutral kind of a failed HTTP exchange. A structured quota code wins for any client error; explicit
    /// context-limit evidence (a code or the provider's message) turns a client error other than 401/403/408/429 into
    /// <see cref="ModelFailureKind.ContextOverflow"/>; a 413 without it is an ordinary invalid request.
    /// </summary>
    public static ModelFailureKind Classify(int status, string? code, string? semanticMessage)
    {
        var clientError = status is >= 400 and < 500;
        if (clientError && Matches(code, QuotaCodes))
        {
            return ModelFailureKind.QuotaExceeded;
        }

        if (clientError && status is not (401 or 403 or 408 or 429)
            && (Matches(code, ContextCodes) || (semanticMessage is not null && ContextLimitPattern().IsMatch(semanticMessage))))
        {
            return ModelFailureKind.ContextOverflow;
        }

        return status switch
        {
            401 or 403 => ModelFailureKind.Authentication,
            402 => ModelFailureKind.QuotaExceeded,
            408 or 504 => ModelFailureKind.Timeout,
            429 => ModelFailureKind.RateLimited,
            500 or 502 or 503 or 529 => ModelFailureKind.Transient,
            >= 400 and < 500 => ModelFailureKind.InvalidRequest,
            _ => ModelFailureKind.Unknown,
        };
    }

    /// <summary>
    /// The wait a provider asked for in <c>Retry-After</c> — delta-seconds, or an HTTP date relative to the response's own
    /// <c>Date</c> (else now) — never negative; <c>null</c> when absent or unparseable. The runtime decides whether to honour it.
    /// </summary>
    public static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var header = response.Headers.RetryAfter;
        TimeSpan? wait = header switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - (response.Headers.Date ?? DateTimeOffset.UtcNow),
            _ => null,
        };

        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    /// <summary>A non-success HTTP response, classified, with its safe reason, status and bounded <c>Retry-After</c>.</summary>
    public static ModelProtocolException HttpFailure(
        string provider, HttpResponseMessage response, ProviderError error, ModelCallDetails details)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(error);
        return StatusFailure(provider, (int)response.StatusCode, error, details, ReadRetryAfter(response));
    }

    /// <summary>A failure the provider reported with a status code (in an HTTP status, or embedded in a reply body).</summary>
    public static ModelProtocolException StatusFailure(
        string provider, int status, ProviderError error, ModelCallDetails details, TimeSpan? retryAfter = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        var reason = string.IsNullOrWhiteSpace(error.Summary)
            ? $"Provider '{provider}' returned HTTP {status}."
            : $"Provider '{provider}' returned HTTP {status}: {error.Summary}";
        return new ModelProtocolException(Sanitize(reason))
        {
            FailureKind = Classify(status, error.Code, error.SemanticMessage),
            ProviderStatusCode = status,
            RetryAfter = retryAfter,
            Details = details,
        };
    }

    /// <summary>No response was obtained at all: DNS, socket, connection or TLS failure.</summary>
    public static ModelProtocolException Unreachable(string provider, HttpRequestException exception, ModelCallDetails details)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new ModelProtocolException(Sanitize($"Provider '{provider}' could not be reached: {exception.Message}"), exception)
        {
            FailureKind = ModelFailureKind.Unreachable,
            Details = details,
        };
    }

    /// <summary>A response was started but its body could not be read to the end (for example the connection reset).</summary>
    public static ModelProtocolException Interrupted(string provider, int status, Exception exception, ModelCallDetails details)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new ModelProtocolException(Sanitize($"Provider '{provider}' reply was interrupted: {exception.Message}"), exception)
        {
            FailureKind = ModelFailureKind.Transient,
            ProviderStatusCode = status,
            Details = details,
        };
    }

    /// <summary>The adapter's outer transport timeout fired while the caller had not cancelled.</summary>
    public static ModelProtocolException TransportTimeout(string provider, TimeSpan timeout, Exception exception, ModelCallDetails details) =>
        new($"Provider '{provider}' did not answer within the {timeout.TotalSeconds:0.###} s transport timeout.", exception)
        {
            FailureKind = ModelFailureKind.Timeout,
            Details = details,
        };

    /// <summary>A reply was obtained but is not a usable model reply.</summary>
    public static ModelProtocolException Malformed(string provider, string what, int status, ModelCallDetails? details, Exception? inner = null)
    {
        var message = Sanitize($"Provider '{provider}' {what}");
        return new ModelProtocolException(message, inner ?? new FormatException(message))
        {
            FailureKind = ModelFailureKind.MalformedResponse,
            ProviderStatusCode = status,
            Details = details,
        };
    }

    /// <summary>
    /// Redacts credential- and identifier-shaped text (bearer tokens, key-shaped values, <c>key=value</c> secrets, user ids,
    /// opaque tokens mixing letters and digits, e-mail addresses), collapses whitespace and bounds the text to
    /// <see cref="MaxReasonCharacters"/>.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var redacted = BearerPattern().Replace(text, $"Bearer {Redacted}");
        redacted = KeyValuePattern().Replace(redacted, match => $"{match.Groups["name"].Value}={Redacted}");
        redacted = ApiKeyPattern().Replace(redacted, Redacted);
        redacted = EmailPattern().Replace(redacted, Redacted);
        redacted = OpaqueTokenPattern().Replace(redacted, match => IsOpaqueToken(match.Value) ? Redacted : match.Value);
        redacted = WhitespacePattern().Replace(redacted, " ").Trim();
        return Bound(redacted, MaxReasonCharacters);
    }

    /// <summary>Cuts <paramref name="text"/> to at most <paramref name="max"/> characters, marking the cut and never splitting a surrogate pair.</summary>
    public static string Bound(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= max)
        {
            return text;
        }

        var keep = max - 1;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return string.Concat(text.AsSpan(0, keep), "…");
    }

    /// <summary>Joins the non-empty parts of a summary with a separator.</summary>
    public static string? Join(params string?[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(" — ");
            }

            builder.Append(part.Trim());
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static bool Matches(string? code, string[] candidates) =>
        code is not null && candidates.Any(candidate => code.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    // An opaque token must mix letters and digits: a long plain word or a long number is left alone.
    private static bool IsOpaqueToken(string value) => value.Any(char.IsLetter) && value.Any(char.IsDigit);

    [GeneratedRegex(
        @"(?i)context[\s_-]?(?:length|window)|maximum context|prompt is too long|too many (?:input |prompt )?tokens|token limit")]
    private static partial Regex ContextLimitPattern();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=\-]+")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(
        @"(?i)(?<name>\b(?:x-api-key|api[_-]?key|access[_-]?token|refresh[_-]?token|token|secret|password|authorization|user[_-]?id)\b)[""']?\s*[:=]\s*[""']?[^\s""',;}]+")]
    private static partial Regex KeyValuePattern();

    [GeneratedRegex(@"\b(?:sk|pk|rk)[-_][A-Za-z0-9_\-]{8,}")]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_\-])[A-Za-z0-9_\-]{32,}(?![A-Za-z0-9_\-])")]
    private static partial Regex OpaqueTokenPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
