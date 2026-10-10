// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AgentTaskStatus, TaskState } from '../../core/api/models';
import { I18n } from '../../core/i18n/i18n';
import { WatchConnection } from '../../core/streaming/task-events';
import { TasksStore } from '../../state/tasks.store';
import { Dashboard } from './dashboard';

function task(status: TaskState['status'] = 0, overrides: Partial<TaskState> = {}): TaskState {
  return {
    id: 'task-1',
    node: 'local',
    goal: 'Inspect the production host',
    status,
    steps: [],
    plans: [],
    createdAtUtc: '2026-09-15T12:00:00Z',
    executionAttempt: 1,
    accounting: { tokensUsed: 0, lifetimeSteps: 0, lifetimeReplans: 0 },
    origin: 1,
    executing: status === 0,
    resumable: false,
    resumeBlockedReason: null,
    ...overrides,
  };
}

describe('Dashboard', () => {
  let fixture: ComponentFixture<Dashboard>;
  let store: {
    statusFilter: ReturnType<typeof signal<AgentTaskStatus>>;
    tasks: ReturnType<typeof signal<TaskState[]>>;
    selectedTaskId: ReturnType<typeof signal<string | null>>;
    selectedTask: ReturnType<typeof signal<TaskState | null>>;
    loading: ReturnType<typeof signal<boolean>>;
    starting: ReturnType<typeof signal<boolean>>;
    resuming: ReturnType<typeof signal<boolean>>;
    cancelling: ReturnType<typeof signal<boolean>>;
    mutatingJournal: ReturnType<typeof signal<boolean>>;
    connection: ReturnType<typeof signal<WatchConnection>>;
    error: ReturnType<typeof signal<string | null>>;
    start: jasmine.Spy;
    selectTask: jasmine.Spy;
    resume: jasmine.Spy;
    cancel: jasmine.Spy;
    recover: jasmine.Spy;
    reconcile: jasmine.Spy;
    isAdministrator: jasmine.Spy;
    refresh: jasmine.Spy;
    setStatusFilter: jasmine.Spy;
  };

  beforeEach(async () => {
    store = {
      statusFilter: signal<AgentTaskStatus>(0),
      tasks: signal([task()]),
      selectedTaskId: signal<string | null>(null),
      selectedTask: signal<TaskState | null>(null),
      loading: signal(false),
      starting: signal(false),
      resuming: signal(false),
      cancelling: signal(false),
      mutatingJournal: signal(false),
      connection: signal<WatchConnection>('connected'),
      error: signal<string | null>(null),
      start: jasmine.createSpy('start').and.resolveTo(),
      selectTask: jasmine.createSpy('selectTask'),
      resume: jasmine.createSpy('resume').and.resolveTo(),
      cancel: jasmine.createSpy('cancel').and.resolveTo(),
      recover: jasmine.createSpy('recover').and.resolveTo(),
      reconcile: jasmine.createSpy('reconcile').and.resolveTo(),
      isAdministrator: jasmine.createSpy('isAdministrator').and.returnValue(true),
      refresh: jasmine.createSpy('refresh').and.resolveTo(),
      setStatusFilter: jasmine.createSpy('setStatusFilter').and.resolveTo(),
    };

    await TestBed.configureTestingModule({
      imports: [Dashboard],
      providers: [{ provide: TasksStore, useValue: store }],
    }).compileComponents();

    fixture = TestBed.createComponent(Dashboard);
    TestBed.inject(I18n).setLanguage('en');
    fixture.detectChanges();
  });

  it('starts a trimmed goal and clears the input', async () => {
    const input = fixture.nativeElement.querySelector('#goal') as HTMLInputElement;
    input.value = '  inspect disks  ';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    const startButton = Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    ).find((button) => button.textContent?.includes('Start task'));
    startButton?.click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(store.start).toHaveBeenCalledOnceWith('inspect disks');
    const component = fixture.componentInstance as unknown as { goal: () => string };
    expect(component.goal()).toBe('');
  });

  it('renders tasks, selects one and offers resume when the server says it is resumable', async () => {
    store.selectedTaskId.set('task-1');
    store.selectedTask.set(task(6, { resumable: true }));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Inspect the production host');
    const buttons = Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    );
    const taskButton = buttons.find((button) => button.textContent?.includes('Inspect the production host'));
    const resumeButton = buttons.find((button) => button.textContent?.includes('Resume this task'));

    taskButton?.click();
    resumeButton?.click();
    await fixture.whenStable();

    expect(store.selectTask).toHaveBeenCalledOnceWith('task-1');
    expect(store.resume).toHaveBeenCalledOnceWith('task-1');
  });

  it('defaults to the live Running view without a refresh button', () => {
    expect(fixture.nativeElement.textContent).toContain('Running tasks');
    expect(fixture.nativeElement.textContent).not.toContain('Refresh');
    const select = fixture.nativeElement.querySelector('select') as HTMLSelectElement;
    expect(select.value).toBe('0');
    expect(select.options.length).toBe(8);
  });

  it('asks the store for another status when the filter changes', () => {
    const select = fixture.nativeElement.querySelector('select') as HTMLSelectElement;
    select.value = '1';
    select.dispatchEvent(new Event('change'));

    expect(store.setStatusFilter).toHaveBeenCalledOnceWith(1);
  });

  it('shows finished tasks newest first, capped, with a refresh button', () => {
    const finished = Array.from({ length: 55 }, (_, index) => ({
      ...task(1),
      id: `t-${index}`,
      goal: `Finished ${index}`,
      createdAtUtc: new Date(Date.UTC(2026, 8, 15, 12, index)).toISOString(),
    }));
    store.statusFilter.set(1);
    store.tasks.set(finished);
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Task history');
    expect(text).toContain('Refresh');
    expect(text).toContain('Finished 54');
    expect(text).not.toContain('Finished 4 ');
    expect(text).toContain('Showing the latest 50 of 55.');
    const items = fixture.nativeElement.querySelectorAll('ul li');
    expect(items.length).toBe(50);
    expect(items[0].textContent).toContain('Finished 54');
  });
  describe('task lifecycle', () => {
    const text = (): string => fixture.nativeElement.textContent as string;
    const buttonWith = (label: string): HTMLButtonElement | undefined =>
      Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>).find((button) =>
        button.textContent?.includes(label),
      );

    function select(state: TaskState): void {
      store.selectedTaskId.set(state.id);
      store.selectedTask.set(state);
      fixture.detectChanges();
    }

    describe('actions come from the server fields, never from the status', () => {
      it('offers Resume only when the task is resumable', () => {
        select(task(6, { resumable: true, executing: false }));
        expect(buttonWith('Resume this task')).toBeDefined();
      });

      it('offers no Resume for any status when the server says it is not resumable', () => {
        for (const status of [0, 1, 2, 3, 4, 5, 6, 7] as const) {
          select(task(status, { resumable: false, executing: false }));
          expect(buttonWith('Resume this task')).withContext(`status ${status}`).toBeUndefined();
        }
      });

      it('offers Resume for a status the old rules hid when the server says it is resumable', () => {
        select(task(1, { resumable: true, executing: false }));
        expect(buttonWith('Resume this task')).toBeDefined();
        select(task(0, { resumable: true, executing: false }));
        expect(buttonWith('Resume this task')).toBeDefined();
      });

      it('offers Cancel only when the task is executing, whatever its status', () => {
        select(task(0, { executing: true }));
        expect(buttonWith('Cancel task')).toBeDefined();
        select(task(0, { executing: false }));
        expect(buttonWith('Cancel task')).toBeUndefined();
        select(task(6, { executing: false, resumable: true }));
        expect(buttonWith('Cancel task')).toBeUndefined();
        select(task(6, { executing: true }));
        expect(buttonWith('Cancel task')).toBeDefined();
      });

      it('asks the store to cancel the selected task', () => {
        select(task(0, { executing: true }));
        buttonWith('Cancel task')!.click();
        expect(store.cancel).toHaveBeenCalledOnceWith('task-1');
      });

      it('disables the buttons while their request is in flight, and says so', () => {
        select(task(6, { resumable: true, executing: true }));
        store.resuming.set(true);
        store.cancelling.set(true);
        fixture.detectChanges();

        const resume = buttonWith('Resuming…')!;
        const cancel = buttonWith('Cancelling…')!;
        expect(resume.disabled).toBeTrue();
        expect(cancel.disabled).toBeTrue();
        resume.click();
        cancel.click();
        expect(store.resume).not.toHaveBeenCalled();
        expect(store.cancel).not.toHaveBeenCalled();
      });

      it('leaves the buttons enabled when nothing is in flight', () => {
        select(task(6, { resumable: true, executing: true }));
        expect(buttonWith('Resume this task')!.disabled).toBeFalse();
        expect(buttonWith('Cancel task')!.disabled).toBeFalse();
      });
    });

    describe('why a task cannot be resumed', () => {
      const blocked = (code: string) =>
        task(6, { resumable: false, executing: false, resumeBlockedReason: { code, message: 'server text' } });

      it('shows the reason in words for a task that is refused', () => {
        const cases: [string, string][] = [
          ['task_completed', 'Start a new task'],
          ['task_policy_blocked', 'does not bypass policy'],
          ['task_delegated', 'Resume the delegation run'],
          ['lifetime_steps_exhausted', 'over its lifetime'],
          ['lifetime_replans_exhausted', 'over its lifetime'],
          ['token_budget_exhausted', 'higher configured cap'],
        ];
        for (const [code, phrase] of cases) {
          select(blocked(code));
          expect(text()).withContext(code).toContain(phrase);
          expect(buttonWith('Resume this task')).withContext(code).toBeUndefined();
        }
      });

      it("shows the server's own message for a code it does not know", () => {
        select(blocked('something_new'));
        expect(text()).toContain('server text');
      });

      it('shows nothing where the server gave no reason', () => {
        select(task(1, { resumable: false, executing: false, resumeBlockedReason: null }));
        expect(fixture.nativeElement.querySelector('[role="note"]')).toBeNull();
      });

      it('says a running task is running and can be cancelled, where an executor holds it', () => {
        select(task(0, { executing: true, resumeBlockedReason: { code: 'task_running', message: 'running' } }));
        expect(text()).toContain('You can cancel it while it runs');
      });

      it('says an interrupted task is not resumable and what to do, where no executor holds it', () => {
        select(task(0, { executing: false, resumeBlockedReason: { code: 'task_running', message: 'running' } }));
        expect(text()).toContain('not executing this task');
        expect(text()).toContain('start a new task');
      });

      it('has the reason in Italian', () => {
        TestBed.inject(I18n).setLanguage('it');
        select(blocked('task_completed'));
        expect(text()).toContain('Avvia un nuovo task');
      });
    });

    describe('Interrupted', () => {
      it('labels a Running task that nothing is executing as Interrupted, in the detail and in the list', () => {
        store.tasks.set([task(0, { executing: false })]);
        select(task(0, { executing: false }));
        const badges = Array.from(fixture.nativeElement.querySelectorAll('bops-status-badge') as NodeListOf<HTMLElement>);
        expect(badges.length).toBe(2);
        for (const badge of badges) {
          expect(badge.textContent).toContain('Interrupted');
          expect(badge.textContent).not.toContain('Running');
          expect(badge.querySelector('.animate-pulse')).toBeNull();
        }
      });

      it('keeps Running for a task that is being executed', () => {
        store.tasks.set([task(0, { executing: true })]);
        select(task(0, { executing: true }));
        const badges = Array.from(fixture.nativeElement.querySelectorAll('bops-status-badge') as NodeListOf<HTMLElement>);
        for (const badge of badges) {
          expect(badge.textContent).toContain('Running');
          expect(badge.textContent).not.toContain('Interrupted');
          expect(badge.querySelector('.animate-pulse')).not.toBeNull();
        }
      });

      it('is not a label of any other status', () => {
        select(task(6, { executing: false }));
        expect(text()).not.toContain('Interrupted');
      });

      it('says Interrupted in Italian', () => {
        TestBed.inject(I18n).setLanguage('it');
        store.tasks.set([task(0, { executing: false })]);
        select(task(0, { executing: false }));
        const badges = Array.from(fixture.nativeElement.querySelectorAll('bops-status-badge') as NodeListOf<HTMLElement>);
        expect(badges.length).toBe(2);
        for (const badge of badges) {
          expect(badge.textContent).toContain('Interrotto');
          expect(badge.textContent).not.toContain('In esecuzione');
        }
      });
    });

    describe('durable mutation journal', () => {
      const journalEntry = {
        executionAttempt: 2,
        stepIndex: 4,
        sequence: 1,
        tool: 'system.setting.apply',
        risk: 'High',
        argumentsFingerprint: '0123456789ab',
        intentAtUtc: '2026-10-10T10:00:00Z',
        plannedStepIndex: 4,
        planRevision: 0,
        outcome: null,
        state: 'Pending' as const,
        knowledge: 'Unknown' as const,
        reconciliation: null,
      };

      it('shows recovery-required state and invokes the administrator recovery action', () => {
        spyOn(window, 'confirm').and.returnValue(true);
        select(task(0, {
          executionAttempt: 2,
          executing: false,
          recoverable: true,
          mutationJournal: { mode: 'Journaled', available: true, unsettledCount: 1, entries: [journalEntry] },
        }));

        expect(text()).toContain('Outcome unknown');
        expect(text()).toContain('Recovery required');
        expect(text()).toContain('Unknown');
        expect(text()).not.toContain('Retry mutation');
        buttonWith('Recover')!.click();

        expect(store.recover).toHaveBeenCalledOnceWith('task-1', 2);
      });

      it('shows reconciliation-required state and invokes all three distinct administrator actions', () => {
        spyOn(window, 'confirm').and.returnValue(true);
        select(task(6, {
          executing: false,
          mutationJournal: { mode: 'Journaled', available: true, unsettledCount: 1, entries: [journalEntry] },
        }));

        expect(text()).toContain('Reconciliation required');
        buttonWith('Verify again')!.click();
        buttonWith('Accept as already applied')!.click();
        buttonWith('Abandon task')!.click();

        expect(store.reconcile.calls.allArgs()).toEqual([
          ['task-1', 'verify'],
          ['task-1', 'acceptDone'],
          ['task-1', 'abandon'],
        ]);
      });

      it('labels verified, accepted and abandoned outcomes distinctly', () => {
        const cases = [
          [{ ...journalEntry, state: 'Settled' as const, knowledge: 'KnownExecuted' as const,
            reconciliation: { action: 'VerifiedDone', verification: 'done', resolvedBy: 'admin', atUtc: '2026-10-10T10:01:00Z' } }, 'Verified done'],
          [{ ...journalEntry, state: 'ReconciledDone' as const, knowledge: 'KnownExecuted' as const,
            reconciliation: { action: 'OperatorAcceptedDone', verification: null, resolvedBy: 'admin', atUtc: '2026-10-10T10:01:00Z' } }, 'Administrator accepted as already applied'],
          [{ ...journalEntry, state: 'Abandoned' as const }, 'Abandoned'],
        ] as const;

        for (const [entry, label] of cases) {
          select(task(6, {
            executing: false,
            mutationJournal: { mode: 'Journaled', available: true, unsettledCount: 0, entries: [entry] },
          }));
          expect(text()).withContext(label).toContain(label);
        }
      });
    });

    describe('connection', () => {
      it('says Reconnecting… while the API is not answering', () => {
        select(task(0));
        expect(text()).not.toContain('Reconnecting');
        store.connection.set('reconnecting');
        fixture.detectChanges();
        expect(text()).toContain('Reconnecting…');
        expect(fixture.nativeElement.querySelector('[role="status"]')).not.toBeNull();
      });

      it('says the session expired and to sign in again', () => {
        select(task(0));
        store.connection.set('sessionExpired');
        fixture.detectChanges();
        expect(text()).toContain('Session expired');
        expect(text()).toContain('sign in again');
        expect(text()).not.toContain('Reconnecting');
      });

      it('has both states in Italian', () => {
        TestBed.inject(I18n).setLanguage('it');
        select(task(0));
        store.connection.set('reconnecting');
        fixture.detectChanges();
        expect(text()).toContain('Riconnessione in corso…');
        store.connection.set('sessionExpired');
        fixture.detectChanges();
        expect(text()).toContain('Sessione scaduta');
      });
    });

    describe('what the server recorded about the task', () => {
      const detail = task(6, {
        executionAttempt: 3,
        executing: false,
        accounting: { tokensUsed: 12345, lifetimeSteps: 17, lifetimeReplans: 2 },
        terminalReason: { kind: 2 },
      });

      it('shows the execution attempt, the tokens used and the terminal reason', () => {
        select(detail);
        const facts = fixture.nativeElement.querySelector('dl[aria-label="Task lifecycle"]') as HTMLElement;
        expect(facts.textContent).toContain('Attempt');
        expect(facts.textContent).toContain('3');
        expect(facts.textContent).toContain('Tokens used');
        expect(facts.textContent).toContain('12,345');
        expect(facts.textContent).toContain('Lifetime steps');
        expect(facts.textContent).toContain('17');
        expect(facts.textContent).toContain('Lifetime replans');
        expect(facts.textContent).toContain('Ended because');
        expect(facts.textContent).toContain('Lifetime step limit reached');
      });

      it('shows no terminal reason while the task runs', () => {
        select(task(0, { terminalReason: null }));
        expect(text()).not.toContain('Ended because');
      });

      it('shows the same facts in Italian, with numbers formatted for the language', () => {
        TestBed.inject(I18n).setLanguage('it');
        select({ ...detail, accounting: { ...detail.accounting, tokensUsed: 1234567 } });
        expect(text()).toContain('Tentativo');
        expect(text()).toContain('Token usati');
        expect(text()).toContain('1.234.567');
        expect(text()).toContain('Terminato perché');
        expect(text()).toContain('Limite di passi complessivo raggiunto');
      });

      it('guides the operator after a provider failure, with the sanitized reason the runtime recorded', () => {
        const call = {
          provider: 'OpenRouter',
          requestedModel: 'm',
          actualModel: null,
          startedAtUtc: '2026-09-19T19:05:53Z',
          durationMs: 10,
          outcome: 1,
          usage: null,
          finishReason: null,
          errorMessage: 'HTTP 401: invalid key',
          payloadTruncated: false,
          failureKind: 5 as const,
        };
        select(
          task(6, {
            executing: false,
            terminalReason: { kind: 8, failureKind: 5 },
            steps: [
              { index: 0, description: 'Model protocol failure', toolCall: null, result: null, observation: null, planRevision: null, modelCalls: [call] },
            ],
          }),
        );
        expect(text()).toContain('provider key in Settings');
        expect(text()).toContain('Provider reason: HTTP 401: invalid key');
      });

      it('gives no provider guidance for a failure that was not the provider’s', () => {
        select(task(6, { executing: false, terminalReason: { kind: 10 } }));
        expect(fixture.nativeElement.querySelector('[role="note"]')).toBeNull();
        expect(text()).toContain('Unexpected runtime failure');
      });
    });
  });

  describe('model call details', () => {
    const modelCall = {
      provider: 'OpenRouter',
      requestedModel: 'openrouter/free',
      actualModel: 'vendor/picked-model',
      startedAtUtc: '2026-09-19T19:05:53Z',
      durationMs: 4321,
      outcome: 0,
      usage: { promptTokens: 10116, completionTokens: 788, estimatedCostUsd: null },
      finishReason: 'stop',
      errorMessage: null,
      payloadTruncated: false,
    };

    function withSteps(steps: TaskState['steps']): void {
      store.selectedTaskId.set('task-1');
      store.selectedTask.set({ ...task(1), steps });
      fixture.detectChanges();
    }

    const toolStep = {
      index: 0,
      description: 'system.cpu',
      toolCall: { id: 'c1', toolName: 'system.cpu', arguments: {} },
      result: { outcome: 0, output: '11%', errorMessage: null, succeeded: true },
      observation: '11%',
      planRevision: 0,
      modelCalls: [modelCall],
    };

    function infoButton(): HTMLButtonElement | null {
      return fixture.nativeElement.querySelector('button[aria-label="Model call details"]');
    }

    it('offers a "?" on a step that has a model call, before its OK', () => {
      withSteps([toolStep]);

      const button = infoButton();
      expect(button).not.toBeNull();
      const row = button!.parentElement as HTMLElement;
      expect(row.textContent!.indexOf('?')).toBeLessThan(row.textContent!.indexOf('OK'));
    });

    it('offers nothing on a step stored before model calls were kept', () => {
      withSteps([{ ...toolStep, modelCalls: undefined }]);

      expect(infoButton()).toBeNull();
    });

    it('keeps the details closed until the "?" is clicked', () => {
      withSteps([toolStep]);

      expect(fixture.nativeElement.textContent).not.toContain('Response time');
      expect(infoButton()!.getAttribute('aria-expanded')).toBe('false');

      infoButton()!.click();
      fixture.detectChanges();

      expect(infoButton()!.getAttribute('aria-expanded')).toBe('true');
      const text = fixture.nativeElement.textContent as string;
      expect(text).toContain('openrouter/free');
      expect(text).toContain('vendor/picked-model');
      expect(text).toContain('4.3 s');
      expect(text).toContain('10,116 in / 788 out');
      expect(text).toContain('stop');
    });

    it('closes them again on a second click', () => {
      withSteps([toolStep]);

      infoButton()!.click();
      fixture.detectChanges();
      infoButton()!.click();
      fixture.detectChanges();

      expect(fixture.nativeElement.textContent).not.toContain('Response time');
    });

    it('opens one step at a time, not every step', () => {
      withSteps([toolStep, { ...toolStep, index: 1 }]);

      (fixture.nativeElement.querySelectorAll('button[aria-label="Model call details"]')[1] as HTMLButtonElement).click();
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelectorAll('[id^="model-info-"] dl').length).toBe(1);
    });

    it('says the tokens were not reported when the provider gave none', () => {
      withSteps([{ ...toolStep, modelCalls: [{ ...modelCall, usage: null }] }]);

      infoButton()!.click();
      fixture.detectChanges();

      expect(fixture.nativeElement.textContent).toContain('not reported');
    });

    it('lists every call of a step that had to ask the model again, and why one failed', () => {
      withSteps([
        {
          ...toolStep,
          modelCalls: [modelCall, { ...modelCall, outcome: 1, errorMessage: 'HTTP 500', usage: null }],
        },
      ]);

      infoButton()!.click();
      fixture.detectChanges();

      const text = fixture.nativeElement.textContent as string;
      expect(text).toContain('Call 1 of 2');
      expect(text).toContain('Call 2 of 2');
      expect(text).toContain('HTTP 500');
    });

    it('shows a single model name when the provider answered with the one requested', () => {
      withSteps([{ ...toolStep, modelCalls: [{ ...modelCall, actualModel: 'openrouter/free' }] }]);

      infoButton()!.click();
      fixture.detectChanges();

      expect(fixture.nativeElement.textContent).not.toContain('→');
    });
  });
});
