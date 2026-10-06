// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace bOps.Abstractions;

/// <summary>How a tool call came to be authorized, recorded on every <see cref="ToolCallAuditEvent"/>.</summary>
public enum AuthorizationKind
{
    /// <summary>Policy placed the call in <see cref="PolicyMode.Automatic"/>.</summary>
    Automatic,

    /// <summary>An operator approved the call.</summary>
    UserApproved,

    /// <summary>An operator rejected the call.</summary>
    UserRejected,

    /// <summary>Policy placed the call in <see cref="PolicyMode.Forbidden"/>.</summary>
    PolicyDenied,

    /// <summary>The model requested a tool name that does not resolve to any registered tool.</summary>
    UnknownTool,

    /// <summary>An entitlement decision denied the call.</summary>
    EntitlementDenied,
}

/// <summary>
/// Something that happened, worth recording forever. Every tool call, every model call, and
/// every policy decision produces one of these — including denials, rejections and timeouts,
/// which are often the most interesting events in the log (agentic/00-project-spec.md,
/// principle 4; agentic/03-security-rules.md, rule S9).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "eventType")]
[JsonDerivedType(typeof(ToolCallAuditEvent), "toolCall")]
[JsonDerivedType(typeof(ModelCallAuditEvent), "modelCall")]
[JsonDerivedType(typeof(PolicyDecisionAuditEvent), "policyDecision")]
[JsonDerivedType(typeof(ApprovalAuditEvent), "approval")]
[JsonDerivedType(typeof(SkillRunAuditEvent), "skillRun")]
[JsonDerivedType(typeof(SettingsChangedAuditEvent), "settingsChanged")]
[JsonDerivedType(typeof(DelegationLifecycleAuditEvent), "delegationLifecycle")]
[JsonDerivedType(typeof(DelegationEnvelopeAuditEvent), "delegationEnvelope")]
[JsonDerivedType(typeof(DelegationJournalAuditEvent), "delegationJournal")]
[JsonDerivedType(typeof(DelegationReconciliationAuditEvent), "delegationReconciliation")]
[JsonDerivedType(typeof(EntitlementDecisionAuditEvent), "entitlementDecision")]
[JsonDerivedType(typeof(PluginLifecycleAuditEvent), "pluginLifecycle")]
[JsonDerivedType(typeof(TaskExecutionFaultAuditEvent), "taskExecutionFault")]
[JsonDerivedType(typeof(TaskLifecycleAuditEvent), "taskLifecycle")]
[JsonDerivedType(typeof(EvidenceReadAuditEvent), "evidenceRead")]
public abstract record AuditEvent
{
    /// <summary>When this event occurred, in UTC.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>The node this event happened on.</summary>
    public required NodeId Node { get; init; }

    /// <summary>The task this event belongs to.</summary>
    public required Guid TaskId { get; init; }

    /// <summary>
    /// The step, within the task, this event belongs to. Evidence-read events expose their additive nullable member of the
    /// same name; this frozen 1.0 integer member remains for source and binary compatibility.
    /// </summary>
    [JsonIgnore]
    public int StepIndex { get; init; } = -1;

    /// <summary>JSON bridge retaining the historic <c>StepIndex</c> field while allowing an evidence-read event to write null.</summary>
    [JsonPropertyName(nameof(StepIndex))]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public int? SerializedStepIndex
    {
        get => this is EvidenceReadAuditEvent evidenceRead ? evidenceRead.StepIndex : StepIndex;
        init
        {
            if (this is EvidenceReadAuditEvent evidenceRead)
            {
                evidenceRead.SetStepIndex(value);
            }
            else
            {
                StepIndex = value ?? -1;
            }
        }
    }

    /// <summary>Who caused this event: the operator who launched the task, or who approved/rejected the step.</summary>
    public required ActorIdentity Actor { get; init; }

    /// <summary>
    /// Which delegated run, agent and envelope this event belongs to (ADR-0030 section 8). <c>null</c> for
    /// every event of a run that is not delegated, and then omitted from the serialized event, so such an
    /// event is byte-identical to one written before delegation existed. It sits on the base type so a
    /// delegated run's events cannot forget it: every event of one carries both the operator
    /// (<see cref="Actor"/>) and the agent.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DelegationCorrelation? Delegation { get; init; }
}

