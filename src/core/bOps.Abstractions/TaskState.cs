// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

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

/// <summary>
/// One call the runtime made to a model, recorded with the step or plan it produced so a task can be
/// troubleshot from what was actually sent and received. The request and reply bodies are bounded by
/// the runtime and marked when cut short.
/// </summary>
public sealed record ModelCallRecord
{
    /// <summary>Creates a model call record.</summary>
    /// <param name="Provider">The provider id that was called.</param>
    /// <param name="RequestedModel">The model bOps asked for, as configured.</param>
    /// <param name="ActualModel">The model the provider reports having served, when it says; can differ from <paramref name="RequestedModel"/>.</param>
    /// <param name="StartedAtUtc">When the call started, in UTC.</param>
    /// <param name="DurationMs">How long the call took, in milliseconds, including any transient retry the adapter made.</param>
    /// <param name="Outcome">Whether the call produced a reply.</param>
    /// <param name="Usage">Tokens sent and received, when the provider reports them.</param>
    /// <param name="FinishReason">Why the provider stopped generating, when it says.</param>
    /// <param name="ErrorMessage">Present when <paramref name="Outcome"/> is <see cref="ModelCallOutcome.Failure"/>.</param>
    /// <param name="RequestJson">The request body as sent, possibly cut short; <c>null</c> when the adapter or the configuration keeps none.</param>
    /// <param name="ResponseJson">The reply body as received, possibly cut short; <c>null</c> when the adapter or the configuration keeps none.</param>
    /// <param name="PayloadTruncated">Whether <paramref name="RequestJson"/> or <paramref name="ResponseJson"/> was cut to the configured limit.</param>
    public ModelCallRecord(
        string Provider, string RequestedModel, string? ActualModel, DateTimeOffset StartedAtUtc, long DurationMs,
        ModelCallOutcome Outcome, ModelUsage? Usage, string? FinishReason, string? ErrorMessage,
        string? RequestJson, string? ResponseJson, bool PayloadTruncated)
    {
        this.Provider = Provider;
        this.RequestedModel = RequestedModel;
        this.ActualModel = ActualModel;
        this.StartedAtUtc = StartedAtUtc;
        this.DurationMs = DurationMs;
        this.Outcome = Outcome;
        this.Usage = Usage;
        this.FinishReason = FinishReason;
        this.ErrorMessage = ErrorMessage;
        this.RequestJson = RequestJson;
        this.ResponseJson = ResponseJson;
        this.PayloadTruncated = PayloadTruncated;
    }

    /// <summary>The provider id that was called.</summary>
    public string Provider { get; init; }

    /// <summary>The model bOps asked for, as configured.</summary>
    public string RequestedModel { get; init; }

    /// <summary>The model the provider reports having served, when it says.</summary>
    public string? ActualModel { get; init; }

    /// <summary>When the call started, in UTC.</summary>
    public DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>How long the call took, in milliseconds.</summary>
    public long DurationMs { get; init; }

    /// <summary>Whether the call produced a reply.</summary>
    public ModelCallOutcome Outcome { get; init; }

    /// <summary>Tokens sent and received, when the provider reports them.</summary>
    public ModelUsage? Usage { get; init; }

    /// <summary>Why the provider stopped generating, when it says.</summary>
    public string? FinishReason { get; init; }

    /// <summary>Present when the call failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The request body as sent, possibly cut short.</summary>
    public string? RequestJson { get; init; }

    /// <summary>The reply body as received, possibly cut short.</summary>
    public string? ResponseJson { get; init; }

    /// <summary>Whether a body was cut to the configured limit.</summary>
    public bool PayloadTruncated { get; init; }

    /// <summary>Which attempt of one logical model call this record is, starting at 1 (ADR-0039); <c>null</c> for a record written before attempts were recorded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ModelAttempt { get; init; }

    /// <summary>The attempt number within the current provider candidate.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProviderAttempt { get; init; }

    /// <summary>Zero for the primary, then one-based for configured fallback candidates.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FallbackOrdinal { get; init; }

    /// <summary>Why this attempt failed, in provider-neutral terms, when it failed and was classified.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelFailureKind? FailureKind { get; init; }

    /// <summary>What the runtime decided after this failed attempt.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelRetryDecision? RetryDecision { get; init; }

    /// <summary>How long the runtime waited before the next attempt, in milliseconds, when it retried.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RetryDelayMs { get; init; }

    /// <summary>The provider's status code for this attempt, when a response was obtained.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProviderStatusCode { get; init; }

    /// <summary>The non-secret provider configuration generation used for this call, when pinned.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ConfigurationGeneration { get; init; }

    /// <summary>The hash of the non-secret provider configuration used for this call, when pinned.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfigurationSnapshotHash { get; init; }
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

    /// <summary>The calls made to the model to obtain this step, oldest first: normally one, more when an empty reply was asked for again. <c>null</c> for a step recorded before calls were kept, or one that needed no model call.</summary>
    public IReadOnlyList<ModelCallRecord>? ModelCalls { get; init; }

    /// <summary>The tool calls the model emitted in the same turn after <see cref="ToolCall"/> and that bOps deliberately did not execute (one call per step), in emission order. Kept so the model's turn can be rebuilt exactly (ADR-0038); <c>null</c> when there were none, and in every step recorded before this was kept.</summary>
    public IReadOnlyList<ModelToolCall>? UnexecutedToolCalls { get; init; }

    /// <summary>The execution attempt that produced this step (ADR-0040 §1); <c>null</c> in a step recorded before execution attempts were kept, read as 1.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExecutionAttempt { get; init; }

    /// <summary>
    /// The typed outcome of runtime post-action verification. <c>null</c> means no verification was recorded (including
    /// persisted tasks written before HARDEN-8); model-facing compaction never infers this value from observation text.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VerificationStatus? VerificationStatus { get; init; }
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

    /// <summary>
    /// The task's current execution attempt (ADR-0040 §1): 1 for its initial execution, one more for every accepted
    /// resume. Distinct from <see cref="ModelCallRecord.ModelAttempt"/>. A task persisted before execution attempts were
    /// kept loads as 1: the source-generated deserializer writes 0 for an absent member, and any value below 1 reads as 1.
    /// </summary>
    public int ExecutionAttempt { get => field < 1 ? 1 : field; init; } = 1;

    /// <summary>What the task has consumed over its lifetime (ADR-0040 §5); <c>null</c> only for a task persisted before accounting was kept.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskAccounting? Accounting { get; init; }

    /// <summary>Who created the task (ADR-0040 §8). Set by the runtime only; <see cref="TaskOrigin.Unknown"/> for a task persisted before it was kept.</summary>
    public TaskOrigin Origin { get; init; }

    /// <summary>For a <see cref="TaskOrigin.Delegated"/> task, the delegated run it belongs to.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DelegationId { get; init; }

    /// <summary>For a <see cref="TaskOrigin.Delegated"/> task, the role it performs.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentRoleKind? DelegationRole { get; init; }

    /// <summary>Why the latest execution attempt ended; <c>null</c> while it runs and for a task persisted before this was kept.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskTerminalReason? TerminalReason { get; init; }

    /// <summary>When a resume acquired the current execution attempt; <c>null</c> for the initial execution.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ResumedAtUtc { get; init; }

    /// <summary>Who resumed the task into its current execution attempt; <c>null</c> for the initial execution.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActorIdentity? ResumedBy { get; init; }

    /// <summary>The safe provider configuration selected at admission; null on records predating ADR-0045.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PinnedProviderConfiguration? PinnedProviderConfiguration { get; init; }
}
