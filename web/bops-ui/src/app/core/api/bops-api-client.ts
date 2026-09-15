// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  AgentTaskStatus,
  AgentTaskStatusName,
  PendingApproval,
  ProvidersResponse,
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

  respondToApproval(approvalId: string, approved: boolean, note?: string, approver?: string): Promise<void> {
    return firstValueFrom(
      this.http.post<void>(`/api/approvals/${approvalId}/respond`, { approved, note, approver }),
    );
  }

  listTools(): Promise<ToolManifest[]> {
    return firstValueFrom(this.http.get<ToolManifest[]>('/api/tools'));
  }

  getProviders(): Promise<ProvidersResponse> {
    return firstValueFrom(this.http.get<ProvidersResponse>('/api/providers'));
  }
}