/// <summary>The closed outcome domain of one attempted internal <c>EvidenceRead/v1</c> directive (ADR-0014 HARDEN-8).</summary>
public enum EvidenceReadResultCode
{
    /// <summary>A non-empty fragment was returned.</summary>
    Success = 0,
    /// <summary>The requested offset was exactly at the end of the source.</summary>
    EndOfEvidence = 1,
    /// <summary>The claimed directive did not have the exact required shape.</summary>
    Malformed = 2,
    /// <summary>The evidence id was malformed, noncanonical or ambiguous.</summary>
    InvalidId = 3,
    /// <summary>The id named a task other than the task currently executing.</summary>
    CrossTaskRejected = 4,
    /// <summary>No addressable tool-call step matched the id.</summary>
    MissingStep = 5,
    /// <summary>The selected persisted source was absent.</summary>
    UnavailableSource = 6,
    /// <summary>The requested UTF-16 range was invalid.</summary>
    OutOfRange = 7,
    /// <summary>The logical call attempted more than four reads.</summary>
    LimitExceeded = 8,
    /// <summary>The execution attempt's active-duration budget expired while processing the read.</summary>
    AttemptBudgetInterrupted = 9,
    /// <summary>Initial planning does not permit evidence reads.</summary>
    NotAllowedInPhase = 10,
}

/// <summary>
/// Metadata-only record of an attempted internal evidence read. It never contains evidence text, model payloads, tool
/// arguments or datastore details and is emitted even when model-payload retention is disabled.
/// </summary>
public sealed record EvidenceReadAuditEvent : AuditEvent
{
    private int? evidenceStepIndex;

    /// <summary>The step or triggering replan index; <c>null</c> during initial planning.</summary>
    [JsonIgnore]
    public new int? StepIndex
    {
        get => evidenceStepIndex;
        init => evidenceStepIndex = value;
    }

    internal void SetStepIndex(int? value) => evidenceStepIndex = value;

    /// <summary>The plan revision for a replan read; <c>null</c> for a normal step and initial planning.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PlanRevision { get; init; }

    /// <summary>The bounded claimed evidence id, or <c>null</c> when it could not be read from a malformed claim.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceId { get; init; }

    /// <summary>The claimed source, or <c>null</c> when malformed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    /// <summary>The requested zero-based UTF-16 offset, or <c>null</c> when malformed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Offset { get; init; }

    /// <summary>The requested UTF-16 length, or <c>null</c> when malformed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RequestedLength { get; init; }

    /// <summary>The number of UTF-16 code units returned; always zero for a rejection.</summary>
    public required int ReturnedLength { get; init; }

    /// <summary>How the attempt ended.</summary>
    public required EvidenceReadResultCode ResultCode { get; init; }
}

/// <summary>
/// A local plugin lifecycle action (ADR-0037): archive install/replace attempt, validation or trust
/// result, commit, enable, disable, activation failure, recovery, stale-precondition refusal, or
/// idempotent replay/conflict. Every field is a neutral category or identifier. It never carries
/// archive bytes, signatures, keys, trust-store content, credentials, stacks, or local paths. Like
/// <see cref="SettingsChangedAuditEvent"/>, it is produced by an administrator acting directly, so
/// <see cref="AuditEvent.TaskId"/> and <see cref="AuditEvent.StepIndex"/> are the documented sentinels
/// (<see cref="Guid.Empty"/> and <c>-1</c>).
/// </summary>
public sealed record PluginLifecycleAuditEvent : AuditEvent
{
    /// <summary>The lifecycle operation, for example <c>install</c>, <c>enable</c>, <c>disable</c> or <c>recover</c>.</summary>
    public required string Operation { get; init; }

    /// <summary>The validation/transaction stage the event describes, for example <c>signature</c>, <c>commit</c> or <c>idempotency</c>.</summary>
    public required string Stage { get; init; }

    /// <summary>Neutral outcome category, for example <c>Succeeded</c>, <c>StaleVersion</c> or <c>IdempotencyConflict</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>The manifest plugin id, once known.</summary>
    public string? PluginId { get; init; }

