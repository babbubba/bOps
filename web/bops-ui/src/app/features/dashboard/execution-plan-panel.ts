// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, input } from '@angular/core';
import { ExecutionPlan, ExecutionPlanStep, ExecutionPlanStepStatus } from '../../core/api/models';
import { TranslatePipe } from '../../core/i18n/translate.pipe';
import { MessageKey } from '../../core/i18n/messages';

const STATUS_TEXT: Record<ExecutionPlanStepStatus, MessageKey> = {
  Pending: 'dashboard.plan.status.Pending',
  Running: 'dashboard.plan.status.Running',
  Correcting: 'dashboard.plan.status.Correcting',
  Completed: 'dashboard.plan.status.Completed',
  Failed: 'dashboard.plan.status.Failed',
  Skipped: 'dashboard.plan.status.Skipped',
  Superseded: 'dashboard.plan.status.Superseded',
  OutcomeUnknown: 'dashboard.plan.status.OutcomeUnknown',
};

const STATUS_CLASS: Record<ExecutionPlanStepStatus, string> = {
  Pending: 'text-muted',
  Running: 'text-status-running',
  Correcting: 'text-status-blocked',
  Completed: 'text-status-completed',
  Failed: 'text-status-failed',
  Skipped: 'text-status-neutral',
  Superseded: 'text-status-neutral',
  OutcomeUnknown: 'text-risk-critical',
};

/**
 * PRE-4: renders the server's execution-plan projection and nothing else. Every status, the current step, the correction kind and the
 * conditional outcome are decided by the server from persisted state; this component keeps no state of its own, so a replaced
 * `plan` input is the whole refresh.
 */
@Component({
  selector: 'bops-execution-plan-panel',
  imports: [TranslatePipe],
  template: `
    <section aria-labelledby="execution-plan-heading" class="min-w-0 rounded-lg border border-default bg-bg p-3">
      <h3 id="execution-plan-heading" class="text-sm font-semibold">{{ 'dashboard.plan.title' | t }}</h3>
      @let current = plan();
      @if (!current) {
        <p class="mt-2 text-sm text-muted">{{ 'dashboard.plan.none' | t }}</p>
      } @else {
        <p class="text-xs text-muted">{{ 'dashboard.plan.revision' | t: { revision: current.activeRevision } }}</p>
        @for (revision of current.revisions; track revision.revision; let first = $first, i = $index) {
          @if (!first) {
            <div role="separator" class="my-3 break-words border-t border-dashed border-default pt-1 text-xs font-medium text-muted">
              {{ 'dashboard.plan.replan' | t: { from: current.revisions[i - 1].revision, to: revision.revision } }}
            </div>
          }
          @if (revision.steps.length === 0) {
            <p class="mt-2 text-sm text-muted">{{ 'dashboard.plan.noSteps' | t }}</p>
          } @else {
            <ol class="mt-2 flex flex-col gap-2" [attr.aria-label]="'dashboard.plan.revision' | t: { revision: revision.revision }">
              @for (step of revision.steps; track step.index) {
                <li
                  [attr.aria-current]="step.current ? 'step' : null"
                  class="min-w-0 rounded-md border-l-4 px-2 py-1.5 text-sm"
                  [class]="step.current ? 'border-accent bg-accent/10' : 'border-transparent'"
                  [class.opacity-60]="step.status === 'Superseded'"
                >
                  <div class="flex min-w-0 gap-2">
                    <span class="shrink-0 font-semibold" aria-hidden="true">{{ step.index + 1 }}</span>
                    <div class="min-w-0 flex-1">
                      <p class="break-words" [class.line-through]="step.status === 'Superseded'">{{ step.objective }}</p>
                      @if (step.expectedTool) {
                        <p class="break-all text-xs text-muted">{{ step.expectedTool }}</p>
                      }
                      <p class="mt-0.5 flex flex-wrap items-center gap-x-2 text-xs font-medium" [class]="statusClass(step)">
                        <span>{{ statusText(step) | t }}</span>
                        @if (step.current) {
                          <span class="rounded border border-accent px-1 text-accent">{{ 'dashboard.plan.current' | t }}</span>
                        }
                        @if (step.conditional) {
                          <span class="text-muted">· {{ 'dashboard.plan.conditional' | t }}</span>
                        }
                        @if (step.conditionOutcome === 'Activated') {
                          <span class="text-muted">· {{ 'dashboard.plan.activated' | t }}</span>
                        }
                      </p>
                      @if (correctionText(step); as correction) {
                        <p class="text-xs text-status-blocked">{{ correction | t }}</p>
                      }
                    </div>
                  </div>
                </li>
              }
            </ol>
          }
        }
      }
    </section>
  `,
})
export class ExecutionPlanPanel {
  readonly plan = input<ExecutionPlan | null | undefined>(null);

  protected statusText(step: ExecutionPlanStep): MessageKey {
    return STATUS_TEXT[step.status];
  }

  protected statusClass(step: ExecutionPlanStep): string {
    return STATUS_CLASS[step.status];
  }

  protected correctionText(step: ExecutionPlanStep): MessageKey | null {
    if (step.status !== 'Correcting') {
      return null;
    }

    return step.correctionKind === 'ArgumentValidation'
      ? 'dashboard.plan.correcting.argumentValidation'
      : 'dashboard.plan.correcting.semantic';
  }
}
