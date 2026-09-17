// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BOpsApiClient } from '../../core/api/bops-api-client';
import { DeletionManifestSummary, PendingApproval, RiskLevel } from '../../core/api/models';
import { ApprovalsStore } from '../../state/approvals.store';
import { Approvals } from './approvals';

const pending: PendingApproval = {
  id: 'approval-1',
  taskId: 'task-1',
  tool: 'docker.stop',
  reason: 'Stopping this container requires approval.',
  requestedAtUtc: '2026-09-15T12:00:00Z',
  arguments: {},
  permanentDeletion: false,
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
  let api: jasmine.SpyObj<BOpsApiClient>;

  beforeEach(async () => {
    store = {
      pending: signal([pending]),
      toolRisk: signal({ 'docker.stop': 3 }),
      loading: signal(false),
      error: signal<string | null>(null),
      approve: jasmine.createSpy('approve').and.resolveTo(),
      reject: jasmine.createSpy('reject').and.resolveTo(),
    };
    api = jasmine.createSpyObj<BOpsApiClient>('BOpsApiClient', [
      'getApprovalDeletionManifest',
      'getApprovalDeletionEntries',
      'approvalDeletionDownloadUrl',
    ]);
    api.approvalDeletionDownloadUrl.and.returnValue('/download');

    await TestBed.configureTestingModule({
      imports: [Approvals],
      providers: [
        { provide: ApprovalsStore, useValue: store },
        { provide: BOpsApiClient, useValue: api },
      ],
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

    expect(store.approve).toHaveBeenCalledOnceWith('approval-1', 'Checked with the operator', false);
  });

  it('rejects without inventing a note', async () => {
    const rejectButton = Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    ).find((button) => button.textContent?.includes('Reject'));
    rejectButton?.click();
    await fixture.whenStable();

    expect(store.reject).toHaveBeenCalledOnceWith('approval-1', undefined);
  });

  it('pages a permanent deletion preview and requires the explicit acknowledgement', async () => {
    const summary: DeletionManifestSummary = {
      id: 'manifest-1',
      status: 1,
      roots: ['/safe/root'],
      warnings: [],
      approvalHash: 'abc123',
      createdAtUtc: new Date().toISOString(),
      expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
      entryCount: 10_000,
      fileCount: 9_999,
      directoryCount: 1,
      linkCount: 0,
      totalBytes: 1024,
      deletedCount: 0,
      failureCount: 0,
    };
    api.getApprovalDeletionManifest.and.resolveTo(summary);
    api.getApprovalDeletionEntries.and.resolveTo({
      entries: [{
        ordinal: 0,
        absolutePath: '/safe/root',
        rootPath: '/safe/root',
        relativePath: '.',
        type: 'directory',
        sizeBytes: null,
        outcome: null,
        error: null,
      }],
      nextCursor: 'next',
    });
    store.pending.set([{
      ...pending,
      tool: 'fs.delete_tree',
      permanentDeletion: true,
      arguments: { manifestId: 'manifest-1', approvalHash: 'abc123' },
    }]);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toMatch(/10[.,]000/);
    expect(fixture.nativeElement.querySelectorAll('tbody tr').length).toBe(1);
    const approve = Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    ).find((button) => button.textContent?.includes('Approve'))!;
    expect(approve.disabled).toBeTrue();

    const acknowledgement = fixture.nativeElement.querySelector('input[type="checkbox"]') as HTMLInputElement;
    acknowledgement.click();
    fixture.detectChanges();
    expect(approve.disabled).toBeFalse();
    approve.click();
    await fixture.whenStable();

    expect(store.approve).toHaveBeenCalledOnceWith('approval-1', undefined, true);
  });
});