    /// <summary>The manifest plugin version, once known.</summary>
    public string? PluginVersion { get; init; }

    /// <summary>The persisted lifecycle state before the action, when the plugin existed.</summary>
    public string? PriorState { get; init; }

    /// <summary>The persisted lifecycle state after the action, when the plugin exists.</summary>
    public string? NewState { get; init; }

    /// <summary>The authoritative lifecycle revision after the action (0 when the plugin does not exist).</summary>
    public long LifecycleVersion { get; init; }

    /// <summary>The publisher trust level category (<c>Verified</c>, <c>Community</c>, <c>Unverified</c>...) for trust results; never key material.</summary>
    public string? PublisherTrust { get; init; }

    /// <summary>Caller-supplied correlation identifier, when present.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Whether an idempotency key accompanied the request.</summary>
    public bool IdempotencyKeyPresent { get; init; }

    /// <summary>Whether the result replays an earlier logical operation for the same idempotency scope.</summary>
    public bool IdempotentReplay { get; init; }
}

/// <summary>
/// Neutral evidence of the host applicability result or entitlement evaluation for one tool invocation.
/// This record is audit evidence only and must never be reused as execution authority.
/// </summary>
public sealed record EntitlementDecisionAuditEvent : AuditEvent
{
    /// <summary>The package that contributed the evaluated tool.</summary>
    public required PackageId Package { get; init; }

    /// <summary>The tool invocation to which this evaluation belongs.</summary>
    public required string Tool { get; init; }

    /// <summary>Host-owned applicability stamped on the execution registration.</summary>
    public required EntitlementApplicability Applicability { get; init; }

    /// <summary>The governed evaluation result; null when applicability is <see cref="EntitlementApplicability.NotGoverned"/>.</summary>
    public EntitlementDecisionKind? Result { get; init; }

    /// <summary>Neutral evaluator source category, when a governed evaluation was attempted.</summary>
    public EntitlementSourceCategory? Source { get; init; }

    /// <summary>Neutral result reason, including <see cref="EntitlementReasonCode.Unavailable"/> for a fail-closed provider failure.</summary>
    public EntitlementReasonCode? Reason { get; init; }

    /// <summary>
    /// Opaque correlation evidence for the evaluation attempt. It is not a credential, token, proof of current entitlement,
    /// or reusable execution authority.
    /// </summary>
    public RequestBinding? Binding { get; init; }

    /// <summary>Non-secret authority/freshness correlation identifier, when supplied by the evaluator.</summary>
    public string? AuthorityId { get; init; }

    /// <summary>Beginning of the accepted validity envelope, when supplied by the evaluator.</summary>
    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>End of the accepted validity envelope, when supplied by the evaluator.</summary>
    public DateTimeOffset? ValidUntil { get; init; }

    /// <summary>Neutral effective limits/constraints or accepted constraint result metadata.</summary>
    public EntitlementConstraints? Constraints { get; init; }
}

/// <summary>
/// A tool was called (or its call was denied before execution). <see cref="Arguments"/> must
/// already be redacted per <see cref="ToolParameter.Sensitive"/> before this event is
/// constructed — redaction happens at the <see cref="ToolArguments"/> boundary, not here
/// (agentic/03-security-rules.md, rule S6).
/// </summary>
public sealed record ToolCallAuditEvent : AuditEvent
{
    /// <summary>The package that contributed the tool.</summary>
    public required PackageId Package { get; init; }

    /// <summary>The tool's name.</summary>
    public required string Tool { get; init; }

    /// <summary>Already redacted. Never the raw <see cref="ToolArguments"/>.</summary>
    public required JsonObject Arguments { get; init; }

    /// <summary>The tool's declared risk.</summary>
    public required RiskLevel Risk { get; init; }

    /// <summary>How this call came to be authorized.</summary>
    public required AuthorizationKind Authorization { get; init; }

    /// <summary>How the call ended.</summary>
    public required ToolOutcome Outcome { get; init; }

    /// <summary>How long the call took, measured by the runtime.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Optional bounded, non-sensitive aggregate metadata supplied by the tool. The runtime never
    /// copies a complete tool output here; large exact datasets remain behind scoped references.
    /// </summary>
    public JsonObject? Summary { get; init; }

