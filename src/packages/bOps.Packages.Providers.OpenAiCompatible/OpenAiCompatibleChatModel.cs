// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Providers.Wire;

namespace bOps.Packages.Providers.OpenAiCompatible;

/// <summary>
/// A single <see cref="IChatModel"/> adapter for every provider that speaks the OpenAI Chat
/// Completions schema — OpenRouter, Ollama, and llama.cpp all do, which is why one adapter
/// covers all three (plan §3.1) rather than one per provider.
///
/// When <see cref="ChatModelOptions.SupportsNativeToolCalling"/> is false, tool calling falls
/// back to a JSON-schema-in-prompt strategy (plan §3.1.1): the tool list is embedded in the
/// system prompt, the model is asked to reply with a single JSON object, and one retry is
/// allowed before the call fails outright — never executing a tool "guessed" from a fuzzy parse. That corrective re-ask is
/// the one inseparable exchange inside one <see cref="CompleteAsync"/>; a transport failure is never retried here
/// (ADR-0039): each HTTP request is one attempt, and a failure is thrown classified for the runtime to decide.
/// </summary>
public sealed class OpenAiCompatibleChatModel(ChatModelOptions options, HttpClient httpClient) : IChatModel
{
    // ADR-0039: the explicit outer transport timeout replaces HttpClient's implicit 100 s; the runtime's own, shorter
    // attempt timeout normally fires first. Applied once, to the client this adapter is given, before any request.
    private readonly HttpClient _httpClient = WithRequestTimeout(httpClient, options);

    private static HttpClient WithRequestTimeout(HttpClient client, ChatModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        client.Timeout = options.EffectiveRequestTimeout;
        return client;
    }

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
        var wireNames = ToolWireNames.ForRequest(request, includeOfferedTools: true);
        var payload = new ChatCompletionRequest
        {
            Model = options.Model,
            Messages = BuildMessages(request, wireNames),
            Tools = request.AvailableTools.Count == 0 ? null : request.AvailableTools.Select(manifest => BuildToolDefinition(manifest, wireNames)).ToList(),
            ToolChoice = request.AvailableTools.Count == 0 ? null : "auto",
        };

        var completion = await SendAsync(payload, ct);
        var message = completion.Body.Choices[0].Message;
        var offered = request.AvailableTools.Select(manifest => manifest.Name).ToHashSet(StringComparer.Ordinal);
        var toolCalls = (message.ToolCalls ?? [])
            .Select(dto => MapResponseCall(dto, wireNames, offered))
            .ToList();

