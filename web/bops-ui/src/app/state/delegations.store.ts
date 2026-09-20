// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withHooks, withMethods, withState } from '@ngrx/signals';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { Delegation, PendingPlanApproval, StartDelegationRequest } from '../core/api/models';
import { AuthService } from '../core/auth/auth.service';

const POLL_INTERVAL_MS = 2000;

interface DelegationsState {
  runs: Delegation[];
  /** Plans waiting for a decision. Only read for a principal with the approver role: the endpoint refuses anyone else. */
  pendingPlans: PendingPlanApproval[];
  loading: boolean;
  /** True while a start, cancel, resume, reconcile or plan decision is in flight. */
  busy: boolean;
  error: string | null;
}

/** The API answers a refusal with `{ message }`; prefer that to the transport's generic text. */
function describeError(err: unknown): string {
  const body = (err as { error?: { message?: unknown } } | null)?.error;
  if (typeof body?.message === 'string') return body.message;
  return err instanceof Error ? err.message : 'Something went wrong.';
}

/**
 * Delegated runs (ADR-0030, /api/delegations): the recent runs and the plans waiting for a person. There is no stream for
 * them, so this polls on a fixed interval, as the approval queue does, starting when the store is created. Every action
 * goes to the API, which checks the role again; hiding a button is a courtesy, never the control.
 */
export const DelegationsStore = signalStore(
  { providedIn: 'root' },
  withState<DelegationsState>({ runs: [], pendingPlans: [], loading: false, busy: false, error: null }),
  withComputed((store) => ({
    /** Runs that wait for a human to decide their plan (the nav badge). */
    awaitingApproval: computed(() => store.runs().filter((run) => run.awaitingPlanApproval)),
  })),
  withMethods((store, api = inject(BOpsApiClient), auth = inject(AuthService)) => {
    /** `keepError`: after a refused action the refresh must not wipe the reason the person is waiting to read. */
    const refresh = async (keepError = false): Promise<void> => {
      try {
        const runs = await api.listDelegations();
        const canApprove = auth.identity()?.roles.includes('approver') === true;
        const pendingPlans = canApprove ? await api.listPendingPlanApprovals() : [];
        patchState(store, keepError ? { runs, pendingPlans, loading: false } : { runs, pendingPlans, loading: false, error: null });
      } catch (err) {
        patchState(store, { loading: false, error: describeError(err) });
      }
    };

    const act = async <T>(action: () => Promise<T>): Promise<T | undefined> => {
      patchState(store, { busy: true, error: null });
      try {
        return await action();
      } catch (err) {
        patchState(store, { error: describeError(err) });
        return undefined;
      } finally {
        patchState(store, { busy: false });
        await refresh(true);
      }
    };

    return {
      refresh: () => refresh(),

      /** Starts a run and returns its id, or `undefined` if the API refused (the reason is in `error`). */
      async start(request: StartDelegationRequest, idempotencyKey?: string): Promise<string | undefined> {
        return (await act(() => api.startDelegation(request, idempotencyKey)))?.delegationId;
      },

      async cancel(id: string): Promise<void> {
        await act(() => api.cancelDelegation(id));
      },

      async resume(id: string): Promise<void> {
        await act(() => api.resumeDelegation(id));
      },

      async reconcile(id: string, decision: 'accept' | 'abandon', note?: string): Promise<void> {
        await act(() => api.reconcileDelegation(id, decision, note));
      },

      /** Decides the plan by its hash: a decision for a plan that has since changed is refused by the API. */
      async decidePlan(id: string, planHash: string, approved: boolean, note?: string): Promise<void> {
        await act(() => api.respondToPlanApproval(id, planHash, approved, note));
      },
    };
  }),
  withHooks((store, auth = inject(AuthService)) => {
    let intervalId: ReturnType<typeof setInterval> | undefined;

    return {
      onInit() {
        if (auth.authenticated()) {
          patchState(store, { loading: true });
          void store.refresh();
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
