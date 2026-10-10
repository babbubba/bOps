// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

/**
 * Mirrors bOps.Api's JSON contract (ADR-0018) — these shapes are hand-written against the real
 * endpoints, not generated from an OpenAPI schema (deferred, see HANDOFF.md). Field names are
 * camelCase because ASP.NET Core's default Minimal API JSON options use the Web naming policy;
 * enums serialize as their underlying int (verified against a live GET /api/tools response), so
 * every enum here is a plain union of the actual wire values, not a TypeScript `enum`.
 */

/** bOps.Abstractions.RiskLevel, in declaration order. */
export type RiskLevel = 0 | 1 | 2 | 3 | 4;
export const RiskLevelName: Record<RiskLevel, string> = {
  0: 'Read',
  1: 'Low',
  2: 'Medium',
  3: 'High',
  4: 'Critical',
};

/** bOps.Abstractions.AgentTaskStatus, in declaration order. */
export type AgentTaskStatus = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7;
export const AgentTaskStatusName: Record<AgentTaskStatus, string> = {
  0: 'Running',
  1: 'Completed',
  2: 'MaxStepsReached',
  3: 'BudgetExceeded',
  4: 'PolicyBlocked',
  5: 'ReplanLimitReached',
  6: 'Failed',
  7: 'Cancelled',
};

export const TaskStatusRunning: AgentTaskStatus = 0;
export const TaskStatusCompleted: AgentTaskStatus = 1;

/** bOps.Abstractions.TaskOrigin, in declaration order (explicit values on the server). */
export type TaskOrigin = 0 | 1 | 2;

/** bOps.Abstractions.TaskTerminalKind, in declaration order (ADR-0040 §6). Append-only on the server. */
export type TaskTerminalKind = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13 | 14 | 15;
export const TaskTerminalKindName: Record<TaskTerminalKind, string> = {
  0: 'Completed',
  1: 'StepLimit',
  2: 'LifetimeStepLimit',
  3: 'TokenBudget',
  4: 'DelegationBudget',
  5: 'ReplanLimit',
  6: 'LifetimeReplanLimit',
  7: 'PolicyBlocked',
  8: 'ModelFailure',
  9: 'EmptyResponse',
  10: 'RuntimeFailure',
  11: 'Cancelled',
  12: 'NotAdmitted',
  13: 'AttemptDurationBudget',
  14: 'MutationOutcomeUnknown',
  15: 'ExecutionInterrupted',
};
export const TaskTerminalModelFailure: TaskTerminalKind = 8;

/** bOps.Abstractions.ModelFailureKind, in declaration order (ADR-0039): the provider-neutral reason a model call failed. */
export type ModelFailureKind = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9;
export const ModelFailureKindName: Record<ModelFailureKind, string> = {
  0: 'Unknown',
  1: 'Transient',
  2: 'RateLimited',
  3: 'Timeout',
  4: 'Unreachable',
  5: 'Authentication',
  6: 'QuotaExceeded',
  7: 'InvalidRequest',
  8: 'ContextOverflow',
  9: 'MalformedResponse',
};

export interface ToolParameter {
  name: string;
  type: number;
  description: string;
  required: boolean;
  sensitive: boolean;
  allowedValues: string[] | null;
}

export interface VerificationSpec {
  verifyToolName: string;
  argumentsFrom: string[];
  description: string;
}

export interface ToolManifest {
  name: string;
  description: string;
  risk: RiskLevel;
  platforms: string[];
  requires: string[];
  parameters: ToolParameter[];
  verification: VerificationSpec | null;
  requiresExplicitApproval: boolean;
  package: string;
}

export interface ModelToolCall {
  id: string;
  toolName: string;
  arguments: Record<string, unknown>;
}

export interface ToolCallResult {
  outcome: number;
  output: string | null;
  errorMessage: string | null;
  succeeded: boolean;
}