    /// <summary>
    /// <c>null</c> for a <see cref="RiskLevel.Read"/> tool, which has nothing to verify, and for
    /// a non-<see cref="RiskLevel.Read"/> call that was never executed at all (denied by policy
    /// or an operator, or rejected by argument validation) — there is nothing to check the effect
    /// of. Set for every non-<see cref="RiskLevel.Read"/> call that was actually attempted,
    /// whatever its <see cref="Outcome"/> (V0.4; agentic/03-security-rules.md, rule S4).
    /// </summary>
    public VerificationStatus? Verification { get; init; }

    /// <summary>The Skill run that originated this call, when applicable.</summary>
    public Guid? SkillRunId { get; init; }

    /// <summary>The originating Skill, when applicable.</summary>
    public string? SkillId { get; init; }

    /// <summary>The originating Capability, when applicable.</summary>
    public string? CapabilityName { get; init; }

    /// <summary>The contextual target, when applicable.</summary>
    public string? Target { get; init; }

    /// <summary>The contextual environment, when applicable.</summary>
    public string? Environment { get; init; }

    /// <summary>The contextual blast radius, when applicable.</summary>
    public BlastRadius? BlastRadius { get; init; }

    /// <summary>
    /// The structured failure classification of the call (HARDEN-6, ADR-0022). <c>null</c> for a success and for any result that
    /// carried no classification, so a record written before HARDEN-6 and one without it serialize identically.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolFailureKind? FailureKind { get; init; }

    /// <summary>
    /// How complete the evidence of a successful call was (HARDEN-6, ADR-0022). <c>null</c> when the tool declared nothing.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolResultCompleteness? Completeness { get; init; }

    /// <summary>The immutable plan hash during execution, absent during evidence gathering.</summary>
    public string? PlanHash { get; init; }
}

/// <summary>
/// How a call to an LLM provider ended (agentic/architecture/adr/0013). A model call can fail —
/// unreachable provider, a non-success HTTP status, a response that cannot be parsed — and rule
/// S9 requires that failure to be audited exactly like a successful call, not silently dropped.
/// </summary>
public enum ModelCallOutcome
{
    /// <summary>The provider returned a usable response.</summary>
    Success,

    /// <summary>The call could not be completed. See the audited event's <see cref="ModelCallAuditEvent.ErrorMessage"/>.</summary>
    Failure,
}

/// <summary>A call was made to an LLM provider. Distinct from a tool call — see plan §5.3.</summary>
public sealed record ModelCallAuditEvent : AuditEvent
{
    /// <summary>The provider id that served this call.</summary>
    public required string Provider { get; init; }

    /// <summary>The specific model that served this call.</summary>
    public required string Model { get; init; }

    /// <summary>Whether the call succeeded. See ADR-0013.</summary>
    public required ModelCallOutcome Outcome { get; init; }

    /// <summary>Present when <see cref="Outcome"/> is <see cref="ModelCallOutcome.Failure"/>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Token and cost accounting, when the provider reports it.</summary>
    public ModelUsage? Usage { get; init; }

    /// <summary>
    /// The model the provider reports having served, which can differ from <see cref="Model"/> (a router
    /// such as <c>openrouter/free</c> picks one). <c>null</c> when the provider does not say, and then
    /// omitted from the serialized event, so such an event is byte-identical to one written before this
    /// field existed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActualModel { get; init; }

    /// <summary>How long the call took, in milliseconds; <c>null</c> (and omitted) for an event written before it was measured.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; init; }

    /// <summary>
    /// Which attempt of one logical model call this event records, starting at 1 (ADR-0039): every attempt is its own
    /// event. Named for the model call, not the task — a task execution attempt is a different concept. <c>null</c> (and
    /// omitted) for an event written before attempts were recorded.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ModelAttempt { get; init; }

    /// <summary>The attempt number within the current provider candidate.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProviderAttempt { get; init; }

    /// <summary>Zero for the primary, then one-based for configured fallback candidates.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FallbackOrdinal { get; init; }

