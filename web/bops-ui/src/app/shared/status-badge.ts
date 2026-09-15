import { Component, input } from '@angular/core';
import { AgentTaskStatus, AgentTaskStatusName } from '../core/api/models';

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
    <span class="inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-medium" [class]="cssClass()">
      @if (status() === 0) {
        <span class="h-1.5 w-1.5 animate-pulse rounded-full bg-current"></span>
      }
      {{ label() }}
    </span>
  `,
})
export class StatusBadge {
  readonly status = input.required<AgentTaskStatus>();

  protected label(): string {
    return AgentTaskStatusName[this.status()];
  }

  protected cssClass(): string {
    return STATUS_CLASSES[this.status()];
  }
}