export interface ModelUsage {
  promptTokens: number;
  completionTokens: number;
  estimatedCostUsd: number | null;
}

/** ModelCallOutcome on the wire: 0 = success, 1 = failure. */
export const ModelCallFailed = 1;

/**
 * One call the runtime made to a model. The request and reply bodies are kept in the task store for
 * troubleshooting and are never sent to the UI, so they are not part of this type.
 */
export interface ModelCallRecord {
  provider: string;
  requestedModel: string;
  actualModel: string | null;
  startedAtUtc: string;
  durationMs: number;
  outcome: number;
  usage: ModelUsage | null;
  finishReason: string | null;
  errorMessage: string | null;
  payloadTruncated: boolean;
  /** Why this attempt failed, in provider-neutral terms. Absent on a record written before failures were classified. */
  failureKind?: ModelFailureKind | null;
}

export interface PlanStep {
  index: number;
  description: string | null;
  toolCall: ModelToolCall | null;
  result: ToolCallResult | null;
  observation: string | null;
  planRevision: number | null;
  /** Absent on a step stored before model calls were kept. */
  modelCalls?: ModelCallRecord[] | null;
  /** Typed runtime verification outcome; absent for legacy and unverified steps. */
  verificationStatus?: number | null;
}

export interface PlannedStep {
  index: number;
  description: string;
  expectedTool: string | null;
}

export interface AgentPlan {
  revision: number;
  rationale: string;
  steps: PlannedStep[];
  modelCalls?: ModelCallRecord[] | null;
}

/** bOps.Runtime.ProjectedStepStatus, as the server projects it (PRE-4). Derived server-side; the UI never recomputes it. */
export type ExecutionPlanStepStatus =
  | 'Pending'
  | 'Running'
  | 'Correcting'
  | 'Completed'
  | 'Failed'
  | 'Skipped'
  | 'Superseded'
  | 'OutcomeUnknown';

/** Which bounded same-step correction is in progress. */
export type ExecutionPlanCorrectionKind = 'Semantic' | 'ArgumentValidation';

/** A conditional step's determined activation: a qualifier next to the status, never a status of its own. */
export type ExecutionPlanConditionOutcome = 'Activated' | 'Skipped';

/** One planned step, operator-safe: no arguments, activation fact, result or rationale ever reach this type. */
export interface ExecutionPlanStep {
  revision: number;
  index: number;
  objective: string;
  expectedTool: string | null;
  status: ExecutionPlanStepStatus;
  current: boolean;
  conditional: boolean;
  conditionOutcome: ExecutionPlanConditionOutcome | null;
  correctionKind: ExecutionPlanCorrectionKind | null;
}

export interface ExecutionPlanRevision {
  revision: number;
  active: boolean;
  steps: ExecutionPlanStep[];
}

/** bOps.Runtime.ProjectedPlan: every accepted plan revision, oldest first. `null` on the task until a plan is persisted. */
export interface ExecutionPlan {
  activeRevision: number;
  revisions: ExecutionPlanRevision[];
}

/** bOps.Api.TaskErrorResponse: the stable `code` and the operator `message` of a refused task request (409, 501, 503) or of `resumeBlockedReason`. */
export interface TaskErrorResponse {
  code: string;
  message: string;
}

export type MutationKnowledge = 'KnownNotExecuted' | 'KnownExecuted' | 'Unknown';
export type TaskMutationState = 'Pending' | 'Settled' | 'Ambiguous' | 'Escalated' | 'ReconciledDone' | 'Abandoned';

export interface TaskMutationJournalEntry {
  executionAttempt: number;
  stepIndex: number;
  sequence: number;
  tool: string;
  risk: string;
  argumentsFingerprint: string;
  intentAtUtc: string;
  plannedStepIndex: number | null;
  planRevision: number | null;
  outcome: { kind: string; toolOutcome: string | null; failureKind: string | null; verification: string | null; atUtc: string } | null;
  state: TaskMutationState;
  knowledge: MutationKnowledge;
  reconciliation: { action: string; verification: string | null; resolvedBy: string; atUtc: string } | null;
}

