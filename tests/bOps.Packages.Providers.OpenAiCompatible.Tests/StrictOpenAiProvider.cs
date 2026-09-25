// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace bOps.Packages.Providers.OpenAiCompatible.Tests;

/// <summary>
/// A deterministic stand-in for a strict OpenAI Chat Completions upstream (ADR-0038; reused by later HARDEN packets and
/// the composition gate). It answers 400 with a realistic body, like the upstream that rejected <c>fs.size</c>, when a
/// request has a function name outside <c>^[a-zA-Z0-9_-]{1,64}$</c>, a tool result whose id is not in the assistant turn
/// right before it, an assistant tool call that is never answered, an explicit <c>null</c> member, or a native tool where
/// none may be offered (a plan or replan call). Otherwise it replays the scripted replies in order.
/// </summary>
internal sealed partial class StrictOpenAiProvider(params string[] replies) : HttpMessageHandler
{
    private int _next;

    /// <summary>When set, any <c>tools</c> or <c>tool_choice</c> member is a violation (plan and replan calls).</summary>
    public bool ForbidNativeTools { get; init; }

    /// <summary>Decides per request body (for a run mixing plan and step calls) whether native tools are a violation.</summary>
    public Func<string, bool>? ForbidNativeToolsWhen { get; init; }

    public List<string> RequestBodies { get; } = [];

    public int Rejections { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        RequestBodies.Add(body);

        if (Validate(body, ForbidNativeTools || ForbidNativeToolsWhen?.Invoke(body) == true) is { } violation)
        {
            Rejections++;
            var raw = JsonSerializer.Serialize(new { error = new { code = 400, message = $"Validation: {violation}", type = "Bad Request" } });
            var rejection = JsonSerializer.Serialize(new
            {
                error = new { message = "Provider returned error", code = 400, metadata = new { raw, provider_name = "Poolside", is_byok = false } },
            });
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(rejection, Encoding.UTF8, "application/json") };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(replies[_next++], Encoding.UTF8, "application/json") };
    }

    /// <summary>The first contract violation in <paramref name="requestJson"/>, or <c>null</c> when a strict upstream would accept it.</summary>
    public static string? Validate(string requestJson, bool forbidNativeTools = false)
    {
        using var document = JsonDocument.Parse(requestJson);
        var root = document.RootElement;

        if (FindNull(root, "$") is { } nullPath)
        {
            return $"Invalid value: null is not allowed at {nullPath}.";
        }

        if (forbidNativeTools && (root.TryGetProperty("tools", out _) || root.TryGetProperty("tool_choice", out _)))
        {
            return "A request with no executable tools must not carry 'tools' or 'tool_choice'.";
        }

        if (root.TryGetProperty("tools", out var tools))
        {
            var index = 0;
            foreach (var tool in tools.EnumerateArray())
            {
                var name = tool.GetProperty("function").GetProperty("name").GetString() ?? string.Empty;
                if (!WireName().IsMatch(name))
                {
                    return InvalidFunctionName(index, name);
                }

                index++;
            }
        }

        var pending = new List<string>();
        var messageIndex = 0;
        foreach (var message in root.GetProperty("messages").EnumerateArray())
        {
            var role = message.GetProperty("role").GetString();

            if (role == "tool")
            {
                var id = message.TryGetProperty("tool_call_id", out var idElement) ? idElement.GetString() : null;
                if (id is null || !pending.Remove(id))
                {
                    return $"messages[{messageIndex}]: tool message references tool_call_id '{id}' which is not in the preceding assistant message.";
                }

                messageIndex++;
                continue;
            }

            if (pending.Count > 0)
            {
                return $"messages[{messageIndex}]: assistant tool_calls {string.Join(", ", pending)} were not answered by tool messages.";
            }

            if (role == "assistant" && message.TryGetProperty("tool_calls", out var calls))
            {
                var callIndex = 0;
                foreach (var call in calls.EnumerateArray())
                {
                    var id = call.GetProperty("id").GetString() ?? string.Empty;
                    var name = call.GetProperty("function").GetProperty("name").GetString() ?? string.Empty;
                    if (!WireName().IsMatch(name))
                    {
                        return $"messages[{messageIndex}].tool_calls[{callIndex}]: " + InvalidFunctionName(callIndex, name);
                    }

                    if (call.GetProperty("function").GetProperty("arguments").ValueKind != JsonValueKind.String)
                    {
                        return $"messages[{messageIndex}].tool_calls[{callIndex}]: arguments must be a string.";
                    }

                    if (id.Length == 0 || pending.Contains(id))
                    {
                        return $"messages[{messageIndex}].tool_calls[{callIndex}]: tool call ids must be unique and non-empty.";
                    }

                    pending.Add(id);
                    callIndex++;
                }
            }
            else if (role == "assistant" && !message.TryGetProperty("content", out _))
            {
                return $"messages[{messageIndex}]: an assistant message needs content or tool_calls.";
            }

            messageIndex++;
        }

        return pending.Count > 0
            ? $"The last assistant tool_calls {string.Join(", ", pending)} were never answered by tool messages."
            : null;
    }

    private static string InvalidFunctionName(int index, string name) =>
        $"Function at index {index} has an invalid name: \"{name}\". Only a-z, A-Z, 0-9, underscores, and dashes are allowed.";

    private static string? FindNull(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return path;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (FindNull(property.Value, $"{path}.{property.Name}") is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (FindNull(item, $"{path}[{i++}]") is { } found)
                    {
                        return found;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex WireName();
}
