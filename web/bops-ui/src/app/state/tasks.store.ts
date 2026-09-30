// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse } from '@angular/common/http';
import { effect, inject, untracked } from '@angular/core';
import { patchState, signalStore, withHooks, withMethods, withState } from '@ngrx/signals';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { I18n } from '../core/i18n/i18n';
import { describeError } from './describe-error';
import { AgentTaskStatus, TaskState, TaskStatusRunning } from '../core/api/models';
import { WatchConnection, watchTaskEvents } from '../core/streaming/task-events';
import { AuthService } from '../core/auth/auth.service';

interface TasksState {
  /** Which status the task list shows; Running is the live view, anything else is history. */
  statusFilter: AgentTaskStatus;
  tasks: TaskState[];
  selectedTaskId: string | null;
  selectedTask: TaskState | null;
  loading: boolean;
  starting: boolean;
  /** A resume request is in flight: the Resume action is disabled until the server has answered. */
  resuming: boolean;
  /** A cancel was requested and the task has not stopped yet; it ends when the watched task is no longer executing. */
  cancelling: boolean;
  /** How the live view of the selected task relates to the API. */
  connection: WatchConnection;
  /** The watch to restart once the operator has signed in again after the session expired. Memory only, never a credential. */
  pendingWatch: { taskId: string; expectedAttempt: number } | null;
  error: string | null;
}

const initialState: TasksState = {
  statusFilter: TaskStatusRunning,
  tasks: [],
  selectedTaskId: null,
  selectedTask: null,
  loading: false,
  starting: false,
  resuming: false,
  cancelling: false,
  connection: 'connected',
  pendingWatch: null,
  error: null,
};

/** A refusal the server explained with a stable code (409, 501, 503 with a body), as opposed to a transport failure. */
function isRefusal(err: unknown): boolean {
  return err instanceof HttpErrorResponse && typeof (err.error as { code?: unknown } | null)?.code === 'string';
}

/**
 * The task list for the chosen status (default Running, polled; any terminal status is a plain
 * on-demand fetch of history) and whichever task is currently selected, kept live by polling
 * (ADR-0018, core/streaming/task-events). One active watch at a time — selecting a different task, or
 * starting/resuming a new one, tears down the previous watch before opening the next.
 *
 * What the operator may do with a task is the server's word (`resumable`, `executing`); this store only sends the request and
 * follows the lifecycle the server reports, starting from the execution attempt the server accepted.
 */
