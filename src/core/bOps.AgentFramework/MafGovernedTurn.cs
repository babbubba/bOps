// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace bOps.AgentFramework;

/// <summary>
/// AF-TURN-01A: the one place a Microsoft Agent Framework <see cref="ChatClientAgent"/> is built and run, and the only way a
/// governed MAF turn reaches the bOps model boundary. A turn is exactly one model call: the agent is created for it, hosts a
/// fresh session, has no framework tool loop, no context provider and no history provider, and its single leaf is the
/// <see cref="GovernedChatClient"/>, whose final context gate compares everything MAF is about to send with the request bOps
/// authorised. MAF is infrastructure for the turn; it holds no operational authority (ADR-0052, ADR-0054).
/// </summary>
internal static class MafGovernedTurn
{
    /// <summary>
    /// The agent options of a governed turn. The standing instructions are the protected instructions bOps projected; the tools
    /// are plain declarations of the step's allow-listed manifests (proposals the model may make, never something MAF can run);
    /// the provided client is used as is, so no function-invocation loop or any other wrapper is put around the leaf.
    /// </summary>
    internal static ChatClientAgentOptions BuildOptions(ModelRequest authorized, MafContextComposition? composition = null)
    {
        ArgumentNullException.ThrowIfNull(authorized);
        return new ChatClientAgentOptions
        {
            // With a composition the standing instructions come from the one admitted bOps provider, not from the options.
            AIContextProviders = composition is null ? null : [composition.Provider],
            ChatOptions = new ChatOptions
            {
                Instructions = composition is null ? authorized.SystemPrompt : null,
                Tools = authorized.AvailableTools.Count == 0 ? null : [.. authorized.AvailableTools.Select(Declare)],
            },
            UseProvidedChatClientAsIs = true,
        };
    }

    /// <summary>Creates the stateless agent of one turn over the governed leaf.</summary>
    internal static ChatClientAgent CreateAgent(GovernedChatClient leaf, ModelRequest authorized, MafContextComposition? composition = null) =>
        new ChatClientAgent(leaf, BuildOptions(authorized, composition));

    /// <summary>
    /// Runs one governed turn. <paramref name="input"/> is what the host hands the agent; the gate refuses it unless it is exactly
    /// the authorised context. The response is the broker's own, so the runtime sees what it sees on the current path; the MAF
    /// response is only checked against it.
    /// </summary>
    internal static async Task<ModelResponse> RunAsync(
        IModelInvocationBroker broker,
        ModelInvocationContext context,
        ModelRequest authorized,
        ModelContextRequest? projectable,
        IEnumerable<ChatMessage> input,
        IAuditSink audit,
        TimeProvider timeProvider,
        CancellationToken ct,
        MafContextComposition? composition = null)
    {
        using var leaf = projectable is null
            ? new GovernedChatClient(broker, () => context, () => authorized, audit, timeProvider, composition?.ProviderId)
            : new GovernedChatClient(broker, () => context, () => projectable, audit, timeProvider, composition?.ProviderId);
        var agent = CreateAgent(leaf, authorized, composition);
        var session = await agent.CreateSessionAsync(ct);
        var run = await agent.RunAsync(input, session, cancellationToken: ct);

        if (leaf.BrokerInvocations != 1 || leaf.LastBrokerResponse is not { } brokered)
        {
            throw Violation($"a governed MAF turn must reach the broker exactly once (reached it {leaf.BrokerInvocations} times)");
        }

        var returned = run.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
            .Select(call => call.CallId + "\n" + call.Name).ToArray();
        var proposed = brokered.ToolCalls.Select(call => call.Id + "\n" + call.ToolName).ToArray();
        if (!returned.SequenceEqual(proposed, StringComparer.Ordinal)
            || !string.Equals(run.Text, brokered.TextResponse ?? string.Empty, StringComparison.Ordinal))
        {
            throw Violation("the MAF response differs from the broker response");
        }

        return brokered;
    }

    private static ModelProtocolException Violation(string detail) =>
        new($"Governed MAF turn integrity violation: {detail}.") { FailureKind = ModelFailureKind.InvalidRequest };

