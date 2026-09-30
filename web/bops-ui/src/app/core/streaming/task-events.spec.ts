// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { fakeAsync, tick } from '@angular/core/testing';
import { TaskState } from '../api/models';
import { TASK_MAX_BACKOFF_MS, TASK_POLL_INTERVAL_MS, WatchConnection, backoffMs, watchTaskEvents } from './task-events';

function task(status: TaskState['status'] = 0, executionAttempt = 1): TaskState {
  return {
    id: 't',
    node: 'local',
    goal: 'g',
    status,
    steps: [],
    plans: [],
    createdAtUtc: '2026-09-15T12:00:00Z',
    executionAttempt,
    accounting: { tokensUsed: 0, lifetimeSteps: 0, lifetimeReplans: 0 },
    origin: 1,
    executing: status === 0,
    resumable: false,
    resumeBlockedReason: null,
  };
}

function http(status: number, headers?: Record<string, string>): HttpErrorResponse {
  return new HttpErrorResponse({ status, headers: new HttpHeaders(headers) });
}

function watch(getTask: () => Promise<TaskState>, expectedAttempt?: number) {
  const snapshots: TaskState[] = [];
  const connections: WatchConnection[] = [];
  const onError = jasmine.createSpy('onError');
  const stop = watchTaskEvents(
    't',
    getTask,
    { onSnapshot: (s) => snapshots.push(s), onConnection: (c) => connections.push(c), onError },
    expectedAttempt,
  );
  return { stop, snapshots, connections, onError };
}

