// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { inject } from '@angular/core';
import { patchState, signalStore, withHooks, withMethods, withState } from '@ngrx/signals';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { I18n } from '../core/i18n/i18n';
import { describeError } from './describe-error';
import { ActiveProviderInfo } from '../core/api/models';
import { AuthService } from '../core/auth/auth.service';

interface ProvidersState {
  registeredProviderIds: string[];
  active: ActiveProviderInfo | null;
  loading: boolean;
  error: string | null;
}

/**
 * What GET /api/providers reports (ADR-0019): which LLM provider packages this host has
 * registered, and which one it is actually configured to use. Unlike TasksStore/ApprovalsStore
 * this is read-only, host-level configuration — it does not change while the app is open, so
 * this loads once on init rather than polling.
 */
export const ProvidersStore = signalStore(
  { providedIn: 'root' },
  withState<ProvidersState>({ registeredProviderIds: [], active: null, loading: false, error: null }),
  withMethods((store, api = inject(BOpsApiClient), i18n = inject(I18n)) => ({
    async refresh(): Promise<void> {
      patchState(store, { loading: true });
      try {
        const providers = await api.getProviders();
        patchState(store, {
          registeredProviderIds: providers.registeredProviderIds,
          active: providers.active,
          loading: false,
          error: null,
        });
      } catch (err) {
        patchState(store, { loading: false, error: describeError(err, i18n) });
      }
    },
  })),
  withHooks((store, auth = inject(AuthService)) => ({
    onInit() {
      if (auth.authenticated()) void store.refresh();
    },
  })),
);
