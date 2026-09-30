// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { discardPeriodicTasks, fakeAsync, TestBed, tick } from '@angular/core/testing';
import { signal } from '@angular/core';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { AuthService } from '../core/auth/auth.service';
import { PlanStep, TaskResumeAcceptedResponse, TaskState } from '../core/api/models';
import { TASK_POLL_INTERVAL_MS, backoffMs } from '../core/streaming/task-events';
import { TasksStore } from './tasks.store';

function task(id: string, status: TaskState['status'] = 0, overrides: Partial<TaskState> = {}): TaskState {
  return {
    id,
    node: 'local',
    goal: `Goal ${id}`,
    status,
    steps: [],
    plans: [],
    createdAtUtc: '2026-09-15T12:00:00Z',
    executionAttempt: 1,
    accounting: { tokensUsed: 0, lifetimeSteps: 0, lifetimeReplans: 0 },
    origin: 1,
    executing: status === 0,
    resumable: false,
    resumeBlockedReason: null,
    ...overrides,
  };
}

const step: PlanStep = {
  index: 0,
  description: 'system.cpu',
  toolCall: { id: 'c1', toolName: 'system.cpu', arguments: {} },
  result: { outcome: 0, output: '11%', errorMessage: null, succeeded: true },
  observation: '11%',
  planRevision: 0,
};

const accepted: TaskResumeAcceptedResponse = {
  taskId: 'task-2',
  status: 0,
  executionAttempt: 2,
  executing: true,
  resumable: false,
  resumeBlockedReason: { code: 'task_running', message: 'running' },
};

/** A failed task the operator could resume: a server-computed `resumable: true`. */
function failedTask(id = 'task-2'): TaskState {
  return task(id, 6, {
    steps: [step],
    plans: [{ revision: 0, rationale: 'r', steps: [] }],
    accounting: { tokensUsed: 1234, lifetimeSteps: 1, lifetimeReplans: 0 },
    terminalReason: { kind: 8, failureKind: 1 },
    executing: false,
    resumable: true,
  });
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

function refusal(status: number, code: string, headers?: Record<string, string>): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: { code, message: 'server text' }, headers: new HttpHeaders(headers) });
}

