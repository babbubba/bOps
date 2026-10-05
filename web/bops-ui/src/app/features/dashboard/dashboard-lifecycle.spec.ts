// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse, HttpHeaders, HttpRequest, HttpResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { signal } from '@angular/core';
import { ComponentFixture, discardPeriodicTasks, fakeAsync, TestBed, tick } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { AgentTaskStatus, AgentTaskStatusName, PlanStep, TaskErrorResponse, TaskState, TaskTerminalKind } from '../../core/api/models';
import { I18n } from '../../core/i18n/i18n';
import { AuthService } from '../../core/auth/auth.service';
import { TASK_POLL_INTERVAL_MS } from '../../core/streaming/task-events';
import { TasksStore } from '../../state/tasks.store';
import { Dashboard } from './dashboard';

/**
 * The Dashboard end to end against a fake bOps API: the real component, store, API client and watcher, with only the HTTP
 * transport replaced by a scripted backend. It stands in for a browser run against a fake server (E2E-6, E2E-7, E2E-8 reconnect):
 * what the fake answers is what ADR-0040 says the real API answers, and the assertions are on what the operator sees.
 */
class FakeBackend {
  readonly requests: string[] = [];
  private readonly tasks = new Map<string, TaskState>();
  private preResume: TaskState | null = null;

  /** While set, every call fails as a restarting API or its proxy would. */
  outage: { status: number; retryAfter?: number } | null = null;
  /** How many GETs after a resume still return the snapshot from before it: what a slow first step used to look like. */
  staleReads = 0;

  add(task: TaskState): void {
    this.tasks.set(task.id, task);
  }

  count(method: string, pathEnd: string): number {
    return this.requests.filter((request) => request.startsWith(`${method} `) && request.endsWith(pathEnd)).length;
  }

  /** The executing host takes one more step. */
  advance(id: string): void {
    const task = this.tasks.get(id)!;
    const index = task.steps.length;
    this.tasks.set(id, {
      ...task,
      steps: [...task.steps, { ...step(index), executionAttempt: task.executionAttempt } as PlanStep],
      accounting: { ...task.accounting, tokensUsed: task.accounting.tokensUsed + 500, lifetimeSteps: task.accounting.lifetimeSteps + 1 },
    });
  }

  finish(id: string, status: AgentTaskStatus, kind: TaskTerminalKind): void {
    this.tasks.set(id, { ...this.tasks.get(id)!, status, executing: false, resumable: false, terminalReason: { kind } });
  }

  handle(request: HttpRequest<unknown>): Observable<HttpResponse<unknown>> {
    this.requests.push(`${request.method} ${request.url}`);
    if (this.outage) {
      const headers = new HttpHeaders(this.outage.retryAfter ? { 'Retry-After': String(this.outage.retryAfter) } : {});
      return throwError(() => new HttpErrorResponse({ status: this.outage!.status, url: request.url, headers }));
    }

    const byStatus = /\/api\/agents\/tasks\?status=(\w+)$/.exec(request.url);
    if (request.method === 'GET' && byStatus) {
      const wanted = Object.entries(AgentTaskStatusName).find(([, name]) => name === byStatus[1])![0];
      return ok([...this.tasks.values()].filter((task) => String(task.status) === wanted));
    }

    const one = /\/api\/agents\/tasks\/([\w-]+)(\/resume)?$/.exec(request.url);
    const task = one ? this.tasks.get(one[1]) : undefined;
    if (!one || !task) {
      return throwError(() => new HttpErrorResponse({ status: 404, url: request.url, error: { message: 'No stored task.' } }));
    }

    if (request.method === 'GET') {
      if (this.staleReads > 0 && this.preResume) {
        this.staleReads--;
        return ok(this.preResume);
      }

      return ok(task);
    }

    if (request.method === 'POST') {
      if (!task.resumable) {
        const reason = task.resumeBlockedReason!;
        return throwError(() => new HttpErrorResponse({ status: 409, url: request.url, error: reason }));
      }

      this.preResume = task;
      const resumed: TaskState = {
        ...task,
        status: 0,
        executionAttempt: task.executionAttempt + 1,
        executing: true,
        resumable: false,
        resumeBlockedReason: { code: 'task_running', message: 'running' },
        terminalReason: null,
      };
      this.tasks.set(task.id, resumed);
      return ok(
        {
          taskId: task.id,
          status: resumed.status,
          executionAttempt: resumed.executionAttempt,
          executing: true,
          resumable: false,
          resumeBlockedReason: resumed.resumeBlockedReason,
        },
        202,
      );
    }

    // DELETE: the host accepts the cancellation and the task stops a moment later.
    return ok(null, 202);
  }
}

