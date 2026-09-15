// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Abstractions;

/// <summary>
/// How an agent task ended. Named to avoid colliding with <see cref="System.Threading.Tasks.TaskStatus"/>,
/// which is in scope everywhere via implicit usings — not a numeric-suffix collision dodge
/// (agentic/02-coding-standards.md forbids those), a genuinely more specific name for what this
/// actually is: the outcome of a bOps agent task, not of a .NET <see cref="System.Threading.Tasks.Task"/>.
/// </summary>
public enum AgentTaskStatus
{
    /// <summary>The task is still executing.</summary>
    Running,

    /// <summary>The model reported the goal achieved.</summary>
    Completed,

    /// <summary>The task's step budget was exhausted before the model reported completion.</summary>
    MaxStepsReached,

    /// <summary>A configured budget (tokens, cost, or time) was exceeded.</summary>
    BudgetExceeded,

    /// <summary>The task ended after repeated policy denials of the same tool (agentic/01-architecture-rules.md, rule C4).</summary>
    PolicyBlocked,

    /// <summary>
    /// The task ended after exhausting its configured replan budget without reaching completion
    /// (agentic/01-architecture-rules.md, rule C8) — the plan kept needing revision faster than
    /// the task made progress.
    /// </summary>
    ReplanLimitReached,

    /// <summary>The task ended because of an unrecoverable runtime failure, not a tool or model outcome.</summary>
    Failed,

    /// <summary>The task was cancelled.</summary>
    Cancelled,
}

/// <summary>One iteration of the agent loop: the tool call requested (if any), its result, and the observation fed back to the model.</summary>
public sealed record PlanStep
{
    /// <summary>Creates a plan step.</summary>
    /// <param name="Index">This step's position in the task, starting at zero.</param>
    /// <param name="Description">A short human-readable label for this step, when one is available.</param>
    /// <param name="ToolCall">The tool call requested by the model, if any.</param>
    /// <param name="Result">The tool call's result, if a tool call was made.</param>
    /// <param name="Observation">The text fed back to the model as the outcome of this step.</param>
    /// <param name="PlanRevision">Which <see cref="AgentPlan.Revision"/> was in effect when this step's tool call was proposed; <c>null</c> when no plan was ever established (agentic/01-architecture-rules.md, rule C8).</param>
    public PlanStep(int Index, string? Description, ModelToolCall? ToolCall, ToolCallResult? Result, string? Observation, int? PlanRevision = null)
    {
        this.Index = Index;
        this.Description = Description;
        this.ToolCall = ToolCall;
        this.Result = Result;
        this.Observation = Observation;
        this.PlanRevision = PlanRevision;
    }

    /// <summary>This step's position in the task, starting at zero.</summary>
    public int Index { get; init; }

    /// <summary>A short human-readable label for this step, when one is available.</summary>
    public string? Description { get; init; }

    /// <summary>The tool call requested by the model, if any.</summary>
    public ModelToolCall? ToolCall { get; init; }

    /// <summary>The tool call's result, if a tool call was made.</summary>
    public ToolCallResult? Result { get; init; }

    /// <summary>The text fed back to the model as the outcome of this step.</summary>
    public string? Observation { get; init; }

    /// <summary>Which <see cref="AgentPlan.Revision"/> was in effect when this step's tool call was proposed; <c>null</c> when no plan was ever established.</summary>
    public int? PlanRevision { get; init; }
}

/// <summary>
/// The state of one agent task: the goal, every step taken, and how it ended. Scoped to a
/// single <see cref="NodeId"/> — a task does not span nodes (agentic/01-architecture-rules.md, rule A3).
/// </summary>
public sealed record TaskState
{
    /// <summary>Creates a task state.</summary>
    /// <param name="Id">The task's unique id.</param>
    /// <param name="Node">The node this task runs on.</param>
    /// <param name="Goal">The operator's original goal.</param>
    /// <param name="Status">How the task ended, or that it is still running.</param>
    /// <param name="Steps">Every step taken so far, in order.</param>
    /// <param name="Plans">Every plan revision produced for this task, in order — the initial plan at revision 0, and one more entry per replan (agentic/01-architecture-rules.md, rule C8). Distinct from <paramref name="Steps"/>, which records tool-call iterations, not planning itself.</param>
    /// <param name="CreatedAtUtc">When the task was created, in UTC.</param>
    public TaskState(Guid Id, NodeId Node, string Goal, AgentTaskStatus Status, IReadOnlyList<PlanStep> Steps, IReadOnlyList<AgentPlan> Plans, DateTimeOffset CreatedAtUtc)
    {
        this.Id = Id;
        this.Node = Node;
        this.Goal = Goal;
        this.Status = Status;
        this.Steps = Steps;
        this.Plans = Plans;
        this.CreatedAtUtc = CreatedAtUtc;
    }

    /// <summary>The task's unique id.</summary>
    public Guid Id { get; init; }

    /// <summary>The node this task runs on.</summary>
    public NodeId Node { get; init; }

    /// <summary>The operator's original goal.</summary>
    public string Goal { get; init; }

    /// <summary>How the task ended, or that it is still running.</summary>
    public AgentTaskStatus Status { get; init; }

    /// <summary>Every step taken so far, in order.</summary>
    public IReadOnlyList<PlanStep> Steps { get; init; }

    /// <summary>Every plan revision produced for this task, in order — the initial plan at revision 0, and one more entry per replan. Distinct from <see cref="Steps"/>, which records tool-call iterations, not planning itself.</summary>
    public IReadOnlyList<AgentPlan> Plans { get; init; }

    /// <summary>When the task was created, in UTC.</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }
}