    /// <summary>Why this attempt failed, in provider-neutral terms; present only when <see cref="Outcome"/> is <see cref="ModelCallOutcome.Failure"/> and the attempt was classified.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelFailureKind? FailureKind { get; init; }

    /// <summary>What the runtime decided after this failed attempt; every value except <see cref="ModelRetryDecision.Retry"/> marks the last attempt.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelRetryDecision? RetryDecision { get; init; }

    /// <summary>How long the runtime waits before the next attempt, in milliseconds, when <see cref="RetryDecision"/> is <see cref="ModelRetryDecision.Retry"/>.</summary>
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

/// <summary>
/// An unexpected exception escaped the runner's normal containment and the host performed the fail-safe transition of a
/// task still persisted <see cref="AgentTaskStatus.Running"/> to <see cref="AgentTaskStatus.Failed"/> (ADR-0039 §9).
/// Deliberately narrow: it is written only when that transition happened, and it is not the general task lifecycle
/// record. <see cref="AuditEvent.StepIndex"/> is the index of the synthetic failure step. It never carries a stack
/// trace, request or response bodies, credentials, headers, prompt content or tool secrets.
/// </summary>
public sealed record TaskExecutionFaultAuditEvent : AuditEvent
{
    /// <summary>The type name of the exception that escaped, without its namespace.</summary>
    public required string ExceptionType { get; init; }

    /// <summary>The persisted reason, redacted and bounded.</summary>
    public required string Reason { get; init; }
}

/// <summary>Which point of a task's lifecycle a <see cref="TaskLifecycleAuditEvent"/> records (ADR-0040 §10).</summary>
public enum TaskLifecycleStage
{
    /// <summary>An execution attempt began: the initial execution of a task, or an admitted resumed attempt.</summary>
    ExecutionStarted = 0,

    /// <summary>A resume acquired the task: it was atomically moved to <see cref="AgentTaskStatus.Running"/> under a new execution attempt.</summary>
    ResumeAccepted = 1,

    /// <summary>A resume of a stored task was refused, lost the transition to another writer, or the store cannot perform it.</summary>
    ResumeRejected = 2,

    /// <summary>An execution attempt reached a terminal status.</summary>
    ExecutionTerminal = 3,

    /// <summary>An executor was fenced: the task had been taken over, so its write was refused and it stopped.</summary>
    ExecutionSuperseded = 4,
}

/// <summary>
/// A task lifecycle transition (ADR-0040 §10): an execution attempt starting or ending, a resume accepted or rejected, an
/// executor superseded. Every event names the <see cref="ExecutionAttempt"/> it concerns and a budget snapshot, so a task's
/// attempts can be followed in the log. Distinct from <see cref="TaskExecutionFaultAuditEvent"/>, which keeps its narrow
/// ADR-0039 meaning. It never carries the goal, prompts, model or tool payloads, refusal text, credentials or stack traces.
/// <see cref="AuditEvent.StepIndex"/> is the task's step count when the event was written.
/// </summary>
public sealed record TaskLifecycleAuditEvent : AuditEvent
{
    /// <summary>True only when a legacy unpinned task captured its first durable provider pin during resume.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LegacyConfigurationMigrated { get; init; }
    /// <summary>What happened.</summary>
    public required TaskLifecycleStage Stage { get; init; }

    /// <summary>The execution attempt concerned: the attempt that started, ended or was superseded; for a resume, the new attempt when accepted and the persisted one when rejected.</summary>
    public required int ExecutionAttempt { get; init; }

    /// <summary>The task's origin.</summary>
    public required TaskOrigin Origin { get; init; }

    /// <summary>For a resume stage, the status the task had before the resume.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentTaskStatus? PriorStatus { get; init; }

    /// <summary>The task's status after the event (for a rejection, its unchanged status).</summary>
    public required AgentTaskStatus Status { get; init; }

    /// <summary>For <see cref="TaskLifecycleStage.ExecutionTerminal"/>, why the attempt ended.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaskTerminalKind? TerminalKind { get; init; }

    /// <summary>For <see cref="TaskLifecycleStage.ResumeRejected"/>, the stable refusal code (for example <c>task_running</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefusalCode { get; init; }

