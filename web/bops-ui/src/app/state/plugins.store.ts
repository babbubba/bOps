// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { AuthService } from '../core/auth/auth.service';
import { I18n } from '../core/i18n/i18n';
import type { MessageKey } from '../core/i18n/messages';
import { describeError } from './describe-error';
import { PackageTrustLevel, PluginCatalogEntry, PluginLifecycleError, PluginLifecycleResponse } from '../core/api/models';

interface PluginsState {
  entries: PluginCatalogEntry[];
  totalCount: number;
  selectedPluginId: string | null;
  enabledFilter: boolean | null;
  trustFilter: PackageTrustLevel | null;
  loading: boolean;
  error: string | null;
  /** True while a lifecycle mutation is in flight (UX-only double-submit guard; the backend's idempotency is authoritative). */
  mutating: boolean;
  /** Outcome of the last lifecycle mutation. `stale` means the state changed and was reloaded. */
  notice: { kind: 'success' | 'error' | 'stale'; text: string } | null;
}

/** Longest backend-supplied message the UI will show. */
const MAX_NOTICE_LENGTH = 300;

const initialState: PluginsState = {
  entries: [],
  totalCount: 0,
  selectedPluginId: null,
  enabledFilter: null,
  trustFilter: null,
  loading: false,
  error: null,
  mutating: false,
  notice: null,
};

/**
 * The plugin catalog (V1.1-F) plus the administrator lifecycle mutations (V1.3-M7). A plain on-demand fetch, unlike TasksStore:
 * nothing is polled or streamed; every successful or failed mutation reloads the catalog from the server.
 *
 * The UI is an operator surface, not an authority: the backend decides authorization, lifecycle legality, staleness and idempotency.
 * Here that means (a) `lifecycleETag` is an opaque token passed back verbatim as `If-Match`; (b) a 412, 428 or idempotency conflict is
 * never retried automatically: the state is reloaded and the operator must decide again; (c) an Idempotency-Key is reused only while
 * the operator's intent (operation, plugin, ETag, version, selected archive) is unchanged, i.e. for an ambiguous transport retry.
 */
export const PluginsStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withComputed((_store, auth = inject(AuthService)) => ({
    /** Presentation only; fails closed while the identity is unknown. The API's administrator policy is what actually enforces it. */
    isAdministrator: computed(() => auth.identity()?.roles.includes('administrator') ?? false),
  })),
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

    let pendingKey: { intent: string; key: string } | null = null;

    const keyFor = (intent: string): string => {
      if (pendingKey?.intent !== intent) {
        pendingKey = { intent, key: crypto.randomUUID() };
      }
      return pendingKey.key;
    };

    const failureText = (err: unknown): string => {
      const body = (err as { error?: Partial<PluginLifecycleError> } | null)?.error;
      if (typeof body?.message === 'string') {
        const detail = [body.category, body.stage].filter((part) => typeof part === 'string' && part).join(' · ');
        const message = body.message.slice(0, MAX_NOTICE_LENGTH);
        return detail ? `${message} (${detail.slice(0, 80)})` : message;
      }
      return describeError(err, i18n);
    };

    /**
     * Runs one lifecycle mutation. Never retries and never re-reads an ETag on the operator's behalf; returns whether it succeeded.
     */
    async function mutate(
      intent: string,
      call: (idempotencyKey: string) => Promise<PluginLifecycleResponse>,
      successKey: MessageKey,
    ): Promise<boolean> {
      if (store.mutating()) {
        return false;
      }

      patchState(store, { mutating: true, notice: null });
      try {
        await call(keyFor(intent));
        pendingKey = null;
        patchState(store, { notice: { kind: 'success', text: i18n.t(successKey) } });
        await refresh();
        return true;
      } catch (err) {
        const status = err instanceof HttpErrorResponse ? err.status : 0;
        const category = (err as { error?: Partial<PluginLifecycleError> } | null)?.error?.category;
        // Only an ambiguous transport outcome keeps the key (a manual retry of the same intent replays safely). Anything the
        // backend answered definitively ends this intent: a stale or conflicting one must be re-decided by the operator.
        if (status !== 0 && status < 500) {
          pendingKey = null;
        }
        const stale = status === 412 || status === 428;
        const text =
          category === 'idempotency_conflict'
            ? `${failureText(err)} ${i18n.t('plugins.notice.reviewAndRetry')}`
            : failureText(err);
        patchState(store, {
          notice: stale
            ? { kind: 'stale', text: i18n.t(status === 412 ? 'plugins.notice.stale' : 'plugins.notice.preconditionMissing') }
            : { kind: 'error', text },
        });
        await refresh();
        return false;
      } finally {
        patchState(store, { mutating: false });
      }
    }

    const archiveToken = (file: File): string => `${file.name}|${file.size}|${file.lastModified}`;

    return {
      refresh,

      clearNotice(): void {
        patchState(store, { notice: null });
      },

      /** Create-only install: the plugin id comes from the archive's manifest on the server, never from the file name. */
      installArchive(file: File): Promise<boolean> {
        return mutate(`install|${archiveToken(file)}`, (key) => api.installPluginArchive(file, key), 'plugins.notice.installed');
      },

      replaceArchive(entry: PluginCatalogEntry, file: File): Promise<boolean> {
        const etag = entry.lifecycleETag;
        if (!etag) {
          return Promise.resolve(false);
        }
        return mutate(
          `replace|${entry.id}|${etag}|${archiveToken(file)}`,
          (key) => api.replacePluginArchive(entry.id, file, etag, key),
          'plugins.notice.replaced',
        );
      },

      /** `confirmedVersion` must be the exact version the operator was shown in the confirmation. */
      enable(entry: PluginCatalogEntry, confirmedVersion: string): Promise<boolean> {
        const etag = entry.lifecycleETag;
        if (!etag) {
          return Promise.resolve(false);
        }
        return mutate(
          `enable|${entry.id}|${etag}|${confirmedVersion}`,
          (key) => api.enablePlugin(entry.id, confirmedVersion, etag, key),
          'plugins.notice.enabled',
        );
      },

      disable(entry: PluginCatalogEntry): Promise<boolean> {
        const etag = entry.lifecycleETag;
        if (!etag) {
          return Promise.resolve(false);
        }
        return mutate(`disable|${entry.id}|${etag}`, (key) => api.disablePlugin(entry.id, etag, key), 'plugins.notice.disabled');
      },

      /** Restores the last successfully activated generation as disabled. Never enables. */
      recover(entry: PluginCatalogEntry): Promise<boolean> {
        const etag = entry.lifecycleETag;
        if (!etag) {
          return Promise.resolve(false);
        }
        return mutate(`recover|${entry.id}|${etag}`, (key) => api.recoverPlugin(entry.id, etag, key), 'plugins.notice.recovered');
      },

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
