// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.AgentFramework;
using bOps.Runtime;
using Microsoft.Extensions.Logging;

namespace bOps.Cli;

/// <summary>
/// The one place the CLI builds its operational runner: the governed model broker with the MAF context projector, and the
/// <see cref="AgentRunner"/> over it (AF-01, ADR-0052). It is the composition <c>Program.cs</c> runs, extracted so a test can drive the same
/// code with a scripted model; it adds nothing to the runner's authority. The knowledge host is the optional last input (ADR-0058): when it
/// is <c>null</c> the runner is built exactly as it was before knowledge was composable.
/// </summary>
internal static class CliRunnerComposition
{
    /// <summary>Builds the runner of a goal, a resume, a recovery or a delegation.</summary>
    internal static AgentRunner Create(CliRunnerInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var broker = new ModelInvocationBroker(
            inputs.Model, inputs.Audit, inputs.Time, inputs.Logger, inputs.Options,
            contextProjector: new MafContextProjector(inputs.Options, inputs.Logger),
            dedicatedSummarizer: inputs.SummarizerBroker);

        return new AgentRunner(
            inputs.Model,
            inputs.Tools,
            inputs.Policy,
            inputs.Approval,
            inputs.Audit,
            inputs.Store,
            inputs.Time,
            inputs.Logger,
            inputs.Options,
            inputs.Skills,
            systemMessages: inputs.SystemMessages,
            invocationBroker: broker,
            knowledgeHost: inputs.Knowledge);
    }
}

/// <summary>What the CLI composition root hands <see cref="CliRunnerComposition"/>.</summary>
internal sealed record CliRunnerInputs(
    IChatModel Model,
    IToolRegistry Tools,
    IPolicyEngine Policy,
    IApprovalProvider Approval,
    IAuditSink Audit,
    ITaskStore Store,
    TimeProvider Time,
    ILogger<AgentRunner> Logger,
    AgentRunnerOptions Options,
    ISkillRegistry Skills,
    ISystemMessageStore? SystemMessages,
    IModelInvocationBroker? SummarizerBroker,
    IKnowledgeTaskHost? Knowledge);
