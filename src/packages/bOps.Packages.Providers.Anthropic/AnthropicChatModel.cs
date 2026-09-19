// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Providers.Anthropic;

/// <summary>
/// The one native (non-OpenAI-compatible) <see cref="IChatModel"/> adapter (ADR-0005): Anthropic's
/// Messages API uses a different wire shape from every other provider this repository talks to —
/// a top-level <c>system</c> string instead of a system message, a tagged-union content-block
/// array instead of a flat <c>content</c> string, and <c>tool_use</c>/<c>tool_result</c> blocks
/// instead of OpenAI's <c>tool_calls</c>/<c>tool</c>-role messages — different enough that sharing
/// <c>OpenAiCompatibleChatModel</c> would mean bending that adapter around a second protocol
/// rather than translating a second protocol into the one shared <see cref="ModelResponse"/> shape.
///
/// Like <c>OpenAiCompatibleChatModel</c>, honors <see cref="ChatModelOptions.SupportsNativeToolCalling"/>
/// with the same JSON-schema-in-prompt fallback strategy (plan §3.1.1) when it is false.
/// </summary>
public sealed class AnthropicChatModel(ChatModelOptions options, HttpClient httpClient) : IChatModel
{
    /// <summary>
    /// Anthropic's Messages API requires <c>max_tokens</c> on every request; <see cref="ChatModelOptions"/>
    /// has no such field (it is a provider-agnostic contract in <c>bOps.Abstractions</c>, which stays
    /// dependency- and provider-detail-free — adding an Anthropic-specific field there would need its
    /// own ADR). A generous fixed budget, not configurable yet.
    /// </summary>
    private const int MaxTokens = 4096;

    private const string AnthropicVersion = "2023-06-01";

    /// <inheritdoc />
    public ChatModelDescriptor Descriptor { get; } = new(options.Provider, options.Model);

