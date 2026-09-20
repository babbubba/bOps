// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, inject, input } from '@angular/core';
import { RiskLevel, RiskLevelName } from '../core/api/models';
import { I18n } from '../core/i18n/i18n';

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
  private readonly i18n = inject(I18n);
  readonly risk = input.required<RiskLevel>();

  protected label(): string {
    return this.i18n.label('risk', RiskLevelName[this.risk()]);
  }

  protected cssClass(): string {
    return RISK_CLASSES[this.risk()];
  }
}
