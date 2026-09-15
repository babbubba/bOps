import { inject } from '@angular/core';
import { patchState, signalStore, withHooks, withMethods, withState } from '@ngrx/signals';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { TaskState, TaskStatusRunning } from '../core/api/models';
import { watchTaskEvents } from '../core/streaming/task-events';

interface TasksState {
  tasks: TaskState[];
  selectedTaskId: string | null;
  selectedTask: TaskState | null;
  loading: boolean;
  starting: boolean;
  error: string | null;
}

const initialState: TasksState = {
  tasks: [],
  selectedTaskId: null,
  selectedTask: null,
  loading: false,
  starting: false,
  error: null,
};

function describeError(err: unknown): string {
  return err instanceof Error ? err.message : 'Something went wrong.';
}

/**
 * Running tasks and whichever one is currently selected, kept live via SSE (ADR-0018,
 * core/streaming/task-events). One active watch at a time — selecting a different task, or
 * starting/resuming a new one, tears down the previous stream before opening the next.
 */
export const TasksStore = signalStore(
  { providedIn: 'root' },
  withState(initialState),
  withMethods((store, api = inject(BOpsApiClient)) => {
    let stopWatching: (() => void) | null = null;

    function watch(taskId: string): void {
      stopWatching?.();
      stopWatching = watchTaskEvents(
        taskId,
        (task) => patchState(store, { selectedTask: task }),
        () => patchState(store, { error: 'Lost the live connection to this task.' }),
      );
    }

    return {
      async refresh(): Promise<void> {
        patchState(store, { loading: true, error: null });
        try {
          const tasks = await api.listTasks(TaskStatusRunning);
          patchState(store, { tasks, loading: false });
        } catch (err) {
          patchState(store, { loading: false, error: describeError(err) });
        }
      },

      async start(goal: string): Promise<void> {
        patchState(store, { starting: true, error: null });
        try {
          const { taskId } = await api.startTask(goal);
          patchState(store, { starting: false, selectedTaskId: taskId, selectedTask: null });
          watch(taskId);
        } catch (err) {
          patchState(store, { starting: false, error: describeError(err) });
        }
      },

      async resume(taskId: string): Promise<void> {
        patchState(store, { error: null });
        try {
          await api.resumeTask(taskId);
          patchState(store, { selectedTaskId: taskId, selectedTask: null });
          watch(taskId);
        } catch (err) {
          patchState(store, { error: describeError(err) });
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
          patchState(store, { error: describeError(err) });
        }
      },

      clearSelection(): void {
        stopWatching?.();
        stopWatching = null;
        patchState(store, { selectedTaskId: null, selectedTask: null });
      },
    };
  }),
  withHooks((store) => {
    let intervalId: ReturnType<typeof setInterval> | undefined;

    return {
      onInit() {
        store.refresh();
        // A light poll for the Running-task list itself (which tasks exist), independent of the
        // SSE stream (which only ever covers the one currently selected task) — catches a task
        // someone else started, or one that just left the list by completing.
        intervalId = setInterval(() => store.refresh(), 3000);
      },
      onDestroy() {
        clearInterval(intervalId);
      },
    };
  }),
);