export interface TaskMutationJournal {
  mode: string;
  available: boolean;
  unsettledCount: number;
  entries: TaskMutationJournalEntry[];
}

/** What a task has consumed over its whole lifetime, across every execution attempt (ADR-0040 §5). A resume never resets it. */
export interface TaskAccounting {
  tokensUsed: number;
  lifetimeSteps: number;
  lifetimeReplans: number;
}

/** Why the latest execution attempt ended (ADR-0040 §6). Carries no free text. */
export interface TaskTerminalReason {
  kind: TaskTerminalKind;
  /** For a `ModelFailure`, the provider-neutral kind of the last failed model attempt. */
  failureKind?: ModelFailureKind | null;
}

/**
 * A task as `GET /api/agents/tasks/{id}` (and the list) sends it: the persisted state plus the lifecycle the server computed
 * (ADR-0040 §9). `executing`, `resumable` and `resumeBlockedReason` are decided by the server; the UI renders them and never
 * derives them from `status`.
 */
export interface TaskState {
  id: string;
  node: string;
  goal: string;
  status: AgentTaskStatus;
  steps: PlanStep[];
  plans: AgentPlan[];
  createdAtUtc: string;
  /** 1 for the initial execution, one more for every accepted resume. Not the same thing as a model call's retry attempt. */
  executionAttempt: number;
  accounting: TaskAccounting;
  origin: TaskOrigin;
  delegationId?: string | null;
  delegationRole?: number | null;
  terminalReason?: TaskTerminalReason | null;
  resumedAtUtc?: string | null;
  resumedBy?: { kind: string; id: string; displayName: string | null } | null;
  /** The read-only execution-plan projection (PRE-4); `null` before any plan is persisted, absent from an older server. */
  executionPlan?: ExecutionPlan | null;
  /** True when the launcher of this host holds an execution attempt of the task. `Running` and not `executing` is an interrupted task. */
  executing: boolean;
  resumable: boolean;
  /** Why an ordinary resume would be refused now; `null` when it would be accepted. */
  resumeBlockedReason: TaskErrorResponse | null;
  mutationJournal?: TaskMutationJournal;
  recoverable?: boolean;
  recoveryBlockedReason?: TaskErrorResponse | null;
}

export interface TaskAcceptedResponse {
  taskId: string;
}

/** Body of an accepted `POST /api/agents/tasks/{id}/resume` (ADR-0040 §9): the durable transition, not a full task. */
export interface TaskResumeAcceptedResponse {
  taskId: string;
  status: AgentTaskStatus;
  executionAttempt: number;
  executing: boolean;
  resumable: boolean;
  resumeBlockedReason: TaskErrorResponse | null;
}

export interface ReconcileTaskResponse {
  taskId: string;
  results: Array<{ executionAttempt: number; stepIndex: number; state: string; reconciliation: { action: string } }>;
  unsettledCount: number;
  resumable: boolean;
  resumeBlockedReason: TaskErrorResponse | null;
}

export interface PendingApproval {
  id: string;
  taskId: string | null;
  tool: string;
  reason: string;
  requestedAtUtc: string;
  arguments: Record<string, unknown>;
  permanentDeletion: boolean;
}

export type DeletionManifestStatus = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9;
export const DeletionManifestReady: DeletionManifestStatus = 1;

export interface DeletionManifestSummary {
  id: string;
  status: DeletionManifestStatus;
  roots: string[];
  warnings: string[];
  approvalHash: string;
  createdAtUtc: string;
  expiresAtUtc: string;
  entryCount: number;
  fileCount: number;
  directoryCount: number;
  linkCount: number;
  totalBytes: number;
  deletedCount: number;
  failureCount: number;
}

