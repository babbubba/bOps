// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  AgentTaskStatus,
  Delegation,
  PendingPlanApproval,
  StartDelegationRequest,
  AgentTaskStatusName,
  DeletionManifestPage,
  DeletionManifestSummary,
  PackageTrustLevel,
  PendingApproval,
  PluginCatalogEntry,
  PluginCatalogPage,
  ProvidersResponse,
  SetProviderProfileRequest,
  SettingsView,
  TaskAcceptedResponse,
  TaskState,
  ToolManifest,
} from './models';

/**
 * Hand-written client against bOps.Api's real endpoints (ADR-0018) — no OpenAPI generation in
 * this pass (HANDOFF.md: deferred until a session sets up that pipeline deliberately). One method
 * per endpoint, thin by construction: this class does no retrying, caching or state — that lives
 * in the SignalStores that call it.
 */
@Injectable({ providedIn: 'root' })
export class BOpsApiClient {
  private readonly http = inject(HttpClient);

  startTask(goal: string): Promise<TaskAcceptedResponse> {
    return firstValueFrom(this.http.post<TaskAcceptedResponse>('/api/agents/tasks', { goal }));
  }

  resumeTask(taskId: string): Promise<TaskAcceptedResponse> {
    return firstValueFrom(this.http.post<TaskAcceptedResponse>(`/api/agents/tasks/${taskId}/resume`, {}));
  }

  getTask(taskId: string): Promise<TaskState> {
    return firstValueFrom(this.http.get<TaskState>(`/api/agents/tasks/${taskId}`));
  }

  listTasks(status: AgentTaskStatus): Promise<TaskState[]> {
    const statusName = AgentTaskStatusName[status];
    return firstValueFrom(this.http.get<TaskState[]>(`/api/agents/tasks?status=${statusName}`));
  }

  listPendingApprovals(): Promise<PendingApproval[]> {
    return firstValueFrom(this.http.get<PendingApproval[]>('/api/approvals/pending'));
  }

  respondToApproval(
    approvalId: string,
    approved: boolean,
    note?: string,
    acknowledgePermanentDeletion = false,
  ): Promise<void> {
    return firstValueFrom(
      this.http.post<void>(`/api/approvals/${approvalId}/respond`, {
        approved,
        note,
        acknowledgePermanentDeletion,
      }),
    );
  }

  getApprovalDeletionManifest(approvalId: string): Promise<DeletionManifestSummary> {
    return firstValueFrom(
      this.http.get<DeletionManifestSummary>(`/api/approvals/${approvalId}/deletion-manifest`),
    );
  }

  getApprovalDeletionEntries(
    approvalId: string,
    cursor?: string,
    limit = 200,
    search?: string,
  ): Promise<DeletionManifestPage> {
    const params = new URLSearchParams({ limit: String(limit) });
    if (cursor) params.set('cursor', cursor);
    if (search) params.set('search', search);
    return firstValueFrom(
      this.http.get<DeletionManifestPage>(
        `/api/approvals/${approvalId}/deletion-manifest/entries?${params.toString()}`,
      ),
    );
  }

  approvalDeletionDownloadUrl(approvalId: string): string {
    return `/api/approvals/${approvalId}/deletion-manifest/download`;
  }

  listTools(): Promise<ToolManifest[]> {
    return firstValueFrom(this.http.get<ToolManifest[]>('/api/tools'));
  }

  getProviders(): Promise<ProvidersResponse> {
    return firstValueFrom(this.http.get<ProvidersResponse>('/api/providers'));
  }

  listPlugins(filter?: { enabled?: boolean; trust?: PackageTrustLevel; limit?: number; offset?: number }): Promise<PluginCatalogPage> {
    const params = new URLSearchParams();
    if (filter?.enabled !== undefined) params.set('enabled', String(filter.enabled));
    if (filter?.trust !== undefined) params.set('trust', String(filter.trust));
    if (filter?.limit !== undefined) params.set('limit', String(filter.limit));
    if (filter?.offset !== undefined) params.set('offset', String(filter.offset));
    const query = params.toString();
    return firstValueFrom(this.http.get<PluginCatalogPage>(`/api/plugins${query ? `?${query}` : ''}`));
  }

  getPlugin(id: string): Promise<PluginCatalogEntry> {
    return firstValueFrom(this.http.get<PluginCatalogEntry>(`/api/plugins/${id}`));
  }

  getSettings(): Promise<SettingsView> {
    return firstValueFrom(this.http.get<SettingsView>('/api/settings'));
  }

  setProviderKey(providerId: string, apiKey: string, expectedVersion: number): Promise<void> {
    return firstValueFrom(
      this.http.put<void>(`/api/settings/providers/${encodeURIComponent(providerId)}/key`, { apiKey, expectedVersion }),
    );
  }

  clearProviderKey(providerId: string, expectedVersion: number): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(
        `/api/settings/providers/${encodeURIComponent(providerId)}/key?expectedVersion=${expectedVersion}`,
      ),
    );
  }

  setProviderProfile(providerId: string, request: SetProviderProfileRequest): Promise<void> {
    return firstValueFrom(
      this.http.put<void>(`/api/settings/providers/${encodeURIComponent(providerId)}/profile`, request),
    );
  }

  setActiveProvider(providerId: string): Promise<void> {
    return firstValueFrom(this.http.put<void>('/api/settings/active-provider', { providerId }));
  }

  // ---- Delegations (V1.2) ----

  startDelegation(request: StartDelegationRequest, idempotencyKey?: string): Promise<{ delegationId: string }> {
    return firstValueFrom(
      this.http.post<{ delegationId: string }>('/api/delegations', request, {
        headers: idempotencyKey ? { 'Idempotency-Key': idempotencyKey } : {},
      }),
    );
  }

  listDelegations(limit = 50): Promise<Delegation[]> {
    return firstValueFrom(this.http.get<Delegation[]>(`/api/delegations?limit=${limit}`));
  }

  getDelegation(id: string): Promise<Delegation> {
    return firstValueFrom(this.http.get<Delegation>(`/api/delegations/${id}`));
  }

  cancelDelegation(id: string): Promise<Delegation> {
    return firstValueFrom(this.http.post<Delegation>(`/api/delegations/${id}/cancel`, {}));
  }

  resumeDelegation(id: string): Promise<{ delegationId: string }> {
    return firstValueFrom(this.http.post<{ delegationId: string }>(`/api/delegations/${id}/resume`, {}));
  }

  reconcileDelegation(id: string, decision: 'accept' | 'abandon', note?: string): Promise<Delegation> {
    return firstValueFrom(this.http.post<Delegation>(`/api/delegations/${id}/reconcile`, { decision, note }));
  }

  listPendingPlanApprovals(): Promise<PendingPlanApproval[]> {
    return firstValueFrom(this.http.get<PendingPlanApproval[]>('/api/delegations/approvals'));
  }

  respondToPlanApproval(id: string, planHash: string, approved: boolean, note?: string): Promise<void> {
    return firstValueFrom(this.http.post<void>(`/api/delegations/${id}/approval`, { planHash, approved, note }));
  }
}
