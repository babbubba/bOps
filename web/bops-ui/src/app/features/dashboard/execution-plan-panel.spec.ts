// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  AgentTaskStatus,
  ExecutionPlan,
  ExecutionPlanStep,
  ExecutionPlanStepStatus,
  TaskState,
} from '../../core/api/models';
import { I18n } from '../../core/i18n/i18n';
import { WatchConnection } from '../../core/streaming/task-events';
import { TasksStore } from '../../state/tasks.store';
import { Dashboard } from './dashboard';
import { ExecutionPlanPanel } from './execution-plan-panel';

function step(
  index: number,
  status: ExecutionPlanStepStatus,
  extra: Partial<ExecutionPlanStep> = {},
  revision = 0,
): ExecutionPlanStep {
  return {
    revision,
    index,
    objective: `objective ${index}`,
    expectedTool: 'system.events',
    status,
    current: false,
    conditional: false,
    conditionOutcome: null,
    correctionKind: null,
    ...extra,
  };
}

function plan(...revisions: ExecutionPlanStep[][]): ExecutionPlan {
  return {
    activeRevision: revisions.length - 1,
    revisions: revisions.map((steps, revision) => ({
      revision,
      active: revision === revisions.length - 1,
      steps,
    })),
  };
}

describe('ExecutionPlanPanel', () => {
  let fixture: ComponentFixture<ExecutionPlanPanel>;
  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const render = (value: ExecutionPlan | null | undefined) => {
    fixture.componentRef.setInput('plan', value);
    fixture.detectChanges();
  };
  const items = () => Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('li'));

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [ExecutionPlanPanel] }).compileComponents();
    fixture = TestBed.createComponent(ExecutionPlanPanel);
    TestBed.inject(I18n).setLanguage('en');
  });

  it('shows completed, current and pending steps with the active revision (UI-1)', () => {
    render(plan([step(0, 'Completed'), step(1, 'Running', { current: true }), step(2, 'Pending')]));

    expect(fixture.nativeElement.querySelector('h3').textContent).toContain('Execution plan');
    expect(text()).toContain('Revision 0');
    expect(items().map((li) => li.textContent)).toEqual([
      jasmine.stringContaining('Completed'),
      jasmine.stringContaining('Running'),
      jasmine.stringContaining('Pending'),
    ]);
    expect(items()[1].getAttribute('aria-current')).toBe('step');
    expect(items()[1].textContent).toContain('Current');
    expect(items()[0].getAttribute('aria-current')).toBeNull();
    expect(text()).toContain('system.events');
  });

  it('names an argument-validation correction on the same step (UI-2)', () => {
    render(
      plan([
        step(0, 'Correcting', { current: true, correctionKind: 'ArgumentValidation' }),
        step(1, 'Pending'),
      ]),
    );

    expect(items()[0].textContent).toContain('Correcting');
    expect(items()[0].textContent).toContain('Argument validation failed — correcting');
    expect(items()[1].textContent).not.toContain('correcting');
    expect(text()).not.toContain('replan');
  });

  it('names a semantic correction distinctly and keeps the step current (UI-3)', () => {
    render(plan([step(0, 'Correcting', { current: true, correctionKind: 'Semantic' }), step(1, 'Pending')]));

    expect(items()[0].textContent).toContain('Step mismatch — correcting');
    expect(items()[0].getAttribute('aria-current')).toBe('step');
    expect(items()[1].getAttribute('aria-current')).toBeNull();
  });

  it('keeps the resolved prefix, supersedes the suffix and draws the boundary (UI-4)', () => {
    render(
      plan(
        [step(0, 'Completed'), step(1, 'Superseded'), step(2, 'Superseded')],
        [step(0, 'Running', { current: true }, 1), step(1, 'Pending', {}, 1)],
      ),
    );

    expect(text()).toContain('Revision 1');
    expect(text()).toContain('Replan · Revision 0 → 1');
    const statuses = items().map((li) => li.textContent ?? '');
    expect(statuses[0]).toContain('Completed');
    expect(statuses[1]).toContain('Superseded');
    expect(statuses[2]).toContain('Superseded');
    expect(statuses[3]).toContain('Running');
    expect(fixture.nativeElement.querySelectorAll('[role="separator"]').length).toBe(1);
  });

  it('shows Activated as a qualifier and Skipped as the main status (UI-5)', () => {
    render(
      plan([
        step(0, 'Completed'),
        step(1, 'Running', { current: true, conditional: true, conditionOutcome: 'Activated' }),
        step(2, 'Skipped', { conditional: true, conditionOutcome: 'Skipped' }),
        step(3, 'Pending', { conditional: true }),
      ]),
    );

    expect(items()[1].textContent).toContain('Running');
    expect(items()[1].textContent).toContain('Conditional');
    expect(items()[1].textContent).toContain('Activated');
    expect(items()[2].textContent).toContain('Skipped');
    expect(items()[3].textContent).toContain('Conditional');
    expect(items()[3].textContent).not.toContain('Activated');
  });

  it('shows an outcome-unknown mutation step distinctly', () => {
    render(plan([step(0, 'OutcomeUnknown', { current: true })]));

    expect(items()[0].textContent).toContain('Outcome unknown');
    expect(items()[0].getAttribute('aria-current')).toBe('step');
  });

  it('translates to Italian (UI-8)', () => {
    TestBed.inject(I18n).setLanguage('it');
    render(
      plan(
        [step(0, 'Completed'), step(1, 'Superseded')],
        [step(0, 'Correcting', { current: true, correctionKind: 'ArgumentValidation' }, 1)],
      ),
    );

    expect(text()).toContain('Piano di esecuzione');
    expect(text()).toContain('Revisione 1');
    expect(text()).toContain('Ripianificazione · Revisione 0 → 1');
    expect(text()).toContain('Completato');
    expect(text()).toContain('Sostituito');
    expect(text()).toContain('Validazione argomenti non riuscita — correzione in corso');
    expect(text()).not.toContain('Execution plan');
  });

  it('has a clear empty state for no plan and for a plan without steps (UI-9)', () => {
    render(null);
    expect(text()).toContain('No execution plan yet');
    expect(items().length).toBe(0);

    render(undefined);
    expect(text()).toContain('No execution plan yet');

    render(plan([]));
    expect(text()).toContain('No further execution steps');
  });
});

