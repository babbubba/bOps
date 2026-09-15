// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TaskState } from '../../core/api/models';
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
    tasks: ReturnType<typeof signal<TaskState[]>>;
    selectedTaskId: ReturnType<typeof signal<string | null>>;
    selectedTask: ReturnType<typeof signal<TaskState | null>>;
    loading: ReturnType<typeof signal<boolean>>;
    starting: ReturnType<typeof signal<boolean>>;
    error: ReturnType<typeof signal<string | null>>;
    start: jasmine.Spy;
    selectTask: jasmine.Spy;
    resume: jasmine.Spy;
  };

  beforeEach(async () => {
    store = {
      tasks: signal([task()]),
      selectedTaskId: signal<string | null>(null),
      selectedTask: signal<TaskState | null>(null),
      loading: signal(false),
      starting: signal(false),
      error: signal<string | null>(null),
      start: jasmine.createSpy('start').and.resolveTo(),
      selectTask: jasmine.createSpy('selectTask'),
      resume: jasmine.createSpy('resume').and.resolveTo(),
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
});