function ok(body: unknown, status = 200): Observable<HttpResponse<unknown>> {
  return of(new HttpResponse({ status, body: structuredClone(body) }));
}

function step(index: number): PlanStep {
  return {
    index,
    description: `step ${index} description`,
    toolCall: { id: `c${index}`, toolName: 'system.cpu', arguments: {} },
    result: { outcome: 0, output: '11%', errorMessage: null, succeeded: true },
    observation: '11%',
    planRevision: 0,
  };
}

function task(id: string, status: TaskState['status'], overrides: Partial<TaskState> = {}): TaskState {
  return {
    id,
    node: 'local',
    goal: `Goal of ${id}`,
    status,
    steps: [step(0)],
    plans: [],
    createdAtUtc: '2026-09-15T12:00:00Z',
    executionAttempt: 1,
    accounting: { tokensUsed: 1000, lifetimeSteps: 1, lifetimeReplans: 0 },
    origin: 1,
    executing: false,
    resumable: false,
    resumeBlockedReason: null,
    ...overrides,
  };
}

const blocked = (code: string): TaskErrorResponse => ({ code, message: `server says ${code}` });

describe('Dashboard task lifecycle against a fake API', () => {
  let backend: FakeBackend;
  let fixture: ComponentFixture<Dashboard>;
  let store: InstanceType<typeof TasksStore>;

  beforeEach(() => {
    backend = new FakeBackend();
    TestBed.configureTestingModule({
      imports: [Dashboard],
      providers: [
        provideHttpClient(withInterceptors([(request) => backend.handle(request)])),
        { provide: AuthService, useValue: { status: signal('authenticated'), authenticated: signal(true), signingOut: signal(false), expireSession: () => undefined } },
      ],
    });
  });

  const text = (): string => fixture.nativeElement.textContent as string;
  /** The status badge of the detail panel: the list and the status filter have their own labels. */
  const detailBadge = (): string => (fixture.nativeElement.querySelector('section:last-child bops-status-badge') as HTMLElement).textContent!;
  const buttonWith = (label: string): HTMLButtonElement | undefined =>
    Array.from(fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>).find((button) =>
      button.textContent?.includes(label),
    );

  /** Opens the Dashboard on the history of a status and clicks the task, as an operator would. */
  function open(statusName: string, goal: string): void {
    fixture = TestBed.createComponent(Dashboard);
    store = TestBed.inject(TasksStore);
    fixture.detectChanges();
    tick();
    const status = Number(Object.entries(AgentTaskStatusName).find(([, name]) => name === statusName)![0]) as AgentTaskStatus;
    void store.setStatusFilter(status);
    tick();
    fixture.detectChanges();
    buttonWith(goal)!.click();
    tick();
    fixture.detectChanges();
  }

  function leave(): void {
    store.clearSelection();
    discardPeriodicTasks();
  }

  describe('E2E-6: a failed task is resumed and followed to completion', () => {
    it('shows Running under the new attempt at once, survives a stale snapshot, and ends Completed without a refresh', fakeAsync(() => {
      backend.add(task('task-1', 6, { resumable: true, terminalReason: { kind: 8, failureKind: 1 }, steps: [step(0)] }));
      open('Failed', 'Goal of task-1');
      expect(text()).toContain('Failed');
      expect(text()).toContain('Model provider failure');
      expect(buttonWith('Resume this task')).toBeDefined();

      backend.staleReads = 3;
      buttonWith('Resume this task')!.click();
      tick();
      fixture.detectChanges();

      expect(backend.count('POST', '/task-1/resume')).toBe(1);
      const badge = detailBadge();
      expect(badge).toContain('Running');
      expect((fixture.nativeElement.querySelector('dl[aria-label="Task lifecycle"]') as HTMLElement).textContent).toContain('Attempt2');
      expect(buttonWith('Resume this task')).toBeUndefined();
      expect(buttonWith('Cancel task')).toBeDefined();
      expect(text()).toContain('Step 1');
      expect(text()).not.toContain('Ended because');

      for (let poll = 0; poll < 3; poll++) {
        tick(TASK_POLL_INTERVAL_MS);
        fixture.detectChanges();
        expect(text()).withContext(`stale read ${poll}`).not.toContain('Model provider failure');
        expect(buttonWith('Cancel task')).withContext(`stale read ${poll}`).toBeDefined();
      }

      backend.advance('task-1');
      tick(TASK_POLL_INTERVAL_MS);
      fixture.detectChanges();
      expect(text()).toContain('Step 2');
      expect(text()).toContain('1,500');

      backend.advance('task-1');
      backend.finish('task-1', 1, 0);
      tick(TASK_POLL_INTERVAL_MS);
      fixture.detectChanges();
      expect(text()).toContain('Step 3');
      expect(detailBadge()).toContain('Completed');
      expect(text()).toContain('Ended because');
      expect(buttonWith('Cancel task')).toBeUndefined();
      expect(buttonWith('Resume this task')).toBeUndefined();

      const requests = backend.requests.length;
      tick(TASK_POLL_INTERVAL_MS * 10);
      expect(backend.requests.length).toBe(requests);
      leave();
    }));

    it('sends one resume when Resume is clicked twice in a row', fakeAsync(() => {
      backend.add(task('task-1', 6, { resumable: true }));
      open('Failed', 'Goal of task-1');

      const resume = buttonWith('Resume this task')!;
      resume.click();
      resume.click();
      tick();

      expect(backend.count('POST', '/task-1/resume')).toBe(1);
      leave();
    }));

    it('says the task is already running, not that the API is unreachable, when another resume won', fakeAsync(() => {
      backend.add(task('task-1', 6, { resumable: true }));
      open('Failed', 'Goal of task-1');
      expect(buttonWith('Resume this task')).toBeDefined();

      // Someone else resumed it after this screen loaded: the server now has it running, and refuses a second resume with its code.
      backend.add(task('task-1', 0, { executionAttempt: 2, executing: true, resumeBlockedReason: blocked('task_running') }));
      buttonWith('Resume this task')!.click();
      tick();
      fixture.detectChanges();

      expect(text()).toContain('already running');
      expect(text()).not.toContain('Cannot reach');
      expect(detailBadge()).toContain('Running');
      expect(buttonWith('Cancel task')).toBeDefined();
      leave();
    }));
  });

  describe('E2E-7: Resume is offered only where the server says it is possible', () => {
    it('hides Resume and shows the reason for every task the server refuses', fakeAsync(() => {
      const refused: [string, TaskState['status'], TaskErrorResponse, string][] = [
        ['completed', 1, blocked('task_completed'), 'Start a new task'],
        ['policy', 4, blocked('task_policy_blocked'), 'does not bypass policy'],
        ['lifetime', 2, blocked('lifetime_steps_exhausted'), 'over its lifetime'],
        ['role', 6, blocked('task_delegated'), 'Resume the delegation run'],
      ];
      for (const [id, status, reason] of refused) {
        backend.add(task(id, status, { resumeBlockedReason: reason, origin: id === 'role' ? 2 : 1 }));
      }

      fixture = TestBed.createComponent(Dashboard);
      store = TestBed.inject(TasksStore);
      fixture.detectChanges();
      tick();

      for (const [id, status, , phrase] of refused) {
        void store.selectTask(id);
        tick();
        fixture.detectChanges();
        expect(buttonWith('Resume this task')).withContext(`${id} (${AgentTaskStatusName[status]})`).toBeUndefined();
        expect(text()).withContext(id).toContain(phrase);
      }
      leave();
    }));

    it('offers Resume for a MaxStepsReached task the server says is resumable, and no Resume at the lifetime cap', fakeAsync(() => {
      backend.add(task('room', 2, { resumable: true, terminalReason: { kind: 1 } }));
      backend.add(task('capped', 2, { resumable: false, resumeBlockedReason: blocked('lifetime_steps_exhausted'), terminalReason: { kind: 2 } }));
      fixture = TestBed.createComponent(Dashboard);
      store = TestBed.inject(TasksStore);
      fixture.detectChanges();
      tick();

      void store.selectTask('room');
      tick();
      fixture.detectChanges();
      expect(buttonWith('Resume this task')).toBeDefined();
      expect(text()).toContain('Step limit of this attempt reached');

      void store.selectTask('capped');
      tick();
      fixture.detectChanges();
      expect(buttonWith('Resume this task')).toBeUndefined();
      expect(text()).toContain('Lifetime step limit reached');
      expect(text()).toContain('over its lifetime');
      leave();
    }));

    it('shows an executing task as Running with Cancel, and an interrupted one as Interrupted without', fakeAsync(() => {
      backend.add(task('live', 0, { executing: true, resumeBlockedReason: blocked('task_running') }));
      backend.add(task('orphan', 0, { executing: false, resumeBlockedReason: blocked('task_running') }));
      fixture = TestBed.createComponent(Dashboard);
      store = TestBed.inject(TasksStore);
      fixture.detectChanges();
      tick();

      void store.selectTask('live');
      tick(TASK_POLL_INTERVAL_MS);
      fixture.detectChanges();
      expect(buttonWith('Cancel task')).toBeDefined();
      expect(buttonWith('Resume this task')).toBeUndefined();

      void store.selectTask('orphan');
      tick(TASK_POLL_INTERVAL_MS);
      fixture.detectChanges();
      expect(text()).toContain('Interrupted');
      expect(buttonWith('Cancel task')).toBeUndefined();
      expect(buttonWith('Resume this task')).toBeUndefined();
      expect(text()).toContain('not executing this task');
      leave();
    }));

    it('cancels an executing task and keeps following it until the server says it stopped', fakeAsync(() => {
      backend.add(task('live', 0, { executing: true, resumeBlockedReason: blocked('task_running') }));
      fixture = TestBed.createComponent(Dashboard);
      store = TestBed.inject(TasksStore);
      fixture.detectChanges();
      tick();
      void store.selectTask('live');
      tick();
      fixture.detectChanges();

      buttonWith('Cancel task')!.click();
      tick();
      fixture.detectChanges();
      expect(backend.count('DELETE', '/live')).toBe(1);
      expect(buttonWith('Cancelling…')!.disabled).toBeTrue();
      expect(detailBadge()).toContain('Running');
      expect(detailBadge()).not.toContain('Cancelled');

      backend.finish('live', 7, 11);
      tick(TASK_POLL_INTERVAL_MS);
      fixture.detectChanges();
      expect(detailBadge()).toContain('Cancelled');
      expect(text()).toContain('Ended because');
      expect(buttonWith('Cancel')).toBeUndefined();
      leave();
    }));
  });

  describe('E2E-8 (reconnect): the API goes away while a task is watched', () => {
    for (const status of [502, 503]) {
      it(`shows Reconnecting… through a ${status} outage of more than a minute and recovers by itself`, fakeAsync(() => {
        backend.add(task('live', 0, { executing: true, resumeBlockedReason: blocked('task_running') }));
        fixture = TestBed.createComponent(Dashboard);
        store = TestBed.inject(TasksStore);
        fixture.detectChanges();
        tick();
        void store.selectTask('live');
        tick();
        fixture.detectChanges();
        expect(text()).not.toContain('Reconnecting');

        backend.outage = { status, retryAfter: 5 };
        tick(TASK_POLL_INTERVAL_MS);
        fixture.detectChanges();
        expect(text()).toContain('Reconnecting…');

        for (let elapsed = 0; elapsed < 70_000; elapsed += 1000) tick(1000);
        fixture.detectChanges();
        expect(text()).toContain('Reconnecting…');
        expect(text()).not.toContain('Lost the live connection');
        expect(buttonWith('Cancel task')).toBeDefined();

        backend.outage = null;
        backend.advance('live');
        tick(30_000);
        fixture.detectChanges();
        expect(text()).not.toContain('Reconnecting');
        expect(text()).toContain('Step 2');
        leave();
      }));
    }

    it('reconnects at once when the browser comes back online, not after the backoff', fakeAsync(() => {
      backend.add(task('live', 0, { executing: true, resumeBlockedReason: blocked('task_running') }));
      fixture = TestBed.createComponent(Dashboard);
      store = TestBed.inject(TasksStore);
      fixture.detectChanges();
      tick();
      void store.selectTask('live');
      tick();

      backend.outage = { status: 0 };
      for (let elapsed = 0; elapsed < 40_000; elapsed += 1000) tick(1000);
      fixture.detectChanges();
      expect(text()).toContain('Reconnecting…');

      backend.outage = null;
      window.dispatchEvent(new Event('online'));
      tick();
      fixture.detectChanges();
      expect(text()).not.toContain('Reconnecting');
      leave();
    }));

    it('says so in Italian', fakeAsync(() => {
      backend.add(task('live', 0, { executing: true, resumeBlockedReason: blocked('task_running') }));
      fixture = TestBed.createComponent(Dashboard);
      store = TestBed.inject(TasksStore);
      TestBed.inject(I18n).setLanguage('it');
      fixture.detectChanges();
      tick();
      void store.selectTask('live');
      tick();
      backend.outage = { status: 503 };
      tick(TASK_POLL_INTERVAL_MS);
      fixture.detectChanges();

      expect(text()).toContain('Riconnessione in corso…');
      leave();
    }));
  });
});
