// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../../core/auth/auth.service';
import { CatalogSkill, Delegation, DelegationReadiness, PendingPlanApproval } from '../../core/api/models';
import { DelegationsStore, ReadinessFailure } from '../../state/delegations.store';
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

function readiness(remediation: boolean, ready: boolean, states: [string, string, string, string], codes?: string[]): DelegationReadiness {
  const roles = ['Discovery', 'Diagnostic', 'Remediation', 'Verification'];
  const codeOf = (state: string): string =>
    ({ ready: 'ready', notRequired: 'not_required', missing: 'profile_missing', malformed: 'reduction_denied' })[state] ?? 'something_new';
  return {
    remediation,
    ready,
    policy: 'loaded',
    roles: roles.map((role, i) => ({
      role,
      state: states[i],
      dimension: states[i] === 'missing' ? 'Profile' : null,
      reasonCode: codes?.[i] ?? codeOf(states[i]),
      reason: states[i] === 'missing' ? `${role} role: no usable profile is configured.` : null,
    })),
    profileDriftCount: 0,
    evaluatedAtUtc: '2026-10-05T12:00:00Z',
  };
}

/** What the server answers for each shape in a test; a test replaces entries before acting. */
let answers: Record<string, DelegationReadiness | undefined> = {};

const skill: CatalogSkill = {
  skillId: 'service.skill',
  package: 'bops.packages.service',
  trust: 'Official',
  capabilities: [
    {
      name: 'service.restore',
      version: '1.0.0',
      description: 'Restores a stopped service.',
      risk: 'High',
      supportsDryRun: false,
      inputSchema: [
        { name: 'serviceName', type: 'String', description: 'The service.', required: true, sensitive: false, allowedValues: null, minimum: null, maximum: null, minLength: 2, maxLength: 8, minItems: null, maxItems: null },
        { name: 'retries', type: 'Integer', description: 'Retries.', required: false, sensitive: false, allowedValues: null, minimum: 0, maximum: 5, minLength: null, maxLength: null, minItems: null, maxItems: null },
        { name: 'mode', type: 'Enum', description: 'How.', required: false, sensitive: false, allowedValues: ['fast', 'safe'], minimum: null, maximum: null, minLength: null, maxLength: null, minItems: null, maxItems: null },
        { name: 'password', type: 'String', description: 'A secret.', required: false, sensitive: true, allowedValues: null, minimum: null, maximum: null, minLength: 12, maxLength: null, minItems: null, maxItems: null },
      ],
    },
    { name: 'service.noop', version: '2.0.0', description: 'Takes nothing.', risk: 'Low', supportsDryRun: true, inputSchema: [] },
  ],
};

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
    readiness: ReturnType<typeof signal<DelegationReadiness | null>>;
    readinessLoading: ReturnType<typeof signal<boolean>>;
    readinessFailure: ReturnType<typeof signal<ReadinessFailure | null>>;
    catalog: ReturnType<typeof signal<CatalogSkill[] | null>>;
    catalogError: ReturnType<typeof signal<string | null>>;
    submitError: ReturnType<typeof signal<string | null>>;
    start: jasmine.Spy;
    cancel: jasmine.Spy;
    resume: jasmine.Spy;
    reconcile: jasmine.Spy;
    decidePlan: jasmine.Spy;
    loadReadiness: jasmine.Spy;
    loadCatalog: jasmine.Spy;
    retryReadiness: jasmine.Spy;
    clearSubmitError: jasmine.Spy;
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
      readiness: signal<DelegationReadiness | null>(null),
      readinessLoading: signal(false),
      readinessFailure: signal<ReadinessFailure | null>(null),
      catalog: signal<CatalogSkill[] | null>([skill]),
      catalogError: signal<string | null>(null),
      submitError: signal<string | null>(null),
      start: jasmine.createSpy('start').and.resolveTo('run-9'),
      cancel: jasmine.createSpy('cancel').and.resolveTo(),
      resume: jasmine.createSpy('resume').and.resolveTo(),
      reconcile: jasmine.createSpy('reconcile').and.resolveTo(),
      decidePlan: jasmine.createSpy('decidePlan').and.resolveTo(),
      // Like the real store: the latest request decides which answer is shown; the server's answer is fixed per shape by the test.
      loadReadiness: jasmine.createSpy('loadReadiness').and.callFake(async (remediation: boolean) => {
        store.readiness.set(answers[String(remediation)] ?? null);
      }),
      loadCatalog: jasmine.createSpy('loadCatalog').and.resolveTo(),
      retryReadiness: jasmine.createSpy('retryReadiness').and.resolveTo(),
      clearSubmitError: jasmine.createSpy('clearSubmitError'),
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

  // ---- the start form (ADR-0044 §9, §13) ----

  type Form = {
    objective: { set(v: string): void };
    setWithChange(v: boolean): void;
    setSkill(v: string): void;
    setCapability(v: string): void;
    setValue(name: string, v: unknown): void;
    target: { set(v: string): void };
    environment: { set(v: string): void };
    rawText: { set(v: string): void };
    setRawMode(v: boolean): void;
    start(): Promise<void>;
    canStart(): boolean;
    readinessAllowsSubmit(): boolean;
  };
  const form = (): Form => fixture.componentInstance as unknown as Form;
  const submit = (): HTMLButtonElement => button('Start')!;
  const readinessPanel = (): HTMLElement => (fixture.nativeElement as HTMLElement).querySelector('[data-testid="readiness"]')!;
  const roleState = (role: string): string | null | undefined =>
    readinessPanel().querySelector(`li[data-role="${role}"]`)?.getAttribute('data-state');

  async function operator(): Promise<void> {
    answers = {
      false: readiness(false, true, ['ready', 'ready', 'notRequired', 'notRequired']),
      true: readiness(true, true, ['ready', 'ready', 'ready', 'ready']),
    };
    await setup(['viewer', 'operator'], []);
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function chooseChange(capability = 'service.restore'): Promise<void> {
    form().setWithChange(true);
    await fixture.whenStable();
    form().setSkill('service.skill');
    form().setCapability(capability);
    form().target.set('web-1');
    form().environment.set('prod');
    fixture.detectChanges();
  }

  it('asks readiness for a diagnosis on load and shows all four roles from the server, with its fixed wording', async () => {
    answers = { false: readiness(false, false, ['missing', 'missing', 'notRequired', 'notRequired']) };
    await setup(['viewer', 'operator'], []);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(store.loadReadiness).toHaveBeenCalledOnceWith(false);
    expect(store.loadCatalog).toHaveBeenCalled();
    expect(['Discovery', 'Diagnostic', 'Remediation', 'Verification'].map(roleState)).toEqual(['missing', 'missing', 'notRequired', 'notRequired']);
    expect(readinessPanel().textContent).toContain('Discovery role: no usable profile is configured.');
    expect(readinessPanel().textContent).toContain('Missing');
    expect(readinessPanel().textContent).toContain('Not required');
    expect(readinessPanel().textContent).toContain('not all usable for this type of delegation');
    expect(text()).not.toContain('executable');
    form().objective.set('Why is nginx slow?');
    fixture.detectChanges();
    expect(submit().disabled).toBeTrue();
  });

  it('enables Submit for a diagnosis only when the server says ready, and starts it', async () => {
    await operator();
    form().objective.set('  Look into nginx  ');
    fixture.detectChanges();

    expect(readinessPanel().textContent).toContain('The required role profiles are usable for this type of delegation.');
    expect(submit().disabled).toBeFalse();
    await form().start();
    expect(store.start.calls.mostRecent().args[0]).toEqual({ objective: 'Look into nginx' });
  });

  it('asks readiness again whenever the change toggle flips, and keeps Submit off until the matching answer arrives', async () => {
    await operator();
    answers['true'] = readiness(true, false, ['ready', 'ready', 'missing', 'missing']);
    form().objective.set('Fix nginx');

    form().setWithChange(true);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(store.loadReadiness).toHaveBeenCalledWith(true);
    expect(['Discovery', 'Diagnostic', 'Remediation', 'Verification'].map(roleState)).toEqual(['ready', 'ready', 'missing', 'missing']);
    expect(text()).toContain('The selected change, target, environment and input are validated when you submit.');
    expect(form().readinessAllowsSubmit()).toBeFalse();

    form().setWithChange(false);
    await fixture.whenStable();
    expect(store.loadReadiness.calls.mostRecent().args).toEqual([false]);
    expect(form().readinessAllowsSubmit()).toBeTrue();
  });

  it('never shows an answer for the other shape as this one, and keeps Submit off while readiness loads', async () => {
    await operator();
    form().objective.set('Fix');
    store.readiness.set(answers['true']!);
    fixture.detectChanges();
    expect(form().readinessAllowsSubmit()).toBeFalse();

    store.readiness.set(answers['false']!);
    store.readinessLoading.set(true);
    fixture.detectChanges();
    expect(submit().disabled).toBeTrue();
    expect(readinessPanel().textContent).toContain('Checking the role profiles');
  });

  for (const [failure, message] of [
    ['sessionExpired', 'Session expired'],
    ['forbidden', 'lacks the viewer role'],
    ['rateLimited', 'Too many requests'],
    ['server', 'could not evaluate readiness'],
    ['unreachable', 'unreachable'],
  ] as const) {
    it(`fails closed when readiness cannot be obtained (${failure})`, async () => {
      await operator();
      form().objective.set('Look');
      store.readiness.set(null);
      store.readinessFailure.set(failure);
      fixture.detectChanges();

      expect(readinessPanel().textContent).toContain(message);
      expect(submit().disabled).toBeTrue();
      expect(readinessPanel().textContent).not.toContain('usable for this type of delegation.');
    });
  }

  it('treats an unknown reason code as not ready, even when the answer says ready', async () => {
    answers = { false: readiness(false, true, ['ready', 'ready', 'notRequired', 'notRequired'], ['ready', 'quota_exceeded', 'not_required', 'not_required']) };
    await setup(['viewer', 'operator'], []);
    await fixture.whenStable();
    form().objective.set('Look');
    fixture.detectChanges();

    expect(readinessPanel().querySelector('li[data-role="Diagnostic"]')!.textContent).toContain('Not ready');
    expect(submit().disabled).toBeTrue();
  });

  it('chooses the Skill and Capability from the catalog and generates the input form from its schema', async () => {
    await operator();
    await chooseChange();

    const params = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('[data-parameter]')).map((e) => e.getAttribute('data-parameter'));
    expect(params).toEqual(['serviceName', 'retries', 'mode', 'password']);
    expect((fixture.nativeElement as HTMLElement).querySelector('#input-password')!.getAttribute('type')).toBe('password');
    expect((fixture.nativeElement as HTMLElement).querySelector('select#input-mode')).not.toBeNull();
    expect(text()).toContain('This Capability does not support a dry run.');
    expect(text()).not.toContain('Skill' + 'Id');

    form().objective.set('Fix nginx');
    form().setValue('serviceName', 'x');
    fixture.detectChanges();
    expect(text()).toContain('serviceName needs at least 2 characters.');
    expect(form().canStart()).toBeFalse();

    form().setValue('serviceName', 'nginx');
    form().setValue('retries', '2');
    fixture.detectChanges();
    expect(form().canStart()).toBeTrue();
    expect(submit().disabled).toBeFalse();
    await form().start();
    expect(store.start.calls.mostRecent().args[0].remediation).toEqual({
      skillId: 'service.skill', capabilityName: 'service.restore', target: 'web-1', environment: 'prod', blastRadius: 'single', dryRun: false,
      input: { serviceName: 'nginx', retries: 2 },
    });
  });

  it('shows a start the runtime refuses as a submit error beside the form, and leaves the readiness panel as it was', async () => {
    await operator();
    await chooseChange();
    form().objective.set('Fix nginx');
    form().setValue('serviceName', 'nginx');
    fixture.detectChanges();
    const asked = store.loadReadiness.calls.count();
    await form().start();
    fixture.detectChanges();
    const panelBefore = readinessPanel().textContent;

    store.runs.set([run({ id: 'run-9', status: 'Denied', denial: { dimension: 'Targets', reason: 'web-1 is outside the role profile.' }, roles: [], planHash: null, approval: null, journal: [] })]);
    fixture.detectChanges();

    const error = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="submit-error"]');
    expect(error?.textContent).toContain('Not started — refused on Targets: web-1 is outside the role profile.');
    expect(readinessPanel().textContent).toBe(panelBefore);
    expect(readinessPanel().textContent).toContain('The required role profiles are usable for this type of delegation.');
    expect(readinessPanel().textContent).toContain('The selected change, target, environment and input are validated when you submit.');
    expect(store.loadReadiness.calls.count()).toBe(asked);
    expect(text()).not.toContain('executable');
  });

  it('never reports a denial after a role ran as a refused start', async () => {
    await operator();
    form().objective.set('Look');
    fixture.detectChanges();
    await form().start();
    store.runs.set([run({ id: 'run-9', status: 'Denied', denial: { dimension: 'Tools', reason: 'Later.' }, planHash: null, approval: null, journal: [] })]);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="submit-error"]')).toBeNull();
  });

  it('says a Capability without input takes none', async () => {
    await operator();
    await chooseChange('service.noop');

    expect(text()).toContain('This capability takes no input.');
  });

  it('never echoes a Sensitive value in a client-side message', async () => {
    await operator();
    await chooseChange();
    form().setValue('serviceName', 'nginx');
    form().setValue('password', 'hunter2');
    fixture.detectChanges();

    expect(text()).toContain('password needs at least 12 characters.');
    expect(text()).not.toContain('hunter2');
  });

  it('refuses a Skill catalog it could not load as a reason to prepare no change, never as an empty valid one', async () => {
    await operator();
    store.catalog.set(null);
    store.catalogError.set('bOps could not be reached.');
    form().objective.set('Fix');
    form().setWithChange(true);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(text()).toContain('The Skill catalog could not be loaded');
    expect(form().canStart()).toBeFalse();
    expect(submit().disabled).toBeTrue();
  });

  it('offers raw JSON as an advanced mode that sends exactly one object, and finds a duplicate key before parsing', async () => {
    await operator();
    await chooseChange();
    form().objective.set('Fix');
    form().setValue('serviceName', 'web');
    form().setRawMode(true);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('textarea[aria-label="Capability input as JSON"]')).not.toBeNull();

    form().rawText.set('{"serviceName":"web","serviceName":"db"}');
    fixture.detectChanges();
    expect(text()).toContain('Duplicate key: serviceName.');
    expect(submit().disabled).toBeTrue();

    form().rawText.set('{"serviceName":');
    fixture.detectChanges();
    expect(text()).toContain('Not valid JSON.');
    expect(submit().disabled).toBeTrue();

    form().rawText.set('{"serviceName":"db","mode":"safe"}');
    fixture.detectChanges();
    expect(submit().disabled).toBeFalse();
    await form().start();
    expect(store.start.calls.mostRecent().args[0].remediation.input).toEqual({ serviceName: 'db', mode: 'safe' });
  });

  it('keeps the JSON editor open on switching back when the form cannot show the object', async () => {
    await operator();
    await chooseChange();
    form().setRawMode(true);
    form().rawText.set('{"serviceName":"db","extra":1}');
    form().setRawMode(false);
    fixture.detectChanges();

    expect(text()).toContain('Unknown field: extra.');
    expect((fixture.nativeElement as HTMLElement).querySelector('textarea[aria-label="Capability input as JSON"]')).not.toBeNull();
  });

  it('shows a refused submit beside the form and leaves the readiness panel as it was', async () => {
    await operator();
    form().setWithChange(true);
    await fixture.whenStable();
    store.submitError.set('Capability input is not valid: Unknown argument \'x\'.');
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="submit-error"]')!.textContent).toContain('Not started:');
    expect(['Discovery', 'Diagnostic', 'Remediation', 'Verification'].map(roleState)).toEqual(['ready', 'ready', 'ready', 'ready']);
    expect(readinessPanel().textContent).not.toContain('Not started');
  });

  it('keeps the same key when a start was refused, so a retry cannot start a second run', async () => {
    await operator();
    store.start.and.resolveTo(undefined);
    form().objective.set('Look');

    await form().start();
    await form().start();

    expect(store.start.calls.argsFor(0)[1]).toBe(store.start.calls.argsFor(1)[1]);
  });

  // ---- typed evidence limitations (ADR-0044 §16) ----

  it('renders typed limitations per model role, and never shows "not recorded" as none', async () => {
    const discovery = {
      ...run().roles[1],
      evidenceLimitations: [
        { stepIndex: 0, toolName: 'system.events', unknownTool: false, outcome: 'Success', failureKind: 'Unspecified', completeness: 'Partial', shortenedFromCharacters: null, evidenceId: 'discovery-0' },
        { stepIndex: 1, toolName: null, unknownTool: true, outcome: 'Failure', failureKind: 'Validation', completeness: 'Unspecified', shortenedFromCharacters: null, evidenceId: null },
      ],
      evidenceLimitationsOmitted: 3,
      findings: [{ id: 'f1', summary: 'Cited partial evidence.', severity: null, evidenceIds: ['discovery-0'], restsOnLimitedEvidence: true }],
    };
    const diagnostic = { ...run().roles[1], role: 'Diagnostic', agentId: 'agent-2', evidenceLimitations: [], findingsReply: { status: 'Malformed', problem: 'DuplicateProperty', discardedFindings: 0 }, findings: [] };
    const historical = { ...run().roles[1], role: 'Discovery', agentId: 'agent-0', evidenceLimitations: null, findings: [] };
    await setup(['viewer'], [run({ roles: [discovery, diagnostic, historical] })]);
    await open();

    const blocks = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="role-limitations"]')).map((b) => b.textContent ?? '');
    expect(blocks.length).toBe(3);
    expect(blocks.join('|')).toContain('step 0: system.events — completeness Partial (evidence discovery-0)');
    expect(blocks.join('|')).toContain('step 1: (unknown tool) — outcome Failure, failure Validation (no evidence produced)');
    expect(blocks.join('|')).toContain('3 earlier limitations are not listed.');
    expect(blocks.join('|')).toContain("No recorded limitations for this role's model-loop evidence.");
    expect(blocks.join('|')).toContain('Diagnostic reply: Malformed, DuplicateProperty');
    expect(blocks.join('|')).toContain('Not recorded.');
    expect(text()).toContain('rests on limited evidence');
  });

  it('shows the plan limitations to the approver, and "unavailable" — never none — when the run could not be read', async () => {
    const waiting = run({ status: 'Running', awaitingPlanApproval: true, runningInThisHost: true, planHash: null, approval: null });
    const withLimitations: PendingPlanApproval = {
      ...waitingPlan,
      findings: [{ ...waitingPlan.findings[0], restsOnLimitedEvidence: true }],
      limitations: {
        available: true,
        roles: [
          { role: 'Discovery', recorded: true, evidenceLimitations: [{ stepIndex: 2, toolName: 'disk.read', unknownTool: false, outcome: 'Failure', failureKind: 'Environment', completeness: 'Unspecified', shortenedFromCharacters: null, evidenceId: null }], evidenceLimitationsOmitted: 0, findingsReply: null },
          { role: 'Diagnostic', recorded: false, evidenceLimitations: null, evidenceLimitationsOmitted: 0, findingsReply: null },
        ],
      },
    };
    await setup(['viewer', 'approver'], [waiting], [withLimitations]);
    await open();

    const shown = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="plan-limitations"]')!.textContent ?? '';
    expect(shown).toContain('step 2: disk.read — outcome Failure, failure Environment (no evidence produced)');
    expect(shown).toContain('Not recorded.');
    expect(text()).toContain('rests on limited evidence');
    TestBed.resetTestingModule();

    await setup(['viewer', 'approver'], [waiting], [{ ...waitingPlan, limitations: { available: false, roles: [] } }]);
    await open();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="plan-limitations"]')!.textContent).toContain('Limitations unavailable / not recorded.');
    TestBed.resetTestingModule();

    await setup(['viewer', 'approver'], [waiting], [waitingPlan]);
    await open();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="plan-limitations"]')!.textContent).toContain('Limitations unavailable / not recorded.');
    expect(text()).not.toContain('No recorded limitations');
  });

  it('shows the API error', async () => {
    await setup(['viewer'], []);
    store.error.set('The store is unavailable.');
    fixture.detectChanges();

    expect(text()).toContain('The store is unavailable.');
  });
});