    /// <inheritdoc />
    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return options.SupportsNativeToolCalling
            ? CompleteNativeAsync(request, ct)
            : CompleteWithFallbackAsync(request, ct);
    }

    private async Task<ModelResponse> CompleteNativeAsync(ModelRequest request, CancellationToken ct)
    {
        var payload = new MessagesRequest
        {
            Model = options.Model,
            MaxTokens = MaxTokens,
            System = request.SystemPrompt,
            Messages = BuildMessages(request.History),
            Tools = request.AvailableTools.Count == 0 ? null : request.AvailableTools.Select(BuildToolDefinition).ToList(),
        };

        var completion = await SendAsync(payload, ct);
        var response = completion.Body;
        var toolCalls = response.Content
            .Where(block => block.Type == "tool_use")
            .Select(block => new ModelToolCall(block.Id!, block.Name!, ToolArguments.FromJson(block.Input ?? new JsonObject())))
            .ToList();
        var text = string.Concat(response.Content.Where(block => block.Type == "text").Select(block => block.Text));

        return new ModelResponse(
            string.IsNullOrEmpty(text) ? null : text, toolCalls, toolCalls.Count == 0, MapUsage(response.Usage))
        {
            Details = completion.Details,
        };
    }

    private async Task<ModelResponse> CompleteWithFallbackAsync(ModelRequest request, CancellationToken ct)
    {
        var system = BuildFallbackSystemPrompt(request);
        var messages = BuildMessages(request.History);
        ModelCallDetails? lastDetails = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var payload = new MessagesRequest { Model = options.Model, MaxTokens = MaxTokens, System = system, Messages = messages };
            var completion = await SendAsync(payload, ct);
            var response = completion.Body;
            var text = string.Concat(response.Content.Where(block => block.Type == "text").Select(block => block.Text));
            lastDetails = completion.Details;

            if (TryParseFallbackJson(text, out var parsed))
            {
                var usage = MapUsage(response.Usage);
                return parsed.IsFinal
                    ? new ModelResponse(parsed.FinalText, [], true, usage) { Details = completion.Details }
                    : new ModelResponse(null, [new ModelToolCall(Guid.NewGuid().ToString("N"), parsed.ToolName!, parsed.Arguments!)], false, usage)
                    {
                        Details = completion.Details,
                    };
            }

            messages.Add(new AnthropicMessageDto { Role = "assistant", Content = [new ContentBlockDto { Type = "text", Text = text }] });
            messages.Add(new AnthropicMessageDto
            {
                Role = "user",
                Content =
                [
                    new ContentBlockDto
                    {
                        Type = "text",
                        Text = "Your last reply was not valid JSON matching the required schema. Reply again with " +
                               "ONLY a JSON object: {\"tool\": \"...\", \"arguments\": {...}} or {\"final\": \"...\"}.",
                    },
                ],
            });
        }

        throw new ModelProtocolException(
            $"Provider '{options.Provider}' did not return valid JSON tool-call output after one retry.")
        {
            Details = lastDetails,
        };
    }

    /// <summary>A reply that parsed, with what was sent and received so the call can be understood afterwards.</summary>
    private sealed record Completion(MessagesResponse Body, ModelCallDetails Details);

    private async Task<Completion> SendAsync(MessagesRequest payload, CancellationToken ct)
    {
        var requestJson = JsonSerializer.Serialize(payload, AnthropicJsonContext.Default.MessagesRequest);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/v1/messages")
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
            };
            httpRequest.Headers.Add("anthropic-version", AnthropicVersion);
            if (!string.IsNullOrEmpty(options.ResolvedApiKey))
            {
                httpRequest.Headers.Add("x-api-key", options.ResolvedApiKey);
            }

            HttpResponseMessage httpResponse;
            try
            {
                httpResponse = await httpClient.SendAsync(httpRequest, ct);
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new ModelProtocolException($"Provider '{options.Provider}' could not be reached: {ex.Message}", ex);
            }

            using (httpResponse)
            {
                if (IsTransient(httpResponse.StatusCode) && attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct);
                    continue;
                }

                var rawBody = await httpResponse.Content.ReadAsStringAsync(ct);
                var failedDetails = new ModelCallDetails(null, null, requestJson, rawBody);

                if (!httpResponse.IsSuccessStatusCode)
                {
                    throw new ModelProtocolException(
                        $"Provider '{options.Provider}' returned HTTP {(int)httpResponse.StatusCode} " +
                        $"({httpResponse.StatusCode}) for the messages request.")
                    {
                        Details = failedDetails,
                    };
                }

                MessagesResponse? body;
                try
                {
                    body = JsonSerializer.Deserialize(rawBody, AnthropicJsonContext.Default.MessagesResponse);
                }
                catch (JsonException ex)
                {
                    throw new ModelProtocolException(
                        $"Provider '{options.Provider}' returned a response that did not match the expected schema.", ex)
                    {
                        Details = failedDetails,
                    };
                }

                if (body is null)
                {
                    throw new ModelProtocolException($"Provider '{options.Provider}' returned an empty response body.")
                    {
                        Details = failedDetails,
                    };
                }

                return new Completion(body, new ModelCallDetails(body.Model, body.StopReason, requestJson, rawBody));
            }
        }

        throw new ModelProtocolException($"Provider '{options.Provider}' exhausted its bounded transient retry budget.");
    }

    private static bool IsTransient(System.Net.HttpStatusCode statusCode) =>
        statusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests or
            System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable or
            System.Net.HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// Maps <paramref name="history"/> into Anthropic messages, merging consecutive turns that map
    /// to the same Anthropic role into one message with several content blocks — required because
    /// the Messages API rejects two consecutive messages with the same role, but the runtime's
    /// agent loop (agentic/01-architecture-rules.md, rule D-007) can append several consecutive
    /// <see cref="ChatRole.Tool"/> turns in a row (the executed call's result, then one
    /// "not executed" turn per tool call the model proposed but the runtime did not run).
    /// </summary>
    private static List<AnthropicMessageDto> BuildMessages(IReadOnlyList<ChatTurn> history)
    {
        var messages = new List<AnthropicMessageDto>();

        foreach (var turn in history)
        {
            var role = MapRole(turn.Role);
            var blocks = BuildContentBlocks(turn);

            if (messages.Count > 0 && messages[^1].Role == role)
            {
                messages[^1].Content.AddRange(blocks);
            }
            else
            {
                messages.Add(new AnthropicMessageDto { Role = role, Content = blocks });
            }
        }

        return messages;
    }

    private static List<ContentBlockDto> BuildContentBlocks(ChatTurn turn)
    {
        if (turn.Role == ChatRole.Tool)
        {
            return [new ContentBlockDto { Type = "tool_result", ToolUseId = turn.ToolCallId, ResultContent = turn.Content ?? string.Empty }];
        }

        var blocks = new List<ContentBlockDto>();
        if (!string.IsNullOrEmpty(turn.Content))
        {
            blocks.Add(new ContentBlockDto { Type = "text", Text = turn.Content });
        }

        if (turn.ToolCalls is { Count: > 0 })
        {
            blocks.AddRange(turn.ToolCalls.Select(call =>
                new ContentBlockDto { Type = "tool_use", Id = call.Id, Name = call.ToolName, Input = call.Arguments.ToJson() }));
        }

        // A message needs at least one content block — an assistant turn with neither text nor
        // tool calls should not occur in practice (rule C1's failure paths always carry an error
        // message as text), but an empty text block is a safe, valid fallback rather than sending
        // a malformed request.
        return blocks.Count == 0 ? [new ContentBlockDto { Type = "text", Text = string.Empty }] : blocks;
    }

    /// <summary>Anthropic has no <c>"tool"</c> role — a tool's result is sent as a <c>"tool_result"</c> content block inside a <c>"user"</c> message.</summary>
    private static string MapRole(ChatRole role) => role switch
    {
        ChatRole.User or ChatRole.Tool => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.System => throw new ArgumentOutOfRangeException(
            nameof(role), role, "A System turn should never appear in ModelRequest.History — it belongs in ModelRequest.SystemPrompt."),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown chat role."),
    };

    private static ToolDto BuildToolDefinition(ToolManifest manifest) =>
        new() { Name = manifest.Name, Description = manifest.Description, InputSchema = BuildParameterSchema(manifest) };

    private static JsonObject BuildParameterSchema(ToolManifest manifest)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var parameter in manifest.Parameters)
        {
            properties[parameter.Name] = BuildParameterSchema(parameter);
            if (parameter.Required)
            {
                required.Add(parameter.Name);
            }
        }

        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    private static JsonObject BuildParameterSchema(ToolParameter parameter)
    {
        if (parameter.Type == ToolParameterType.PathList)
        {
            return new JsonObject
            {
                ["type"] = "array",
                ["description"] = parameter.Description,
                ["items"] = new JsonObject { ["type"] = "string" },
            };
        }

        var schema = new JsonObject { ["type"] = MapJsonSchemaType(parameter.Type), ["description"] = parameter.Description };

        if (parameter.AllowedValues is { Count: > 0 })
        {
            schema["enum"] = new JsonArray(parameter.AllowedValues.Select(value => (JsonNode)value).ToArray());
        }

        return schema;
    }

    private static string MapJsonSchemaType(ToolParameterType type) => type switch
    {
        ToolParameterType.Integer => "integer",
        ToolParameterType.Number => "number",
        ToolParameterType.Boolean => "boolean",
        ToolParameterType.String or ToolParameterType.Path or ToolParameterType.Duration or ToolParameterType.Enum => "string",
        ToolParameterType.PathList => "array",
        _ => "string",
    };

    private static string BuildFallbackSystemPrompt(ModelRequest request)
    {
        var toolsArray = new JsonArray();
        foreach (var manifest in request.AvailableTools)
        {
            toolsArray.Add(new JsonObject
            {
                ["name"] = manifest.Name,
                ["description"] = manifest.Description,
                ["parameters"] = BuildParameterSchema(manifest),
            });
        }

        const string instructions =
            """
            Respond with ONLY a single JSON object and no other text:
            {"tool": "<tool name>", "arguments": {...}}
            or, if the goal is already achieved:
            {"final": "<your final answer>"}
            """;

        return $"{request.SystemPrompt}\n\n" +
               $"You do not have native tool calling in this session. Available tools, as JSON:\n" +
               $"{toolsArray.ToJsonString()}\n\n{instructions}";
    }

    private static bool TryParseFallbackJson(string text, out FallbackParseResult result)
    {
        try
        {
            if (JsonNode.Parse(text.Trim()) is not JsonObject node)
            {
                result = FallbackParseResult.None;
                return false;
            }

            if (node.TryGetPropertyValue("final", out var finalNode) && finalNode is not null)
            {
                result = new FallbackParseResult(true, finalNode.GetValue<string>(), null, null);
                return true;
            }

            if (node.TryGetPropertyValue("tool", out var toolNode) && toolNode is not null)
            {
                var argumentsNode = node.TryGetPropertyValue("arguments", out var argsNode) ? argsNode as JsonObject : null;
                result = new FallbackParseResult(false, null, toolNode.GetValue<string>(), ToolArguments.FromJson(argumentsNode ?? new JsonObject()));
                return true;
            }

            result = FallbackParseResult.None;
            return false;
        }
        catch (JsonException)
        {
            result = FallbackParseResult.None;
            return false;
        }
    }

    private static ModelUsage? MapUsage(AnthropicUsageDto? usage) =>
        usage is null ? null : new ModelUsage(usage.InputTokens, usage.OutputTokens, null);

    private sealed record FallbackParseResult(bool IsFinal, string? FinalText, string? ToolName, ToolArguments? Arguments)
    {
        public static FallbackParseResult None { get; } = new(false, null, null, null);
    }
}
