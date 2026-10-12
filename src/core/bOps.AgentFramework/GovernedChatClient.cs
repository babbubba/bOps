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
/// or tool executor of its own. Every request first passes the final context gate (ADR-0054 G2-1): the effective MAF context
/// must be exactly the <see cref="ModelRequest"/> bOps authorised for this call, otherwise the call is audited and refused
/// before the broker, with no model call.
/// </summary>
/// <param name="broker">The governed broker that makes the actual call.</param>
/// <param name="contextFactory">Supplies the task-owned context of the logical call.</param>
/// <param name="authorizedRequestFactory">
/// Supplies the request bOps projected for this call: the protected instructions, the turns with the provenance bOps assigned,
/// and the step's allow-listed tools. It is the only source of authority for what may reach the model.
/// </param>
/// <param name="audit">Receives the audit event of every refusal.</param>
/// <param name="timeProvider">The clock for the audit event; the system clock by default.</param>
public sealed class GovernedChatClient(
    IModelInvocationBroker broker,
    Func<ModelInvocationContext> contextFactory,
    Func<ModelRequest> authorizedRequestFactory,
    IAuditSink audit,
    TimeProvider? timeProvider = null) : IChatClient
{
    private readonly IModelInvocationBroker broker = broker ?? throw new ArgumentNullException(nameof(broker));
    private readonly Func<ModelInvocationContext> contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    private readonly Func<ModelRequest> authorizedRequestFactory = authorizedRequestFactory ?? throw new ArgumentNullException(nameof(authorizedRequestFactory));
    private readonly IAuditSink audit = audit ?? throw new ArgumentNullException(nameof(audit));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private bool disposed;

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(messages);

        var context = contextFactory();
        var authorized = authorizedRequestFactory();
        var verdict = ContextGate.Evaluate([.. messages], options, authorized, broker.Descriptor);
        if (verdict.Request is not { } request)
        {
            throw await RefuseAsync(context, authorized, verdict, cancellationToken);
        }

        var response = await broker.InvokeAsync(request, context, cancellationToken);
        return MapResponse(response);
    }

    /// <summary>Audits the refusal and returns the failure that stops the call before the broker. The audit carries the code, never any message content.</summary>
    private async Task<ModelProtocolException> RefuseAsync(
        ModelInvocationContext context, ModelRequest authorized, ContextGateResult verdict, CancellationToken ct)
    {
        var code = verdict.Code!.Value.ToString();
        var reason = $"Context gate rejected the request before any model call: {code}. {verdict.Detail}";
        await audit.WriteAsync(
            new ModelCallAuditEvent
            {
                TimestampUtc = timeProvider.GetUtcNow(),
                Node = NodeId.Local,
                TaskId = context.TaskId,
                StepIndex = context.StepIndex,
                Actor = context.Actor,
                Provider = broker.Descriptor.ProviderId,
                Model = broker.Descriptor.ModelId,
                Outcome = ModelCallOutcome.Failure,
                ErrorMessage = reason,
                FailureKind = ModelFailureKind.InvalidRequest,
                RetryDecision = ModelRetryDecision.NotRetryable,
                ContextGateRejection = code,
                ContextProvenance = authorized.History.Count == 0 ? null : ContextProvenanceCount.Summarize(authorized.History),
                Delegation = context.Delegation,
            },
            ct);
        return new ModelProtocolException(reason) { FailureKind = ModelFailureKind.InvalidRequest };
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

    private static ChatResponse MapResponse(ModelResponse response)
    {
        var contents = new List<AIContent>();
        if (response.TextResponse is not null)
        {
            contents.Add(new TextContent(response.TextResponse));
        }

        foreach (var call in response.ToolCalls)
        {
            contents.Add(MafChatTurnMapper.ToMafFunctionCall(call));
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