describe('watchTaskEvents', () => {
  describe('following a task', () => {
    it('keeps polling a running task of the current attempt', fakeAsync(() => {
      const getTask = jasmine.createSpy('getTask').and.resolveTo(task(0));
      const { stop, snapshots } = watch(getTask);

      tick();
      tick(TASK_POLL_INTERVAL_MS);
      tick(TASK_POLL_INTERVAL_MS);

      expect(getTask).toHaveBeenCalledTimes(3);
      expect(snapshots.length).toBe(3);
      stop();
    }));

    it('stops after a terminal snapshot of the current attempt', fakeAsync(() => {
      const getTask = jasmine.createSpy('getTask').and.returnValues(Promise.resolve(task(0)), Promise.resolve(task(1)));
      const { snapshots } = watch(getTask);

      tick();
      tick(TASK_POLL_INTERVAL_MS);
      tick(TASK_POLL_INTERVAL_MS * 5);

      expect(getTask).toHaveBeenCalledTimes(2);
      expect(snapshots.map((s) => s.status)).toEqual([0, 1]);
    }));

    it('ignores a snapshot of an earlier attempt: it is not shown, does not end the watch, and polling goes on', fakeAsync(() => {
      const stale = task(6, 1);
      const getTask = jasmine
        .createSpy('getTask')
        .and.returnValues(Promise.resolve(stale), Promise.resolve(stale), Promise.resolve(task(0, 2)), Promise.resolve(task(1, 2)));
      const { snapshots } = watch(getTask, 2);

      tick();
      expect(snapshots).toEqual([]);
      tick(TASK_POLL_INTERVAL_MS);
      expect(snapshots).toEqual([]);
      tick(TASK_POLL_INTERVAL_MS);
      tick(TASK_POLL_INTERVAL_MS);
      tick(TASK_POLL_INTERVAL_MS * 5);

      expect(getTask).toHaveBeenCalledTimes(4);
      expect(snapshots.map((s) => [s.status, s.executionAttempt])).toEqual([
        [0, 2],
        [1, 2],
      ]);
    }));

    it('accepts a snapshot of exactly the expected attempt', fakeAsync(() => {
      const { snapshots, stop } = watch(() => Promise.resolve(task(0, 3)), 3);
      tick();
      expect(snapshots.length).toBe(1);
      stop();
    }));

    it('accepts a snapshot of a later attempt than expected', fakeAsync(() => {
      const { snapshots } = watch(() => Promise.resolve(task(1, 5)), 3);
      tick();
      expect(snapshots.length).toBe(1);
      expect(snapshots[0].executionAttempt).toBe(5);
    }));

    it('treats a snapshot without an attempt as attempt 1', fakeAsync(() => {
      const legacy = { ...task(0), executionAttempt: undefined } as unknown as TaskState;
      const { snapshots, stop } = watch(() => Promise.resolve(legacy), 1);
      tick();
      expect(snapshots.length).toBe(1);
      stop();
    }));

    it('does not poll again after being stopped', fakeAsync(() => {
      const getTask = jasmine.createSpy('getTask').and.resolveTo(task(0));
      const { stop } = watch(getTask);

      tick();
      stop();
      tick(TASK_POLL_INTERVAL_MS * 5);

      expect(getTask).toHaveBeenCalledTimes(1);
    }));

    it('drops the answer of a poll that was in flight when it was stopped', fakeAsync(() => {
      let release: (t: TaskState) => void = () => undefined;
      const getTask = jasmine.createSpy('getTask').and.returnValue(new Promise<TaskState>((resolve) => (release = resolve)));
      const { stop, snapshots } = watch(getTask);

      stop();
      release(task(0));
      tick(TASK_POLL_INTERVAL_MS * 3);

      expect(snapshots).toEqual([]);
      expect(getTask).toHaveBeenCalledTimes(1);
    }));
  });

  describe('transient failures', () => {
    for (const status of [0, 429, 502, 503, 504]) {
      it(`recovers from a burst of ${status}: reconnecting, then connected again`, fakeAsync(() => {
        let calls = 0;
        const getTask = jasmine
          .createSpy('getTask')
          .and.callFake(() => (++calls <= 3 ? Promise.reject(http(status)) : Promise.resolve(task(0))));
        const { stop, snapshots, connections, onError } = watch(getTask);

        tick();
        tick(backoffMs(1));
        tick(backoffMs(2));
        tick(backoffMs(3));

        expect(getTask).toHaveBeenCalledTimes(4);
        expect(snapshots.length).toBe(1);
        expect(connections).toEqual(['reconnecting', 'connected']);
        expect(onError).not.toHaveBeenCalled();
        stop();
      }));
    }

    it('survives a 503 outage of more than a simulated minute and recovers without a manual action', fakeAsync(() => {
      let outage = true;
      const getTask = jasmine.createSpy('getTask').and.callFake(() => (outage ? Promise.reject(http(503)) : Promise.resolve(task(0))));
      const { stop, snapshots, connections, onError } = watch(getTask);

      tick();
      for (let elapsed = 0; elapsed < 75_000; elapsed += 1000) tick(1000);
      expect(connections).toEqual(['reconnecting']);
      expect(snapshots).toEqual([]);

      outage = false;
      tick(TASK_MAX_BACKOFF_MS);

      expect(connections).toEqual(['reconnecting', 'connected']);
      expect(snapshots.length).toBeGreaterThan(0);
      expect(onError).not.toHaveBeenCalled();
      stop();
    }));

    it('does not give up after any number of consecutive transient failures', fakeAsync(() => {
      const getTask = jasmine.createSpy('getTask').and.rejectWith(http(503));
      const { stop, onError } = watch(getTask);

      tick();
      for (let i = 0; i < 100; i++) tick(TASK_MAX_BACKOFF_MS);

      expect(getTask.calls.count()).toBeGreaterThan(50);
      expect(onError).not.toHaveBeenCalled();
      stop();
    }));

    it('backs off exponentially up to a finite ceiling', () => {
      expect(backoffMs(1)).toBe(TASK_POLL_INTERVAL_MS * 2);
      expect(backoffMs(2)).toBe(TASK_POLL_INTERVAL_MS * 4);
      expect(backoffMs(3)).toBeGreaterThan(backoffMs(2));
      expect(backoffMs(50)).toBe(TASK_MAX_BACKOFF_MS);
      expect(backoffMs(1_000_000)).toBe(TASK_MAX_BACKOFF_MS);
    });

    it('never polls faster than its backoff while failing', fakeAsync(() => {
      const getTask = jasmine.createSpy('getTask').and.rejectWith(http(503));
      const { stop } = watch(getTask);

      tick();
      tick(backoffMs(1) - 1);
      expect(getTask).toHaveBeenCalledTimes(1);
      tick(1);
      expect(getTask).toHaveBeenCalledTimes(2);
      stop();
    }));

    it('waits at least Retry-After before the next attempt', fakeAsync(() => {
      let calls = 0;
      const getTask = jasmine
        .createSpy('getTask')
        .and.callFake(() => (++calls < 2 ? Promise.reject(http(429, { 'Retry-After': '20' })) : Promise.resolve(task(1))));
      watch(getTask);

      tick();
      tick(19_000);
      expect(getTask).toHaveBeenCalledTimes(1);
      tick(1_000);
      expect(getTask).toHaveBeenCalledTimes(2);
    }));

    it('bounds an absurd Retry-After instead of waiting for it', fakeAsync(() => {
      let calls = 0;
      const getTask = jasmine
        .createSpy('getTask')
        .and.callFake(() => (++calls < 2 ? Promise.reject(http(503, { 'Retry-After': '86400' })) : Promise.resolve(task(1))));
      watch(getTask);

      tick();
      tick(120_000);

      expect(getTask).toHaveBeenCalledTimes(2);
    }));

    it('ignores a Retry-After it cannot read', fakeAsync(() => {
      let calls = 0;
      const getTask = jasmine
        .createSpy('getTask')
        .and.callFake(() => (++calls < 2 ? Promise.reject(http(503, { 'Retry-After': 'soon' })) : Promise.resolve(task(1))));
      watch(getTask);

      tick();
      tick(backoffMs(1));

      expect(getTask).toHaveBeenCalledTimes(2);
    }));

    it('returns to the regular interval once a poll succeeds, with the failure count reset', fakeAsync(() => {
      const outcomes = ['fail', 'fail', 'ok', 'fail', 'ok'];
      let next = 0;
      const getTask = jasmine
        .createSpy('getTask')
        .and.callFake(() => (outcomes[next++] === 'fail' ? Promise.reject(http(503)) : Promise.resolve(task(0))));
      const { stop } = watch(getTask);

      tick();
      tick(backoffMs(1));
      tick(backoffMs(2));
      expect(getTask).toHaveBeenCalledTimes(3);
      tick(TASK_POLL_INTERVAL_MS);
      expect(getTask).toHaveBeenCalledTimes(4);
      tick(backoffMs(1));
      expect(getTask).toHaveBeenCalledTimes(5);
      stop();
    }));

    it('counts a stale snapshot as the API answering: no reconnecting, and the failure count is reset', fakeAsync(() => {
      const { stop, connections } = watch(() => Promise.resolve(task(6, 1)), 2);
      tick();
      tick(TASK_POLL_INTERVAL_MS);
      expect(connections).toEqual([]);
      stop();
    }));
  });

  describe('permanent failures', () => {
    it('reports a 401 as sessionExpired, not as reconnecting, and stops polling', fakeAsync(() => {
      const getTask = jasmine.createSpy('getTask').and.rejectWith(http(401));
      const { connections, onError } = watch(getTask);

      tick();
      tick(TASK_MAX_BACKOFF_MS * 2);

      expect(connections).toEqual(['sessionExpired']);
      expect(getTask).toHaveBeenCalledTimes(1);
      expect(onError).not.toHaveBeenCalled();
    }));

    it('ends on a 404 and does not retry it', fakeAsync(() => {
      const getTask = jasmine.createSpy('getTask').and.rejectWith(http(404));
      const { connections, onError } = watch(getTask);

      tick();
      tick(TASK_MAX_BACKOFF_MS * 2);

      expect(getTask).toHaveBeenCalledTimes(1);
      expect(onError).toHaveBeenCalledOnceWith('notFound', jasmine.any(HttpErrorResponse));
      expect(connections).toEqual([]);
    }));

    it('ends on an error it cannot classify as transient', fakeAsync(() => {
      const getTask = jasmine.createSpy('getTask').and.rejectWith(new Error('boom'));
      const { onError } = watch(getTask);

      tick();
      tick(TASK_MAX_BACKOFF_MS * 2);

      expect(getTask).toHaveBeenCalledTimes(1);
      expect(onError).toHaveBeenCalledOnceWith('failed', jasmine.any(Error));
    }));

    it('ends on a 500 or 403: waiting does not fix those', fakeAsync(() => {
      for (const status of [403, 500]) {
        const getTask = jasmine.createSpy('getTask').and.rejectWith(http(status));
        const { onError } = watch(getTask);
        tick();
        expect(onError).toHaveBeenCalledOnceWith('failed', jasmine.any(HttpErrorResponse));
      }
    }));
  });

  describe('waking up', () => {
    const online = () => window.dispatchEvent(new Event('online'));
    const visibility = (state: DocumentVisibilityState) => {
      spyOnProperty(document, 'visibilityState', 'get').and.returnValue(state);
      document.dispatchEvent(new Event('visibilitychange'));
    };

    it('polls at once when the browser comes back online, instead of waiting out a long backoff', fakeAsync(() => {
      let outage = true;
      const getTask = jasmine.createSpy('getTask').and.callFake(() => (outage ? Promise.reject(http(0)) : Promise.resolve(task(0))));
      const { stop, connections } = watch(getTask);
      tick();
      tick(backoffMs(1));
      tick(backoffMs(2));
      expect(getTask).toHaveBeenCalledTimes(3);

      outage = false;
      online();
      tick();

      expect(getTask).toHaveBeenCalledTimes(4);
      expect(connections).toEqual(['reconnecting', 'connected']);
      stop();
    }));

    it('polls at once when the tab becomes visible', fakeAsync(() => {
      let outage = true;
      const getTask = jasmine.createSpy('getTask').and.callFake(() => (outage ? Promise.reject(http(503)) : Promise.resolve(task(0))));
      const { stop, connections } = watch(getTask);
      tick();
      tick(backoffMs(1));
      expect(getTask).toHaveBeenCalledTimes(2);

      outage = false;
      visibility('visible');
      tick();

      expect(getTask).toHaveBeenCalledTimes(3);
      expect(connections).toEqual(['reconnecting', 'connected']);
      stop();
    }));

    it('does nothing when the tab is hidden', fakeAsync(() => {
      const getTask = jasmine.createSpy('getTask').and.rejectWith(http(503));
      const { stop } = watch(getTask);
      tick();

      visibility('hidden');
      tick();

      expect(getTask).toHaveBeenCalledTimes(1);
      stop();
    }));

    it('does not start a second poll while one is in flight, however many events arrive', fakeAsync(() => {
      let release: (t: TaskState) => void = () => undefined;
      const getTask = jasmine.createSpy('getTask').and.callFake(() => new Promise<TaskState>((resolve) => (release = resolve)));
      const { stop } = watch(getTask);

      online();
      online();
      visibility('visible');
      tick();
      expect(getTask).toHaveBeenCalledTimes(1);

      release(task(0));
      tick();
      stop();
    }));

    it('supersedes the pending retry: one poll now and one timer afterwards, not both', fakeAsync(() => {
      const getTask = jasmine.createSpy('getTask').and.rejectWith(http(503));
      const { stop } = watch(getTask);
      tick();
      expect(getTask).toHaveBeenCalledTimes(1);

      online();
      tick();
      expect(getTask).toHaveBeenCalledTimes(2);

      tick(backoffMs(1) + 1);
      expect(getTask).toHaveBeenCalledTimes(2);
      tick(backoffMs(2) - backoffMs(1));
      expect(getTask).toHaveBeenCalledTimes(3);
      stop();
    }));

    it('removes its listeners and timers when stopped, so a later event polls nothing', fakeAsync(() => {
      const addWindow = spyOn(window, 'addEventListener').and.callThrough();
      const removeWindow = spyOn(window, 'removeEventListener').and.callThrough();
      const addDocument = spyOn(document, 'addEventListener').and.callThrough();
      const removeDocument = spyOn(document, 'removeEventListener').and.callThrough();
      const getTask = jasmine.createSpy('getTask').and.rejectWith(http(503));
      const { stop } = watch(getTask);
      tick();

      stop();
      online();
      visibility('visible');
      tick(TASK_MAX_BACKOFF_MS * 2);

      expect(getTask).toHaveBeenCalledTimes(1);
      const added = addWindow.calls.allArgs().find(([name]) => name === 'online')![1];
      expect(removeWindow).toHaveBeenCalledWith('online', added as EventListener);
      const addedVisibility = addDocument.calls.allArgs().find(([name]) => name === 'visibilitychange')![1];
      expect(removeDocument).toHaveBeenCalledWith('visibilitychange', addedVisibility as EventListener);
    }));

    it('keeps exactly one listener of each kind across a long watch', fakeAsync(() => {
      const addWindow = spyOn(window, 'addEventListener').and.callThrough();
      const getTask = jasmine.createSpy('getTask').and.rejectWith(http(503));
      const { stop } = watch(getTask);

      for (let i = 0; i < 20; i++) tick(TASK_MAX_BACKOFF_MS);

      expect(addWindow.calls.allArgs().filter(([name]) => name === 'online').length).toBe(1);
      stop();
    }));

    it('removes its listeners when the watch ends by itself', fakeAsync(() => {
      const getTask = jasmine.createSpy('getTask').and.resolveTo(task(1));
      watch(getTask);
      tick();

      online();
      tick(TASK_POLL_INTERVAL_MS);

      expect(getTask).toHaveBeenCalledTimes(1);
    }));
  });
});
