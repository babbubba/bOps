// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { TaskState, TaskStatusRunning } from '../api/models';

/**
 * Authenticated task watching. Native EventSource cannot attach the bearer header and putting a
 * credential in the URL would leak it, so the local UI polls the authenticated typed API client.
 * The server's SSE endpoint remains available to clients that can set request headers.
 */
export function watchTaskEvents(
  taskId: string,
  getTask: (taskId: string) => Promise<TaskState>,
  onSnapshot: (task: TaskState) => void,
  onError?: () => void,
): () => void {
  let stopped = false;
  let timer: ReturnType<typeof setTimeout> | undefined;

  const poll = async (): Promise<void> => {
    try {
      const task = await getTask(taskId);
      if (stopped) return;
      onSnapshot(task);
      if (task.status !== TaskStatusRunning) {
        stopped = true;
        return;
      }
      timer = setTimeout(() => void poll(), 500);
    } catch {
      if (!stopped) onError?.();
      stopped = true;
    }
  };

  void poll();
  return () => {
    stopped = true;
    if (timer) clearTimeout(timer);
  };
}
