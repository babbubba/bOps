// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { SystemMessage, SystemMessageFilter } from '../core/api/models';
import { I18n } from '../core/i18n/i18n';
import { describeError } from './describe-error';

/** The default page size of the System Messages page (the API's own default and a cap of 200). */
export const SYSTEM_MESSAGES_PAGE_SIZE = 50;

interface SystemMessagesState {
  /** The filters of the page on screen — what was last applied, never what is merely typed. */
  filter: SystemMessageFilter;
  /** The rows of the page on screen only. A page replaces the previous one; nothing is accumulated or filtered on the client. */
  items: SystemMessage[];
  /** The cursor that fetched each page seen on this path (`undefined` for the first), so Previous can ask for the same page again. */
  cursors: Array<string | undefined>;
  cursorIndex: number;
  nextCursor: string | null;
  loading: boolean;
  /** Whether a response (or failure) has arrived since the filters last changed: tells "loading" from "empty". */
  loaded: boolean;
  error: string | null;
}

const initial: SystemMessagesState = {
  filter: {},
  items: [],
  cursors: [undefined],
  cursorIndex: 0,
  nextCursor: null,
  loading: false,
  loaded: false,
  error: null,
};

/**
 * The System Messages page (ADR-0049). Every filter and the page size go to `GET /api/system-messages`, which filters (AND) and pages
 * by an opaque keyset cursor; this store only remembers which cursor produced which page so Next and Previous are exact. A response
 * that arrives after a newer request was made is dropped, so a slow page never replaces the one the operator asked for last.
 */
export const SystemMessagesStore = signalStore(
  { providedIn: 'root' },
  withState<SystemMessagesState>(initial),
  withMethods((store, api = inject(BOpsApiClient), i18n = inject(I18n)) => {
    let latest = 0;

    async function load(filter: SystemMessageFilter, cursors: Array<string | undefined>, cursorIndex: number): Promise<void> {
      const request = ++latest;
      patchState(store, { filter, cursors, cursorIndex, loading: true, error: null });
      try {
        const page = await api.listSystemMessages(filter, SYSTEM_MESSAGES_PAGE_SIZE, cursors[cursorIndex]);
        if (request !== latest) return;
        const seen = new Set<string>();
        const items = page.items.filter((item) => !seen.has(item.id) && !!seen.add(item.id));
        patchState(store, { items, nextCursor: page.nextCursor, loading: false, loaded: true });
      } catch (err) {
        if (request !== latest) return;
        patchState(store, { items: [], nextCursor: null, loading: false, loaded: true, error: describeError(err, i18n) });
      }
    }

    return {
      /** Applies new filters and shows the newest page of the result. */
      apply: (filter: SystemMessageFilter) => load(filter, [undefined], 0),

      /** Clears every filter. */
      reset: () => load({}, [undefined], 0),

      /** Fetches the current page again (the newest first page when on the first). */
      refresh: () => load(store.filter(), store.cursors(), store.cursorIndex()),

      /** The next, older page — only when the server sent a cursor for it. */
      async next(): Promise<void> {
        const cursor = store.nextCursor();
        if (!cursor || store.loading()) return;
        await load(store.filter(), [...store.cursors().slice(0, store.cursorIndex() + 1), cursor], store.cursorIndex() + 1);
      },

      /** The previous, newer page, fetched again with the cursor that produced it. */
      async previous(): Promise<void> {
        if (store.cursorIndex() === 0 || store.loading()) return;
        await load(store.filter(), store.cursors(), store.cursorIndex() - 1);
      },
    };
  }),
);
