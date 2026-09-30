// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { discardPeriodicTasks, fakeAsync, TestBed, tick } from '@angular/core/testing';
import { TaskState } from '../core/api/models';
import { AuthService } from '../core/auth/auth.service';
import { authInterceptor } from '../core/auth/auth.interceptor';
import { TASK_POLL_INTERVAL_MS } from '../core/streaming/task-events';
import { TasksStore } from './tasks.store';

const TASK_URL = '/api/agents/tasks/t';

function runningTask(executionAttempt = 1): TaskState {
  return {
    id: 't',
    node: 'local',
    goal: 'Goal t',
    status: 0,
    steps: [],
    plans: [],
    createdAtUtc: '2026-09-15T12:00:00Z',
    executionAttempt,
    accounting: { tokensUsed: 0, lifetimeSteps: 0, lifetimeReplans: 0 },
    origin: 1,
    executing: true,
    resumable: false,
    resumeBlockedReason: null,
  };
}

/**
 * Sign-out versus session expiry, through the real AuthService, TasksStore, BOpsApiClient and auth interceptor, with only the
 * HTTP backend faked. A deliberate sign-out ends the watch; only a refusal while still signed in is an expiry.
 */
describe('Task watch across sign-out and session expiry', () => {
  let auth: AuthService;
  let http: HttpTestingController;
  let store: InstanceType<typeof TasksStore>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  function signIn(key: string): void {
    void auth.signIn(key);
    http.expectOne('/api/session/me').flush({ id: 'alice', displayName: 'Alice', roles: ['operator'] });
    tick();
    TestBed.tick();
    tick();
  }

  /** The list poll is not under test; answer whatever it asked so it never holds a request open. */
  function answerLists(): void {
    http.match((request) => request.url.startsWith('/api/agents/tasks?')).forEach((request) => request.flush([]));
    tick();
  }

  function taskGets(): TestRequest[] {
    return http.match((request) => request.url === TASK_URL);
  }

  /** Signs in, selects the running task and lets the watch make its first poll; returns with that poll answered. */
  function startWatching(key: string, executionAttempt = 1): void {
    signIn(key);
    store = TestBed.inject(TasksStore);
    tick();
    answerLists();

    void store.selectTask('t');
    const select = taskGets();
    expect(select.length).toBe(1);
    select[0].flush(runningTask(executionAttempt));
    tick();

    const first = taskGets();
    expect(first.length).toBe(1);
    expect(first[0].request.headers.get('Authorization')).toBe(`Bearer ${key}`);
    first[0].flush(runningTask(executionAttempt));
    tick();
  }

  function finish(): void {
    store.clearSelection();
    discardPeriodicTasks();
  }

  it('stops the watch and clears the pending watch on a deliberate sign-out, without an expiry, and never restores it', fakeAsync(() => {
    startWatching('key-1');

    auth.signOut();
    TestBed.tick();
    tick();

    expect(auth.authenticated()).toBeFalse();
    expect(auth.sessionExpired()).toBeFalse();
    expect(store.pendingWatch()).toBeNull();
    expect(store.connection()).toBe('connected');
    expect(store.selectedTaskId()).toBeNull();

    tick(TASK_POLL_INTERVAL_MS * 10);
    expect(taskGets().length).toBe(0);

    signIn('key-2');
    tick(TASK_POLL_INTERVAL_MS * 10);
    expect(taskGets().length).toBe(0);
    expect(auth.sessionExpired()).toBeFalse();
    expect(store.pendingWatch()).toBeNull();
    expect(store.connection()).toBe('connected');
    expect(store.selectedTaskId()).toBeNull();
    finish();
  }));

  for (const effectsRanBefore401 of [false, true]) {
    it(`does not turn a 401 answering a poll in flight at sign-out into an expiry (store notified of the sign-out first: ${effectsRanBefore401})`, fakeAsync(() => {
      startWatching('key-1');

      tick(TASK_POLL_INTERVAL_MS);
      const inFlight = taskGets();
      expect(inFlight.length).toBe(1);
      expect(inFlight[0].request.headers.get('Authorization')).toBe('Bearer key-1');

      auth.signOut();
      if (effectsRanBefore401) TestBed.tick();
      inFlight[0].flush(null, { status: 401, statusText: 'Unauthorized' });
      tick();
      TestBed.tick();
      tick();

      expect(auth.authenticated()).toBeFalse();
      expect(auth.sessionExpired()).toBeFalse();
      expect(store.pendingWatch()).toBeNull();
      expect(store.connection()).toBe('connected');

      tick(TASK_POLL_INTERVAL_MS * 10);
      expect(taskGets().length).toBe(0);

      signIn('key-2');
      tick(TASK_POLL_INTERVAL_MS * 10);
      expect(taskGets().length).toBe(0);
      expect(auth.sessionExpired()).toBeFalse();
      expect(store.pendingWatch()).toBeNull();
      finish();
    }));
  }

  it('still treats a 401 while signed in as an expiry, keeps the watch to restore, and restores it on the next sign-in', fakeAsync(() => {
    startWatching('key-1', 2);

    tick(TASK_POLL_INTERVAL_MS);
    const refused = taskGets();
    expect(refused.length).toBe(1);
    refused[0].flush(null, { status: 401, statusText: 'Unauthorized' });
    tick();
    TestBed.tick();
    tick();

    expect(auth.authenticated()).toBeFalse();
    expect(auth.sessionExpired()).toBeTrue();
    expect(store.connection()).toBe('sessionExpired');
    expect(store.pendingWatch()).toEqual({ taskId: 't', expectedAttempt: 2 });
    expect(store.selectedTaskId()).toBe('t');

    tick(TASK_POLL_INTERVAL_MS * 10);
    expect(taskGets().length).toBe(0);

    void auth.signIn('key-2');
    http.expectOne('/api/session/me').flush({ id: 'alice', displayName: 'Alice', roles: ['operator'] });
    tick();
    TestBed.tick();
    tick();

    const restored = taskGets();
    expect(restored.length).toBe(1);
    expect(restored[0].request.headers.get('Authorization')).toBe('Bearer key-2');
    // An answer of attempt 1 is older than the expected attempt 2: the restored watch fences it out, so it is never shown.
    restored[0].flush({ ...runningTask(1), goal: 'stale' });
    tick();
    expect(store.selectedTask()?.goal).toBe('Goal t');
    expect(store.connection()).toBe('connected');
    expect(store.pendingWatch()).toBeNull();

    tick(TASK_POLL_INTERVAL_MS);
    const next = taskGets();
    expect(next.length).toBe(1);
    next[0].flush({ ...runningTask(2), goal: 'fresh' });
    tick();
    expect(store.selectedTask()?.goal).toBe('fresh');
    expect(auth.sessionExpired()).toBeFalse();
    finish();
  }));
});