export const TasksStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withMethods((store, api = inject(BOpsApiClient), i18n = inject(I18n), auth = inject(AuthService)) => {
    let stopWatching: (() => void) | null = null;

    function stopWatch(): void {
      stopWatching?.();
      stopWatching = null;
    }

    function watch(taskId: string, expectedAttempt: number): void {
      stopWatch();
      patchState(store, { connection: 'connected', pendingWatch: null });
      stopWatching = watchTaskEvents(
        taskId,
        (id) => api.getTask(id),
        {
          onSnapshot: (task) => {
            if (store.selectedTaskId() !== taskId) return;
            patchState(store, {
              selectedTask: task,
              cancelling: store.cancelling() && task.executing && task.status === TaskStatusRunning,
            });
          },
          onConnection: (connection) => {
            // A refusal that arrives after the operator signed out (the answer to a poll already in flight) is not an expiry:
            // signing out ends the watch, it does not leave one to be restored for whoever signs in next.
            if (connection === 'sessionExpired' && !auth.authenticated()) return;
            patchState(store, { connection });
            if (connection === 'sessionExpired') {
              patchState(store, { pendingWatch: { taskId, expectedAttempt } });
              auth.expireSession();
            }
          },
          onError: (failure) =>
            patchState(store, {
              cancelling: false,
              error: failure === 'notFound' ? i18n.t('task.error.gone') : i18n.t('common.error.liveConnection'),
            }),
        },
        expectedAttempt,
      );
    }

    /** Brings the selected task to what the server says now, after a request that failed because the state had moved on. */
    async function reloadSelected(taskId: string): Promise<void> {
      try {
        const task = await api.getTask(taskId);
        if (store.selectedTaskId() !== taskId) return;
        patchState(store, { selectedTask: task });
        if (task.status === TaskStatusRunning) {
          watch(taskId, task.executionAttempt);
        }
      } catch {
        // The failure that made this reload necessary is already what the operator sees; a second message would only hide it.
      }
    }

    /** The text of the last list failure, so that a recovered list clears its own message and never one about a resume or a cancel. */
    let listError: string | null = null;

    async function refresh(): Promise<void> {
      const status = store.statusFilter();
      patchState(store, { loading: true });
      try {
        const tasks = await api.listTasks(status);
        // The operator may have switched filters while this request was in flight.
        if (store.statusFilter() === status) {
          patchState(store, { tasks, loading: false, ...(listError !== null && store.error() === listError ? { error: null } : {}) });
          listError = null;
        }
      } catch (err) {
        if (store.statusFilter() === status) {
          listError = describeError(err, i18n);
          patchState(store, { loading: false, error: listError });
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
          patchState(store, { starting: false, selectedTaskId: taskId, selectedTask: null, cancelling: false });
          watch(taskId, 1);
        } catch (err) {
          patchState(store, { starting: false, error: describeError(err, i18n) });
        }
      },

      /**
       * Resumes a task. The 202 describes the transition the server persisted, so the selected task shows it at once — `Running`,
       * under the new execution attempt — and keeps its steps, plans and accounting. The watch then ignores any snapshot of an
       * earlier attempt.
       */
      async resume(taskId: string): Promise<void> {
        if (store.resuming()) {
          return;
        }

        patchState(store, { resuming: true, error: null });
        try {
          const accepted = await api.resumeTask(taskId);
          const current = store.selectedTask();
          patchState(store, {
            resuming: false,
            cancelling: false,
            selectedTaskId: taskId,
            selectedTask:
              current?.id === taskId
                ? {
                    ...current,
                    status: accepted.status,
                    executionAttempt: accepted.executionAttempt,
                    executing: accepted.executing,
                    resumable: accepted.resumable,
                    resumeBlockedReason: accepted.resumeBlockedReason,
                    terminalReason: null,
                  }
                : null,
          });
          watch(taskId, accepted.executionAttempt);
        } catch (err) {
          patchState(store, { resuming: false, error: describeError(err, i18n) });
          if (isRefusal(err)) {
            await reloadSelected(taskId);
          }
        }
      },

      /**
       * Asks the host to stop the task. Cancellation is asynchronous, so a 202 does not make the task `Cancelled` here: the watch
       * follows the server's state until it says the task is no longer executing.
       */
      async cancel(taskId: string): Promise<void> {
        if (store.cancelling()) {
          return;
        }

        patchState(store, { cancelling: true, error: null });
        try {
          await api.cancelTask(taskId);
          if (store.selectedTaskId() === taskId) {
            const current = store.selectedTask();
            watch(taskId, current?.id === taskId ? current.executionAttempt : 1);
          } else {
            patchState(store, { cancelling: false });
          }
        } catch (err) {
          const notRunning = err instanceof HttpErrorResponse && err.status === 404;
          patchState(store, {
            cancelling: false,
            error: notRunning ? i18n.t('task.error.cancelNotRunning') : describeError(err, i18n),
          });
          if (notRunning) {
            await reloadSelected(taskId);
          }
        }
      },

      async selectTask(taskId: string): Promise<void> {
        stopWatch();
        patchState(store, {
          selectedTaskId: taskId,
          selectedTask: null,
          error: null,
          cancelling: false,
          connection: 'connected',
          pendingWatch: null,
        });
        try {
          const task = await api.getTask(taskId);
          if (store.selectedTaskId() !== taskId) return;
          patchState(store, { selectedTask: task });
          if (task.status === TaskStatusRunning) {
            watch(taskId, task.executionAttempt);
          }
        } catch (err) {
          if (store.selectedTaskId() === taskId) {
            patchState(store, { error: describeError(err, i18n) });
          }
        }
      },

      /** Restarts the watch that a 401 ended, now that the operator has signed in again. */
      restoreWatch(): void {
        const pending = store.pendingWatch();
        if (pending && store.selectedTaskId() === pending.taskId) {
          watch(pending.taskId, pending.expectedAttempt);
        } else {
          patchState(store, { pendingWatch: null });
        }
      },

      clearSelection(): void {
        stopWatch();
        patchState(store, {
          selectedTaskId: null,
          selectedTask: null,
          cancelling: false,
          connection: 'connected',
          pendingWatch: null,
        });
      },
    };
  }),
  withHooks((store, auth = inject(AuthService)) => {
    let intervalId: ReturnType<typeof setInterval> | undefined;

    return {
      onInit() {
        if (auth.authenticated()) void store.refresh();
        // A light poll for the Running-task list itself (which tasks exist), independent of the
        // watch (which only ever covers the one currently selected task) — catches a task
        // someone else started, or one that just left the list by completing.
        // History (any terminal status) has nothing live to watch, so it is never polled.
        intervalId = setInterval(() => {
          if (auth.authenticated() && store.statusFilter() === TaskStatusRunning) void store.refresh();
        }, 3000);

        // After a 401 the operator signs in again through the ordinary login; the task they were following picks up where it was.
        effect(() => {
          if (auth.authenticated() && store.pendingWatch()) {
            untracked(() => store.restoreWatch());
          }
        });

        // Signing out on purpose ends what the operator was following. An expiry is told apart by the watch it leaves pending:
        // only the expiry path leaves one, so a sign-out never resumes under the next identity.
        let wasAuthenticated = auth.authenticated();
        effect(() => {
          const authenticated = auth.authenticated();
          untracked(() => {
            if (wasAuthenticated && !authenticated && !store.pendingWatch()) store.clearSelection();
            wasAuthenticated = authenticated;
          });
        });
      },
      onDestroy() {
        clearInterval(intervalId);
      },
    };
  }),
);
