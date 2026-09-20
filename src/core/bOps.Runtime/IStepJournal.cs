// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using bOps.Abstractions;

namespace bOps.Runtime;

/// <summary>
/// Where a delegated role records its side-effecting steps in two durable writes (ADR-0030 section 7): the intent, committed
/// before the step runs, and the outcome, committed after. The runner calls it around every step above <see cref="RiskLevel.Read"/>
/// and does not run the step when the intent could not be committed, so a step that ran is always in the journal. Internal: the
/// orchestrator supplies it and nothing else can attach one to a step.
/// </summary>
internal interface IStepJournal
{
    /// <summary>Commits the intent to run a step. Returns only when it is durable; throws when it could not be written, which stops the step.</summary>
    /// <param name="stepIndex">The step's index within the approved plan.</param>
    /// <param name="toolName">The tool the step calls.</param>
    /// <param name="arguments">The step's arguments; only their hash is kept.</param>
    Task BeginAsync(int stepIndex, string toolName, ToolArguments arguments);

    /// <summary>Commits how the step ended.</summary>
    /// <param name="stepIndex">The step's index within the approved plan.</param>
    /// <param name="outcome">How it ended.</param>
    Task CompleteAsync(int stepIndex, StepOutcome outcome);
}
