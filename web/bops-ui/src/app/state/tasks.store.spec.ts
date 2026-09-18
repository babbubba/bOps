// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { discardPeriodicTasks, fakeAsync, TestBed, tick } from '@angular/core/testing';
import { signal } from '@angular/core';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { AuthService } from '../core/auth/auth.service';
import { TaskState } from '../core/api/models';
import { TasksStore } from './tasks.store';

function task(id: string, status: TaskState['status'] = 0): TaskState {
  return {
    id,
    node: 'local',
    goal: `Goal ${id}`,
    status,
    steps: [],
    plans: [],
    createdAtUtc: '2026-09-15T12:00:00Z',
  };
}

describe('TasksStore', () => {
  let api: jasmine.SpyObj<BOpsApiClient>;

  beforeEach(() => {
    api = jasmine.createSpyObj<BOpsApiClient>('BOpsApiClient', [
      'listTasks',
      'startTask',
      'resumeTask',
      'getTask',
    ]);
    api.listTasks.and.resolveTo([]);
    api.startTask.and.resolveTo({ taskId: 'started' });
    api.resumeTask.and.resolveTo({ taskId: 'resumed' });
    api.getTask.and.resolveTo(task('selected'));

    TestBed.configureTestingModule({
      providers: [
        TasksStore,
        { provide: BOpsApiClient, useValue: api },
        { provide: AuthService, useValue: { authenticated: signal(true) } },
      ],
    });
  });

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
    discardPeriodicTasks();
  }));

  it('replaces the active watch on resume and reports a polling error', fakeAsync(() => {
    const store = TestBed.inject(TasksStore);
    tick();

    void store.start('First task');
    tick();
    api.getTask.and.rejectWith(new Error('stream failed'));
    void store.resume('task-2');
    tick();

    expect(api.resumeTask).toHaveBeenCalledOnceWith('task-2');
    expect(store.selectedTaskId()).toBe('task-2');
    expect(store.error()).toBe('Lost the live connection to this task.');
    discardPeriodicTasks();
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
});
