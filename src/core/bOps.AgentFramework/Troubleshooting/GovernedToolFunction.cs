// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using bOps.Abstractions;
using bOps.Runtime;
using Microsoft.Extensions.AI;

namespace bOps.AgentFramework.Troubleshooting;

/// <summary>
/// The executable function MAF's function-invocation layer calls when an agent's model asks for a tool (C01-R23). Its body does one
/// thing: hand the canonical tool name and the arguments to the agent's <see cref="WorkflowAgentHandle"/>, which is the bOps gateway.
/// It holds no executor, registry, policy or package reference, so there is nothing here that could run a tool or decide whether one may
/// run; whatever comes back, including a refusal and a validation failure, is the text the model reads next.
/// </summary>
internal sealed class GovernedToolFunction : AIFunction
{
    private readonly ToolManifest manifest;
    private readonly WorkflowAgentHandle handle;
    private readonly AIFunctionDeclaration declaration;
    private readonly Action<MafWorkflowEvent>? progress;
    private readonly Func<int> round;

    internal GovernedToolFunction(ToolManifest manifest, WorkflowAgentHandle handle, Action<MafWorkflowEvent>? progress, Func<int> round)
    {
        this.manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        this.handle = handle ?? throw new ArgumentNullException(nameof(handle));
        declaration = (AIFunctionDeclaration)MafGovernedTurn.Declare(manifest);
        this.progress = progress;
        this.round = round ?? throw new ArgumentNullException(nameof(round));
    }

    /// <inheritdoc />
    public override string Name => manifest.Name;

    /// <inheritdoc />
    public override string Description => manifest.Description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => declaration.JsonSchema;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var callId = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId ?? $"call-{Guid.NewGuid():N}";
        var json = new JsonObject();
        foreach (var (key, value) in arguments)
        {
            json[key] = value is null ? null : JsonSerializer.SerializeToNode(value);
        }

        progress?.Invoke(new MafWorkflowEvent(MafWorkflowEventKind.ToolRequested, handle.Name, round(), manifest.Name));
        var result = await handle.ExecuteAsync(callId, manifest.Name, ToolArguments.FromJson(json), cancellationToken);
        progress?.Invoke(new MafWorkflowEvent(
            MafWorkflowEventKind.ToolCompleted, handle.Name, round(), $"{manifest.Name} → {result.Status} ({result.EvidenceId})"));
        // The evidence id is a typed value the runtime assigned, so the model can cite what a result shows; the observation after it is the
        // bounded, delimited tool output, which is data.
        return $"evidence_id: {result.EvidenceId}\nstatus: {result.Status}\n{result.Observation}";
    }
}
