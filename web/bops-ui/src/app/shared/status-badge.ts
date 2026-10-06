// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, inject, input } from '@angular/core';
import { AgentTaskStatus, AgentTaskStatusName } from '../core/api/models';
import { I18n } from '../core/i18n/i18n';

const STATUS_CLASSES: Record<AgentTaskStatus, string> = {
  0: 'bg-status-running/15 text-status-running',
  1: 'bg-status-completed/15 text-status-completed',
  2: 'bg-status-blocked/15 text-status-blocked',
  3: 'bg-status-blocked/15 text-status-blocked',
  4: 'bg-status-blocked/15 text-status-blocked',
  5: 'bg-status-blocked/15 text-status-blocked',
  6: 'bg-status-failed/15 text-status-failed',
  7: 'bg-status-neutral/15 text-status-neutral',
};

@Component({
  selector: 'bops-status-badge',
  template: `
    <span
      class="inline-flex max-w-48 shrink-0 items-center gap-1.5 truncate whitespace-nowrap rounded-full px-2 py-0.5 text-xs font-medium"
      [class]="cssClass()"
      [attr.title]="label()"
      [attr.aria-label]="label()"
    >
      @if (interrupted()) {
        <span class="h-1.5 w-1.5 rounded-full border border-current"></span>
      } @else if (status() === 0) {
        <span class="h-1.5 w-1.5 animate-pulse rounded-full bg-current"></span>
      }
      {{ label() }}
    </span>
  `,
})
export class StatusBadge {
  private readonly i18n = inject(I18n);
  readonly status = input.required<AgentTaskStatus>();
  /** A Running task that no executor holds (see isInterrupted): labelled as such and not pulsing, so it is not mistaken for live work. */
  readonly interrupted = input(false);

  protected label(): string {
    if (this.interrupted()) {
      return this.i18n.t('dashboard.status.interrupted');
    }

    return this.i18n.label('taskStatus', AgentTaskStatusName[this.status()]);
  }

  protected cssClass(): string {
    return this.interrupted() ? 'bg-status-blocked/15 text-status-blocked' : STATUS_CLASSES[this.status()];
  }
}
