// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace bOps.Packages.Providers.OpenAiCompatible;

internal sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("messages")]
    public required List<ChatMessageDto> Messages { get; set; }

    [JsonPropertyName("tools")]
    public List<ToolDefinitionDto>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public string? ToolChoice { get; set; }
}

internal sealed class ChatMessageDto
{
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<ToolCallDto>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }
}

internal sealed class ToolCallDto
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public required FunctionCallDto Function { get; set; }
}

internal sealed class FunctionCallDto
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>A JSON-encoded string per the OpenAI schema, not a nested object.</summary>
    [JsonPropertyName("arguments")]
    public required string Arguments { get; set; }
}

internal sealed class ToolDefinitionDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public required FunctionDefinitionDto Function { get; set; }
}

internal sealed class FunctionDefinitionDto
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public required string Description { get; set; }

    [JsonPropertyName("parameters")]
    public required JsonObject Parameters { get; set; }
}

internal sealed class ChatCompletionResponse
{
    /// <summary>The model the provider says served the call; a router such as <c>openrouter/free</c> reports the one it picked.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("choices")]
    public required List<ChatCompletionChoiceDto> Choices { get; set; }

    [JsonPropertyName("usage")]
    public UsageDto? Usage { get; set; }
}

internal sealed class ChatCompletionChoiceDto
{
    [JsonPropertyName("message")]
    public required ChatMessageDto Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

internal sealed class UsageDto
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
internal sealed partial class OpenAiJsonContext : JsonSerializerContext;
