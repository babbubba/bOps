// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse } from '@angular/common/http';
import { TaskState, TaskStatusRunning } from '../api/models';

/** Delay between two polls of a running task. The server-side store changes a step every few seconds at most. */
export const TASK_POLL_INTERVAL_MS = 2000;

/** The longest wait between two polls while the API is failing. Finite, so a recovered API is noticed within this. */
export const TASK_MAX_BACKOFF_MS = 30_000;

/** The longest `Retry-After` honoured: a server asking for more than this is still polled again after it. */
const MAX_RETRY_AFTER_MS = 120_000;

/** Statuses that say "try again later": rate limited, or nothing listening yet behind a proxy or gateway. */
const TRANSIENT_STATUSES = [429, 502, 503, 504];

/**
 * How the live view relates to the API: `connected` (the last poll answered), `reconnecting` (the API is not answering; polling
 * goes on with backoff), `sessionExpired` (the credential was refused; polling has stopped until the operator signs in again).
 */
export type WatchConnection = 'connected' | 'reconnecting' | 'sessionExpired';

/** Why a watch ended without the task finishing: the task is gone (`notFound`), or the API refused in a way polling cannot fix (`failed`). */
export type WatchFailure = 'notFound' | 'failed';

export interface TaskWatchHandlers {
  /** A snapshot of the task from the expected execution attempt or a later one. Never a stale one. */
  onSnapshot(task: TaskState): void;
  onConnection?(state: WatchConnection): void;
  onError?(failure: WatchFailure, error: unknown): void;
}

function isTransient(err: unknown): boolean {
  return err instanceof HttpErrorResponse && (err.status === 0 || TRANSIENT_STATUSES.includes(err.status));
}

/** The server's `Retry-After` in seconds, bounded, so a rate-limited poll waits until the bucket has refilled. Dates are not parsed. */
function retryAfterMs(err: unknown): number | null {
  if (!(err instanceof HttpErrorResponse)) return null;
  const seconds = Number(err.headers?.get('Retry-After'));
  return Number.isFinite(seconds) && seconds > 0 ? Math.min(seconds * 1000, MAX_RETRY_AFTER_MS) : null;
}

/** The wait before the next poll after `failures` failures in a row: doubling from the poll interval, up to the ceiling. */
export function backoffMs(failures: number): number {
  return Math.min(TASK_POLL_INTERVAL_MS * 2 ** Math.min(failures, 10), TASK_MAX_BACKOFF_MS);
}

/**
 * Authenticated task watching. Native EventSource cannot attach the bearer header and putting a
 * credential in the URL would leak it, so the local UI polls the authenticated typed API client.
 * The server's SSE endpoint remains available to clients that can set request headers.
 *
 * The watch follows one execution attempt onward. A snapshot from an attempt before `expectedAttempt` is the state a resume
 * has not yet replaced: it is ignored, never shown and never ends the watch. The watch ends when a snapshot of the expected
 * attempt or later is terminal, when the task is gone (404), when the credential is refused (401, `sessionExpired`), or when the
 * caller stops it.
 *
 * A failure that says "try again later" (no answer, 429, 502, 503, 504) never ends the watch, however long it lasts: it is
 * `reconnecting`, retried with capped exponential backoff or the server's `Retry-After`, and woken at once when the browser comes
 * back online or the tab becomes visible. One poll runs at a time; one timer and one pair of listeners exist at a time.
 */
export function watchTaskEvents(
  taskId: string,
  getTask: (taskId: string) => Promise<TaskState>,
  handlers: TaskWatchHandlers,
  expectedAttempt = 1,
): () => void {
  let stopped = false;
  let inFlight = false;
  let failures = 0;
  let connection: WatchConnection = 'connected';
  let timer: ReturnType<typeof setTimeout> | undefined;

  const setConnection = (next: WatchConnection): void => {
    if (next !== connection) {
      connection = next;
      handlers.onConnection?.(next);
    }
  };

  const clearTimer = (): void => {
    if (timer !== undefined) {
      clearTimeout(timer);
      timer = undefined;
    }
  };

  const schedule = (delayMs: number): void => {
    clearTimer();
    timer = setTimeout(() => {
      timer = undefined;
      void poll();
    }, delayMs);
  };

  const poll = async (): Promise<void> => {
    if (stopped || inFlight) return;
    clearTimer();
    inFlight = true;

    let task: TaskState;
    try {
      task = await getTask(taskId);
    } catch (err) {
      inFlight = false;
      if (stopped) return;
      onFailure(err);
      return;
    }

    inFlight = false;
    if (stopped) return;
    failures = 0;
    setConnection('connected');

    if ((task.executionAttempt ?? 1) < expectedAttempt) {
      schedule(TASK_POLL_INTERVAL_MS);
      return;
    }

    handlers.onSnapshot(task);
    if (task.status !== TaskStatusRunning) {
      stop();
      return;
    }

    schedule(TASK_POLL_INTERVAL_MS);
  };

  const onFailure = (err: unknown): void => {
    if (err instanceof HttpErrorResponse && err.status === 401) {
      stop();
      setConnection('sessionExpired');
      return;
    }

    if (err instanceof HttpErrorResponse && err.status === 404) {
      stop();
      handlers.onError?.('notFound', err);
      return;
    }

    if (isTransient(err)) {
      failures++;
      setConnection('reconnecting');
      schedule(Math.max(backoffMs(failures), retryAfterMs(err) ?? 0));
      return;
    }

    stop();
    handlers.onError?.('failed', err);
  };

  const wake = (): void => {
    if (!stopped) void poll();
  };
  const onVisibilityChange = (): void => {
    if (document.visibilityState === 'visible') wake();
  };

  function stop(): void {
    stopped = true;
    clearTimer();
    window.removeEventListener('online', wake);
    document.removeEventListener('visibilitychange', onVisibilityChange);
  }

  window.addEventListener('online', wake);
  document.addEventListener('visibilitychange', onVisibilityChange);
  void poll();
  return stop;
}
