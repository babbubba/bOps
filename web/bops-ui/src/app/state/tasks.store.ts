// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { inject } from '@angular/core';
import { patchState, signalStore, withHooks, withMethods, withState } from '@ngrx/signals';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { I18n } from '../core/i18n/i18n';
import { describeError } from './describe-error';
import { AgentTaskStatus, TaskState, TaskStatusRunning } from '../core/api/models';
import { watchTaskEvents } from '../core/streaming/task-events';
import { AuthService } from '../core/auth/auth.service';

interface TasksState {
  /** Which status the task list shows; Running is the live view, anything else is history. */
  statusFilter: AgentTaskStatus;
  tasks: TaskState[];
  selectedTaskId: string | null;
  selectedTask: TaskState | null;
  loading: boolean;
  starting: boolean;
  error: string | null;
}

const initialState: TasksState = {
  statusFilter: TaskStatusRunning,
  tasks: [],
  selectedTaskId: null,
  selectedTask: null,
  loading: false,
  starting: false,
  error: null,
};

/**
 * The task list for the chosen status (default Running, polled; any terminal status is a plain\n * on-demand fetch of history) and whichever task is currently selected, kept live via SSE\n * (ADR-0018, core/streaming/task-events). One active watch at a time — selecting a different task, or
 * starting/resuming a new one, tears down the previous stream before opening the next.
 */
export const TasksStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withMethods((store, api = inject(BOpsApiClient), i18n = inject(I18n)) => {
    let stopWatching: (() => void) | null = null;

    function watch(taskId: string): void {
      stopWatching?.();
      stopWatching = watchTaskEvents(
        taskId,
        (id) => api.getTask(id),
        (task) => patchState(store, { selectedTask: task }),
        () => patchState(store, { error: i18n.t('common.error.liveConnection') }),
      );
    }

    async function refresh(): Promise<void> {
      const status = store.statusFilter();
      patchState(store, { loading: true, error: null });
      try {
        const tasks = await api.listTasks(status);
        // The operator may have switched filters while this request was in flight.
        if (store.statusFilter() === status) {
          patchState(store, { tasks, loading: false });
        }
      } catch (err) {
        if (store.statusFilter() === status) {
          patchState(store, { loading: false, error: describeError(err, i18n) });
        }
      }
    }

    return {
      refresh,

      async setStatusFilter(status: AgentTaskStatus): Promise<void> {
        if (status === store.statusFilter()) {
          return;
        }

        patchState(store, { statusFilter: status, tasks: [] });
        await refresh();
      },

      async start(goal: string): Promise<void> {
        patchState(store, { starting: true, error: null });
        try {
          const { taskId } = await api.startTask(goal);
          patchState(store, { starting: false, selectedTaskId: taskId, selectedTask: null });
          watch(taskId);
        } catch (err) {
          patchState(store, { starting: false, error: describeError(err, i18n) });
        }
      },

      async resume(taskId: string): Promise<void> {
        patchState(store, { error: null });
        try {
          await api.resumeTask(taskId);
          patchState(store, { selectedTaskId: taskId, selectedTask: null });
          watch(taskId);
        } catch (err) {
          patchState(store, { error: describeError(err, i18n) });
        }
      },

      async selectTask(taskId: string): Promise<void> {
        patchState(store, { selectedTaskId: taskId, selectedTask: null, error: null });
        try {
          const task = await api.getTask(taskId);
          patchState(store, { selectedTask: task });
          if (task.status === TaskStatusRunning) {
            watch(taskId);
          }
        } catch (err) {
          patchState(store, { error: describeError(err, i18n) });
        }
      },

      clearSelection(): void {
        stopWatching?.();
        stopWatching = null;
        patchState(store, { selectedTaskId: null, selectedTask: null });
      },
    };
  }),
  withHooks((store, auth = inject(AuthService)) => {
    let intervalId: ReturnType<typeof setInterval> | undefined;

    return {
      onInit() {
        if (auth.authenticated()) void store.refresh();
        // A light poll for the Running-task list itself (which tasks exist), independent of the
        // SSE stream (which only ever covers the one currently selected task) — catches a task
        // someone else started, or one that just left the list by completing.
        // History (any terminal status) has nothing live to watch, so it is never polled.
        intervalId = setInterval(() => {
          if (auth.authenticated() && store.statusFilter() === TaskStatusRunning) void store.refresh();
        }, 3000);
      },
      onDestroy() {
        clearInterval(intervalId);
      },
    };
  }),
);