    /// <summary>Tokens the task has used over its lifetime.</summary>
    public required long TokensUsed { get; init; }

    /// <summary>Executable steps the task has taken over its lifetime.</summary>
    public required int LifetimeSteps { get; init; }

    /// <summary>Replans the task has made over its lifetime.</summary>
    public required int LifetimeReplans { get; init; }

    /// <summary>The configured cumulative token cap, or <c>null</c> when none is configured.</summary>
    public int? MaxTotalTokens { get; init; }

    /// <summary>The configured per-attempt step cap.</summary>
    public required int MaxSteps { get; init; }

    /// <summary>The configured lifetime step cap.</summary>
    public required int MaxLifetimeSteps { get; init; }

    /// <summary>The configured per-attempt replan cap.</summary>
    public required int MaxReplans { get; init; }

    /// <summary>The configured lifetime replan cap.</summary>
    public required int MaxLifetimeReplans { get; init; }
}

/// <summary>A policy decision was made for a tool call, whatever the outcome that followed.</summary>
public sealed record PolicyDecisionAuditEvent : AuditEvent
{
    /// <summary>The package that contributed the tool.</summary>
    public required PackageId Package { get; init; }

    /// <summary>The tool's name.</summary>
    public required string Tool { get; init; }

    /// <summary>The mode policy decided.</summary>
    public required PolicyMode Mode { get; init; }

    /// <summary>Why this mode was decided.</summary>
    public required string Reason { get; init; }

    /// <summary>The Skill run that originated this decision, when applicable.</summary>
    public Guid? SkillRunId { get; init; }

    /// <summary>The originating Skill, when applicable.</summary>
    public string? SkillId { get; init; }

    /// <summary>The originating Capability, when applicable.</summary>
    public string? CapabilityName { get; init; }

    /// <summary>The contextual target, when applicable.</summary>
    public string? Target { get; init; }

    /// <summary>The contextual environment, when applicable.</summary>
    public string? Environment { get; init; }

    /// <summary>The contextual blast radius, when applicable.</summary>
    public BlastRadius? BlastRadius { get; init; }
}

/// <summary>
/// An operator approved or rejected a call policy placed in <see cref="PolicyMode.Approval"/>
/// (V0.3, ADR-0015). Distinct from <see cref="PolicyDecisionAuditEvent"/> — that records what
/// policy decided (that approval is required and why); this records what the human decided, and
/// by whom, which policy cannot know in advance. <see cref="AuditEvent.Actor"/> on the base type
/// is the actor who launched the task; <see cref="Approver"/> is who actually approved or
/// rejected the call, which is not always the same identity once remote approval exists (it is
/// today, in the CLI) — an audit log that cannot say who approved is not an audit log
/// (agentic/01-architecture-rules.md, rule B7).
/// </summary>
public sealed record ApprovalAuditEvent : AuditEvent
{
    /// <summary>The package that contributed the tool.</summary>
    public required PackageId Package { get; init; }

    /// <summary>The tool's name.</summary>
    public required string Tool { get; init; }

    /// <summary>Whether the call was approved.</summary>
    public required bool Approved { get; init; }

    /// <summary>Who actually approved or rejected the call.</summary>
    public required ActorIdentity Approver { get; init; }

    /// <summary>An optional note from the approver.</summary>
    public string? Note { get; init; }

    /// <summary>The Skill run that originated this approval, when applicable.</summary>
    public Guid? SkillRunId { get; init; }

    /// <summary>The originating Skill, when applicable.</summary>
    public string? SkillId { get; init; }

    /// <summary>The originating Capability, when applicable.</summary>
    public string? CapabilityName { get; init; }
}

/// <summary>The phase represented by a <see cref="SkillRunAuditEvent"/>.</summary>
public enum SkillRunStage
{
    /// <summary>The activated provider was resolved for use.</summary>
    ProviderResolved,

    /// <summary>Evidence gathering and plan preparation ended.</summary>
    Preparation,

    /// <summary>Execution of the prepared immutable plan ended.</summary>
    Execution,
}

/// <summary>The audited outcome of one Skill run phase.</summary>
public enum SkillRunOutcome
{
    /// <summary>The phase completed successfully.</summary>
    Success,

