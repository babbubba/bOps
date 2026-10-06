// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  CatalogCapability,
  CatalogSkill,
  Delegation,
  DelegationRole,
  DelegationStep,
  DelegationRoleOrder,
  EvidenceLimitation,
  InputParameter,
  PendingPlanApproval,
  RoleReadiness,
  StartDelegationRequest,
} from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { I18n } from '../../core/i18n/i18n';
import type { MessageKey } from '../../core/i18n/messages';
import { TranslatePipe } from '../../core/i18n/translate.pipe';
import { DelegationsStore, ReadinessFailure } from '../../state/delegations.store';
import { FieldValue, InputProblem, buildInput, emptyValues, isChoice, parseRaw, rawProblems, valuesFrom } from './capability-input';

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

/** The readiness reason codes and states this UI knows (ADR-0044 §6.2). Anything else is shown as not ready: never fail open. */
const KNOWN_REASON_CODES = ['ready', 'not_required', 'profile_missing', 'policy_load_failed', 'profile_for_other_role', 'reduction_denied'];
const STATE_KEYS: Record<string, MessageKey> = {
  ready: 'delegations.readiness.state.ready',
  missing: 'delegations.readiness.state.missing',
  malformed: 'delegations.readiness.state.malformed',
  notRequired: 'delegations.readiness.state.notRequired',
};

/** What the panel says when readiness could not be obtained (ADR-0044 §13): never "ready". */
const FAILURE_KEYS: Record<ReadinessFailure, MessageKey> = {
  sessionExpired: 'delegations.readiness.failure.sessionExpired',
  forbidden: 'delegations.readiness.failure.forbidden',
  rateLimited: 'delegations.readiness.failure.rateLimited',
  server: 'delegations.readiness.failure.server',
  unreachable: 'delegations.readiness.failure.unreachable',
};

/**
 * The Delegations view (ADR-0030 section 9): a run's roles in order, what was found and which evidence it cites, the plan by its
 * hash and the prompt to approve it, the verification verdict, the step journal and anything awaiting reconciliation.
 * Everything a run or a plan carries is untrusted text (a model wrote the findings, a tool the evidence): it is only ever
 * interpolated, which Angular escapes, and never bound as HTML. Buttons follow the role, but the API decides.
 *
 * The start form (ADR-0044 §9, §13) shows the server's role readiness for the request shape being prepared and allows Submit only
 * when the latest answer for that shape says `ready: true`; it never decides which roles are required. A change is chosen from the
 * activated catalog, and its input comes from a form generated from the Capability's input schema, or, as an advanced fallback,
 * from one JSON object. Client checks are a convenience; the server validates again and decides.
 */
@Component({
  selector: 'bops-delegations',
  imports: [FormsModule, TranslatePipe],
  templateUrl: './delegations.html',
})
export class Delegations {
  protected readonly store = inject(DelegationsStore);
  private readonly auth = inject(AuthService);
  protected readonly i18n = inject(I18n);

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
  protected readonly values = signal<Record<string, FieldValue>>({});
  protected readonly rawMode = signal(false);
  protected readonly rawText = signal('{}');
  /** Why switching from the JSON editor back to the form was refused; the editor stays open. */
  protected readonly rawSwitchProblem = signal<InputProblem | null>(null);
  /** One key per form: a double click, or a retry after a timeout, returns the same run instead of starting another. */
  private idempotencyKey = crypto.randomUUID();

  constructor() {
    if (this.canOperate()) {
      void this.store.loadReadiness(false);
      void this.store.loadCatalog();
    }
  }

  protected readonly skills = computed<CatalogSkill[]>(() => this.store.catalog() ?? []);

  protected readonly selectedSkill = computed<CatalogSkill | null>(() => this.skills().find((skill) => skill.skillId === this.skillId()) ?? null);

  protected readonly selectedCapability = computed<CatalogCapability | null>(
    () => this.selectedSkill()?.capabilities.find((capability) => capability.name === this.capabilityName()) ?? null,
  );

  protected readonly schema = computed<InputParameter[]>(() => this.selectedCapability()?.inputSchema ?? []);