    /// <summary>A plain function declaration of one allow-listed manifest. It carries no body, so the framework cannot invoke it.</summary>
    internal static AITool Declare(ToolManifest manifest)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var parameter in manifest.Parameters)
        {
            properties[parameter.Name] = ParameterSchema(parameter);
            if (parameter.Required)
            {
                required.Add(parameter.Name);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
        return AIFunctionFactory.CreateDeclaration(
            manifest.Name, manifest.Description, JsonSerializer.SerializeToElement(schema));
    }

    private static JsonObject ParameterSchema(ToolParameter parameter)
    {
        var schema = new JsonObject
        {
            ["type"] = parameter.Type switch
            {
                ToolParameterType.Integer => "integer",
                ToolParameterType.Number => "number",
                ToolParameterType.Boolean => "boolean",
                ToolParameterType.PathList => "array",
                _ => "string",
            },
            ["description"] = parameter.Description,
        };
        if (parameter.Type == ToolParameterType.PathList)
        {
            schema["items"] = new JsonObject { ["type"] = "string" };
        }

        if (parameter.AllowedValues is { Count: > 0 })
        {
            schema["enum"] = new JsonArray([.. parameter.AllowedValues.Select(value => (JsonNode)value)]);
        }

        return schema;
    }
}

/// <summary>
/// An <see cref="IModelInvocationBroker"/> that runs every model call as one governed MAF turn (AF-TURN-01A) in front of an inner
/// broker. It changes nothing the runtime can observe: the inner broker still projects the context, selects the model, retries,
/// accounts and audits, and its response is returned unchanged. It is not composed by any production host; a host opts in by
/// passing it as the runner's invocation broker once the equivalence suite is accepted.
/// </summary>
public sealed class MafGovernedTurnBroker : IModelInvocationBroker
{
    private readonly IModelInvocationBroker inner;
    private readonly IAuditSink audit;
    private readonly TimeProvider timeProvider;
    private readonly MafContextAssembly assembly;
    private int composedTurns;

    /// <summary>Creates the broker over the real governed broker.</summary>
    /// <param name="inner">The broker that makes the actual call.</param>
    /// <param name="audit">Receives the audit event of a context-gate refusal.</param>
    /// <param name="timeProvider">The clock for that audit event; the system clock by default.</param>
    /// <param name="assembly">
    /// How the context is handed to the agent. <see cref="MafContextAssembly.BopsProvider"/> (AF-TURN-01B) composes the one
    /// bOps-authored context provider for each call that carries protected context; it is chosen by the host and never by data.
    /// </param>
    public MafGovernedTurnBroker(
        IModelInvocationBroker inner, IAuditSink audit, TimeProvider? timeProvider = null, MafContextAssembly assembly = MafContextAssembly.InputOnly)
    {
        this.assembly = assembly;
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>How many turns this broker composed through the bOps context provider. Zero in <see cref="MafContextAssembly.InputOnly"/>.</summary>
    internal int ComposedTurns => Volatile.Read(ref composedTurns);

    /// <inheritdoc />
    public ChatModelDescriptor Descriptor => inner.Descriptor;

    /// <inheritdoc />
    public Task<ModelResponse> InvokeAsync(ModelRequest request, ModelInvocationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return MafGovernedTurn.RunAsync(
            inner, context, request, null, MafChatTurnMapper.ToMaf(request.History), audit, timeProvider, ct);
    }

    /// <inheritdoc />
    public Task<ModelResponse> InvokeAsync(ModelContextRequest request, ModelInvocationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var authorized = request.ToUnprojectedRequest();
        if (assembly == MafContextAssembly.BopsProvider)
        {
            var composition = MafContextComposition.Create(request);
            Interlocked.Increment(ref composedTurns);
            return MafGovernedTurn.RunAsync(
                inner, context, authorized, request, MafChatTurnMapper.ToMaf(composition.InputTurns), audit, timeProvider, ct, composition);
        }

        return MafGovernedTurn.RunAsync(
            inner, context, authorized, request, MafChatTurnMapper.ToMaf(authorized.History), audit, timeProvider, ct);
    }
}
