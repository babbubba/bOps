// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.AI;
using MafChatRole = Microsoft.Extensions.AI.ChatRole;

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>
/// The leaf of every agent of a MAF troubleshooting workflow (C01-R23): the only way one reaches a model, and it reaches it through
/// the governed <see cref="IModelInvocationBroker"/>. It has no credential, retry, fallback or tool executor of its own. Unlike the
/// one-turn <see cref="GovernedChatClient"/> it serves a framework tool loop, so it sees a conversation that grows with each tool
/// result; for that reason the standing instructions are the bOps-authored ones it was built with, never anything the conversation
/// carries, and the tools it offers are the manifests the agent was given, never a name the conversation proposes.
/// </summary>
internal sealed class WorkflowGovernedChatClient : IChatClient
{
    private readonly IModelInvocationBroker broker;
    private readonly WorkflowToolSession session;
    private readonly WorkflowAgentHandle agent;
    private readonly string systemPrompt;
    private readonly Dictionary<string, ToolManifest> manifests;
    private readonly WorkflowBudget budget;
    private bool disposed;

    internal WorkflowGovernedChatClient(
        IModelInvocationBroker broker,
        WorkflowToolSession session,
        WorkflowAgentHandle agent,
        string systemPrompt,
        IReadOnlyList<ToolManifest> offeredTools,
        WorkflowBudget budget)
    {
        this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.agent = agent ?? throw new ArgumentNullException(nameof(agent));
        this.systemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));
        this.budget = budget ?? throw new ArgumentNullException(nameof(budget));
        manifests = (offeredTools ?? throw new ArgumentNullException(nameof(offeredTools)))
            .ToDictionary(manifest => manifest.Name, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(messages);

        // What the framework presents as tools must be exactly tools this agent was given; a name it adds is refused before any model call.
        var presented = options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
        if (presented.FirstOrDefault(name => !manifests.ContainsKey(name) || !agent.AllowedTools.Contains(name)) is { } outside)
        {
            throw new ModelProtocolException($"Workflow agent '{agent.Name}' was presented a tool outside its authority: '{outside}'.")
            {
                FailureKind = ModelFailureKind.InvalidRequest,
            };
        }

        budget.BeginModelCall();
        var history = MafChatTurnMapper.FromMaf(messages.Where(message => message.Role != MafChatRole.System));
        var request = new ModelRequest(systemPrompt, history, [.. presented.Select(name => manifests[name])]);
        var context = new ModelInvocationContext(session.RunId, session.NextModelCallStepIndex(), session.Actor, new List<ModelCallRecord>())
        {
            Workflow = new WorkflowCorrelation(session.RunId, agent.Name, agent.InstanceId, null),
            AccountTokens = budget.ChargeTokens,
        };

        var response = await broker.InvokeAsync(request, context, cancellationToken);

        // A call the framework cannot match to an offered function never reaches a function body, so it would not be audited by the
        // gateway. It is handed to the gateway here, which refuses and audits it; the framework then answers the model that the function
        // does not exist.
        foreach (var call in response.ToolCalls.Where(call => call.ToolNameError is not null || !presented.Contains(call.ToolName, StringComparer.Ordinal)))
        {
            await agent.RefuseAsync(call, cancellationToken);
        }

        return GovernedChatClient.MapResponse(response);
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
}