export interface DeletionManifestEntry {
  ordinal: number;
  absolutePath: string;
  rootPath: string;
  relativePath: string;
  type: string;
  sizeBytes: number | null;
  outcome: string | null;
  error: string | null;
}

export interface DeletionManifestPage {
  entries: DeletionManifestEntry[];
  nextCursor: string | null;
}

/** bOps.Abstractions.PackageTrustLevel, in declaration order. */
export type PackageTrustLevel = 0 | 1 | 2 | 3;
export const PackageTrustLevelName: Record<PackageTrustLevel, string> = {
  0: 'Unverified',
  1: 'Community',
  2: 'Verified',
  3: 'Official',
};

export interface PluginDependency {
  name: string;
  version: string;
}

/**
 * bOps.Api.PluginCatalogEntry (V1.1-F, GET /api/plugins). `enabled` (persisted operator intent)
 * and `loaded` (actually registered in this process right now) are two distinct fields on
 * purpose — never merge them into one "status" in the UI. Likewise `declaredMaxRisk` (from the
 * manifest) and `effectiveMaxRisk` (observed from currently-registered tools, null when not
 * loaded) must stay visibly separate: a declaration is not enforcement.
 */
export interface PluginCatalogEntry {
  id: string;
  version: string;
  publisher: string;
  installedAtUtc: string;
  enabled: boolean;
  loaded: boolean;
  compatible: boolean;
  signaturePresent: boolean;
  verified: boolean;
  trust: PackageTrustLevel;
  keyId: string | null;
  declaredCapabilities: string[];
  dependencies: PluginDependency[];
  declaredMaxRisk: RiskLevel | null;
  effectiveMaxRisk: RiskLevel | null;
  loadError: string | null;
  /**
   * V1.3-M6 lifecycle projection. `lifecycleState` is deliberately a plain string: the server may add values, and the UI must render
   * an unknown one generically. `lifecycleETag` is an opaque precondition token — never parsed or reconstructed. `lifecycleFailure`
   * (the persisted lifecycle failure) is a different concept from `loadError` (a runtime load diagnostic).
   */
  lifecycleState?: string | null;
  lifecycleETag?: string | null;
  lifecycleFailure?: string | null;
  recoveryAvailable?: boolean;
}

/** bOps.Api.PluginLifecycleResponse — the success body of a lifecycle mutation. */
export interface PluginLifecycleResponse {
  pluginId: string;
  version: string | null;
  state: string | null;
}

/** bOps.Api.PluginLifecycleError — the sanitized body of a refused lifecycle mutation. */
export interface PluginLifecycleError {
  message: string;
  category: string;
  stage?: string | null;
}

export interface PluginCatalogPage {
  entries: PluginCatalogEntry[];
  totalCount: number;
}

/** The severities of a system message (ADR-0049), in increasing order of concern. The API sends and accepts the names. */
export const SystemMessageSeverities = ['Information', 'Warning', 'Error', 'Critical'] as const;
export type SystemMessageSeverityName = (typeof SystemMessageSeverities)[number];

/** One operational system message (`GET /api/system-messages`). `metadata` is data the runtime wrote: never markup, a URL or an instruction. */
export interface SystemMessage {
  id: string;
  timestampUtc: string;
  node: string;
  source: string;
  severity: string;
  code: string;
  message: string;
  metadata: Record<string, unknown>;
  taskId: string | null;
  componentType: string | null;
  componentId: string | null;
}

/** One newest-first page; `nextCursor` is the opaque keyset cursor of the following page, or `null` on the last. */
export interface SystemMessagePage {
  items: SystemMessage[];
  nextCursor: string | null;
}

/** The server-side filters. Every supplied one narrows the result (AND); `fromUtc`/`toUtc` are inclusive ISO 8601 UTC instants. */
export interface SystemMessageFilter {
  fromUtc?: string;
  toUtc?: string;
  severity?: SystemMessageSeverityName;
  contains?: string;
}