describe('Dashboard execution plan panel', () => {
  const secret = 'SECRET-MARKER';
  let fixture: ComponentFixture<Dashboard>;
  let selectedTask: ReturnType<typeof signal<TaskState | null>>;

  function task(executionPlan: ExecutionPlan | null | undefined): TaskState {
    return {
      id: 'task-1',
      node: 'local',
      goal: 'Inspect the production host',
      status: 0,
      steps: [],
      plans: [],
      createdAtUtc: '2026-09-15T12:00:00Z',
      executionAttempt: 1,
      accounting: { tokensUsed: 0, lifetimeSteps: 0, lifetimeReplans: 0 },
      origin: 1,
      executing: true,
      resumable: false,
      resumeBlockedReason: null,
      executionPlan,
    };
  }

  beforeEach(async () => {
    selectedTask = signal<TaskState | null>(null);
    const store = {
      statusFilter: signal<AgentTaskStatus>(0),
      tasks: signal<TaskState[]>([]),
      selectedTaskId: signal<string | null>('task-1'),
      selectedTask,
      loading: signal(false),
      starting: signal(false),
      resuming: signal(false),
      cancelling: signal(false),
      connection: signal<WatchConnection>('connected'),
      error: signal<string | null>(null),
      selectTask: jasmine.createSpy('selectTask'),
      refresh: jasmine.createSpy('refresh'),
      setStatusFilter: jasmine.createSpy('setStatusFilter'),
    };
    await TestBed.configureTestingModule({
      imports: [Dashboard],
      providers: [{ provide: TasksStore, useValue: store }],
    }).compileComponents();
    fixture = TestBed.createComponent(Dashboard);
    TestBed.inject(I18n).setLanguage('en');
    fixture.detectChanges();
  });

  const panelText = () =>
    (fixture.nativeElement.querySelector('bops-execution-plan-panel') as HTMLElement).textContent ?? '';

  it('re-renders the same panel from a replaced snapshot with no parallel state (UI-6)', () => {
    const snapshot = task(plan([step(0, 'Running', { current: true }), step(1, 'Pending')]));
    selectedTask.set(snapshot);
    fixture.detectChanges();
    const first = panelText();

    selectedTask.set({ ...snapshot });
    fixture.detectChanges();
    expect(panelText()).toBe(first);

    selectedTask.set(
      task(plan([step(0, 'Completed'), step(1, 'Superseded')], [step(0, 'Running', { current: true }, 1)])),
    );
    fixture.detectChanges();
    expect(panelText()).toContain('Replan · Revision 0 → 1');
    expect(panelText()).toContain('Superseded');
  });

  it('never renders a forbidden field the wire object might carry (UI-7)', () => {
    const poisoned = plan([step(0, 'Running', { current: true })]);
    Object.assign(poisoned.revisions[0].steps[0], {
      expectedArguments: { path: secret },
      arguments: secret,
      output: secret,
      observation: secret,
      rationale: secret,
      activation: { factType: secret, factKey: secret },
      facts: [secret],
      requestJson: secret,
      responseJson: secret,
    });
    Object.assign(poisoned, { rationale: secret });
    selectedTask.set(task(poisoned));
    fixture.detectChanges();

    expect(panelText()).toContain('objective 0');
    expect(panelText()).not.toContain(secret);
    expect(fixture.nativeElement.querySelector('bops-execution-plan-panel').innerHTML).not.toContain(secret);
  });

  it('shows the empty state for a task with no or an older-server plan (UI-9)', () => {
    selectedTask.set(task(null));
    fixture.detectChanges();
    expect(panelText()).toContain('No execution plan yet');

    selectedTask.set(task(undefined));
    fixture.detectChanges();
    expect(panelText()).toContain('No execution plan yet');
  });
});