describe('TasksStore', () => {
  let api: jasmine.SpyObj<BOpsApiClient>;
  let authenticated: ReturnType<typeof signal<boolean>>;
  let auth: { authenticated: ReturnType<typeof signal<boolean>>; expireSession: jasmine.Spy };

  beforeEach(() => {
    api = jasmine.createSpyObj<BOpsApiClient>('BOpsApiClient', ['listTasks', 'startTask', 'resumeTask', 'cancelTask', 'getTask']);
    api.listTasks.and.resolveTo([]);
    api.startTask.and.resolveTo({ taskId: 'started' });
    api.resumeTask.and.resolveTo(accepted);
    api.cancelTask.and.resolveTo();
    api.getTask.and.resolveTo(task('selected'));

    authenticated = signal(true);
    auth = { authenticated, expireSession: jasmine.createSpy('expireSession').and.callFake(() => authenticated.set(false)) };

    TestBed.configureTestingModule({
      providers: [TasksStore, { provide: BOpsApiClient, useValue: api }, { provide: AuthService, useValue: auth }],
    });
  });

  /** Ends the watch and the list poll so the fakeAsync zone is left with no timer. */
  function finish(store: InstanceType<typeof TasksStore>): void {
    store.clearSelection();
    discardPeriodicTasks();
  }

  it('loads running tasks immediately and refreshes them every three seconds', fakeAsync(() => {
    api.listTasks.and.resolveTo([task('running')]);

    const store = TestBed.inject(TasksStore);
    tick();

    expect(api.listTasks).toHaveBeenCalledOnceWith(0);
    expect(store.tasks()).toEqual([task('running')]);
    expect(store.loading()).toBeFalse();

    tick(3000);
    expect(api.listTasks).toHaveBeenCalledTimes(2);
    discardPeriodicTasks();
  }));

  it('fetches history once for a terminal status and stops polling it', fakeAsync(() => {
    api.listTasks.and.callFake(async (status) => (status === 1 ? [task('done', 1)] : []));
    const store = TestBed.inject(TasksStore);
    tick();
    expect(store.statusFilter()).toBe(0);

    void store.setStatusFilter(1);
    tick();
    expect(api.listTasks).toHaveBeenCalledWith(1);
    expect(store.tasks()).toEqual([task('done', 1)]);

    api.listTasks.calls.reset();
    tick(9000);
    expect(api.listTasks).not.toHaveBeenCalled();

    void store.setStatusFilter(0);
    tick();
    expect(api.listTasks).toHaveBeenCalledOnceWith(0);
    discardPeriodicTasks();
  }));

  it('ignores a slow response for a filter that is no longer selected', fakeAsync(() => {
    let releaseRunning: (tasks: TaskState[]) => void = () => undefined;
    api.listTasks.and.callFake((status) =>
      status === 0
        ? new Promise<TaskState[]>((resolve) => (releaseRunning = resolve))
        : Promise.resolve([task('failed', 6)]),
    );
    const store = TestBed.inject(TasksStore);

    void store.setStatusFilter(6);
    tick();
    releaseRunning([task('late-running')]);
    tick();

    expect(store.tasks()).toEqual([task('failed', 6)]);
    discardPeriodicTasks();
  }));

  it('starts a task and consumes authenticated live snapshots', fakeAsync(() => {
    api.startTask.and.resolveTo({ taskId: 'task-1' });
    api.getTask.and.resolveTo(task('task-1'));
    const store = TestBed.inject(TasksStore);
    tick();

    void store.start('Inspect this host');
    tick();

    expect(api.startTask).toHaveBeenCalledOnceWith('Inspect this host');
    expect(store.selectedTaskId()).toBe('task-1');
    expect(api.getTask).toHaveBeenCalledWith('task-1');
    expect(store.selectedTask()).toEqual(task('task-1'));
    finish(store);
  }));

  it('does not watch a selected terminal task and exposes API errors', fakeAsync(() => {
    api.getTask.and.resolveTo(task('done', 1));
    const store = TestBed.inject(TasksStore);
    tick();

    void store.selectTask('done');
    tick();
    expect(store.selectedTask()).toEqual(task('done', 1));
    expect(api.getTask).toHaveBeenCalledTimes(1);

    api.getTask.and.rejectWith(new Error('Task unavailable'));
    void store.selectTask('missing');
    tick();
    expect(store.error()).toBe('Task unavailable');
    discardPeriodicTasks();
  }));

  it('stops following the previous task when another one is selected', fakeAsync(() => {
    const store = TestBed.inject(TasksStore);
    api.getTask.and.callFake(async (id) => task(id));
    void store.selectTask('first');
    tick();
    api.getTask.and.callFake(async (id) => task(id, id === 'second' ? 1 : 0));
    void store.selectTask('second');
    tick();
    api.getTask.calls.reset();

    tick(TASK_POLL_INTERVAL_MS * 3);

    expect(api.getTask).not.toHaveBeenCalled();
    expect(store.selectedTask()?.id).toBe('second');
    discardPeriodicTasks();
  }));

  it('watches a selected running task from its own execution attempt', fakeAsync(() => {
    const store = TestBed.inject(TasksStore);
    const current = task('t', 0, { executionAttempt: 3 });
    api.getTask.and.returnValues(
      Promise.resolve(current),
      Promise.resolve(task('t', 6, { executionAttempt: 2, executing: false })),
      Promise.resolve(task('t', 0, { executionAttempt: 3, steps: [step] })),
    );

    void store.selectTask('t');
    tick();
    tick(TASK_POLL_INTERVAL_MS);

    expect(store.selectedTask()?.executionAttempt).toBe(3);
    expect(store.selectedTask()?.status).toBe(0);
    finish(store);
  }));

  describe('resume', () => {
    async function selectFailed(store: InstanceType<typeof TasksStore>): Promise<void> {
      api.getTask.and.resolveTo(failedTask());
      await store.selectTask('task-2');
    }

    it('consumes the typed 202 and shows the task Running under the new attempt before any further GET answers', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      void selectFailed(store);
      tick();
      expect(store.selectedTask()?.resumable).toBeTrue();

      const firstWatcherGet = deferred<TaskState>();
      api.getTask.and.returnValue(firstWatcherGet.promise);
      void store.resume('task-2');
      tick();

      const shown = store.selectedTask()!;
      expect(api.resumeTask).toHaveBeenCalledOnceWith('task-2');
      expect(shown.status).toBe(0);
      expect(shown.executionAttempt).toBe(2);
      expect(shown.executing).toBeTrue();
      expect(shown.resumable).toBeFalse();
      expect(shown.resumeBlockedReason).toEqual({ code: 'task_running', message: 'running' });
      expect(store.selectedTaskId()).toBe('task-2');
      expect(store.resuming()).toBeFalse();
      expect(api.getTask).toHaveBeenCalledTimes(2);
      finish(store);
    }));

    it('keeps the steps, plans and accounting already on screen, and clears the old terminal reason', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      void selectFailed(store);
      tick();
      expect(store.selectedTask()?.terminalReason).toEqual({ kind: 8, failureKind: 1 });

      api.getTask.and.returnValue(deferred<TaskState>().promise);
      void store.resume('task-2');
      tick();

      const shown = store.selectedTask()!;
      expect(shown.steps).toEqual([step]);
      expect(shown.plans.length).toBe(1);
      expect(shown.accounting.tokensUsed).toBe(1234);
      expect(shown.goal).toBe('Goal task-2');
      expect(shown.terminalReason).toBeNull();
      finish(store);
    }));

    it('hands the accepted execution attempt to the watch: the stale pre-resume snapshot neither replaces the task nor ends the watch', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      void selectFailed(store);
      tick();
      const stale = failedTask();
      const live = task('task-2', 0, { executionAttempt: 2, steps: [step, { ...step, index: 1 }] });
      const done = task('task-2', 1, { executionAttempt: 2, steps: [step, { ...step, index: 1 }, { ...step, index: 2 }] });
      api.getTask.and.returnValues(Promise.resolve(stale), Promise.resolve(stale), Promise.resolve(live), Promise.resolve(done));

      void store.resume('task-2');
      tick();
      expect(store.selectedTask()?.status).toBe(0);
      expect(store.selectedTask()?.executionAttempt).toBe(2);

      tick(TASK_POLL_INTERVAL_MS);
      expect(store.selectedTask()?.status).toBe(0);
      expect(store.selectedTask()?.steps.length).toBe(1);

      tick(TASK_POLL_INTERVAL_MS);
      expect(store.selectedTask()?.steps.length).toBe(2);

      tick(TASK_POLL_INTERVAL_MS);
      expect(store.selectedTask()?.status).toBe(1);
      expect(store.selectedTask()?.steps.length).toBe(3);

      const calls = api.getTask.calls.count();
      tick(TASK_POLL_INTERVAL_MS * 5);
      expect(api.getTask.calls.count()).toBe(calls);
      discardPeriodicTasks();
    }));

    it('sends one request however often Resume is clicked while it is in flight', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      void selectFailed(store);
      tick();
      const pending = deferred<TaskResumeAcceptedResponse>();
      api.resumeTask.and.returnValue(pending.promise);
      api.getTask.and.returnValue(deferred<TaskState>().promise);

      void store.resume('task-2');
      void store.resume('task-2');
      void store.resume('task-2');
      tick();
      expect(store.resuming()).toBeTrue();
      expect(api.resumeTask).toHaveBeenCalledTimes(1);

      pending.resolve(accepted);
      tick();
      expect(store.resuming()).toBeFalse();
      expect(api.resumeTask).toHaveBeenCalledTimes(1);
      finish(store);
    }));

    it('allows another resume after the request failed', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      void selectFailed(store);
      tick();
      api.resumeTask.and.rejectWith(refusal(409, 'task_completed'));
      void store.resume('task-2');
      tick();
      expect(store.resuming()).toBeFalse();

      void store.resume('task-2');
      tick();
      expect(api.resumeTask).toHaveBeenCalledTimes(2);
      finish(store);
    }));

    it('does not invent a task when the resumed one is not on screen: it keeps the id and lets the watch fetch it', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      tick();
      const live = task('task-2', 0, { executionAttempt: 2 });
      api.getTask.and.resolveTo(live);

      void store.resume('task-2');
      tick();

      expect(store.selectedTaskId()).toBe('task-2');
      expect(store.selectedTask()).toEqual(live);
      finish(store);
    }));

    it('maps a stable refusal code to the operator text and refreshes the task the server now has', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      void selectFailed(store);
      tick();
      const running = task('task-2', 0, { executionAttempt: 2, steps: [step] });
      api.resumeTask.and.rejectWith(refusal(409, 'task_running'));
      api.getTask.and.resolveTo(running);

      void store.resume('task-2');
      tick();

      expect(store.error()).toContain('already running');
      expect(store.error()).not.toContain('Cannot reach');
      expect(store.selectedTask()).toEqual(running);
      expect(store.resuming()).toBeFalse();
      finish(store);
    }));

    it('says a duplicate resume lost the race rather than that the API is unreachable', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      void selectFailed(store);
      tick();
      api.resumeTask.and.rejectWith(refusal(409, 'resume_conflict'));
      api.getTask.and.resolveTo(task('task-2', 0, { executionAttempt: 2 }));

      void store.resume('task-2');
      tick();

      expect(store.error()).toContain('changed while you were resuming');
      finish(store);
    }));

    it('says bOps is busy, for how long, when the executor did not admit the resume', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      void selectFailed(store);
      tick();
      api.resumeTask.and.rejectWith(refusal(503, 'executor_unavailable', { 'Retry-After': '5' }));

      void store.resume('task-2');
      tick();

      expect(store.error()).toContain('bOps is busy');
      expect(store.error()).toContain('5 s');
      finish(store);
    }));

    it('does not reload the task after a failure that is not a refusal, and says the API is unreachable for a bare 503', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      void selectFailed(store);
      tick();
      api.getTask.calls.reset();
      api.resumeTask.and.rejectWith(new HttpErrorResponse({ status: 503 }));

      void store.resume('task-2');
      tick();

      expect(store.error()).toContain('Cannot reach the bOps API');
      expect(api.getTask).not.toHaveBeenCalled();
      finish(store);
    }));

    it('keeps a resume error on screen across the three-second list refresh', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      void selectFailed(store);
      tick();
      api.resumeTask.and.rejectWith(refusal(409, 'task_completed'));
      void store.resume('task-2');
      tick();
      expect(store.error()).toContain('completed');

      tick(3000);
      tick(3000);

      expect(store.error()).toContain('completed');
      finish(store);
    }));

    it('clears the error of a failed list refresh once the list comes back, and only that one', fakeAsync(() => {
      api.listTasks.and.rejectWith(new HttpErrorResponse({ status: 0 }));
      const store = TestBed.inject(TasksStore);
      tick();
      expect(store.error()).toContain('Cannot reach');

      api.listTasks.and.resolveTo([]);
      tick(3000);
      expect(store.error()).toBeNull();
      discardPeriodicTasks();
    }));
  });

  describe('cancel', () => {
    it('issues the cancel request for the task', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      api.getTask.and.resolveTo(task('t', 0));
      void store.selectTask('t');
      tick();

      void store.cancel('t');
      tick();

      expect(api.cancelTask).toHaveBeenCalledOnceWith('t');
      finish(store);
    }));

    it('sends one request however often Cancel is clicked', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      api.getTask.and.resolveTo(task('t', 0));
      void store.selectTask('t');
      tick();
      const pending = deferred<void>();
      api.cancelTask.and.returnValue(pending.promise);

      void store.cancel('t');
      void store.cancel('t');
      tick();
      expect(store.cancelling()).toBeTrue();
      pending.resolve();
      tick();
      void store.cancel('t');
      tick();

      expect(api.cancelTask).toHaveBeenCalledTimes(1);
      finish(store);
    }));

    it('does not make the task Cancelled itself: it follows the server until the task is no longer executing', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      api.getTask.and.resolveTo(task('t', 0, { steps: [step] }));
      void store.selectTask('t');
      tick();

      api.getTask.and.returnValues(
        Promise.resolve(task('t', 0, { steps: [step] })),
        Promise.resolve(task('t', 0, { steps: [step] })),
        Promise.resolve(task('t', 7, { steps: [step], executing: false, terminalReason: { kind: 11 } })),
      );
      void store.cancel('t');
      tick();
      expect(store.selectedTask()?.status).toBe(0);
      expect(store.selectedTask()?.executing).toBeTrue();
      expect(store.cancelling()).toBeTrue();

      tick(TASK_POLL_INTERVAL_MS);
      expect(store.selectedTask()?.status).toBe(0);
      expect(store.cancelling()).toBeTrue();

      tick(TASK_POLL_INTERVAL_MS);
      expect(store.selectedTask()?.status).toBe(7);
      expect(store.selectedTask()?.terminalReason).toEqual({ kind: 11 });
      expect(store.cancelling()).toBeFalse();
      discardPeriodicTasks();
    }));

    it('says there is nothing to cancel, and refreshes the task, when the host is not running it (404)', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      api.getTask.and.resolveTo(task('t', 0));
      void store.selectTask('t');
      tick();
      const interrupted = task('t', 0, { executing: false });
      api.cancelTask.and.rejectWith(new HttpErrorResponse({ status: 404, error: { message: 'Task is not running in this host.' } }));
      api.getTask.and.resolveTo(interrupted);

      void store.cancel('t');
      tick();

      expect(store.error()).toContain('nothing to cancel');
      expect(store.cancelling()).toBeFalse();
      expect(store.selectedTask()).toEqual(interrupted);
      finish(store);
    }));

    it('reports another failure through the ordinary error text and allows a retry', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      api.getTask.and.resolveTo(task('t', 0));
      void store.selectTask('t');
      tick();
      api.cancelTask.and.rejectWith(new HttpErrorResponse({ status: 0 }));

      void store.cancel('t');
      tick();

      expect(store.error()).toContain('Cannot reach');
      expect(store.cancelling()).toBeFalse();
      finish(store);
    }));
  });

  describe('live connection', () => {
    it('shows reconnecting during an outage of the API and is connected again afterwards, without a manual action', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      api.getTask.and.resolveTo(task('t', 0));
      void store.selectTask('t');
      tick();

      api.getTask.and.rejectWith(new HttpErrorResponse({ status: 503 }));
      tick(TASK_POLL_INTERVAL_MS);
      expect(store.connection()).toBe('reconnecting');
      expect(store.error()).toBeNull();

      for (let elapsed = 0; elapsed < 65_000; elapsed += 1000) tick(1000);
      expect(store.connection()).toBe('reconnecting');

      api.getTask.and.resolveTo(task('t', 0, { steps: [step] }));
      tick(backoffMs(10));
      expect(store.connection()).toBe('connected');
      expect(store.selectedTask()?.steps.length).toBe(1);
      finish(store);
    }));

    it('turns a 401 into a session-expired state, goes back to sign-in, and keeps the task to watch after signing in again', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      api.getTask.and.resolveTo(task('t', 0, { executionAttempt: 2 }));
      void store.selectTask('t');
      tick();

      api.getTask.and.rejectWith(new HttpErrorResponse({ status: 401 }));
      tick(TASK_POLL_INTERVAL_MS);

      expect(store.connection()).toBe('sessionExpired');
      expect(auth.expireSession).toHaveBeenCalledTimes(1);
      expect(store.pendingWatch()).toEqual({ taskId: 't', expectedAttempt: 2 });
      expect(store.selectedTaskId()).toBe('t');
      expect(store.selectedTask()?.id).toBe('t');

      api.getTask.calls.reset();
      tick(TASK_POLL_INTERVAL_MS * 10);
      expect(api.getTask).not.toHaveBeenCalled();

      api.getTask.and.resolveTo(task('t', 0, { executionAttempt: 2, steps: [step] }));
      authenticated.set(true);
      TestBed.tick();
      tick();

      expect(api.getTask).toHaveBeenCalledTimes(1);
      expect(store.connection()).toBe('connected');
      expect(store.pendingWatch()).toBeNull();
      expect(store.selectedTask()?.steps.length).toBe(1);
      finish(store);
    }));

    it('does not restore a watch for a task that is no longer selected', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      api.getTask.and.resolveTo(task('t', 0));
      void store.selectTask('t');
      tick();
      api.getTask.and.rejectWith(new HttpErrorResponse({ status: 401 }));
      tick(TASK_POLL_INTERVAL_MS);
      store.clearSelection();

      api.getTask.calls.reset();
      authenticated.set(true);
      TestBed.tick();
      tick(TASK_POLL_INTERVAL_MS * 3);

      expect(api.getTask).not.toHaveBeenCalled();
      discardPeriodicTasks();
    }));

    it('ends the watch with a clear message when the task is gone (404)', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      api.getTask.and.resolveTo(task('t', 0));
      void store.selectTask('t');
      tick();

      api.getTask.and.rejectWith(new HttpErrorResponse({ status: 404 }));
      tick(TASK_POLL_INTERVAL_MS);
      api.getTask.calls.reset();
      tick(TASK_POLL_INTERVAL_MS * 10);

      expect(store.error()).toContain('no longer exists');
      expect(api.getTask).not.toHaveBeenCalled();
      discardPeriodicTasks();
    }));

    it('reports a failure of the live connection that waiting cannot fix', fakeAsync(() => {
      const store = TestBed.inject(TasksStore);
      api.getTask.and.resolveTo(task('t', 0));
      void store.selectTask('t');
      tick();

      api.getTask.and.rejectWith(new Error('stream failed'));
      tick(TASK_POLL_INTERVAL_MS);

      expect(store.error()).toBe('Lost the live connection to this task.');
      discardPeriodicTasks();
    }));
  });
});