/** The provider this host is actually configured to use — never carries the API key's value (ADR-0019), only whether one is present. */
export interface ActiveProviderInfo {
  provider: string;
  model: string;
  baseUrl: string;
  hasApiKey: boolean;
}

/** bOps.Api.ProvidersResponse (ADR-0019, GET /api/providers). */
export interface ProvidersResponse {
  registeredProviderIds: string[];
  active: ActiveProviderInfo | null;
}

/** Where GET /api/settings' active provider came from (ADR-0029, bOps.Api.ProviderResolution.Source). */
export type ActiveProviderSource = 'EnvironmentOverride' | 'Settings' | 'Default';

/**
 * bOps.Api.SettingsProviderView (ADR-0029, administrator-only GET /api/settings). The key half
 * (`hasStoredKey`/`keyMask*`) and the profile half (`baseUrl`/`model`/…) describe two separate
 * stores and can be present independently — never conflate "has a key" with "has a profile".
 * Never carries a key's plaintext.
 */
export interface SettingsProviderView {
  providerId: string;
  isActive: boolean;
  hasStoredKey: boolean;
  keyMaskPrefix: string | null;
  keyMaskSuffix: string | null;
  keyPlaintextLength: number | null;
  keyUpdatedUtc: string | null;
  baseUrl: string | null;
  model: string | null;
  supportsNativeToolCalling: boolean | null;
  extraParameters: Record<string, string> | null;
  profileUpdatedUtc: string | null;
  /** ADR-0045: a usable credential exists for this provider right now (advisory; carries no key material). */
  hasUsableCredential: boolean;
  /** ADR-0045: `environment`, `vault` or `none`. Where the usable credential comes from, never its value. */
  credentialSource: string;
  /** ADR-0045: this provider is the persisted selection, but another provider is effective (host-owned). */
  isPersistedSelectionShadowed: boolean;
  /** ADR-0045: the persisted profile fields (`baseUrl`, `model`, `supportsNativeToolCalling`) a host variable overrides. */
  shadowedFields: string[];
  /** ADR-0045: `extraParameters` is stored and returned but consumed by no provider. */
  extraParametersPersistedOnly: boolean;
}

/** Where one effective, non-secret value came from (ADR-0045 section 7). */
export type EffectiveFieldSource = 'environment' | 'settings' | 'default';

/** Where the effective fallback chain came from: host `configuration` shadows `settings`; `default` is the empty chain. */
export type EffectiveFallbackSource = 'configuration' | 'settings' | 'default';

/** bOps.Api.FallbackEntryView (ADR-0045): one stored fallback candidate, provider and model only — never a secret. */
export interface FallbackEntry {
  provider: string;
  model: string;
}

/** bOps.Api.EffectiveFallbackView (ADR-0045): one candidate new executions are pinned with. `credentialAvailable` is advisory only. */
export interface EffectiveFallback {
  ordinal: number;
  provider: string;
  model: string;
  baseUrl: string;
  supportsNativeToolCalling: boolean;
  credentialAvailable: boolean;
}

/** bOps.Api.SettingsView (ADR-0029, GET /api/settings). `vaultVersion` must be echoed back as `expectedVersion` on every key write. */
export interface SettingsView {
  vaultVersion: number;
  activeProviderId: string | null;
  activeProviderSource: ActiveProviderSource;
  providers: SettingsProviderView[];
  /** Concurrency token for non-key Settings writes (selection, profile, fallback chain). */
  settingsRevision: number;
  /** Monotonic generation of the effective configuration new executions are pinned with. */
  configurationGeneration: number;
  /** The provider selected in Settings, whether or not it is effective. */
  persistedActiveProviderId: string | null;
  /** True when `persistedActiveProviderId` is stored but another provider is effective. */
  persistedActiveProviderShadowed: boolean;
  effectiveBaseUrl: string;
  effectiveModel: string;
  effectiveSupportsNativeToolCalling: boolean;
  effectiveBaseUrlSource: EffectiveFieldSource | string;
  effectiveModelSource: EffectiveFieldSource | string;
  effectiveToolCallingSource: EffectiveFieldSource | string;
  effectiveCredentialAvailable: boolean;
  effectiveCredentialSource: string;
  /** The administrator's stored ordered fallback list, whether or not it is currently effective. */
  persistedFallbacks: FallbackEntry[];
  /** The ordered candidates new executions are pinned with. */
  effectiveFallbacks: EffectiveFallback[];
  effectiveFallbackSource: EffectiveFallbackSource | string;
  /** True when a stored fallback list exists but host configuration owns the effective list. */
  persistedFallbacksShadowed: boolean;
}

