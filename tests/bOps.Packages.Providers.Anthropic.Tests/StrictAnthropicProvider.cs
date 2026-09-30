// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace bOps.Packages.Providers.Anthropic.Tests;

/// <summary>
/// A deterministic stand-in for the Anthropic Messages API tool contract (ADR-0038; reused by later HARDEN packets and the
/// composition gate). It answers <c>400 invalid_request_error</c> when a tool or <c>tool_use</c> name is outside
/// <c>^[a-zA-Z0-9_-]{1,64}$</c>, when a <c>tool_use</c> is not answered by a <c>tool_result</c> in the very next user message
/// (or a <c>tool_result</c> answers nothing), when a member is an explicit <c>null</c>, or when a native tool is offered where
/// none may be (a plan or replan call). Otherwise it replays the scripted replies in order.
/// </summary>
internal sealed partial class StrictAnthropicProvider(params string[] replies) : HttpMessageHandler
{
    private int _next;

    /// <summary>When set, a <c>tools</c> member is a violation (plan and replan calls).</summary>
    public bool ForbidNativeTools { get; init; }

    public List<string> RequestBodies { get; } = [];

    public int Rejections { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        RequestBodies.Add(body);

        if (Validate(body, ForbidNativeTools) is { } violation)
        {
            Rejections++;
            var rejection = JsonSerializer.Serialize(new { type = "error", error = new { type = "invalid_request_error", message = violation } });
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(rejection, Encoding.UTF8, "application/json") };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(replies[_next++], Encoding.UTF8, "application/json") };
    }

    /// <summary>The first contract violation in <paramref name="requestJson"/>, or <c>null</c> when the API would accept it.</summary>
    public static string? Validate(string requestJson, bool forbidNativeTools = false)
    {
        using var document = JsonDocument.Parse(requestJson);
        var root = document.RootElement;

        if (FindNull(root, "$") is { } nullPath)
        {
            return $"{nullPath}: Input should be a valid value, not null.";
        }

        if (forbidNativeTools && root.TryGetProperty("tools", out _))
        {
            return "A request with no executable tools must not carry 'tools'.";
        }

        if (root.TryGetProperty("tools", out var tools))
        {
            var index = 0;
            foreach (var tool in tools.EnumerateArray())
            {
                var name = tool.GetProperty("name").GetString() ?? string.Empty;
                if (!WireName().IsMatch(name))
                {
                    return $"tools.{index}.name: String should match pattern '^[a-zA-Z0-9_-]{{1,64}}$'";
                }

                index++;
            }
        }

        var pending = new List<string>();
        var messageIndex = 0;
        foreach (var message in root.GetProperty("messages").EnumerateArray())
        {
            var role = message.GetProperty("role").GetString();
            var blocks = message.GetProperty("content").EnumerateArray().ToList();
            var seenNonResult = false;

            for (var b = 0; b < blocks.Count; b++)
            {
                var type = blocks[b].GetProperty("type").GetString();
                if (type == "tool_result")
                {
                    var id = blocks[b].GetProperty("tool_use_id").GetString();
                    if (role != "user" || seenNonResult || id is null || !pending.Remove(id))
                    {
                        return $"messages.{messageIndex}.content.{b}: unexpected `tool_use_id` '{id}' in `tool_result` block: " +
                               "each tool_result block must have a corresponding tool_use block in the previous message.";
                    }

                    continue;
                }

                seenNonResult = true;
            }

            if (pending.Count > 0)
            {
                return $"messages.{messageIndex}: `tool_use` ids {string.Join(", ", pending)} were found without `tool_result` blocks immediately after.";
            }

            foreach (var (block, b) in blocks.Select((block, b) => (block, b)))
            {
                if (block.GetProperty("type").GetString() != "tool_use")
                {
                    continue;
                }

                var name = block.GetProperty("name").GetString() ?? string.Empty;
                var id = block.GetProperty("id").GetString() ?? string.Empty;
                if (role != "assistant" || !WireName().IsMatch(name) || !WireName().IsMatch(id) || pending.Contains(id))
                {
                    return $"messages.{messageIndex}.content.{b}: invalid `tool_use` block (name '{name}', id '{id}').";
                }

                if (block.GetProperty("input").ValueKind != JsonValueKind.Object)
                {
                    return $"messages.{messageIndex}.content.{b}.input: Input should be an object.";
                }

                pending.Add(id);
            }

            messageIndex++;
        }

        return pending.Count > 0 ? $"The last message ends with unanswered `tool_use` ids {string.Join(", ", pending)}." : null;
    }

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
