// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { ModelCallFailed, TaskState, TaskStatusRunning } from '../core/api/models';

/**
 * What the operator calls "Interrupted": the task is recorded as `Running` and no executor of this host holds it (ADR-0040 §9).
 * A view label only — the server has no such status, and the task is not resumable (whether it is is the server's `resumable`).
 */
export function isInterrupted(task: Pick<TaskState, 'status' | 'executing'>): boolean {
  return task.status === TaskStatusRunning && !task.executing;
}

/**
 * The provider's reason for the most recent failed model call of a task, as the runtime recorded it: sanitized and bounded on the
 * server (ADR-0039). The request and reply bodies never reach the UI, so this is the only provider text there is.
 */
export function lastFailedModelReason(task: TaskState): string | null {
  const calls = [...task.plans, ...task.steps].flatMap((entry) => entry.modelCalls ?? []);
  for (let index = calls.length - 1; index >= 0; index--) {
    if (calls[index].outcome === ModelCallFailed && calls[index].errorMessage) {
      return calls[index].errorMessage;
    }
  }

  return null;
}