    /// <summary>The phase failed validation or package execution.</summary>
    Failure,

    /// <summary>The phase exceeded its declared timeout.</summary>
    Timeout,

    /// <summary>The phase was refused before executing its plan.</summary>
    Refused,
}

/// <summary>Correlates Skill provider resolution, preparation and execution without recording evidence data.</summary>
public sealed record SkillRunAuditEvent : AuditEvent
{
    /// <summary>The terminal, non-resumable V1.1 Skill run.</summary>
    public required Guid RunId { get; init; }

    /// <summary>The host-assigned package identity.</summary>
    public required PackageId Package { get; init; }

    /// <summary>The activated Skill identity.</summary>
    public required string SkillId { get; init; }

    /// <summary>The selected Capability identity.</summary>
    public required string CapabilityName { get; init; }

    /// <summary>The audited run phase.</summary>
    public required SkillRunStage Stage { get; init; }

    /// <summary>How this phase ended.</summary>
    public required SkillRunOutcome Outcome { get; init; }

    /// <summary>The immutable plan hash, once a plan exists.</summary>
    public string? PlanHash { get; init; }

    /// <summary>Number of evidence records in the report at this phase.</summary>
    public int EvidenceCount { get; init; }

    /// <summary>Number of findings in the report at this phase.</summary>
    public int FindingCount { get; init; }

    /// <summary>A bounded failure explanation, absent on success.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>How a <see cref="SettingsChangedAuditEvent"/> mutated Settings (ADR-0029).</summary>
public enum SettingsChangeOperation
{
    /// <summary>A value was stored where none existed before.</summary>
    Set,

    /// <summary>An existing value was overwritten.</summary>
    Replace,

    /// <summary>A stored value was removed.</summary>
    Clear,

    /// <summary>The active provider selection changed.</summary>
    SelectProvider,
}

/// <summary>How a <see cref="SettingsChangedAuditEvent"/> ended.</summary>
public enum SettingsChangeOutcome
{
    /// <summary>The change was applied.</summary>
    Success,

    /// <summary>The change was refused by authorization.</summary>
    Denied,

    /// <summary>The change failed — for example, a stale concurrency version or an unknown provider id.</summary>
    Failure,
}

/// <summary>
/// An administrator changed Settings (ADR-0029): a provider's stored API key or the active
/// provider selection. Never carries a secret value or a reversible fingerprint of one. Unlike
/// every other <see cref="AuditEvent"/>, this one is not produced from inside an <c>AgentTask</c>
/// — it is an administrator acting directly through the API. <see cref="AuditEvent.TaskId"/> and
/// <see cref="AuditEvent.StepIndex"/> are documented sentinels here (<see cref="Guid.Empty"/> and
/// <c>-1</c> respectively), not real task coordinates.
/// </summary>
public sealed record SettingsChangedAuditEvent : AuditEvent
{
    /// <summary>The safe effective generation after a successful mutation, when recorded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ConfigurationGeneration { get; init; }

    /// <summary>Whether the write published a new tuple, remained shadowed or inactive, or changed only a credential.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicationEffect { get; init; }
    /// <summary>The setting that changed, for example <c>"provider.apiKey"</c> or <c>"provider.active"</c>.</summary>
    public required string SettingName { get; init; }

    /// <summary>What kind of change this was.</summary>
    public required SettingsChangeOperation Operation { get; init; }

    /// <summary>The provider id the change applies to.</summary>
    public required string ProviderId { get; init; }

    /// <summary>How the change ended.</summary>
    public required SettingsChangeOutcome Outcome { get; init; }
}

/// <summary>
/// Writes audit events to node-local, append-only storage. Aggregation across nodes is a
/// separate concern layered on top — no implementation writes directly to a remote destination
/// (agentic/01-architecture-rules.md, rule A6).
/// </summary>
public interface IAuditSink
{
    /// <summary>Writes one audit event. Must not throw for a well-formed event — audit failures are themselves an operational concern, not a reason to lose the event silently.</summary>
    /// <param name="evt">The event to write.</param>
    /// <param name="ct">Cancelled if the write should be abandoned.</param>
    Task WriteAsync(AuditEvent evt, CancellationToken ct = default);
}
