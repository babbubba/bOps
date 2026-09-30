// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Packages.Providers.Wire;

namespace bOps.Packages.Providers.OpenAiCompatible;

/// <summary>
/// Reads the safe parts of an OpenAI-compatible error body (ADR-0039 §6). Handles the OpenAI shape
/// (<c>{"error":{"message","type","code"}}</c>), a router's nested upstream error (<c>error.metadata.raw</c>,
/// <c>error.metadata.provider_name</c>, <c>error.metadata.previous_errors</c>) and a plain <c>{"error":"text"}</c>. It reads
/// only those named fields: identifiers such as <c>user_id</c>, flags such as <c>is_byok</c>, and anything else in the body
/// are never extracted. A body that is not JSON contributes nothing.
/// </summary>
internal static class OpenAiErrorBody
{
    /// <summary>The safe facts in <paramref name="body"/>, or <see cref="ProviderError.None"/>.</summary>
    public static ProviderError Read(string? body)
    {
        if (TryParseObject(body) is not { } root)
        {
            return ProviderError.None;
        }

        if (Text(root["error"]) is { } plainError)
        {
            return new ProviderError(null, plainError, plainError);
        }

        if (root["error"] is not JsonObject error)
        {
            return ProviderError.None;
        }

        var message = Text(error["message"]);
        var code = Text(error["code"]);
        var type = Text(error["type"]);
        var metadata = error["metadata"] as JsonObject;
        var upstreamName = Text(metadata?["provider_name"]);
        var (upstreamMessage, upstreamCode) = ReadRaw(metadata?["raw"]);
        var previous = SummarizePrevious(metadata?["previous_errors"] as JsonArray);

        var upstream = upstreamMessage is null
            ? (upstreamName is null ? null : $"upstream {upstreamName}")
            : upstreamName is null ? $"upstream: {upstreamMessage}" : $"upstream {upstreamName}: {upstreamMessage}";
        var summary = ProviderFailures.Join(
            message,
            upstream,
            previous is null ? null : $"(previous upstream errors: {previous})");

        return new ProviderError(
            ProviderFailures.Join(code, type, upstreamCode),
            ProviderFailures.Join(message, upstreamMessage),
            summary);
    }

    /// <summary>
    /// The numeric <c>error.code</c> of a reply that carries an error object instead of choices (a router can answer a failed
    /// upstream call with a success status and the real status inside the body), or <c>null</c>.
    /// </summary>
    public static int? EmbeddedStatus(string? body) =>
        TryParseObject(body)?["error"] is JsonObject error
        && error["code"] is JsonValue code
        && code.GetValueKind() == JsonValueKind.Number
        && code.TryGetValue<int>(out var status)
        && status is >= 400 and < 600
            ? status
            : null;

    // metadata.raw is usually the upstream provider's own error body as a JSON string; otherwise it is plain text.
    private static (string? Message, string? Code) ReadRaw(JsonNode? raw)
    {
        var rawObject = raw as JsonObject;
        if (rawObject is null && Text(raw) is { } rawText)
        {
            rawObject = TryParseObject(rawText);
            if (rawObject is null)
            {
                return (rawText, null);
            }
        }

        if (rawObject is null)
        {
            return (null, null);
        }

        var inner = rawObject["error"] as JsonObject ?? rawObject;
        return (Text(inner["message"]) ?? Text(rawObject["error"]), ProviderFailures.Join(Text(inner["code"]), Text(inner["type"])));
    }

    private static string? SummarizePrevious(JsonArray? previous)
    {
        if (previous is null || previous.Count == 0)
        {
            return null;
        }

        var counts = previous
            .Select(entry => entry is JsonObject item ? Text(item["code"]) ?? "?" : "?")
            .GroupBy(code => code, StringComparer.Ordinal)
            .Select(group => $"{group.Key}×{group.Count().ToString(CultureInfo.InvariantCulture)}");
        return string.Join(", ", counts);
    }

    private static JsonObject? TryParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return StrictJson.Parse(text) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        var text = value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.ToJsonString(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
