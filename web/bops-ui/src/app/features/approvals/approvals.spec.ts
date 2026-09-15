// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PendingApproval, RiskLevel } from '../../core/api/models';
import { ApprovalsStore } from '../../state/approvals.store';
import { Approvals } from './approvals';

const pending: PendingApproval = {
  id: 'approval-1',
  taskId: 'task-1',
  tool: 'docker.stop',
  reason: 'Stopping this container requires approval.',
  requestedAtUtc: '2026-09-15T12:00:00Z',
};

describe('Approvals', () => {
  let fixture: ComponentFixture<Approvals>;
  let store: {
    pending: ReturnType<typeof signal<PendingApproval[]>>;
    toolRisk: ReturnType<typeof signal<Record<string, RiskLevel>>>;
    loading: ReturnType<typeof signal<boolean>>;
    error: ReturnType<typeof signal<string | null>>;
    approve: jasmine.Spy;
    reject: jasmine.Spy;
  };

  beforeEach(async () => {
    store = {
      pending: signal([pending]),
      toolRisk: signal({ 'docker.stop': 3 }),
      loading: signal(false),
      error: signal<string | null>(null),
      approve: jasmine.createSpy('approve').and.resolveTo(),
      reject: jasmine.createSpy('reject').and.resolveTo(),
    };

    await TestBed.configureTestingModule({
      imports: [Approvals],
      providers: [{ provide: ApprovalsStore, useValue: store }],
    }).compileComponents();

    fixture = TestBed.createComponent(Approvals);
    fixture.detectChanges();
  });

  it('renders the request and forwards an optional note when approving', async () => {
    expect(fixture.nativeElement.textContent).toContain('docker.stop');
    expect(fixture.nativeElement.textContent).toContain('High');
    expect(fixture.nativeElement.textContent).toContain(pending.reason);

    const input = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    input.value = 'Checked with the operator';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    const approveButton = Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    ).find((button) => button.textContent?.includes('Approve'));
    approveButton?.click();
    await fixture.whenStable();

    expect(store.approve).toHaveBeenCalledOnceWith('approval-1', 'Checked with the operator');
  });

  it('rejects without inventing a note', async () => {
    const rejectButton = Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    ).find((button) => button.textContent?.includes('Reject'));
    rejectButton?.click();
    await fixture.whenStable();

    expect(store.reject).toHaveBeenCalledOnceWith('approval-1', undefined);
  });
});
