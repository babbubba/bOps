// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { discardPeriodicTasks, fakeAsync, TestBed, tick } from '@angular/core/testing';
import { BOpsApiClient } from '../core/api/bops-api-client';
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

class FakeEventSource {
  static readonly instances: FakeEventSource[] = [];

  readonly close = jasmine.createSpy('close');
  private readonly listeners = new Map<string, EventListenerOrEventListenerObject[]>();

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, listener: EventListenerOrEventListenerObject): void {
    const listeners = this.listeners.get(type) ?? [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  emitSnapshot(snapshot: TaskState): void {
    this.emit('snapshot', new MessageEvent<string>('snapshot', { data: JSON.stringify(snapshot) }));
  }

  emitError(): void {
    this.emit('error', new Event('error'));
  }

  private emit(type: string, event: Event): void {
    for (const listener of this.listeners.get(type) ?? []) {
      if (typeof listener === 'function') {
        listener(event);
      } else {
        listener.handleEvent(event);
      }
    }
  }
}

describe('TasksStore', () => {
  let api: jasmine.SpyObj<BOpsApiClient>;
  let originalEventSource: typeof EventSource;

  beforeEach(() => {
    FakeEventSource.instances.length = 0;
    originalEventSource = globalThis.EventSource;
    globalThis.EventSource = FakeEventSource as unknown as typeof EventSource;

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
      providers: [TasksStore, { provide: BOpsApiClient, useValue: api }],
    });
  });

  afterEach(() => {
    globalThis.EventSource = originalEventSource;
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

  it('starts a task, consumes live snapshots and closes the stream at terminal state', fakeAsync(() => {
    api.startTask.and.resolveTo({ taskId: 'task-1' });
    const store = TestBed.inject(TasksStore);
    tick();

    void store.start('Inspect this host');
    tick();

    expect(api.startTask).toHaveBeenCalledOnceWith('Inspect this host');
    expect(store.selectedTaskId()).toBe('task-1');
    expect(FakeEventSource.instances[0].url).toBe('/api/agents/tasks/task-1/events');

    const liveTask = task('task-1');
    FakeEventSource.instances[0].emitSnapshot(liveTask);
    expect(store.selectedTask()).toEqual(liveTask);

    FakeEventSource.instances[0].emitSnapshot(task('task-1', 1));
    expect(FakeEventSource.instances[0].close).toHaveBeenCalled();
    discardPeriodicTasks();
  }));

  it('replaces the active stream on resume and reports a stream error', fakeAsync(() => {
    const store = TestBed.inject(TasksStore);
    tick();

    void store.start('First task');
    tick();
    const firstSource = FakeEventSource.instances[0];

    void store.resume('task-2');
    tick();

    expect(api.resumeTask).toHaveBeenCalledOnceWith('task-2');
    expect(firstSource.close).toHaveBeenCalled();
    expect(store.selectedTaskId()).toBe('task-2');

    FakeEventSource.instances[1].emitError();
    expect(store.error()).toBe('Lost the live connection to this task.');
    expect(FakeEventSource.instances[1].close).toHaveBeenCalled();
    discardPeriodicTasks();
  }));

  it('does not watch a selected terminal task and exposes API errors', fakeAsync(() => {
    api.getTask.and.resolveTo(task('done', 1));
    const store = TestBed.inject(TasksStore);
    tick();

    void store.selectTask('done');
    tick();
    expect(store.selectedTask()).toEqual(task('done', 1));
    expect(FakeEventSource.instances).toHaveSize(0);

    api.getTask.and.rejectWith(new Error('Task unavailable'));
    void store.selectTask('missing');
    tick();
    expect(store.error()).toBe('Task unavailable');
    discardPeriodicTasks();
  }));
});
