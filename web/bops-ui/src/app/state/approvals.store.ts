// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { inject } from '@angular/core';
import { patchState, signalStore, withHooks, withMethods, withState } from '@ngrx/signals';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { PendingApproval, RiskLevel } from '../core/api/models';
import { AuthService } from '../core/auth/auth.service';

const POLL_INTERVAL_MS = 2000;

interface ApprovalsState {
  pending: PendingApproval[];
  /** Tool name → risk, loaded once from GET /api/tools — PendingApproval itself carries no risk (bOps.Api's DTO), and an operator deciding approve/reject needs it. */
  toolRisk: Record<string, RiskLevel>;
  loading: boolean;
  error: string | null;
}

function describeError(err: unknown): string {
  return err instanceof Error ? err.message : 'Something went wrong.';
}

/**
 * The pending-approval queue (ADR-0018, bOps.Api.ApiApprovalProvider). There is no SSE stream for
 * approvals — they are process-local, ephemeral state the server never persists — so this polls
 * GET /api/approvals/pending on a fixed interval, starting as soon as the store is created
 * (providedIn: 'root', so this runs for the whole app session).
 */
export const ApprovalsStore = signalStore(
  { providedIn: 'root' },
  withState<ApprovalsState>({ pending: [], toolRisk: {}, loading: false, error: null }),
  withMethods((store, api = inject(BOpsApiClient)) => ({
    async refresh(): Promise<void> {
      try {
        const pending = await api.listPendingApprovals();
        patchState(store, { pending, loading: false, error: null });
      } catch (err) {
        patchState(store, { loading: false, error: describeError(err) });
      }
    },

    async loadToolRisk(): Promise<void> {
      try {
        const tools = await api.listTools();
        const toolRisk = Object.fromEntries(tools.map((t) => [t.name, t.risk]));
        patchState(store, { toolRisk });
      } catch {
        // Non-critical: the approval queue still works, just without a risk badge.
      }
    },

    async approve(id: string, note?: string, acknowledgePermanentDeletion = false): Promise<void> {
      await api.respondToApproval(id, true, note, acknowledgePermanentDeletion);
      patchState(store, { pending: store.pending().filter((a) => a.id !== id) });
    },

    async reject(id: string, note?: string): Promise<void> {
      await api.respondToApproval(id, false, note, false);
      patchState(store, { pending: store.pending().filter((a) => a.id !== id) });
    },
  })),
  withHooks((store, auth = inject(AuthService)) => {
    let intervalId: ReturnType<typeof setInterval> | undefined;

    return {
      onInit() {
        if (auth.authenticated()) {
          patchState(store, { loading: true });
          void store.refresh();
          void store.loadToolRisk();
        }
        intervalId = setInterval(() => {
          if (auth.authenticated()) void store.refresh();
        }, POLL_INTERVAL_MS);
      },
      onDestroy() {
        clearInterval(intervalId);
      },
    };
  }),
);
