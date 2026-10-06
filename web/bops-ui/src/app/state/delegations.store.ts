// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { computed, inject } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { patchState, signalStore, withComputed, withHooks, withMethods, withState } from '@ngrx/signals';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { I18n } from '../core/i18n/i18n';
import { describeError } from './describe-error';
import { CatalogSkill, Delegation, DelegationReadiness, PendingPlanApproval, StartDelegationRequest } from '../core/api/models';
import { AuthService } from '../core/auth/auth.service';

const POLL_INTERVAL_MS = 2000;
const READINESS_BACKOFF_MS = 5000;

/**
 * Why readiness could not be obtained (ADR-0044 §13). Every one of them keeps Submit disabled: a failure to learn readiness is
 * never read as "ready".
 */
export type ReadinessFailure = 'sessionExpired' | 'forbidden' | 'rateLimited' | 'server' | 'unreachable';

/** Codes of a refused start that mean the catalog may have changed since it was read (ADR-0044 §9.4). */
const CATALOG_REFUSALS = ['unknown_capability', 'capability_input_invalid'];

interface DelegationsState {
  runs: Delegation[];
  /** Plans waiting for a decision. Only read for a principal with the approver role: the endpoint refuses anyone else. */
  pendingPlans: PendingPlanApproval[];
  loading: boolean;
  /** True while a start, cancel, resume, reconcile or plan decision is in flight. */
  busy: boolean;
  error: string | null;
  /** The latest readiness answer for the shape last asked about; `null` while none is known for it. */
  readiness: DelegationReadiness | null;
  readinessLoading: boolean;
  readinessFailure: ReadinessFailure | null;
  /** The activated Skill catalog; `null` until read, or when reading it failed (see `catalogError`). */
  catalog: CatalogSkill[] | null;
  catalogError: string | null;
  /** Why the last start was refused, shown beside the form and never mixed with the readiness panel. */
  submitError: string | null;
}

/**
 * Delegated runs (ADR-0030, /api/delegations): the recent runs and the plans waiting for a person. There is no stream for
 * them, so this polls on a fixed interval, as the approval queue does, starting when the store is created. Every action
 * goes to the API, which checks the role again; hiding a button is a courtesy, never the control.
 *
 * It also holds what the start form needs (ADR-0044): the server's role readiness for the request shape being prepared — the
 * latest request wins, a superseded answer is discarded — and the activated Skill catalog. Neither is computed here.
 */
