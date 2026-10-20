// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// Turns what an adapter observed about one reply into the minimum generation-limit telemetry (<see cref="ModelOutputObservation"/>).
/// It is a pure function of the reply and the limit bOps requested: it reads no provider configuration, invents no parameter and
/// carries no reply, reasoning or request text. A reply's classification never changes how the runtime treats it; it only says why.
/// </summary>
internal static class ModelOutputClassifier
{
    /// <summary>The longest finish reason kept; a provider's value is bounded and reduced to a closed alphabet before it is recorded.</summary>
    internal const int MaxFinishReasonCharacters = 32;

    /// <summary>Classifies a reply that was obtained.</summary>
    /// <param name="response">The reply, as the adapter returned it (before the broker set a cut reply's text aside).</param>
    /// <param name="requestedMaxOutputTokens">The output limit bOps requested for the attempt, or <c>null</c> when it requested none.</param>
    internal static ModelOutputObservation Observe(ModelResponse response, int? requestedMaxOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(response);
        var details = response.Details;
        var truncated = string.Equals(details?.FinishReason, "length", StringComparison.OrdinalIgnoreCase);
        var contentCharacters = details?.ContentCharacters ?? response.TextResponse?.Length ?? 0;
        var usableCalls = response.ToolCalls.Count(call => call.ToolNameError is null && call.ArgumentsError is null);
        var completionTokens = response.Usage?.CompletionTokens;

        ModelOutputClass outputClass;
        if (truncated)
        {
            outputClass = usableCalls > 0 ? ModelOutputClass.ToolCall
                : response.ToolCalls.Count > 0 ? ModelOutputClass.TruncatedToolCall
                : contentCharacters > 0 ? ModelOutputClass.TruncatedContent
                : ModelOutputClass.TruncatedBeforeContent;
        }
        else if (response.ToolCalls.Count > 0)
        {
            outputClass = ModelOutputClass.ToolCall;
        }
        else if (!string.IsNullOrWhiteSpace(response.TextResponse))
        {
            outputClass = string.Equals(details?.FinishReason, "content_filter", StringComparison.OrdinalIgnoreCase)
                ? ModelOutputClass.Other
                : ModelOutputClass.Complete;
        }
        else
        {
            outputClass = details?.FinishReason is null or "stop" or "tool_calls" ? ModelOutputClass.Empty : ModelOutputClass.Other;
        }

        return new ModelOutputObservation
        {
            RequestedMaxOutputTokens = requestedMaxOutputTokens,
            FinishReason = BoundFinishReason(details?.FinishReason),
            PromptTokens = response.Usage?.PromptTokens,
            CompletionTokens = completionTokens,
            ReasoningTokens = details?.ReasoningTokens,
            ReasoningCharacters = details?.ReasoningCharacters,
            ContentCharacters = contentCharacters,
            ToolCallCount = response.ToolCalls.Count,
            Truncated = truncated,
            Class = outputClass,
            LimitEvidence = !truncated ? ModelOutputLimitEvidence.NotTruncated
                : completionTokens is null ? ModelOutputLimitEvidence.Unknown
                : requestedMaxOutputTokens is null ? ModelOutputLimitEvidence.UnrequestedLimit
                : completionTokens >= requestedMaxOutputTokens ? ModelOutputLimitEvidence.RequestedLimitReached
                : ModelOutputLimitEvidence.BelowRequestedLimit,
        };
    }

    /// <summary>Describes an attempt that obtained no reply: what was requested, and the finish reason if the provider still said one.</summary>
    internal static ModelOutputObservation ObserveNoResponse(ModelCallDetails? details, int? requestedMaxOutputTokens) => new()
    {
        RequestedMaxOutputTokens = requestedMaxOutputTokens,
        FinishReason = BoundFinishReason(details?.FinishReason),
        Class = ModelOutputClass.NoResponse,
        LimitEvidence = ModelOutputLimitEvidence.NotTruncated,
    };

    /// <summary>A provider's finish reason reduced to at most <see cref="MaxFinishReasonCharacters"/> characters of <c>[A-Za-z0-9_.-]</c>.</summary>
    internal static string? BoundFinishReason(string? finishReason)
    {
        if (string.IsNullOrWhiteSpace(finishReason))
        {
            return null;
        }

        var span = finishReason.AsSpan().Trim();
        if (span.Length > MaxFinishReasonCharacters)
        {
            span = span[..MaxFinishReasonCharacters];
        }

        return string.Create(span.Length, span.ToString(), static (buffer, source) =>
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                var c = source[i];
                buffer[i] = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '.' or '-' ? c : '?';
            }
        });
    }
}
