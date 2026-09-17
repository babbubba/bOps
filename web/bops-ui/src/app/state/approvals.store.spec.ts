// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { discardPeriodicTasks, fakeAsync, TestBed, tick } from '@angular/core/testing';
import { signal } from '@angular/core';
import { BOpsApiClient } from '../core/api/bops-api-client';
import { AuthService } from '../core/auth/auth.service';
import { PendingApproval, ToolManifest } from '../core/api/models';
import { ApprovalsStore } from './approvals.store';

const approval: PendingApproval = {
  id: 'approval-1',
  taskId: 'task-1',
  tool: 'docker.stop',
  reason: 'Stopping a container requires approval.',
  requestedAtUtc: '2026-09-15T12:00:00Z',
  arguments: {},
  permanentDeletion: false,
};

const tool: ToolManifest = {
  name: 'docker.stop',
  description: 'Stops a container.',
  risk: 3,
  platforms: ['windows', 'linux'],
  requires: ['docker'],
  parameters: [],
  verification: null,
  requiresExplicitApproval: false,
  package: 'bOps.Packages.Docker',
};

describe('ApprovalsStore', () => {
  let api: jasmine.SpyObj<BOpsApiClient>;

  beforeEach(() => {
    api = jasmine.createSpyObj<BOpsApiClient>('BOpsApiClient', [
      'listPendingApprovals',
      'listTools',
      'respondToApproval',
    ]);
    api.listPendingApprovals.and.resolveTo([]);
    api.listTools.and.resolveTo([]);
    api.respondToApproval.and.resolveTo();

    TestBed.configureTestingModule({
      providers: [
        ApprovalsStore,
        { provide: BOpsApiClient, useValue: api },
        { provide: AuthService, useValue: { authenticated: signal(true) } },
      ],
    });
  });

  it('loads approvals and tool risks immediately, then polls every two seconds', fakeAsync(() => {
    api.listPendingApprovals.and.resolveTo([approval]);
    api.listTools.and.resolveTo([tool]);

    const store = TestBed.inject(ApprovalsStore);
    expect(store.loading()).toBeTrue();
    tick();

    expect(store.pending()).toEqual([approval]);
    expect(store.toolRisk()).toEqual({ 'docker.stop': 3 });
    expect(store.loading()).toBeFalse();

    tick(2000);
    expect(api.listPendingApprovals).toHaveBeenCalledTimes(2);
    expect(api.listTools).toHaveBeenCalledTimes(1);
    discardPeriodicTasks();
  }));

  it('removes an approval after approving or rejecting it', fakeAsync(() => {
    api.listPendingApprovals.and.resolveTo([
      approval,
      { ...approval, id: 'approval-2', tool: 'fs.delete' },
    ]);
    const store = TestBed.inject(ApprovalsStore);
    tick();

    void store.approve('approval-1', 'approved locally');
    tick();
    expect(api.respondToApproval).toHaveBeenCalledWith('approval-1', true, 'approved locally', false);
    expect(store.pending().map((item) => item.id)).toEqual(['approval-2']);

    void store.reject('approval-2');
    tick();
    expect(api.respondToApproval).toHaveBeenCalledWith('approval-2', false, undefined, false);
    expect(store.pending()).toEqual([]);
    discardPeriodicTasks();
  }));

  it('reports approval refresh errors without making tool-risk failure fatal', fakeAsync(() => {
    api.listPendingApprovals.and.rejectWith(new Error('Approval API unavailable'));
    api.listTools.and.rejectWith(new Error('Tools API unavailable'));

    const store = TestBed.inject(ApprovalsStore);
    tick();

    expect(store.error()).toBe('Approval API unavailable');
    expect(store.toolRisk()).toEqual({});
    expect(store.loading()).toBeFalse();
    discardPeriodicTasks();
  }));
});
