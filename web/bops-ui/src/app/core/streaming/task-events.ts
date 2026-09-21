// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse } from '@angular/common/http';
import { TaskState, TaskStatusRunning } from '../api/models';

/** Delay between two polls of a running task. The server-side store changes a step every few seconds at most. */
export const TASK_POLL_INTERVAL_MS = 2000;

/** Backoff ceiling, and how many failures in a row (with no successful poll between) are tolerated before giving up. */
const MAX_BACKOFF_MS = 30_000;
const MAX_CONSECUTIVE_FAILURES = 8;

/** Statuses that say "try again later": rate limited, or nothing listening yet behind a proxy or gateway. */
const TRANSIENT_STATUSES = [429, 502, 503, 504];

function isTransient(err: unknown): boolean {
  return err instanceof HttpErrorResponse && (err.status === 0 || TRANSIENT_STATUSES.includes(err.status));
}

/** The server's `Retry-After` (seconds) when it sent one, so a 429 is retried when the bucket has refilled. */
function retryAfterMs(err: unknown): number | null {
  if (!(err instanceof HttpErrorResponse)) return null;
  const seconds = Number(err.headers?.get('Retry-After'));
  return Number.isFinite(seconds) && seconds > 0 ? seconds * 1000 : null;
}

/**
 * Authenticated task watching. Native EventSource cannot attach the bearer header and putting a
 * credential in the URL would leak it, so the local UI polls the authenticated typed API client.
 * The server's SSE endpoint remains available to clients that can set request headers.
 *
 * A transient failure (429, 502-504, no answer) is retried with exponential backoff — or the server's
 * `Retry-After` — and never surfaces on its own; only a permanent failure (404, 401, …) or too many
 * consecutive transient ones stop the watch and call `onError`.
 */
export function watchTaskEvents(
  taskId: string,
  getTask: (taskId: string) => Promise<TaskState>,
  onSnapshot: (task: TaskState) => void,
  onError?: () => void,
): () => void {
  let stopped = false;
  let failures = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;

  const poll = async (): Promise<void> => {
    try {
      const task = await getTask(taskId);
      if (stopped) return;
      failures = 0;
      onSnapshot(task);
      if (task.status !== TaskStatusRunning) {
        stopped = true;
        return;
      }
      timer = setTimeout(() => void poll(), TASK_POLL_INTERVAL_MS);
    } catch (err) {
      if (stopped) return;
      failures++;
      if (isTransient(err) && failures < MAX_CONSECUTIVE_FAILURES) {
        const backoff = Math.min(TASK_POLL_INTERVAL_MS * 2 ** failures, MAX_BACKOFF_MS);
        timer = setTimeout(() => void poll(), Math.max(backoff, retryAfterMs(err) ?? 0));
        return;
      }
      stopped = true;
      onError?.();
    }
  };

  void poll();
  return () => {
    stopped = true;
    if (timer) clearTimeout(timer);
  };
}
