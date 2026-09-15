// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>
/// One step the model intends to take as part of achieving the goal — a stated intention, not a
/// commitment. The runtime never executes a <see cref="PlannedStep"/> directly: it still asks
/// the model, at each iteration, for the concrete <see cref="ModelToolCall"/> — with real
/// arguments — that the step requires (agentic/00-project-spec.md, principle 1: the LLM never
/// touches the machine, it only proposes). See <see cref="AgentPlan"/>.
/// </summary>
public sealed record PlannedStep
{
    /// <summary>Creates a planned step.</summary>
    /// <param name="Index">This step's position within its <see cref="AgentPlan"/>, starting at zero.</param>
    /// <param name="Description">What this step is meant to accomplish, in the model's own words.</param>
    /// <param name="ExpectedTool">The tool the model expects to use for this step, when it already knows; <c>null</c> if undecided.</param>
    public PlannedStep(int Index, string Description, string? ExpectedTool)
    {
        this.Index = Index;
        this.Description = Description;
        this.ExpectedTool = ExpectedTool;
    }

    /// <summary>This step's position within its <see cref="AgentPlan"/>, starting at zero.</summary>
    public int Index { get; init; }

    /// <summary>What this step is meant to accomplish, in the model's own words.</summary>
    public string Description { get; init; }

    /// <summary>The tool the model expects to use for this step, when it already knows; <c>null</c> if undecided.</summary>
    public string? ExpectedTool { get; init; }
}

/// <summary>
/// One revision of the model's plan for a task. <see cref="Revision"/> starts at 0 for the plan
/// produced before the first step executes, and increases by one each time the runtime asks the
/// model to replan — after a step's outcome deviates from what a working plan should produce, or
/// after the model continues past every step the current plan named
/// (agentic/01-architecture-rules.md, rule C8). A task's complete planning history is kept in
/// <see cref="TaskState.Plans"/>, distinct from <see cref="TaskState.Steps"/>, which records
/// tool-call iterations, not the act of planning itself.
/// </summary>
public sealed record AgentPlan
{
    /// <summary>Creates a plan revision.</summary>
    /// <param name="Revision">Which revision this is: 0 for the initial plan, incrementing by one on each replan.</param>
    /// <param name="Rationale">The model's stated reasoning for this plan, or for why it changed from the previous one.</param>
    /// <param name="Steps">The steps the model currently intends to take. May be empty when planning could not produce a usable response.</param>
    public AgentPlan(int Revision, string Rationale, IReadOnlyList<PlannedStep> Steps)
    {
        this.Revision = Revision;
        this.Rationale = Rationale;
        this.Steps = Steps;
    }

    /// <summary>Which revision this is: 0 for the initial plan, incrementing by one on each replan.</summary>
    public int Revision { get; init; }

    /// <summary>The model's stated reasoning for this plan, or for why it changed from the previous one.</summary>
    public string Rationale { get; init; }

    /// <summary>The steps the model currently intends to take. May be empty when planning could not produce a usable response.</summary>
    public IReadOnlyList<PlannedStep> Steps { get; init; }
}