  /** The input the active mode describes, and its problems; only one source is ever sent. */
  protected readonly input = computed<{ input: Record<string, unknown>; problems: InputProblem[] }>(() => {
    const schema = this.schema();
    if (!this.rawMode()) {
      return buildInput(schema, this.values());
    }

    const parsed = parseRaw(this.rawText(), schema);
    if ('problem' in parsed) {
      return { input: {}, problems: [parsed.problem] };
    }

    return { input: parsed.input, problems: rawProblems(schema, parsed.input) };
  });

  /** Submit follows the server: the latest answer for the current shape, `ready: true`, and nothing in it this UI does not know. */
  protected readonly readinessAllowsSubmit = computed(() => {
    const readiness = this.store.readiness();
    return (
      !this.store.readinessLoading() &&
      this.store.readinessFailure() === null &&
      readiness !== null &&
      readiness.remediation === this.withChange() &&
      readiness.ready === true &&
      readiness.roles.every((role) => this.knownRole(role))
    );
  });

  protected setWithChange(value: boolean): void {
    this.withChange.set(value);
    this.store.clearSubmitError();
    void this.store.loadReadiness(value);
  }

  protected setSkill(skillId: string): void {
    this.skillId.set(skillId);
    this.setCapability('');
  }

  protected setCapability(name: string): void {
    this.capabilityName.set(name);
    this.values.set(emptyValues(this.schema()));
    this.rawMode.set(false);
    this.rawText.set('{}');
    this.rawSwitchProblem.set(null);
    if (this.selectedCapability()?.supportsDryRun === false) {
      this.dryRun.set(false);
    }
  }

  protected setValue(name: string, value: FieldValue): void {
    this.values.update((values) => ({ ...values, [name]: value }));
  }

  protected listValue(name: string): string[] {
    const value = this.values()[name];
    return Array.isArray(value) ? value : [];
  }

  protected textValue(name: string): string {
    const value = this.values()[name];
    return typeof value === 'string' ? value : '';
  }

  protected booleanValue(name: string): string {
    const value = this.values()[name];
    return value === true ? 'true' : value === false ? 'false' : '';
  }

  protected setBoolean(name: string, text: string): void {
    this.setValue(name, text === 'true' ? true : text === 'false' ? false : null);
  }

  protected addEntry(name: string): void {
    this.setValue(name, [...this.listValue(name), '']);
  }

  protected setEntry(name: string, index: number, text: string): void {
    this.setValue(name, this.listValue(name).map((entry, i) => (i === index ? text : entry)));
  }

  protected removeEntry(name: string, index: number): void {
    this.setValue(name, this.listValue(name).filter((_, i) => i !== index));
  }

  protected isChoice(parameter: InputParameter): boolean {
    return isChoice(parameter);
  }

  protected problemFor(name: string): InputProblem | undefined {
    return this.input().problems.find((problem) => problem.parameter === name);
  }

  protected describe(problem: InputProblem): string {
    return this.i18n.t(problem.key, problem.params);
  }

  /** Switching to JSON writes the form's current object; switching back keeps the editor open unless the form can show it exactly. */
  protected setRawMode(raw: boolean): void {
    this.rawSwitchProblem.set(null);
    if (raw) {
      this.rawText.set(JSON.stringify(buildInput(this.schema(), this.values()).input, null, 2));
      this.rawMode.set(true);
      return;
    }

    const parsed = parseRaw(this.rawText(), this.schema());
    if ('problem' in parsed) {
      this.rawSwitchProblem.set(parsed.problem);
      return;
    }

    const mapped = valuesFrom(this.schema(), parsed.input);
    if ('problem' in mapped) {
      this.rawSwitchProblem.set(mapped.problem);
      return;
    }

    this.values.set(mapped.values);
    this.rawMode.set(false);
  }

  // ---- readiness panel ----

  protected knownRole(role: RoleReadiness): boolean {
    return KNOWN_REASON_CODES.includes(role.reasonCode) && role.state in STATE_KEYS;
  }

  /** The state label; an unknown code or state reads as "not ready", whatever the state says. */
  protected stateLabel(role: RoleReadiness): string {
    return this.knownRole(role) ? this.i18n.t(STATE_KEYS[role.state]) : this.i18n.t('delegations.readiness.state.unknown');
  }

  protected roleReady(role: RoleReadiness): boolean {
    return this.knownRole(role) && (role.state === 'ready' || role.state === 'notRequired');
  }

