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

export interface TaskState {
  id: string;
  node: string;
  goal: string;
  status: AgentTaskStatus;
  steps: PlanStep[];
  plans: AgentPlan[];
  createdAtUtc: string;
}

export interface TaskAcceptedResponse {
  taskId: string;
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
}

export interface PluginCatalogPage {
  entries: PluginCatalogEntry[];
  totalCount: number;
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
}

/** bOps.Api.SettingsView (ADR-0029, GET /api/settings). `vaultVersion` must be echoed back as `expectedVersion` on every key write. */
export interface SettingsView {
  vaultVersion: number;
  activeProviderId: string | null;
  activeProviderSource: ActiveProviderSource;
  providers: SettingsProviderView[];
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
  findings: { id: string; summary: string; severity: string | null; evidenceIds: string[] }[];
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
