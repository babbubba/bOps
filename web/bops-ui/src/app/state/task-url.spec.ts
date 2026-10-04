// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, signal } from '@angular/core';
import { TestBed, discardPeriodicTasks, fakeAsync, tick } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { AgentTaskStatus, TaskState } from '../core/api/models';
import { AuthService, AuthStatus } from '../core/auth/auth.service';
import { TASK_POLL_INTERVAL_MS } from '../core/streaming/task-events';
import { Dashboard } from '../features/dashboard/dashboard';
import { TasksStore, isCanonicalTaskId } from './tasks.store';

const ID = '3f2b8c1e-5d4a-4b6f-9a7e-0c1d2e3f4a5b';

function task(status: AgentTaskStatus, executionAttempt = 1): TaskState {
  return {
    id: ID,
    node: 'local',
    goal: 'Restore me',
    status,
    steps: [],
    plans: [],
    createdAtUtc: '2026-10-05T08:00:00Z',
    executionAttempt,
    accounting: { tokensUsed: 0, lifetimeSteps: 0, lifetimeReplans: 0 },
    origin: 1,
    executing: status === 0,
    resumable: false,
    resumeBlockedReason: null,
  };
}

@Component({ template: '' })
class Elsewhere {}

/** ADR-0043 §15: the selected task lives in `/dashboard?task=<uuid>`, so a reload (or a sign-in after one) restores the watch. */
describe('Selected task in the dashboard URL', () => {
  let api: jasmine.SpyObj<BOpsApiClient>;
  let status: ReturnType<typeof signal<AuthStatus>>;

  beforeEach(() => {
    api = jasmine.createSpyObj<BOpsApiClient>('BOpsApiClient', ['listTasks', 'startTask', 'resumeTask', 'cancelTask', 'getTask']);
    api.listTasks.and.resolveTo([]);
    api.getTask.and.resolveTo(task(0, 2));
    api.startTask.and.resolveTo({ taskId: ID });
    status = signal<AuthStatus>('authenticated');
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'dashboard', component: Dashboard },
          { path: 'approvals', component: Elsewhere },
        ]),
        { provide: BOpsApiClient, useValue: api },
        {
          provide: AuthService,
          useValue: { status, authenticated: computed(() => status() === 'authenticated'), signingOut: signal(false), expireSession: () => undefined },
        },
      ],
    });
  });

  it('recognises only canonical task ids', () => {
    expect(isCanonicalTaskId(ID)).toBeTrue();
    expect(isCanonicalTaskId(ID.toUpperCase())).toBeFalse();
    expect(isCanonicalTaskId('not-a-task')).toBeFalse();
    expect(isCanonicalTaskId(`${ID}x`)).toBeFalse();
    expect(isCanonicalTaskId(null)).toBeFalse();
  });

  it('writes ?task= on select and start, keeps other parameters, and removes it on clear', fakeAsync(() => {
    const router = TestBed.inject(Router);
    void router.navigateByUrl('/approvals?tab=x');
    tick();
    const store = TestBed.inject(TasksStore);

    void store.selectTask(ID);
    tick();
    expect(router.url).toBe(`/approvals?tab=x&task=${ID}`);

    store.clearSelection();
    tick();
    expect(router.url).toBe('/approvals?tab=x');

    void store.start('goal');
    tick();
    expect(router.url).toBe(`/approvals?tab=x&task=${ID}`);
    store.clearSelection();
    tick();
    discardPeriodicTasks();
  }));

  it('removes the parameter when the selected task is gone (404)', fakeAsync(() => {
    const router = TestBed.inject(Router);
    void router.navigateByUrl(`/approvals?task=${ID}`);
    tick();
    api.getTask.and.rejectWith(new HttpErrorResponse({ status: 404 }));

    void TestBed.inject(TasksStore).selectTask(ID);
    tick();

    expect(router.url).toBe('/approvals');
    discardPeriodicTasks();
  }));

  it('after a reload, re-selects the task from the URL and resumes the watch from its execution attempt', fakeAsync(() => {
    let harness!: RouterTestingHarness;
    void RouterTestingHarness.create(`/dashboard?task=${ID}`).then((created) => (harness = created));
    tick();
    harness.detectChanges();
    const store = TestBed.inject(TasksStore);

    expect(api.getTask).toHaveBeenCalledWith(ID);
    expect(store.selectedTaskId()).toBe(ID);
    expect(store.selectedTask()?.executionAttempt).toBe(2);

    api.getTask.calls.reset();
    tick(TASK_POLL_INTERVAL_MS);
    expect(api.getTask).toHaveBeenCalledOnceWith(ID);
    expect(TestBed.inject(Router).url).toBe(`/dashboard?task=${ID}`);

    store.clearSelection();
    discardPeriodicTasks();
  }));

  it('shows a task that finished during the reload, without starting a watch', fakeAsync(() => {
    api.getTask.and.resolveTo(task(1));
    let harness!: RouterTestingHarness;
    void RouterTestingHarness.create(`/dashboard?task=${ID}`).then((created) => (harness = created));
    tick();
    harness.detectChanges();

    expect(TestBed.inject(TasksStore).selectedTask()?.status).toBe(1);
    api.getTask.calls.reset();
    tick(TASK_POLL_INTERVAL_MS * 5);
    expect(api.getTask).not.toHaveBeenCalled();
    discardPeriodicTasks();
  }));

  it('strips a malformed id from the URL and never polls for it', fakeAsync(() => {
    let harness!: RouterTestingHarness;
    void RouterTestingHarness.create('/dashboard?task=..%2F..%2Fsecrets').then((created) => (harness = created));
    tick();
    harness.detectChanges();
    tick();

    expect(api.getTask).not.toHaveBeenCalled();
    expect(TestBed.inject(Router).url).toBe('/dashboard');
    tick(TASK_POLL_INTERVAL_MS * 5);
    expect(api.getTask).not.toHaveBeenCalled();
    discardPeriodicTasks();
  }));

  it('keeps ?task= through a boot that found no session, so the sign-in that follows restores the watch', fakeAsync(() => {
    status.set('initializing');
    const router = TestBed.inject(Router);
    void router.navigateByUrl(`/approvals?task=${ID}`);
    tick();
    TestBed.inject(TasksStore);
    TestBed.tick();

    status.set('unauthenticated');
    TestBed.tick();
    tick();

    expect(router.url).toBe(`/approvals?task=${ID}`);
    discardPeriodicTasks();
  }));
});
