// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// The runtime's own, provider-neutral last line of defence for failure text it records, audits or shows (ADR-0039 §6).
/// A provider package already extracts a safe reason; this does not parse any provider body — it only redacts
/// credential- and identifier-shaped text and bounds the length, because a third-party adapter may not sanitize.
/// Deliberately internal: the algorithm is not a public contract.
/// </summary>
internal static partial class ModelFailureText
{
    /// <summary>The longest safe reason kept, in characters.</summary>
    public const int MaxReasonCharacters = 500;

    private const string Redacted = "[redacted]";

    /// <summary>Redacts secret- and identifier-shaped text, collapses whitespace and bounds the result to <see cref="MaxReasonCharacters"/>.</summary>
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

    /// <summary>Whether the generic model-call retry may try a failure of this kind again.</summary>
    public static bool IsRetryable(ModelFailureKind kind) =>
        kind is ModelFailureKind.Transient or ModelFailureKind.RateLimited or ModelFailureKind.Timeout or ModelFailureKind.Unreachable;

    /// <summary>The provider-neutral sentence an operator sees first for a terminal failure of this kind.</summary>
    public static string OperatorReason(ModelFailureKind kind) => kind switch
    {
        ModelFailureKind.Authentication => "Provider authentication failed. Check the configured provider credential.",
        ModelFailureKind.QuotaExceeded => "Provider account quota or credit is unavailable.",
        ModelFailureKind.RateLimited => "Provider rate limit exceeded.",
        ModelFailureKind.Timeout => "Provider/model call timed out.",
        ModelFailureKind.Unreachable => "Provider could not be reached. Check the provider URL and the network.",
        ModelFailureKind.Transient => "Provider is temporarily unavailable.",
        ModelFailureKind.InvalidRequest => "Provider rejected the model request.",
        ModelFailureKind.ContextOverflow => "Model context limit exceeded.",
        ModelFailureKind.MalformedResponse => "Provider/model returned a malformed response.",
        _ => "Model call failed unexpectedly.",
    };

    // An opaque token must mix letters and digits: a long plain word or a long number is left alone.
    private static bool IsOpaqueToken(string value) => value.Any(char.IsLetter) && value.Any(char.IsDigit);

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
