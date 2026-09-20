// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse } from '@angular/common/http';
import { discardPeriodicTasks, fakeAsync, TestBed, tick } from '@angular/core/testing';
import { signal } from '@angular/core';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { AuthService } from '../core/auth/auth.service';
import { Delegation, PendingPlanApproval } from '../core/api/models';
import { DelegationsStore } from './delegations.store';

function run(overrides: Partial<Delegation> = {}): Delegation {
  return {
    id: 'run-1',
    status: 'Running',
    objective: 'Why did nginx stop?',
    actorId: 'alice',
    actorDisplayName: 'Alice',
    runningInThisHost: true,
    awaitingPlanApproval: false,
    planHash: null,
    approval: null,
    roles: [],
    journal: [],
    resumeCount: 0,
    denial: null,
    errorMessage: null,
    createdAtUtc: '2026-09-20T10:00:00Z',
    updatedAtUtc: '2026-09-20T10:00:00Z',
    ...overrides,
  };
}

const plan: PendingPlanApproval = {
  delegationId: 'run-1',
  planHash: 'hash-1',
  requestedAtUtc: '2026-09-20T10:01:00Z',
  skillId: 'service.skill',
  capabilityName: 'service.restore',
  target: 'web-1',
  environment: 'prod',
  blastRadius: 'Single',
  rationale: 'Restart it.',
  steps: [],
  findings: [],
  authority: null,
};

describe('DelegationsStore', () => {
  let api: jasmine.SpyObj<BOpsApiClient>;
  let roles: string[];

  beforeEach(() => {
    roles = ['viewer', 'operator', 'approver', 'administrator'];
    api = jasmine.createSpyObj<BOpsApiClient>('BOpsApiClient', [
      'listDelegations',
      'listPendingPlanApprovals',
      'startDelegation',
      'cancelDelegation',
      'resumeDelegation',
      'reconcileDelegation',
      'respondToPlanApproval',
    ]);
    api.listDelegations.and.resolveTo([]);
    api.listPendingPlanApprovals.and.resolveTo([]);

    TestBed.configureTestingModule({
      providers: [
        DelegationsStore,
        { provide: BOpsApiClient, useValue: api },
        {
          provide: AuthService,
          useValue: { authenticated: signal(true), identity: () => ({ id: 'alice', displayName: 'Alice', roles }) },
        },
      ],
    });
  });

  it('loads the runs and the waiting plans at once, then polls every two seconds', fakeAsync(() => {
    api.listDelegations.and.resolveTo([run({ awaitingPlanApproval: true })]);
    api.listPendingPlanApprovals.and.resolveTo([plan]);

    const store = TestBed.inject(DelegationsStore);
    expect(store.loading()).toBeTrue();
    tick();

    expect(store.runs().length).toBe(1);
    expect(store.pendingPlans()).toEqual([plan]);
    expect(store.awaitingApproval().length).toBe(1);
    expect(store.loading()).toBeFalse();

    tick(2000);
    expect(api.listDelegations).toHaveBeenCalledTimes(2);
    discardPeriodicTasks();
  }));

  it('does not ask for the waiting plans when the principal is not an approver (the API would refuse it)', fakeAsync(() => {
    roles = ['viewer', 'operator'];
    api.listDelegations.and.resolveTo([run()]);

    const store = TestBed.inject(DelegationsStore);
    tick();

    expect(api.listPendingPlanApprovals).not.toHaveBeenCalled();
    expect(store.pendingPlans()).toEqual([]);
    expect(store.runs().length).toBe(1);
    discardPeriodicTasks();
  }));

  it('says why the API refused, from its message, not the transport text', fakeAsync(() => {
    api.listDelegations.and.rejectWith(new HttpErrorResponse({ status: 500, error: { message: 'The store is unavailable.' } }));

    const store = TestBed.inject(DelegationsStore);
    tick();

    expect(store.error()).toBe('The store is unavailable.');
    discardPeriodicTasks();
  }));

  it('starts a run with its idempotency key and returns the id', fakeAsync(() => {
    api.startDelegation.and.resolveTo({ delegationId: 'run-9' });
    const store = TestBed.inject(DelegationsStore);
    tick();

    let id: string | undefined;
    void store.start({ objective: 'Look into it' }, 'key-1').then((value) => (id = value));
    tick();

    expect(id).toBe('run-9');
    expect(api.startDelegation).toHaveBeenCalledOnceWith({ objective: 'Look into it' }, 'key-1');
    expect(store.busy()).toBeFalse();
    discardPeriodicTasks();
  }));

  it('returns nothing and keeps the reason when a start is refused', fakeAsync(() => {
    api.startDelegation.and.rejectWith(new HttpErrorResponse({ status: 403, error: { message: 'No.' } }));
    const store = TestBed.inject(DelegationsStore);
    tick();

    let id: string | undefined = 'unset';
    void store.start({ objective: 'x' }).then((value) => (id = value));
    tick();

    expect(id).toBeUndefined();
    expect(store.error()).toBe('No.');
    expect(store.busy()).toBeFalse();
    discardPeriodicTasks();
  }));

  it('decides a plan by its hash, and refreshes afterwards', fakeAsync(() => {
    api.respondToPlanApproval.and.resolveTo();
    const store = TestBed.inject(DelegationsStore);
    tick();
    api.listDelegations.calls.reset();

    void store.decidePlan('run-1', 'hash-1', true, 'ok');
    tick();

    expect(api.respondToPlanApproval).toHaveBeenCalledOnceWith('run-1', 'hash-1', true, 'ok');
    expect(api.listDelegations).toHaveBeenCalledTimes(1);
    discardPeriodicTasks();
  }));

  it('reports a decision the API refused, such as a plan that has changed', fakeAsync(() => {
    api.respondToPlanApproval.and.rejectWith(new HttpErrorResponse({ status: 409, error: { message: 'Not the plan waiting.' } }));
    const store = TestBed.inject(DelegationsStore);
    tick();

    void store.decidePlan('run-1', 'stale', true);
    tick();

    expect(store.error()).toBe('Not the plan waiting.');
    discardPeriodicTasks();
  }));

  it('sends cancel, resume and reconcile to the API', fakeAsync(() => {
    api.cancelDelegation.and.resolveTo(run());
    api.resumeDelegation.and.resolveTo({ delegationId: 'run-1' });
    api.reconcileDelegation.and.resolveTo(run());
    const store = TestBed.inject(DelegationsStore);
    tick();

    void store.cancel('run-1');
    void store.resume('run-2');
    void store.reconcile('run-3', 'abandon', 'gone');
    tick();

    expect(api.cancelDelegation).toHaveBeenCalledOnceWith('run-1');
    expect(api.resumeDelegation).toHaveBeenCalledOnceWith('run-2');
    expect(api.reconcileDelegation).toHaveBeenCalledOnceWith('run-3', 'abandon', 'gone');
    discardPeriodicTasks();
  }));
});