/** bOps.Api.SetFallbacksRequest (ADR-0045, PUT /api/settings/fallbacks): the complete ordered list; an empty list clears it. */
export interface SetFallbacksRequest {
  fallbacks: FallbackEntry[];
  expectedRevision: number;
}

/** bOps.Api.SetProviderProfileRequest (ADR-0029, PUT /api/settings/providers/{id}/profile). */
export interface SetProviderProfileRequest {
  baseUrl: string;
  model: string;
  supportsNativeToolCalling: boolean;
  extraParameters: Record<string, string> | null;
}

// ---- Delegations (bOps.Api /api/delegations, ADR-0030 section 9, V1.2) ----
// Unlike the older shapes above, every enum here is sent as its name (a string), and no field carries what a tool
// returned: the API omits the data of each piece of evidence, the authority of each role and the model calls.

export interface DelegationEvidence {
  id: string;
  kind: string;
  description: string;
  sourceTool: string;
  observedAtUtc: string;
}

export interface DelegationFinding {
  id: string;
  summary: string;
  severity: string | null;
  evidenceIds: string[];
  /**
   * `true` only when the finding cites Evidence that is itself typed as limited (ADR-0044 §16); `false` means only that no cited
   * Evidence item is marked limited, never that the finding is unaffected; `null`/absent when the run recorded no limitations.
   */
  restsOnLimitedEvidence?: boolean | null;
}

/** One typed limitation of a role's model-loop evidence collection (ADR-0044 §16). Enums by name; no tool or model text. */
export interface EvidenceLimitation {
  stepIndex: number;
  toolName: string | null;
  unknownTool: boolean;
  outcome: string;
  failureKind: string;
  completeness: string;
  shortenedFromCharacters: number | null;
  evidenceId: string | null;
}

/** How the Diagnostic role's final reply was read: `status` Valid, Absent or Malformed, and its `problem`. */
export interface DiagnosticReply {
  status: string;
  problem: string;
  discardedFindings: number;
}

/** The limitation metadata of a run's model roles beside a pending plan; `available: false` when the run could not be read. */
export interface PlanLimitations {
  available: boolean;
  roles: {
    role: string;
    recorded: boolean;
    evidenceLimitations: EvidenceLimitation[] | null;
    evidenceLimitationsOmitted: number;
    findingsReply: DiagnosticReply | null;
  }[];
}

// ---- Delegation readiness (GET /api/delegations/readiness, ADR-0044 §6) ----

/** One role. `state` is `ready`, `missing`, `malformed` (shown as "Not usable") or `notRequired`; clients branch on `reasonCode`. */
export interface RoleReadiness {
  role: string;
  state: string;
  dimension: string | null;
  reasonCode: string;
  reason: string | null;
}

/** Role/profile readiness for a request shape. `ready` is authoritative; it never says a particular change will be accepted. */
export interface DelegationReadiness {
  remediation: boolean;
  ready: boolean;
  policy: string;
  roles: RoleReadiness[];
  profileDriftCount: number;
  evaluatedAtUtc: string;
}

// ---- Skill catalog (GET /api/skills, ADR-0044 §8) ----