        return new ModelResponse(message.Content, toolCalls, toolCalls.Count == 0, MapUsage(completion.Body.Usage))
        {
            Details = completion.Details,
        };
    }

    private async Task<ModelResponse> CompleteWithFallbackAsync(ModelRequest request, CancellationToken ct)
    {
        var messages = BuildMessages(
            request with { SystemPrompt = BuildFallbackSystemPrompt(request) }, ToolWireNames.ForRequest(request, includeOfferedTools: false));
        var offered = request.AvailableTools.Select(manifest => manifest.Name).ToHashSet(StringComparer.Ordinal);
        ModelCallDetails? lastDetails = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var payload = new ChatCompletionRequest { Model = options.Model, Messages = messages };
            var completion = await SendAsync(payload, ct);
            var text = completion.Body.Choices[0].Message.Content ?? string.Empty;
            lastDetails = completion.Details;

            if (TryParseFallbackJson(text, out var parsed))
            {
                var usage = MapUsage(completion.Body.Usage);
                if (parsed.IsFinal)
                {
                    return new ModelResponse(parsed.FinalText, [], true, usage) { Details = completion.Details };
                }

                // The same closed rule as the native path: only a tool offered in this request can be named.
                var call = new ModelToolCall(Guid.NewGuid().ToString("N"), parsed.ToolName!, parsed.Arguments!)
                {
                    ToolNameError = offered.Contains(parsed.ToolName!) ? null : ToolWireNames.DescribeUnknown(parsed.ToolName!),
                };
                return new ModelResponse(null, [call], false, usage) { Details = completion.Details };
            }

            messages.Add(new ChatMessageDto { Role = "assistant", Content = text });
            messages.Add(new ChatMessageDto
            {
                Role = "user",
                Content = "Your last reply was not valid JSON matching the required schema. Reply again with " +
                          "ONLY a JSON object: {\"tool\": \"...\", \"arguments\": {...}} or {\"final\": \"...\"}.",
            });
        }

        throw ProviderFailures.Malformed(
            options.Provider, "did not return valid JSON tool-call output after one corrective re-ask.", 200, lastDetails);
    }

    /// <summary>A reply that parsed, with what was sent and received so the call can be understood afterwards.</summary>
    private sealed record Completion(ChatCompletionResponse Body, ModelCallDetails Details);

    /// <summary>
    /// Sends one request — exactly one HTTP attempt (ADR-0039 §3): a failure is thrown as a classified
    /// <see cref="ModelProtocolException"/> with a safe reason, and the runtime decides whether to try again.
    /// </summary>
    private async Task<Completion> SendAsync(ChatCompletionRequest payload, CancellationToken ct)
    {
        var requestJson = JsonSerializer.Serialize(payload, OpenAiJsonContext.Default.ChatCompletionRequest);
        var sentOnly = new ModelCallDetails(null, null, requestJson, null);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(options.ResolvedApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ResolvedApiKey);
        }

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await _httpClient.SendAsync(httpRequest, ct);
        }
        catch (HttpRequestException ex)
        {
            throw ProviderFailures.Unreachable(options.Provider, ex, sentOnly);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw ProviderFailures.TransportTimeout(options.Provider, _httpClient.Timeout, ex, sentOnly);
        }

        using (httpResponse)
        {
            var status = (int)httpResponse.StatusCode;
            string rawBody;
            try
            {
                rawBody = await httpResponse.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException ex)
            {
                throw ProviderFailures.Interrupted(options.Provider, status, ex, sentOnly);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw ProviderFailures.TransportTimeout(options.Provider, _httpClient.Timeout, ex, sentOnly);
            }

            var failedDetails = new ModelCallDetails(null, null, requestJson, rawBody);

            if (!httpResponse.IsSuccessStatusCode)
            {
                throw ProviderFailures.HttpFailure(options.Provider, httpResponse, OpenAiErrorBody.Read(rawBody), failedDetails);
            }

            // A router can report a failed upstream call with a success status and the real status inside the body.
            if (rawBody.Contains("\"error\"", StringComparison.Ordinal) && OpenAiErrorBody.EmbeddedStatus(rawBody) is { } embedded)
            {
                throw ProviderFailures.StatusFailure(options.Provider, embedded, OpenAiErrorBody.Read(rawBody), failedDetails);
            }

            ChatCompletionResponse? body;
            try
            {
                body = JsonSerializer.Deserialize(rawBody, OpenAiJsonContext.Default.ChatCompletionResponse);
            }
            catch (JsonException ex)
            {
                throw ProviderFailures.Malformed(
                    options.Provider, "returned a response that did not match the expected schema.", status, failedDetails, ex);
            }

            if (body is null || body.Choices.Count == 0)
            {
                throw ProviderFailures.Malformed(
                    options.Provider, $"returned {(body is null ? "an empty response body" : "no choices")}.", status, failedDetails);
            }

            return new Completion(body, new ModelCallDetails(body.Model, body.Choices[0].FinishReason, requestJson, rawBody));
        }
    }

    private static List<ChatMessageDto> BuildMessages(ModelRequest request, ToolWireNames wireNames)
    {
        var messages = new List<ChatMessageDto> { new() { Role = "system", Content = request.SystemPrompt } };

        foreach (var turn in request.History)
        {
            messages.Add(new ChatMessageDto
            {
                Role = MapRole(turn.Role),
                Content = turn.Content,
                ToolCalls = turn.ToolCalls?.Select(call => MapToolCall(call, wireNames)).ToList(),
                ToolCallId = turn.ToolCallId,
            });
        }

        return messages;
    }

    /// <summary>
    /// Turns a tool call in a reply back into a canonical <see cref="ModelToolCall"/> through this request's closed
    /// alias map (ADR-0038). A name that is not an offered alias is never passed on as a tool name, and arguments
    /// that are present but malformed are never turned into an empty object.
    /// </summary>
    private static ModelToolCall MapResponseCall(ToolCallDto dto, ToolWireNames wireNames, HashSet<string> offered)
    {
        var (arguments, argumentsError) = ParseArguments(dto.Function.Arguments);

        if (!wireNames.TryGetCanonical(dto.Function.Name, out var canonical) || !offered.Contains(canonical))
        {
            return new ModelToolCall(dto.Id, dto.Function.Name, ToolArguments.Empty)
            {
                ToolNameError = ToolWireNames.DescribeUnknown(dto.Function.Name),
            };
        }

        return new ModelToolCall(dto.Id, canonical, arguments) { ArgumentsError = argumentsError };
    }

    private static string MapRole(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown chat role."),
    };

    /// <summary>
    /// A call whose raw name a provider adapter already rejected (<see cref="ModelToolCall.ToolNameError"/> is set)
    /// is never looked up in this request's alias map — that raw name was deliberately excluded from it
    /// (<see cref="ToolWireNames.ForRequest"/>, HARDEN-1 review M-1) — and is sent instead under the map's bounded,
    /// non-executable <see cref="ToolWireNames.InvalidToolPlaceholder"/>, so a strict provider still sees every call
    /// the model emitted answered, without the rejected name ever reaching the wire.
    /// </summary>
    private static ToolCallDto MapToolCall(ModelToolCall call, ToolWireNames wireNames) => new()
    {
        Id = call.Id,
        Function = new FunctionCallDto
        {
            Name = call.ToolNameError is null ? wireNames.ToWire(call.ToolName) : wireNames.InvalidToolPlaceholder,
            Arguments = call.Arguments.ToJson().ToJsonString(),
        },
    };

    private static ToolDefinitionDto BuildToolDefinition(ToolManifest manifest, ToolWireNames wireNames) => new()
    {
        Function = new FunctionDefinitionDto
        {
            Name = wireNames.ToWire(manifest.Name),
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
            // A repeated property name anywhere in the reply — including inside "arguments" — is a parse-time
            // JsonException here too (ADR-0038, HARDEN-1 review M-2), so it takes the existing unparseable-reply retry.
            if (StrictJson.Parse(text.Trim()) is not JsonObject node)
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
                // No "arguments" member is an explicit empty object; one that is present but not an object is an
                // unparseable reply (retried once), never repaired into {} (ADR-0038).
                node.TryGetPropertyValue("arguments", out var argsNode);
                if (argsNode is not null and not JsonObject)
                {
                    result = FallbackParseResult.None;
                    return false;
                }

                result = new FallbackParseResult(false, null, toolNode.GetValue<string>(), ToolArguments.FromJson(argsNode as JsonObject ?? new JsonObject()));
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

    /// <summary>
    /// Parses the JSON-encoded arguments string of a native tool call. No payload at all (a missing or null member) is
    /// an explicit empty object, since some providers send that for tools without parameters and the runtime still
    /// enforces required parameters; a payload that is present but blank, not JSON, or not a JSON object is malformed
    /// and is reported, never repaired (ADR-0038). The error text never contains any of the payload.
    /// </summary>
    private static (ToolArguments Arguments, string? Error) ParseArguments(string? json)
    {
        if (json is null)
        {
            return (ToolArguments.Empty, null);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return (ToolArguments.Empty, "the arguments payload was empty");
        }

        JsonNode? node;
        try
        {
            // A repeated property name is caught here too, at parse time (ADR-0038, HARDEN-1 review M-2), never left
            // to surface later as an unhandled ArgumentException from ordinary enumeration or property access.
            node = StrictJson.Parse(json);
        }
        catch (JsonException)
        {
            return (ToolArguments.Empty, "the arguments payload was not valid JSON");
        }

        return node is JsonObject obj
            ? (ToolArguments.FromJson(obj), null)
            : (ToolArguments.Empty, "the arguments payload was not a JSON object");
    }

    private static ModelUsage? MapUsage(UsageDto? usage) =>
        usage is null ? null : new ModelUsage(usage.PromptTokens, usage.CompletionTokens, null);

    private sealed record FallbackParseResult(bool IsFinal, string? FinalText, string? ToolName, ToolArguments? Arguments)
    {
        public static FallbackParseResult None { get; } = new(false, null, null, null);
    }
}
