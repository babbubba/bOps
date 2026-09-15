// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace bOps.Packages.Providers.Anthropic;

internal sealed class MessagesRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("max_tokens")]
    public required int MaxTokens { get; set; }

    [JsonPropertyName("system")]
    public string? System { get; set; }

    [JsonPropertyName("messages")]
    public required List<AnthropicMessageDto> Messages { get; set; }

    [JsonPropertyName("tools")]
    public List<ToolDto>? Tools { get; set; }
}

internal sealed class AnthropicMessageDto
{
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [JsonPropertyName("content")]
    public required List<ContentBlockDto> Content { get; set; }
}

/// <summary>
/// One block of a message's content array. Anthropic's content blocks are a tagged union
/// (<see cref="Type"/> is <c>"text"</c>, <c>"tool_use"</c> or <c>"tool_result"</c>) — modeled as
/// one DTO with every field optional rather than three separate types, since
/// <see cref="System.Text.Json"/> source generation has no polymorphic-by-sibling-field support
/// as clean as a single flat shape here.
/// </summary>
internal sealed class ContentBlockDto
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    /// <summary>Present when <see cref="Type"/> is <c>"text"</c>.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>Present when <see cref="Type"/> is <c>"tool_use"</c>: the id later echoed back as <see cref="ToolUseId"/> on the matching <c>"tool_result"</c> block.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Present when <see cref="Type"/> is <c>"tool_use"</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Present when <see cref="Type"/> is <c>"tool_use"</c>: the tool's arguments, as a JSON object (not a JSON-encoded string, unlike the OpenAI schema).</summary>
    [JsonPropertyName("input")]
    public JsonObject? Input { get; set; }

    /// <summary>Present when <see cref="Type"/> is <c>"tool_result"</c>: the <see cref="Id"/> of the <c>"tool_use"</c> block this answers.</summary>
    [JsonPropertyName("tool_use_id")]
    public string? ToolUseId { get; set; }

    /// <summary>Present when <see cref="Type"/> is <c>"tool_result"</c>: the tool's output, as plain text.</summary>
    [JsonPropertyName("content")]
    public string? ResultContent { get; set; }
}

internal sealed class ToolDto
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public required string Description { get; set; }

    [JsonPropertyName("input_schema")]
    public required JsonObject InputSchema { get; set; }
}

internal sealed class MessagesResponse
{
    [JsonPropertyName("content")]
    public required List<ContentBlockDto> Content { get; set; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicUsageDto? Usage { get; set; }
}

internal sealed class AnthropicUsageDto
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(MessagesRequest))]
[JsonSerializable(typeof(MessagesResponse))]
internal sealed partial class AnthropicJsonContext : JsonSerializerContext;