/** `ToolParameter` field for field; `type` is one of the eight `ToolParameterType` names. */
export interface InputParameter {
  name: string;
  type: 'String' | 'Integer' | 'Number' | 'Boolean' | 'Path' | 'Duration' | 'Enum' | 'PathList' | string;
  description: string;
  required: boolean;
  sensitive: boolean;
  allowedValues: string[] | null;
  minimum: number | null;
  maximum: number | null;
  minLength: number | null;
  maxLength: number | null;
  minItems: number | null;
  maxItems: number | null;
}

export interface CatalogCapability {
  name: string;
  version: string;
  description: string;
  risk: string;
  supportsDryRun: boolean;
  inputSchema: InputParameter[];
}

export interface CatalogSkill {
  skillId: string;
  package: string;
  trust: string;
  capabilities: CatalogCapability[];
}

export interface SkillCatalog {
  skills: CatalogSkill[];
}

export interface DelegationVerification {
  status: string;
  detail: string | null;
  evidence: DelegationEvidence[];
}

export interface DelegationRole {
  role: string;
  agentId: string;
  status: string;
  steps: number;
  tokens: number;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  findings: DelegationFinding[];
  evidence: DelegationEvidence[];
  planHash: string | null;
  verification: DelegationVerification | null;
  errorMessage: string | null;
  /** The role's model-loop evidence limitations; `null`/absent = not recorded, never "none". */
  evidenceLimitations?: EvidenceLimitation[] | null;
  evidenceLimitationsOmitted?: number;
  findingsReply?: DiagnosticReply | null;
}

export interface DelegationStep {
  stepIndex: number;
  tool: string;
  argumentsHash: string;
  intentAtUtc: string;
  outcome: string | null;
  verification: string | null;
  reconciliation: string | null;
}

export interface Delegation {
  id: string;
  status: string;
  objective: string;
  actorId: string;
  actorDisplayName: string | null;
  runningInThisHost: boolean;
  awaitingPlanApproval: boolean;
  planHash: string | null;
  approval: { planHash: string; approverId: string; approverDisplayName: string | null; approvedAtUtc: string } | null;
  roles: DelegationRole[];
  journal: DelegationStep[];
  resumeCount: number;
  denial: { dimension: string; reason: string } | null;
  errorMessage: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface PendingPlanApproval {
  delegationId: string;
  planHash: string;
  requestedAtUtc: string;
  skillId: string;
  capabilityName: string;
  target: string;
  environment: string;
  blastRadius: string;
  rationale: string;
  steps: { index: number; tool: string; arguments: Record<string, unknown>; description: string | null }[];
  findings: { id: string; summary: string; severity: string | null; evidenceIds: string[]; restsOnLimitedEvidence?: boolean | null }[];
  /** The run's persisted model-role limitations, joined by the API; absent or `available: false` means unavailable, never none. */
  limitations?: PlanLimitations | null;
  authority: {
    tools: string[];
    maxRisk: string;
    maxBlastRadius: string;
    targets: string[];
    environments: string[];
    maxSteps: number;
    deadlineUtc: string;
  } | null;
}

export interface StartDelegationRequest {
  objective: string;
  maxSteps?: number;
  maxTokens?: number;
  remediation?: {
    skillId: string;
    capabilityName: string;
    target: string;
    environment: string;
    blastRadius?: string;
    dryRun: boolean;
    /** The Capability input, one object from either the schema form or the advanced JSON editor. */
    input?: Record<string, unknown>;
  };
}

export const DelegationRoleOrder = ['Discovery', 'Diagnostic', 'Remediation', 'Verification'];

/** Statuses a run ends in; anything else is still under way or waiting for a person. */
export const DelegationTerminalStatuses = [
  'Completed',
  'DiagnosisCompleted',
  'Rejected',
  'VerificationFailed',
  'Denied',
  'PolicyBlocked',
  'BudgetExceeded',
  'DeadlineExceeded',
  'Cancelled',
  'Abandoned',
  'Failed',
];
