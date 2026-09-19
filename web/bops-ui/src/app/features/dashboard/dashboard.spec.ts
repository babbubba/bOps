// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AgentTaskStatus, TaskState } from '../../core/api/models';
import { TasksStore } from '../../state/tasks.store';
import { Dashboard } from './dashboard';

function task(status: TaskState['status'] = 0): TaskState {
  return {
    id: 'task-1',
    node: 'local',
    goal: 'Inspect the production host',
    status,
    steps: [],
    plans: [],
    createdAtUtc: '2026-09-15T12:00:00Z',
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
    error: ReturnType<typeof signal<string | null>>;
    start: jasmine.Spy;
    selectTask: jasmine.Spy;
    resume: jasmine.Spy;
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
      error: signal<string | null>(null),
      start: jasmine.createSpy('start').and.resolveTo(),
      selectTask: jasmine.createSpy('selectTask'),
      resume: jasmine.createSpy('resume').and.resolveTo(),
      refresh: jasmine.createSpy('refresh').and.resolveTo(),
      setStatusFilter: jasmine.createSpy('setStatusFilter').and.resolveTo(),
    };

    await TestBed.configureTestingModule({
      imports: [Dashboard],
      providers: [{ provide: TasksStore, useValue: store }],
    }).compileComponents();

    fixture = TestBed.createComponent(Dashboard);
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

  it('renders tasks, selects one and offers resume for a non-terminal success status', async () => {
    store.selectedTaskId.set('task-1');
    store.selectedTask.set(task(4));
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

      expect(fixture.nativeElement.querySelectorAll('dl').length).toBe(1);
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