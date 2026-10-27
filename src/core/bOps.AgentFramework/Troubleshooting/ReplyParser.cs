// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>
/// Reads the JSON object an agent ends its reply with. A model reply is untrusted data: it is parsed leniently for shape, every text is
/// bounded, and what cannot be read is reported as unreadable rather than repaired. Nothing parsed here grants anything.
/// </summary>
internal static class ReplyParser
{
    internal const int MaxTextLength = 2_000;
    internal const int MaxItems = 20;

    /// <summary>The first JSON object in <paramref name="text"/>, or <c>null</c> when there is none that parses.</summary>
    internal static JsonObject? ExtractObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text[start..(end + 1)]) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>A bounded string member, or an empty string.</summary>
    internal static string Text(JsonObject source, string name) =>
        source[name] is JsonValue value && value.TryGetValue<string>(out var text) ? Bound(text) : string.Empty;

    /// <summary>A bounded list of strings from an array member (anything that is not a string is skipped).</summary>
    internal static IReadOnlyList<string> Strings(JsonObject source, string name) =>
        source[name] is JsonArray array
            ? [.. array.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? Bound(text) : null)
                .Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text!).Take(MaxItems)]
            : [];

    /// <summary>The objects of an array member (anything that is not an object is skipped).</summary>
    internal static IReadOnlyList<JsonObject> Objects(JsonObject source, string name) =>
        source[name] is JsonArray array ? [.. array.OfType<JsonObject>().Take(MaxItems)] : [];

    /// <summary>The claims of an array member: <c>{"text": "...", "evidence": ["id", ...]}</c>, or a bare string (a claim citing nothing).</summary>
    internal static IReadOnlyList<WorkflowMessages.Claim> Claims(JsonObject source, string name)
    {
        if (source[name] is not JsonArray array)
        {
            return [];
        }

        var claims = new List<WorkflowMessages.Claim>();
        foreach (var item in array.Take(MaxItems))
        {
            switch (item)
            {
                case JsonObject claim when Text(claim, "text") is { Length: > 0 } text:
                    claims.Add(new WorkflowMessages.Claim(text, Strings(claim, "evidence")));
                    break;
                case JsonValue value when value.TryGetValue<string>(out var bare) && !string.IsNullOrWhiteSpace(bare):
                    claims.Add(new WorkflowMessages.Claim(Bound(bare), []));
                    break;
            }
        }

        return claims;
    }

    internal static string Bound(string text) =>
        text.Length <= MaxTextLength ? text.Trim() : text[..MaxTextLength].Trim() + "…";
}
