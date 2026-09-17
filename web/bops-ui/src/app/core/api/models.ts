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

export interface PlanStep {
  index: number;
  description: string | null;
  toolCall: ModelToolCall | null;
  result: ToolCallResult | null;
  observation: string | null;
  planRevision: number | null;
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
