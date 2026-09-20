// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { I18n } from '../core/i18n/i18n';
import { describeError } from './describe-error';
import { SetProviderProfileRequest, SettingsView } from '../core/api/models';

interface SettingsState {
  view: SettingsView | null;
  loading: boolean;
  saving: boolean;
  error: string | null;
  conflict: boolean;
}

const initialState: SettingsState = {
  view: null,
  loading: false,
  saving: false,
  error: null,
  conflict: false,
};

/**
 * Administrator-only Settings (ADR-0029): active provider, each provider's profile, and each
 * provider's stored API key (write-only — never returned in plaintext). Every mutation
 * `refresh()`es afterward rather than optimistically patching local state, so the UI always
 * reflects what the server actually persisted, including its new `vaultVersion`.
 */
export const SettingsStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withMethods((store, api = inject(BOpsApiClient), i18n = inject(I18n)) => {
    async function refresh(): Promise<void> {
      patchState(store, { loading: true, error: null });
      try {
        const view = await api.getSettings();
        patchState(store, { view, loading: false, conflict: false });
      } catch (err) {
        // The Settings endpoints exist only when the vault is configured (Program.cs), so a 404 means "not on this host".
        if (err instanceof HttpErrorResponse && err.status === 404) {
          patchState(store, { view: null, loading: false, error: i18n.t('settings.error.notAvailable') });
        } else {
          patchState(store, { loading: false, error: describeError(err, i18n) });
        }
      }
    }

    async function mutate(action: () => Promise<void>): Promise<boolean> {
      patchState(store, { saving: true, error: null, conflict: false });
      try {
        await action();
        await refresh();
        patchState(store, { saving: false });
        return true;
      } catch (err) {
        const conflict = err instanceof HttpErrorResponse && err.status === 409;
        const errorMessage = describeError(err, i18n);
        await refresh();
        patchState(store, { saving: false, error: errorMessage, conflict });
        return false;
      }
    }

    return {
      refresh,

      setProviderKey(providerId: string, apiKey: string): Promise<boolean> {
        const expectedVersion = store.view()?.vaultVersion ?? 0;
        return mutate(() => api.setProviderKey(providerId, apiKey, expectedVersion));
      },

      clearProviderKey(providerId: string): Promise<boolean> {
        const expectedVersion = store.view()?.vaultVersion ?? 0;
        return mutate(() => api.clearProviderKey(providerId, expectedVersion));
      },

      setProviderProfile(providerId: string, request: SetProviderProfileRequest): Promise<boolean> {
        return mutate(() => api.setProviderProfile(providerId, request));
      },

      setActiveProvider(providerId: string): Promise<boolean> {
        return mutate(() => api.setActiveProvider(providerId));
      },
    };
  }),
);
