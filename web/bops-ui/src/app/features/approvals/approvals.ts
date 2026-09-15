// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RiskBadge } from '../../shared/risk-badge';
import { RiskLevel } from '../../core/api/models';
import { ApprovalsStore } from '../../state/approvals.store';

@Component({
  selector: 'bops-approvals',
  imports: [FormsModule, RiskBadge],
  templateUrl: './approvals.html',
})
export class Approvals {
  protected readonly approvals = inject(ApprovalsStore);
  protected readonly notes = signal<Record<string, string>>({});

  protected noteFor(id: string): string {
    return this.notes()[id] ?? '';
  }

  protected setNote(id: string, value: string): void {
    this.notes.update((notes) => ({ ...notes, [id]: value }));
  }

  protected riskFor(tool: string): RiskLevel | null {
    return this.approvals.toolRisk()[tool] ?? null;
  }

  protected async approve(id: string): Promise<void> {
    await this.approvals.approve(id, this.noteFor(id) || undefined);
  }

  protected async reject(id: string): Promise<void> {
    await this.approvals.reject(id, this.noteFor(id) || undefined);
  }

  protected formatTime(iso: string): string {
    return new Date(iso).toLocaleTimeString();
  }
}