export const DelegationsStore = signalStore(
  { providedIn: 'root' },
  withState<DelegationsState>({
    runs: [],
    pendingPlans: [],
    loading: false,
    busy: false,
    error: null,
    readiness: null,
    readinessLoading: false,
    readinessFailure: null,
    catalog: null,
    catalogError: null,
    submitError: null,
  }),
  withComputed((store) => ({
    /** Runs that wait for a human to decide their plan (the nav badge). */
    awaitingApproval: computed(() => store.runs().filter((run) => run.awaitingPlanApproval)),
  })),
  withMethods((store, api = inject(BOpsApiClient), auth = inject(AuthService), i18n = inject(I18n)) => {
    /** `keepError`: after a refused action the refresh must not wipe the reason the person is waiting to read. */
    const refresh = async (keepError = false): Promise<void> => {
      try {
        const runs = await api.listDelegations();
        const canApprove = auth.identity()?.roles.includes('approver') === true;
        const pendingPlans = canApprove ? await api.listPendingPlanApprovals() : [];
        patchState(store, keepError ? { runs, pendingPlans, loading: false } : { runs, pendingPlans, loading: false, error: null });
      } catch (err) {
        patchState(store, { loading: false, error: describeError(err, i18n) });
      }
    };

    const act = async <T>(action: () => Promise<T>): Promise<T | undefined> => {
      patchState(store, { busy: true, error: null });
      try {
        return await action();
      } catch (err) {
        patchState(store, { error: describeError(err, i18n) });
        return undefined;
      } finally {
        patchState(store, { busy: false });
        await refresh(true);
      }
    };

    let readinessRequest = 0;
    let readinessShape: boolean | null = null;
    let backoff: ReturnType<typeof setTimeout> | undefined;

    const loadReadiness = async (remediation: boolean): Promise<void> => {
      clearTimeout(backoff);
      const request = ++readinessRequest;
      readinessShape = remediation;
      patchState(store, { readiness: null, readinessLoading: true, readinessFailure: null });
      try {
        const readiness = await api.getDelegationReadiness(remediation);
        if (request !== readinessRequest) return;
        // An answer for another shape than the one asked about is never shown as this one's.
        if (readiness.remediation !== remediation) {
          patchState(store, { readinessLoading: false, readinessFailure: 'server' });
          return;
        }

        patchState(store, { readiness, readinessLoading: false });
      } catch (err) {
        if (request !== readinessRequest) return;
        const failure = readinessFailureOf(err);
        patchState(store, { readinessLoading: false, readinessFailure: failure });
        if (failure === 'rateLimited') {
          backoff = setTimeout(() => void loadReadiness(remediation), retryDelay(err));
        }
      }
    };

    const loadCatalog = async (): Promise<void> => {
      try {
        const catalog = await api.getSkills();
        patchState(store, { catalog: catalog.skills, catalogError: null });
      } catch (err) {
        patchState(store, { catalog: null, catalogError: describeError(err, i18n) });
      }
    };

    return {
      refresh: () => refresh(),
      loadReadiness,
      loadCatalog,

      /** Asks again for the shape last asked about (after a failure, or when the browser comes back online). */
      retryReadiness(): Promise<void> {
        return readinessShape === null ? Promise.resolve() : loadReadiness(readinessShape);
      },

      /** Starts a run and returns its id, or `undefined` if the API refused (the reason is in `submitError`). */
      async start(request: StartDelegationRequest, idempotencyKey?: string): Promise<string | undefined> {
        patchState(store, { busy: true, submitError: null });
        try {
          return (await api.startDelegation(request, idempotencyKey)).delegationId;
        } catch (err) {
          patchState(store, { submitError: describeError(err, i18n) });
          const code = (err as { error?: { code?: unknown } | null } | null)?.error?.code;
          if (typeof code === 'string' && CATALOG_REFUSALS.includes(code)) {
            await loadCatalog();
          }

          return undefined;
        } finally {
          patchState(store, { busy: false });
          await refresh(true);
        }
      },

      clearSubmitError(): void {
        patchState(store, { submitError: null });
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
    const online = (): void => {
      if (store.readinessFailure() === 'unreachable') void store.retryReadiness();
    };

    return {
      onInit() {
        if (auth.authenticated()) {
          patchState(store, { loading: true });
          void store.refresh();
        }
        intervalId = setInterval(() => {
          if (auth.authenticated()) void store.refresh();
        }, POLL_INTERVAL_MS);
        globalThis.addEventListener?.('online', online);
      },
      onDestroy() {
        clearInterval(intervalId);
        globalThis.removeEventListener?.('online', online);
      },
    };
  }),
);

function readinessFailureOf(err: unknown): ReadinessFailure {
  if (!(err instanceof HttpErrorResponse)) return 'server';
  switch (err.status) {
    case 0:
      return 'unreachable';
    case 401:
      return 'sessionExpired';
    case 403:
      return 'forbidden';
    case 429:
      return 'rateLimited';
    default:
      return 'server';
  }
}

function retryDelay(err: unknown): number {
  const seconds = err instanceof HttpErrorResponse ? Number(err.headers?.get('Retry-After')) : NaN;
  return Number.isFinite(seconds) && seconds > 0 ? Math.ceil(seconds) * 1000 : READINESS_BACKOFF_MS;
}