  protected failureText(failure: ReadinessFailure): string {
    return this.i18n.t(FAILURE_KEYS[failure]);
  }

  // ---- run detail ----

  protected select(id: string): void {
    this.selectedId.set(id);
  }

  protected roles(run: Delegation): DelegationRole[] {
    return [...run.roles].sort((a, b) => DelegationRoleOrder.indexOf(a.role) - DelegationRoleOrder.indexOf(b.role));
  }

  protected isModelRole(role: string): boolean {
    return role === 'Discovery' || role === 'Diagnostic';
  }

  /** One limitation as a line of typed facts. No tool output or model text exists to be shown. */
  protected limitationLine(limitation: EvidenceLimitation): string {
    const facts: string[] = [];
    if (limitation.completeness === 'Partial' || limitation.completeness === 'Unavailable') {
      facts.push(this.i18n.t('delegations.limitations.completeness', { value: limitation.completeness }));
    }

    if (limitation.outcome !== 'Success') {
      facts.push(this.i18n.t('delegations.limitations.outcome', { outcome: limitation.outcome, kind: limitation.failureKind }));
    }

    if (limitation.shortenedFromCharacters !== null) {
      facts.push(this.i18n.t('delegations.limitations.shortened', { count: limitation.shortenedFromCharacters }));
    }

    const tool = limitation.unknownTool
      ? this.i18n.t('delegations.limitations.unknownTool')
      : (limitation.toolName ?? this.i18n.t('delegations.limitations.toolOmitted'));
    const evidence = limitation.evidenceId
      ? this.i18n.t('delegations.limitations.evidence', { id: limitation.evidenceId })
      : this.i18n.t('delegations.limitations.noEvidence');
    return this.i18n.t('delegations.limitations.line', { step: limitation.stepIndex, tool, facts: facts.join('; '), evidence });
  }

  protected replyLine(reply: { status: string; problem: string; discardedFindings: number }): string {
    return reply.problem === 'None'
      ? this.i18n.t('delegations.limitations.reply', { status: reply.status, count: reply.discardedFindings })
      : this.i18n.t('delegations.limitations.replyProblem', { status: reply.status, problem: reply.problem, count: reply.discardedFindings });
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
    return this.i18n.dateTime(iso);
  }

  /** A role's status, steps and tokens on one line, each in the active language (steps and tokens by plural rule). */
  protected roleStats(role: DelegationRole): string {
    return this.i18n.t('delegations.roles.stats', {
      status: this.i18n.label('roleStatus', role.status),
      steps: this.i18n.t('delegations.roles.steps', { count: role.steps }),
      tokens: this.i18n.t('delegations.roles.tokens', { count: role.tokens }),
    });
  }

  /** How a journal entry ended: how it was reconciled if it was, else its outcome, else that it is not known. */
  protected journalOutcome(entry: DelegationStep): string {
    if (entry.reconciliation) return this.i18n.label('reconciliation', entry.reconciliation);
    if (entry.outcome) return this.i18n.label('stepOutcome', entry.outcome);
    return this.i18n.t('delegations.journal.unknown');
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

  /** Whether the form is complete enough to send; readiness is checked separately and gates the button too. */
  protected canStart(): boolean {
    if (this.objective().trim() === '') return false;
    if (!this.withChange()) return true;
    if (this.store.catalogError() !== null || this.selectedCapability() === null) return false;
    if ([this.target(), this.environment()].some((v) => v.trim() === '')) return false;
    if (this.dryRun() && this.selectedCapability()?.supportsDryRun === false) return false;
    return this.input().problems.length === 0;
  }

  protected async start(): Promise<void> {
    if (!this.canStart() || !this.readinessAllowsSubmit() || this.starting()) return;
    this.starting.set(true);
    try {
      const request: StartDelegationRequest = { objective: this.objective().trim() };
      if (this.withChange()) {
        request.remediation = {
          skillId: this.skillId(),
          capabilityName: this.capabilityName(),
          target: this.target().trim(),
          environment: this.environment().trim(),
          blastRadius: this.blastRadius(),
          dryRun: this.dryRun(),
          input: this.input().input,
        };
      }

      const id = await this.store.start(request, this.idempotencyKey);
      if (id !== undefined) {
        this.selectedId.set(id);
        this.objective.set('');
        if (this.withChange()) {
          this.setWithChange(false);
        }

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
