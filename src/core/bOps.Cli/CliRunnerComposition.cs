// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;
using bOps.AgentFramework;
using bOps.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace bOps.Cli;

/// <summary>
/// The one place the CLI builds its operational runner: the governed model broker with the MAF context projector, and the
/// <see cref="AgentRunner"/> over it (AF-01, ADR-0052). It is the composition <c>Program.cs</c> runs, extracted so a test can drive the same
/// code with a scripted model; it adds nothing to the runner's authority. The knowledge host is the optional last input (ADR-0058): when it
/// is <c>null</c> the runner is built exactly as it was before knowledge was composable. The last, optional input is the operator's
/// opt-in to the MAF governed turn host (BOPS-0122, C01-R22): off by default, and then the composition is the one that ran before it existed.
/// The two switches are independent: Knowledge is served by the runner (the index, the control function, the audited reads), so with the
/// MAF host on it reaches the model as exactly the protected context the runner authorised, and the final context gate compares it as
/// it compares any other turn.
/// </summary>
internal static class CliRunnerComposition
{
    /// <summary>The host configuration key that opts in to the MAF governed turn host. Absent or <c>false</c> is the default path.</summary>
    internal const string MafGovernedTurnKey = "Cli:Experimental:MafGovernedTurn";

    /// <summary>
    /// Reads the opt-in. A missing or empty value is the default (off); anything but a boolean is refused, so a typo can never be read
    /// as a silent "off" or "on".
    /// </summary>
    internal static bool ReadMafGovernedTurn(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[MafGovernedTurnKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return bool.TryParse(raw, out var enabled)
            ? enabled
            : throw new InvalidOperationException($"'{MafGovernedTurnKey}' must be 'true' or 'false' (found '{raw}').");
    }

    /// <summary>Builds the runner of a goal, a resume, a recovery or a delegation.</summary>
    internal static AgentRunner Create(CliRunnerInputs inputs) => Compose(inputs).Runner;

    /// <summary>
    /// Builds the runner and returns the model invocation broker it was given, so a test can tell which path was composed. When
    /// <see cref="CliRunnerInputs.MafGovernedTurn"/> is set, every model call is one governed MAF turn in front of the same governed broker
    /// (C01-R22); the broker still projects the context, retries, accounts and audits, and the runner is not told the difference.
    /// Knowledge, when composed, is independent of that switch.
    /// </summary>
    internal static (AgentRunner Runner, IModelInvocationBroker Broker) Compose(CliRunnerInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var governed = new ModelInvocationBroker(
            inputs.Model, inputs.Audit, inputs.Time, inputs.Logger, inputs.Options,
            contextProjector: new MafContextProjector(inputs.Options, inputs.Logger),
            dedicatedSummarizer: inputs.SummarizerBroker);
        IModelInvocationBroker broker = inputs.MafGovernedTurn
            ? new MafGovernedTurnBroker(governed, inputs.Audit, inputs.Time, MafContextAssembly.BopsProvider)
            : governed;

        var runner = new AgentRunner(
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
        return (runner, broker);
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
    IKnowledgeTaskHost? Knowledge,
    bool MafGovernedTurn = false);
