// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;

namespace bOps.Packages.Providers.OpenAiCompatible;

/// <summary>
/// A single <see cref="IChatModel"/> adapter for every provider that speaks the OpenAI Chat
/// Completions schema — OpenRouter, Ollama, and llama.cpp all do, which is why one adapter
/// covers all three (plan §3.1) rather than one per provider.
///
/// When <see cref="ChatModelOptions.SupportsNativeToolCalling"/> is false, tool calling falls
/// back to a JSON-schema-in-prompt strategy (plan §3.1.1): the tool list is embedded in the
/// system prompt, the model is asked to reply with a single JSON object, and one retry is
/// allowed before the call fails outright — never executing a tool "guessed" from a fuzzy parse.
/// </summary>
public sealed class OpenAiCompatibleChatModel(ChatModelOptions options, HttpClient httpClient) : IChatModel
{
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
        var payload = new ChatCompletionRequest
        {
            Model = options.Model,
            Messages = BuildMessages(request),
            Tools = request.AvailableTools.Count == 0 ? null : request.AvailableTools.Select(BuildToolDefinition).ToList(),
            ToolChoice = request.AvailableTools.Count == 0 ? null : "auto",
        };

        var response = await SendAsync(payload, ct);
        var message = response.Choices[0].Message;
        var toolCalls = (message.ToolCalls ?? [])
            .Select(dto => new ModelToolCall(dto.Id, dto.Function.Name, ParseArguments(dto.Function.Arguments)))
            .ToList();

        return new ModelResponse(message.Content, toolCalls, toolCalls.Count == 0, MapUsage(response.Usage));
    }

    private async Task<ModelResponse> CompleteWithFallbackAsync(ModelRequest request, CancellationToken ct)
    {
        var messages = BuildMessages(request with { SystemPrompt = BuildFallbackSystemPrompt(request) });

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var payload = new ChatCompletionRequest { Model = options.Model, Messages = messages };
            var response = await SendAsync(payload, ct);
            var text = response.Choices[0].Message.Content ?? string.Empty;

            if (TryParseFallbackJson(text, out var parsed))
            {
                var usage = MapUsage(response.Usage);
                return parsed.IsFinal
                    ? new ModelResponse(parsed.FinalText, [], true, usage)
                    : new ModelResponse(null, [new ModelToolCall(Guid.NewGuid().ToString("N"), parsed.ToolName!, parsed.Arguments!)], false, usage);
            }

            messages.Add(new ChatMessageDto { Role = "assistant", Content = text });
            messages.Add(new ChatMessageDto
            {
                Role = "user",
                Content = "Your last reply was not valid JSON matching the required schema. Reply again with " +
                          "ONLY a JSON object: {\"tool\": \"...\", \"arguments\": {...}} or {\"final\": \"...\"}.",
            });
        }

        throw new ModelProtocolException(
            $"Provider '{options.Provider}' did not return valid JSON tool-call output after one retry.");
    }

    private async Task<ChatCompletionResponse> SendAsync(ChatCompletionRequest payload, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/chat/completions")
            {
                Content = JsonContent.Create(payload, OpenAiJsonContext.Default.ChatCompletionRequest),
            };
            if (!string.IsNullOrEmpty(options.ResolvedApiKey))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ResolvedApiKey);
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

                if (!httpResponse.IsSuccessStatusCode)
                {
                    throw new ModelProtocolException(
                        $"Provider '{options.Provider}' returned HTTP {(int)httpResponse.StatusCode} " +
                        $"({httpResponse.StatusCode}) for the chat completion request.");
                }

                try
                {
                    var body = await httpResponse.Content.ReadFromJsonAsync(OpenAiJsonContext.Default.ChatCompletionResponse, ct);
                    return body ?? throw new ModelProtocolException($"Provider '{options.Provider}' returned an empty response body.");
                }
                catch (JsonException ex)
                {
                    throw new ModelProtocolException(
                        $"Provider '{options.Provider}' returned a response that did not match the expected schema.", ex);
                }
            }
        }

        throw new ModelProtocolException($"Provider '{options.Provider}' exhausted its bounded transient retry budget.");
    }

    private static bool IsTransient(System.Net.HttpStatusCode statusCode) =>
        statusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests or
            System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable or
            System.Net.HttpStatusCode.GatewayTimeout;

    private static List<ChatMessageDto> BuildMessages(ModelRequest request)
    {
        var messages = new List<ChatMessageDto> { new() { Role = "system", Content = request.SystemPrompt } };

        foreach (var turn in request.History)
        {
            messages.Add(new ChatMessageDto
            {
                Role = MapRole(turn.Role),
                Content = turn.Content,
                ToolCalls = turn.ToolCalls?.Select(MapToolCall).ToList(),
                ToolCallId = turn.ToolCallId,
            });
        }

        return messages;
    }

    private static string MapRole(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown chat role."),
    };

    private static ToolCallDto MapToolCall(ModelToolCall call) => new()
    {
        Id = call.Id,
        Function = new FunctionCallDto { Name = call.ToolName, Arguments = call.Arguments.ToJson().ToJsonString() },
    };

    private static ToolDefinitionDto BuildToolDefinition(ToolManifest manifest) => new()
    {
        Function = new FunctionDefinitionDto
        {
            Name = manifest.Name,
            Description = manifest.Description,
            Parameters = BuildParameterSchema(manifest),
        },
    };

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

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        };
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

        var schema = new JsonObject
        {
            ["type"] = MapJsonSchemaType(parameter.Type),
            ["description"] = parameter.Description,
        };

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

    private static ToolArguments ParseArguments(string json)
    {
        try
        {
            return ToolArguments.FromJson(JsonNode.Parse(json) as JsonObject ?? new JsonObject());
        }
        catch (JsonException)
        {
            return ToolArguments.Empty;
        }
    }

    private static ModelUsage? MapUsage(UsageDto? usage) =>
        usage is null ? null : new ModelUsage(usage.PromptTokens, usage.CompletionTokens, null);

    private sealed record FallbackParseResult(bool IsFinal, string? FinalText, string? ToolName, ToolArguments? Arguments)
    {
        public static FallbackParseResult None { get; } = new(false, null, null, null);
    }
}
