// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { fakeAsync, tick } from '@angular/core/testing';
import { TaskState } from '../api/models';
import { TASK_POLL_INTERVAL_MS, watchTaskEvents } from './task-events';

function task(status: TaskState['status'] = 0): TaskState {
  return { id: 't', node: 'local', goal: 'g', status, steps: [], plans: [], createdAtUtc: '2026-09-15T12:00:00Z' };
}

function http(status: number, headers?: Record<string, string>): HttpErrorResponse {
  return new HttpErrorResponse({ status, headers: new HttpHeaders(headers) });
}

describe('watchTaskEvents', () => {
  it('polls a running task every interval and stops once it is terminal', fakeAsync(() => {
    const getTask = jasmine.createSpy('getTask').and.returnValues(Promise.resolve(task(0)), Promise.resolve(task(1)));
    const snapshots: TaskState[] = [];

    watchTaskEvents('t', getTask, (s) => snapshots.push(s));
    tick();
    tick(TASK_POLL_INTERVAL_MS);
    tick(TASK_POLL_INTERVAL_MS * 3);

    expect(getTask).toHaveBeenCalledTimes(2);
    expect(snapshots.length).toBe(2);
  }));

  it('retries a 429 with backoff instead of stopping, without reporting an error', fakeAsync(() => {
    let calls = 0;
    const getTask = jasmine
      .createSpy('getTask')
      .and.callFake(() => (++calls < 3 ? Promise.reject(http(429)) : Promise.resolve(task(0))));
    const onError = jasmine.createSpy('onError');
    const onSnapshot = jasmine.createSpy('onSnapshot');

    const stop = watchTaskEvents('t', getTask, onSnapshot, onError);
    tick();
    tick(TASK_POLL_INTERVAL_MS * 2); // first backoff
    tick(TASK_POLL_INTERVAL_MS * 4); // second backoff

    expect(getTask).toHaveBeenCalledTimes(3);
    expect(onSnapshot).toHaveBeenCalledTimes(1);
    expect(onError).not.toHaveBeenCalled();
    stop();
  }));

  it('waits at least Retry-After before the next attempt', fakeAsync(() => {
    let calls = 0;
    const getTask = jasmine
      .createSpy('getTask')
      .and.callFake(() => (++calls < 2 ? Promise.reject(http(429, { 'Retry-After': '20' })) : Promise.resolve(task(1))));

    watchTaskEvents('t', getTask, () => undefined);
    tick();
    tick(19_000);
    expect(getTask).toHaveBeenCalledTimes(1);
    tick(1_000);
    expect(getTask).toHaveBeenCalledTimes(2);
  }));

  it('reports a permanent failure at once', fakeAsync(() => {
    const getTask = jasmine.createSpy('getTask').and.rejectWith(http(404));
    const onError = jasmine.createSpy('onError');

    watchTaskEvents('t', getTask, () => undefined, onError);
    tick();
    tick(60_000);

    expect(getTask).toHaveBeenCalledTimes(1);
    expect(onError).toHaveBeenCalledTimes(1);
  }));

  it('gives up after too many consecutive transient failures', fakeAsync(() => {
    const getTask = jasmine.createSpy('getTask').and.rejectWith(http(429));
    const onError = jasmine.createSpy('onError');

    watchTaskEvents('t', getTask, () => undefined, onError);
    for (let i = 0; i < 20; i++) tick(30_000);

    expect(onError).toHaveBeenCalledTimes(1);
    expect(getTask).toHaveBeenCalledTimes(8);
  }));

  it('does not poll again after being stopped', fakeAsync(() => {
    const getTask = jasmine.createSpy('getTask').and.resolveTo(task(0));

    const stop = watchTaskEvents('t', getTask, () => undefined);
    tick();
    stop();
    tick(TASK_POLL_INTERVAL_MS * 5);

    expect(getTask).toHaveBeenCalledTimes(1);
  }));
});
