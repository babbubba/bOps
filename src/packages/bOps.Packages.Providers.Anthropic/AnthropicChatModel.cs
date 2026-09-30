// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Packages.Providers.Wire;

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
/// with the same JSON-schema-in-prompt fallback strategy (plan §3.1.1) when it is false. A transport failure is never
/// retried here (ADR-0039): each HTTP request is one attempt, and a failure is thrown classified for the runtime to decide.
/// </summary>
public sealed class AnthropicChatModel(ChatModelOptions options, HttpClient httpClient) : IChatModel
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
        var wireNames = ToolWireNames.ForRequest(request, includeOfferedTools: true);
        var payload = new MessagesRequest
        {
            Model = options.Model,
            MaxTokens = MaxTokens,
            System = request.SystemPrompt,
            Messages = BuildMessages(request.History, wireNames),
            Tools = request.AvailableTools.Count == 0 ? null : request.AvailableTools.Select(manifest => BuildToolDefinition(manifest, wireNames)).ToList(),
        };

        var completion = await SendAsync(payload, ct);
        var response = completion.Body;
        var offered = request.AvailableTools.Select(manifest => manifest.Name).ToHashSet(StringComparer.Ordinal);
        var toolCalls = response.Content
            .Where(block => block.Type == "tool_use")
            .Select(block => MapResponseCall(block, wireNames, offered))
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
        var messages = BuildMessages(request.History, ToolWireNames.ForRequest(request, includeOfferedTools: false));
        var offered = request.AvailableTools.Select(manifest => manifest.Name).ToHashSet(StringComparer.Ordinal);
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

        throw ProviderFailures.Malformed(
            options.Provider, "did not return valid JSON tool-call output after one corrective re-ask.", 200, lastDetails);
    }

    /// <summary>A reply that parsed, with what was sent and received so the call can be understood afterwards.</summary>
    private sealed record Completion(MessagesResponse Body, ModelCallDetails Details);

    /// <summary>
    /// Sends one request — exactly one HTTP attempt (ADR-0039 §3): a failure is thrown as a classified
    /// <see cref="ModelProtocolException"/> with a safe reason, and the runtime decides whether to try again.
    /// </summary>
    private async Task<Completion> SendAsync(MessagesRequest payload, CancellationToken ct)
    {
        var requestJson = JsonSerializer.Serialize(payload, AnthropicJsonContext.Default.MessagesRequest);
        var sentOnly = new ModelCallDetails(null, null, requestJson, null);

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
                throw ProviderFailures.HttpFailure(options.Provider, httpResponse, ReadError(rawBody), failedDetails);
            }

            MessagesResponse? body;
            try
            {
                body = JsonSerializer.Deserialize(rawBody, AnthropicJsonContext.Default.MessagesResponse);
            }
            catch (JsonException ex)
            {
                throw ProviderFailures.Malformed(
                    options.Provider, "returned a response that did not match the expected schema.", status, failedDetails, ex);
            }

            if (body is null)
            {
                throw ProviderFailures.Malformed(options.Provider, "returned an empty response body.", status, failedDetails);
            }

            return new Completion(body, new ModelCallDetails(body.Model, body.StopReason, requestJson, rawBody));
        }
    }

    /// <summary>
    /// The safe parts of an Anthropic error body, <c>{"type":"error","error":{"type","message"}}</c> (ADR-0039 §6): only the
    /// error type (for classification) and message; nothing else in the body is read.
    /// </summary>
    private static ProviderError ReadError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return ProviderError.None;
        }

        try
        {
            if (StrictJson.Parse(body) is not JsonObject { } root || root["error"] is not JsonObject error)
            {
                return ProviderError.None;
            }

            var type = error["type"] is JsonValue typeValue && typeValue.GetValueKind() == JsonValueKind.String
                ? typeValue.GetValue<string>()
                : null;
            var message = error["message"] is JsonValue messageValue && messageValue.GetValueKind() == JsonValueKind.String
                ? messageValue.GetValue<string>()
                : null;
            return new ProviderError(type, message, ProviderFailures.Join(type, message));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            return ProviderError.None;
        }
    }

    /// <summary>
    /// Maps <paramref name="history"/> into Anthropic messages, merging consecutive turns that map
    /// to the same Anthropic role into one message with several content blocks — required because
    /// the Messages API rejects two consecutive messages with the same role, but the runtime's
    /// agent loop (agentic/01-architecture-rules.md, rule D-007) can append several consecutive
    /// <see cref="ChatRole.Tool"/> turns in a row (the executed call's result, then one
    /// "not executed" turn per tool call the model proposed but the runtime did not run).
    /// </summary>
    private static List<AnthropicMessageDto> BuildMessages(IReadOnlyList<ChatTurn> history, ToolWireNames wireNames)
    {
        var messages = new List<AnthropicMessageDto>();

        foreach (var turn in history)
        {
            var role = MapRole(turn.Role);
            var blocks = BuildContentBlocks(turn, wireNames);

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

    /// <summary>
    /// Turns a <c>tool_use</c> block back into a canonical <see cref="ModelToolCall"/> through this request's closed
    /// alias map (ADR-0038). A name that is not an offered alias is never passed on as a tool name. The input is
    /// already a JSON value; a block with no input at all is an explicit empty object, and the runtime still
    /// enforces required parameters.
    /// </summary>
    private static ModelToolCall MapResponseCall(ContentBlockDto block, ToolWireNames wireNames, HashSet<string> offered)
    {
        var wireName = block.Name ?? string.Empty;
        if (!wireNames.TryGetCanonical(wireName, out var canonical) || !offered.Contains(canonical))
        {
            return new ModelToolCall(block.Id!, wireName, ToolArguments.Empty)
            {
                ToolNameError = ToolWireNames.DescribeUnknown(wireName),
            };
        }

        var (arguments, argumentsError) = ParseInput(block.Input);
        return new ModelToolCall(block.Id!, canonical, arguments) { ArgumentsError = argumentsError };
    }

    /// <summary>
    /// Parses a <c>tool_use</c> block's <c>input</c>: absent is an explicit empty object (the runtime still enforces
    /// required parameters), and a value that is present but not an object, or an object with a repeated property
    /// name at any depth, is malformed and reported, never repaired (ADR-0038, HARDEN-1 review M-2). Re-parsing the
    /// raw text strictly, rather than trusting <see cref="ContentBlockDto.Input"/>'s own already-deserialized shape,
    /// is what lets a repeated key survive long enough to be reported instead of throwing during response
    /// deserialization in <see cref="SendAsync"/>, outside this method's containment.
    /// </summary>
    private static (ToolArguments Arguments, string? Error) ParseInput(JsonElement? input)
    {
        if (input is not { } element)
        {
            return (ToolArguments.Empty, null);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return (ToolArguments.Empty, "the arguments payload was not a JSON object");
        }

        try
        {
            return StrictJson.Parse(element.GetRawText()) is JsonObject obj
                ? (ToolArguments.FromJson(obj), null)
                : (ToolArguments.Empty, "the arguments payload was not a JSON object");
        }
        catch (JsonException)
        {
            return (ToolArguments.Empty, "the arguments payload was not valid JSON");
        }
    }

    private static List<ContentBlockDto> BuildContentBlocks(ChatTurn turn, ToolWireNames wireNames)
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
            // A call whose raw name was already rejected (ToolNameError set) is sent under the request's bounded,
            // non-executable placeholder instead of being looked up in the alias map, which deliberately excludes it
            // (HARDEN-1 review M-1).
            blocks.AddRange(turn.ToolCalls.Select(call => new ContentBlockDto
            {
                Type = "tool_use",
                Id = call.Id,
                Name = call.ToolNameError is null ? wireNames.ToWire(call.ToolName) : wireNames.InvalidToolPlaceholder,
                Input = JsonSerializer.SerializeToElement(call.Arguments.ToJson()),
            }));
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

    private static ToolDto BuildToolDefinition(ToolManifest manifest, ToolWireNames wireNames) =>
        new() { Name = wireNames.ToWire(manifest.Name), Description = manifest.Description, InputSchema = BuildParameterSchema(manifest) };

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

    private static ModelUsage? MapUsage(AnthropicUsageDto? usage) =>
        usage is null ? null : new ModelUsage(usage.InputTokens, usage.OutputTokens, null);

    private sealed record FallbackParseResult(bool IsFinal, string? FinalText, string? ToolName, ToolArguments? Arguments)
    {
        public static FallbackParseResult None { get; } = new(false, null, null, null);
    }
}
