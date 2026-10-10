// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.AI;
using BopsChatRole = bOps.Abstractions.ChatRole;
using MafChatRole = Microsoft.Extensions.AI.ChatRole;

namespace bOps.AgentFramework;

/// <summary>
/// Adapts the <see cref="IChatClient"/> surface consumed by Microsoft Agent Framework to the
/// bOps-governed model invocation broker. It has no credentials, retry policy, fallback selection
/// or tool executor of its own.
/// </summary>
public sealed class GovernedChatClient(
    IModelInvocationBroker broker,
    Func<ModelInvocationContext> contextFactory) : IChatClient
{
    private readonly IModelInvocationBroker broker = broker ?? throw new ArgumentNullException(nameof(broker));
    private readonly Func<ModelInvocationContext> contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    private bool disposed;

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(messages);
        ValidateOptions(options);

        var history = messages.SelectMany(MapMessage).ToArray();
        var request = new ModelRequest(options?.Instructions ?? string.Empty, history, []);
        var response = await broker.InvokeAsync(request, contextFactory(), cancellationToken);
        return MapResponse(response);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return serviceType == typeof(ChatClientMetadata)
            ? new ChatClientMetadata(broker.Descriptor.ProviderId, null, broker.Descriptor.ModelId)
            : null;
    }

    /// <inheritdoc />
    public void Dispose() => disposed = true;

    private void ValidateOptions(ChatOptions? options)
    {
        if (options is null)
        {
            return;
        }

        if (options.Tools is { Count: > 0 })
        {
            throw new NotSupportedException(
                "MAF tools are not enabled by AF-01; operational tool authority remains with bOps.");
        }

        if (!string.IsNullOrEmpty(options.ModelId)
            && !string.Equals(options.ModelId, broker.Descriptor.ModelId, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "A MAF caller cannot override the provider/model selected and pinned by bOps.");
        }

        if (!string.IsNullOrEmpty(options.ConversationId)
            || options.ContinuationToken is not null
            || options.AllowBackgroundResponses == true)
        {
            throw new NotSupportedException("Stateful or background MAF responses are not enabled by AF-01.");
        }

        if (options.Temperature is not null
            || options.MaxOutputTokens is not null
            || options.TopP is not null
            || options.TopK is not null
            || options.FrequencyPenalty is not null
            || options.PresencePenalty is not null
            || options.Seed is not null
            || options.Reasoning is not null
            || options.ResponseFormat is not null
            || options.StopSequences is { Count: > 0 }
            || options.RawRepresentationFactory is not null
            || options.AdditionalProperties is { Count: > 0 })
        {
            throw new NotSupportedException(
                "This MAF request uses generation options that the AF-01 bOps model contract does not represent.");
        }
    }

    private static IEnumerable<ChatTurn> MapMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var role = MapRole(message.Role);
        var textItems = message.Contents.OfType<TextContent>().Select(content => content.Text).ToArray();
        var unsupported = message.Contents.Where(content => content is not TextContent
            && content is not FunctionCallContent
            && content is not FunctionResultContent).ToArray();
        if (unsupported.Length > 0)
        {
            throw new NotSupportedException(
                $"MAF content type '{unsupported[0].GetType().Name}' is not representable by the AF-01 bOps model contract.");
        }

        var text = textItems.Length == 0 ? null : string.Join('\n', textItems);
        var functionCalls = message.Contents.OfType<FunctionCallContent>().ToArray();
        var functionResults = message.Contents.OfType<FunctionResultContent>().ToArray();

        if (role == BopsChatRole.Assistant)
        {
            if (functionResults.Length > 0)
            {
                throw new NotSupportedException("An assistant MAF message cannot be mapped with function-result content.");
            }

            yield return new ChatTurn
            {
                Role = role,
                Content = text,
                ToolCalls = functionCalls.Length == 0 ? null : functionCalls.Select(MapFunctionCall).ToArray(),
            };
            yield break;
        }

        if (role == BopsChatRole.Tool)
        {
            if (functionCalls.Length > 0 || functionResults.Length == 0 || text is not null)
            {
                throw new NotSupportedException(
                    "A MAF tool message must contain one or more function results and no unrelated content.");
            }

            foreach (var result in functionResults)
            {
                yield return ChatTurn.FromToolResult(result.CallId, SerializeFunctionResult(result.Result));
            }

            yield break;
        }

        if (functionCalls.Length > 0 || functionResults.Length > 0)
        {
            throw new NotSupportedException("Function content is supported only on assistant and tool MAF messages.");
        }

        yield return new ChatTurn { Role = role, Content = text };
    }

    private static ModelToolCall MapFunctionCall(FunctionCallContent call)
    {
        var node = JsonSerializer.SerializeToNode(call.Arguments);
        if (node is not JsonObject arguments)
        {
            throw new NotSupportedException("MAF function-call arguments must map to a JSON object.");
        }

        return new ModelToolCall(call.CallId, call.Name, ToolArguments.FromJson(arguments));
    }

    private static string SerializeFunctionResult(object? result) => result switch
    {
        null => "null",
        string text => text,
        _ => JsonSerializer.Serialize(result),
    };

    private static BopsChatRole MapRole(MafChatRole role)
    {
        if (role == MafChatRole.System)
        {
            return BopsChatRole.System;
        }

        if (role == MafChatRole.User)
        {
            return BopsChatRole.User;
        }

        if (role == MafChatRole.Assistant)
        {
            return BopsChatRole.Assistant;
        }

        if (role == MafChatRole.Tool)
        {
            return BopsChatRole.Tool;
        }

        throw new NotSupportedException($"MAF role '{role}' is not representable by the bOps model contract.");
    }

    private static ChatResponse MapResponse(ModelResponse response)
    {
        var contents = new List<AIContent>();
        if (response.TextResponse is not null)
        {
            contents.Add(new TextContent(response.TextResponse));
        }

        foreach (var call in response.ToolCalls)
        {
            var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(call.Arguments.ToJson())
                ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            contents.Add(new FunctionCallContent(call.Id, call.ToolName, arguments));
        }

        var mapped = new ChatResponse(new ChatMessage(MafChatRole.Assistant, contents))
        {
            ModelId = response.Details?.ActualModel,
            FinishReason = response.Details?.FinishReason is { Length: > 0 } reason
                ? new ChatFinishReason(reason)
                : response.ToolCalls.Count > 0
                    ? ChatFinishReason.ToolCalls
                    : response.IsFinal ? ChatFinishReason.Stop : null,
        };
        if (response.Usage is { } usage)
        {
            mapped.Usage = new UsageDetails
            {
                InputTokenCount = usage.PromptTokens,
                OutputTokenCount = usage.CompletionTokens,
                TotalTokenCount = usage.PromptTokens + usage.CompletionTokens,
            };
        }

        return mapped;
    }
}
