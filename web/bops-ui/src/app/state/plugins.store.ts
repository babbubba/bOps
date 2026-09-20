// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { I18n } from '../core/i18n/i18n';
import { describeError } from './describe-error';
import { PackageTrustLevel, PluginCatalogEntry } from '../core/api/models';

interface PluginsState {
  entries: PluginCatalogEntry[];
  totalCount: number;
  selectedPluginId: string | null;
  enabledFilter: boolean | null;
  trustFilter: PackageTrustLevel | null;
  loading: boolean;
  error: string | null;
}

const initialState: PluginsState = {
  entries: [],
  totalCount: 0,
  selectedPluginId: null,
  enabledFilter: null,
  trustFilter: null,
  loading: false,
  error: null,
};

/**
 * The read-only plugin catalog (V1.1-F) — a plain on-demand fetch, unlike TasksStore: a plugin's
 * catalog entry never changes while you are looking at it (no enable/disable/upload in this
 * batch), so there is nothing here to poll or stream.
 */
export const PluginsStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withMethods((store, api = inject(BOpsApiClient), i18n = inject(I18n)) => {
    async function refresh(): Promise<void> {
      patchState(store, { loading: true, error: null });
      try {
        const page = await api.listPlugins({
          enabled: store.enabledFilter() ?? undefined,
          trust: store.trustFilter() ?? undefined,
        });
        patchState(store, { entries: page.entries, totalCount: page.totalCount, loading: false });
      } catch (err) {
        patchState(store, { loading: false, error: describeError(err, i18n) });
      }
    }

    return {
      refresh,

      setEnabledFilter(enabledFilter: boolean | null): void {
        patchState(store, { enabledFilter });
        void refresh();
      },

      setTrustFilter(trustFilter: PackageTrustLevel | null): void {
        patchState(store, { trustFilter });
        void refresh();
      },

      selectPlugin(pluginId: string): void {
        patchState(store, { selectedPluginId: pluginId });
      },

      clearSelection(): void {
        patchState(store, { selectedPluginId: null });
      },
    };
  }),
);
