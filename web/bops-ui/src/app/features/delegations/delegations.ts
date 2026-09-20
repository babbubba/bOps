// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  Delegation,
  DelegationRole,
  DelegationRoleOrder,
  PendingPlanApproval,
  StartDelegationRequest,
} from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { DelegationsStore } from '../../state/delegations.store';

const STATUS_CLASSES: Record<string, string> = {
  Running: 'bg-status-running/15 text-status-running',
  Completed: 'bg-status-completed/15 text-status-completed',
  DiagnosisCompleted: 'bg-status-completed/15 text-status-completed',
  Failed: 'bg-status-failed/15 text-status-failed',
  VerificationFailed: 'bg-status-failed/15 text-status-failed',
  RequiresReconciliation: 'bg-risk-high/15 text-risk-high',
  Denied: 'bg-status-blocked/15 text-status-blocked',
  PolicyBlocked: 'bg-status-blocked/15 text-status-blocked',
  BudgetExceeded: 'bg-status-blocked/15 text-status-blocked',
  DeadlineExceeded: 'bg-status-blocked/15 text-status-blocked',
};
const NEUTRAL_CLASS = 'bg-status-neutral/15 text-status-neutral';

/**
 * The Delegations view (ADR-0030 section 9): a run's roles in order, what was found and which evidence it cites, the plan by its
 * hash and the prompt to approve it, the verification verdict, the step journal and anything awaiting reconciliation.
 * Everything a run or a plan carries is untrusted text (a model wrote the findings, a tool the evidence): it is only ever
 * interpolated, which Angular escapes, and never bound as HTML. Buttons follow the role, but the API decides.
 */
@Component({
  selector: 'bops-delegations',
  imports: [FormsModule],
  templateUrl: './delegations.html',
})
export class Delegations {
  protected readonly store = inject(DelegationsStore);
  private readonly auth = inject(AuthService);

  protected readonly selectedId = signal<string | null>(null);
  protected readonly notes = signal<Record<string, string>>({});

  protected readonly canOperate = computed(() => this.hasRole('operator'));
  protected readonly canApprove = computed(() => this.hasRole('approver'));
  protected readonly canReconcile = computed(() => this.hasRole('administrator'));

  protected readonly selected = computed<Delegation | null>(() => {
    const id = this.selectedId();
    return id === null ? null : (this.store.runs().find((run) => run.id === id) ?? null);
  });

  protected readonly pendingPlan = computed<PendingPlanApproval | null>(() => {
    const id = this.selectedId();
    return id === null ? null : (this.store.pendingPlans().find((plan) => plan.delegationId === id) ?? null);
  });

  // ---- start form ----
  protected readonly starting = signal(false);
  protected readonly objective = signal('');
  protected readonly withChange = signal(false);
  protected readonly skillId = signal('');
  protected readonly capabilityName = signal('');
  protected readonly target = signal('');
  protected readonly environment = signal('');
  protected readonly blastRadius = signal('single');
  protected readonly dryRun = signal(false);
  /** One key per form: a double click, or a retry after a timeout, returns the same run instead of starting another. */
  private idempotencyKey = crypto.randomUUID();

  protected select(id: string): void {
    this.selectedId.set(id);
  }

  protected roles(run: Delegation): DelegationRole[] {
    return [...run.roles].sort((a, b) => DelegationRoleOrder.indexOf(a.role) - DelegationRoleOrder.indexOf(b.role));
  }

  protected statusClass(status: string): string {
    return STATUS_CLASSES[status] ?? NEUTRAL_CLASS;
  }

  protected noteFor(id: string): string {
    return this.notes()[id] ?? '';
  }

  protected setNote(id: string, value: string): void {
    this.notes.update((notes) => ({ ...notes, [id]: value }));
  }

  protected formatTime(iso: string | null): string {
    return iso ? new Date(iso).toLocaleString() : '';
  }

  protected json(value: unknown): string {
    return JSON.stringify(value);
  }

  /** Cancel is offered for a run still under way; a run waiting for reconciliation is settled with Reconcile instead. */
  protected canCancel(run: Delegation): boolean {
    return this.canOperate() && run.status === 'Running';
  }

  /** Resume is offered only when nothing in this host is executing the run: a crash or a restart left it running. */
  protected canResume(run: Delegation): boolean {
    return this.canOperate() && run.status === 'Running' && !run.runningInThisHost;
  }

  protected canReconcileRun(run: Delegation): boolean {
    return this.canReconcile() && run.status === 'RequiresReconciliation';
  }

  protected cancel(run: Delegation): Promise<void> {
    return this.store.cancel(run.id);
  }

  protected resume(run: Delegation): Promise<void> {
    return this.store.resume(run.id);
  }

  protected reconcile(run: Delegation, decision: 'accept' | 'abandon'): Promise<void> {
    return this.store.reconcile(run.id, decision, this.noteFor(run.id) || undefined);
  }

  protected decide(plan: PendingPlanApproval, approved: boolean): Promise<void> {
    return this.store.decidePlan(plan.delegationId, plan.planHash, approved, this.noteFor(plan.delegationId) || undefined);
  }

  protected canStart(): boolean {
    if (this.objective().trim() === '') return false;
    if (!this.withChange()) return true;
    return [this.skillId(), this.capabilityName(), this.target(), this.environment()].every((v) => v.trim() !== '');
  }

  protected async start(): Promise<void> {
    if (!this.canStart() || this.starting()) return;
    this.starting.set(true);
    try {
      const request: StartDelegationRequest = { objective: this.objective().trim() };
      if (this.withChange()) {
        request.remediation = {
          skillId: this.skillId().trim(),
          capabilityName: this.capabilityName().trim(),
          target: this.target().trim(),
          environment: this.environment().trim(),
          blastRadius: this.blastRadius(),
          dryRun: this.dryRun(),
        };
      }

      const id = await this.store.start(request, this.idempotencyKey);
      if (id !== undefined) {
        this.selectedId.set(id);
        this.objective.set('');
        this.withChange.set(false);
        this.idempotencyKey = crypto.randomUUID();
      }
    } finally {
      this.starting.set(false);
    }
  }

  private hasRole(role: string): boolean {
    return this.auth.identity()?.roles.includes(role) === true;
  }
}
