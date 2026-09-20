// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../../core/auth/auth.service';
import { Delegation, PendingPlanApproval } from '../../core/api/models';
import { DelegationsStore } from '../../state/delegations.store';
import { Delegations } from './delegations';

const injected = '<img src=x onerror="window.__pwned=true">';

function run(overrides: Partial<Delegation> = {}): Delegation {
  return {
    id: 'run-1',
    status: 'Completed',
    objective: 'Why did nginx stop?',
    actorId: 'alice',
    actorDisplayName: 'Alice',
    runningInThisHost: false,
    awaitingPlanApproval: false,
    planHash: 'plan-hash-abc',
    approval: { planHash: 'plan-hash-abc', approverId: 'bob', approverDisplayName: 'Bob', approvedAtUtc: '2026-09-20T10:05:00Z' },
    roles: [
      {
        role: 'Verification',
        agentId: 'agent-4',
        status: 'Completed',
        steps: 1,
        tokens: 0,
        startedAtUtc: null,
        completedAtUtc: null,
        findings: [],
        evidence: [],
        planHash: null,
        verification: { status: 'Confirmed', detail: 'read the service', evidence: [] },
        errorMessage: null,
      },
      {
        role: 'Discovery',
        agentId: 'agent-1',
        status: 'Completed',
        steps: 2,
        tokens: 10,
        startedAtUtc: null,
        completedAtUtc: null,
        findings: [{ id: 'f1', summary: 'The service has stopped.', severity: 'High', evidenceIds: ['discovery-0'] }],
        evidence: [
          { id: 'discovery-0', kind: 'Fact', description: 'Read host.info.', sourceTool: 'host.info', observedAtUtc: '2026-09-20T10:00:00Z' },
        ],
        planHash: null,
        verification: null,
        errorMessage: null,
      },
    ],
    journal: [{ stepIndex: 0, tool: 'service.restart', argumentsHash: 'h', intentAtUtc: '2026-09-20T10:06:00Z', outcome: 'Succeeded', verification: 'Confirmed', reconciliation: null }],
    resumeCount: 0,
    denial: null,
    errorMessage: null,
    createdAtUtc: '2026-09-20T10:00:00Z',
    updatedAtUtc: '2026-09-20T10:07:00Z',
    ...overrides,
  };
}

const waitingPlan: PendingPlanApproval = {
  delegationId: 'run-1',
  planHash: 'hash-77',
  requestedAtUtc: '2026-09-20T10:01:00Z',
  skillId: 'service.skill',
  capabilityName: 'service.restore',
  target: 'web-1',
  environment: 'prod',
  blastRadius: 'Single',
  rationale: 'Restart the service.',
  steps: [{ index: 0, tool: 'service.restart', arguments: { name: 'nginx' }, description: 'Restart nginx.' }],
  findings: [{ id: 'f1', summary: 'The service has stopped.', severity: 'High', evidenceIds: ['discovery-0'] }],
  authority: { tools: ['service.restart'], maxRisk: 'High', maxBlastRadius: 'Single', targets: ['web-1'], environments: ['prod'], maxSteps: 5, deadlineUtc: '2026-09-20T11:00:00Z' },
};

