import { Component, input } from '@angular/core';
import { RiskLevel, RiskLevelName } from '../core/api/models';

const RISK_CLASSES: Record<RiskLevel, string> = {
  0: 'bg-risk-read/15 text-risk-read',
  1: 'bg-risk-low/15 text-risk-low',
  2: 'bg-risk-medium/15 text-risk-medium',
  3: 'bg-risk-high/15 text-risk-high',
  4: 'bg-risk-critical/15 text-risk-critical',
};

@Component({
  selector: 'bops-risk-badge',
  template: `
    <span class="inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium" [class]="cssClass()">
      {{ label() }}
    </span>
  `,
})
export class RiskBadge {
  readonly risk = input.required<RiskLevel>();

  protected label(): string {
    return RiskLevelName[this.risk()];
  }

  protected cssClass(): string {
    return RISK_CLASSES[this.risk()];
  }
}
