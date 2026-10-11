// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.AI;
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

        var history = MafChatTurnMapper.FromMaf(messages);
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
