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
public sealed class GovernedChatClient : IChatClient
{
    private readonly IModelInvocationBroker broker;
    private readonly Func<ModelInvocationContext> contextFactory;
    private readonly Func<(ModelRequest Authorized, ModelContextRequest? Projectable)> authorizedFactory;
    private readonly IAuditSink audit;
    private readonly TimeProvider timeProvider;
    private readonly string? admittedProviderId;
    private bool disposed;

    /// <summary>Creates a client whose broker receives the authorised request as it is, with no context projection.</summary>
    /// <param name="broker">The governed broker that makes the actual call.</param>
    /// <param name="contextFactory">Supplies the task-owned context of the logical call.</param>
    /// <param name="authorizedRequestFactory">
    /// Supplies the request bOps projected for this call: the protected instructions, the turns with the provenance bOps assigned,
    /// and the step's allow-listed tools. It is the only source of authority for what may reach the model.
    /// </param>
    /// <param name="audit">Receives the audit event of every refusal.</param>
    /// <param name="timeProvider">The clock for the audit event; the system clock by default.</param>
    /// <param name="admittedContextProviderId">
    /// The id the host assigned to the one bOps-authored context provider instance it composed for this call, or <c>null</c> when no
    /// provider is composed (every provider message is then refused). Admission never replaces the exact comparison.
    /// </param>
    public GovernedChatClient(
        IModelInvocationBroker broker,
        Func<ModelInvocationContext> contextFactory,
        Func<ModelRequest> authorizedRequestFactory,
        IAuditSink audit,
        TimeProvider? timeProvider = null,
        string? admittedContextProviderId = null)
        : this(broker, contextFactory, Wrap(authorizedRequestFactory), audit, timeProvider, admittedContextProviderId)
    {
    }

    /// <summary>
    /// Creates a client for a request that the broker still has to project (compact) per candidate model. The messages MAF presents
    /// must equal <see cref="ModelContextRequest.ToUnprojectedRequest"/>; once the gate accepts them the broker receives the
    /// authorised <see cref="ModelContextRequest"/> itself, so projection, recovery and audit are exactly those of the current path.
    /// </summary>
    /// <param name="broker">The governed broker that makes the actual call.</param>
    /// <param name="contextFactory">Supplies the task-owned context of the logical call.</param>
    /// <param name="authorizedContextRequestFactory">Supplies the context request bOps authorised for this call.</param>
    /// <param name="audit">Receives the audit event of every refusal.</param>
    /// <param name="timeProvider">The clock for the audit event; the system clock by default.</param>
    /// <param name="admittedContextProviderId">See the constructor above.</param>
    public GovernedChatClient(
        IModelInvocationBroker broker,
        Func<ModelInvocationContext> contextFactory,
        Func<ModelContextRequest> authorizedContextRequestFactory,
        IAuditSink audit,
        TimeProvider? timeProvider = null,
        string? admittedContextProviderId = null)
        : this(broker, contextFactory, Wrap(authorizedContextRequestFactory), audit, timeProvider, admittedContextProviderId)
    {
    }

    private GovernedChatClient(
        IModelInvocationBroker broker,
        Func<ModelInvocationContext> contextFactory,
        Func<(ModelRequest, ModelContextRequest?)> authorizedFactory,
        IAuditSink audit,
        TimeProvider? timeProvider,
        string? admittedContextProviderId)
    {
        this.admittedProviderId = admittedContextProviderId;
        this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
        this.contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        this.authorizedFactory = authorizedFactory;
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>How many times this client reached the broker. A governed turn must reach it exactly once.</summary>
    internal int BrokerInvocations { get; private set; }

    /// <summary>The broker's own response to the last call, before it was mapped to the MAF shape.</summary>
    internal ModelResponse? LastBrokerResponse { get; private set; }

    private static Func<(ModelRequest, ModelContextRequest?)> Wrap(Func<ModelRequest> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return () => (factory(), null);
    }

    private static Func<(ModelRequest, ModelContextRequest?)> Wrap(Func<ModelContextRequest> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return () =>
        {
            var projectable = factory();
            return (projectable.ToUnprojectedRequest(), projectable);
        };
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(messages);

        var context = contextFactory();
        var (authorized, projectable) = authorizedFactory();
        var verdict = ContextGate.Evaluate([.. messages], options, authorized, broker.Descriptor, admittedProviderId);
        if (verdict.Request is not { } request)
        {
            throw await RefuseAsync(context, authorized, verdict, cancellationToken);
        }

        // The gate has proven the effective context is the authorised one; the broker receives the bOps request, never MAF's.
        BrokerInvocations++;
        var response = projectable is null
            ? await broker.InvokeAsync(request, context, cancellationToken)
            : await broker.InvokeAsync(projectable with { AvailableTools = request.AvailableTools }, context, cancellationToken);
        LastBrokerResponse = response;
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

    internal static ChatResponse MapResponse(ModelResponse response)
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