describe('Delegations', () => {
  let fixture: ComponentFixture<Delegations>;
  let store: {
    runs: ReturnType<typeof signal<Delegation[]>>;
    pendingPlans: ReturnType<typeof signal<PendingPlanApproval[]>>;
    loading: ReturnType<typeof signal<boolean>>;
    busy: ReturnType<typeof signal<boolean>>;
    error: ReturnType<typeof signal<string | null>>;
    start: jasmine.Spy;
    cancel: jasmine.Spy;
    resume: jasmine.Spy;
    reconcile: jasmine.Spy;
    decidePlan: jasmine.Spy;
  };

  const text = (): string => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const button = (label: string): HTMLButtonElement | undefined =>
    Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find((b) => b.textContent?.trim() === label);

  async function setup(roles: string[], runs: Delegation[], plans: PendingPlanApproval[] = []): Promise<void> {
    store = {
      runs: signal(runs),
      pendingPlans: signal(plans),
      loading: signal(false),
      busy: signal(false),
      error: signal<string | null>(null),
      start: jasmine.createSpy('start').and.resolveTo('run-9'),
      cancel: jasmine.createSpy('cancel').and.resolveTo(),
      resume: jasmine.createSpy('resume').and.resolveTo(),
      reconcile: jasmine.createSpy('reconcile').and.resolveTo(),
      decidePlan: jasmine.createSpy('decidePlan').and.resolveTo(),
    };
    await TestBed.configureTestingModule({
      imports: [Delegations],
      providers: [
        { provide: DelegationsStore, useValue: store },
        { provide: AuthService, useValue: { identity: signal({ id: 'alice', displayName: 'Alice', roles }) } },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(Delegations);
    fixture.detectChanges();
  }

  async function open(id = 'run-1'): Promise<void> {
    (fixture.componentInstance as unknown as { select(id: string): void }).select(id);
    fixture.detectChanges();
  }

  it('lists the runs and says when there are none', async () => {
    await setup(['viewer'], []);
    expect(text()).toContain('No delegations yet.');
  });

  it('shows the roles in pipeline order, with findings, evidence ids and the verification verdict', async () => {
    await setup(['viewer'], [run()]);
    await open();

    const shown = text();
    const order = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('section[aria-label="Roles"] ol > li'))
      .map((item) => item.querySelector('span.font-semibold')?.textContent?.trim());
    expect(order).toEqual(['Discovery', 'Verification']);
    expect(shown).toContain('The service has stopped.');
    expect(shown).toContain('discovery-0');
    expect(shown).toContain('Verification verdict: Confirmed');
    expect(shown).toContain('plan-hash-abc');
    expect(shown).toContain('Approved by Bob');
    expect(shown).toContain('service.restart');
    expect(shown).toContain('what the tools returned is not shown');
  });

  it('renders untrusted text as text, never as markup', async () => {
    const hostile = run({
      objective: injected,
      errorMessage: injected,
      roles: [{ ...run().roles[1], findings: [{ id: 'f', summary: injected, severity: null, evidenceIds: ['e'] }], evidence: [{ id: 'e', kind: 'Fact', description: injected, sourceTool: injected, observedAtUtc: '2026-09-20T10:00:00Z' }] }],
    });
    await setup(['viewer'], [hostile]);
    await open();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('img')).toBeNull();
    expect(text()).toContain('<img src=x');
    expect((window as unknown as { __pwned?: boolean }).__pwned).toBeUndefined();
  });

  it('shows the plan waiting for a decision to an approver, and decides it by its hash', async () => {
    await setup(['viewer', 'approver'], [run({ status: 'Running', awaitingPlanApproval: true, runningInThisHost: true, planHash: null, approval: null })], [waitingPlan]);
    await open();

    expect(text()).toContain('hash-77');
    expect(text()).toContain('service.restart');
    expect(text()).toContain('Authority the change will run under');
    button('Approve exactly this plan')!.click();
    expect(store.decidePlan).toHaveBeenCalledOnceWith('run-1', 'hash-77', true, undefined);
    button('Reject')!.click();
    expect(store.decidePlan).toHaveBeenCalledWith('run-1', 'hash-77', false, undefined);
  });

  it('offers no way to decide a plan to someone who is not an approver', async () => {
    await setup(['viewer', 'operator'], [run({ status: 'Running', awaitingPlanApproval: true, runningInThisHost: true, planHash: null, approval: null })], []);
    await open();

    expect(text()).toContain('Waiting for an approver.');
    expect(button('Approve exactly this plan')).toBeUndefined();
    expect(button('Reject')).toBeUndefined();
  });

  it('offers reconcile only to an administrator, and cancel and resume only to an operator', async () => {
    const waiting = run({ status: 'RequiresReconciliation' });
    await setup(['viewer', 'operator'], [waiting]);
    await open();
    expect(text()).toContain('Only an administrator can settle this.');
    expect(button('Accept as done')).toBeUndefined();
    expect(button('Abandon run')).toBeUndefined();
    TestBed.resetTestingModule();

    await setup(['viewer', 'administrator'], [waiting]);
    await open();
    button('Accept as done')!.click();
    expect(store.reconcile).toHaveBeenCalledOnceWith('run-1', 'accept', undefined);
    button('Abandon run')!.click();
    expect(store.reconcile).toHaveBeenCalledWith('run-1', 'abandon', undefined);
    expect(button('Cancel run')).toBeUndefined();
    TestBed.resetTestingModule();

    const running = run({ status: 'Running', runningInThisHost: false });
    await setup(['viewer'], [running]);
    await open();
    expect(button('Cancel run')).toBeUndefined();
    expect(button('Resume')).toBeUndefined();
    TestBed.resetTestingModule();

    await setup(['viewer', 'operator'], [running]);
    await open();
    button('Cancel run')!.click();
    expect(store.cancel).toHaveBeenCalledOnceWith('run-1');
    button('Resume')!.click();
    expect(store.resume).toHaveBeenCalledOnceWith('run-1');
  });

  it('offers neither cancel nor resume for a run that has ended, even to an operator', async () => {
    await setup(['viewer', 'operator'], [run({ status: 'Completed' })]);
    await open();

    expect(button('Cancel run')).toBeUndefined();
    expect(button('Resume')).toBeUndefined();
  });

  it('does not let a non-approver decide a plan even when the plan is on screen', async () => {
    await setup(['viewer', 'operator'], [run({ status: 'Running', awaitingPlanApproval: true, runningInThisHost: true, planHash: null, approval: null })], [waitingPlan]);
    await open();

    expect(text()).toContain('hash-77');
    expect(text()).toContain('Only an approver can decide this plan.');
    expect(button('Approve exactly this plan')).toBeUndefined();
    expect(button('Reject')).toBeUndefined();
  });

  it('does not offer resume for a run this host is executing', async () => {
    await setup(['viewer', 'operator'], [run({ status: 'Running', runningInThisHost: true })]);
    await open();

    expect(button('Cancel run')).toBeDefined();
    expect(button('Resume')).toBeUndefined();
  });

  it('shows a denial with the dimension it was refused on', async () => {
    await setup(['viewer'], [run({ status: 'Denied', denial: { dimension: 'Profile', reason: 'No profile for the role.' }, roles: [], planHash: null, approval: null, journal: [] })]);
    await open();

    expect(text()).toContain('Refused on Profile: No profile for the role.');
  });

  it('hides the start form from someone who cannot operate', async () => {
    await setup(['viewer'], []);
    expect(text()).not.toContain('Start a delegation');
  });

  it('starts a diagnosis, then a change with its companions, each with the same key until it succeeds', async () => {
    await setup(['viewer', 'operator'], []);
    const component = fixture.componentInstance as unknown as {
      objective: { set(v: string): void };
      withChange: { set(v: boolean): void };
      skillId: { set(v: string): void };
      capabilityName: { set(v: string): void };
      target: { set(v: string): void };
      environment: { set(v: string): void };
      start(): Promise<void>;
      canStart(): boolean;
    };

    expect(component.canStart()).toBeFalse();
    component.objective.set('  Look into nginx  ');
    expect(component.canStart()).toBeTrue();
    await component.start();
    expect(store.start.calls.mostRecent().args[0]).toEqual({ objective: 'Look into nginx' });
    const firstKey = store.start.calls.mostRecent().args[1] as string;
    expect(firstKey).toBeTruthy();

    component.objective.set('Fix nginx');
    component.withChange.set(true);
    expect(component.canStart()).toBeFalse();
    component.skillId.set('service.skill');
    component.capabilityName.set('service.restore');
    component.target.set('web-1');
    expect(component.canStart()).toBeFalse();
    component.environment.set('prod');
    expect(component.canStart()).toBeTrue();
    await component.start();
    const request = store.start.calls.mostRecent().args[0];
    expect(request.remediation).toEqual({ skillId: 'service.skill', capabilityName: 'service.restore', target: 'web-1', environment: 'prod', blastRadius: 'single', dryRun: false });
    expect(store.start.calls.mostRecent().args[1]).not.toBe(firstKey);
  });

  it('keeps the same key when a start was refused, so a retry cannot start a second run', async () => {
    await setup(['viewer', 'operator'], []);
    store.start.and.resolveTo(undefined);
    const component = fixture.componentInstance as unknown as { objective: { set(v: string): void }; start(): Promise<void> };
    component.objective.set('Look');

    await component.start();
    await component.start();

    expect(store.start.calls.argsFor(0)[1]).toBe(store.start.calls.argsFor(1)[1]);
  });

  it('shows the API error', async () => {
    await setup(['viewer'], []);
    store.error.set('The store is unavailable.');
    fixture.detectChanges();

    expect(text()).toContain('The store is unavailable.');
  });
});
