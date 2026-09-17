// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { BOpsApiClient } from '../core/api/bops-api-client';
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

function describeError(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    const message = (err.error as { message?: string } | null)?.message;
    return message ?? err.message;
  }

  return err instanceof Error ? err.message : 'Something went wrong.';
}

/**
 * Administrator-only Settings (ADR-0029): active provider, each provider's profile, and each
 * provider's stored API key (write-only — never returned in plaintext). Every mutation
 * `refresh()`es afterward rather than optimistically patching local state, so the UI always
 * reflects what the server actually persisted, including its new `vaultVersion`.
 */
export const SettingsStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withMethods((store, api = inject(BOpsApiClient)) => {
    async function refresh(): Promise<void> {
      patchState(store, { loading: true, error: null });
      try {
        const view = await api.getSettings();
        patchState(store, { view, loading: false, conflict: false });
      } catch (err) {
        patchState(store, { loading: false, error: describeError(err) });
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
        const errorMessage = describeError(err);
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
