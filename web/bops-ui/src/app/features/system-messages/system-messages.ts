// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  SystemMessage,
  SystemMessageFilter,
  SystemMessageSeverities,
  SystemMessageSeverityName,
} from '../../core/api/models';
import { I18n } from '../../core/i18n/i18n';
import { TranslatePipe } from '../../core/i18n/translate.pipe';
import { SystemMessagesStore } from '../../state/system-messages.store';
import { localInputToUtcIso } from './system-messages-time';

/** A shape and a word alongside the colour, so severity never depends on colour alone. */
const SEVERITY_PRESENTATION: Record<string, { icon: string; classes: string }> = {
  Information: { icon: 'ℹ', classes: 'bg-risk-low/15 text-risk-low' },
  Warning: { icon: '⚠', classes: 'bg-risk-medium/15 text-risk-medium' },
  Error: { icon: '✖', classes: 'bg-risk-high/15 text-risk-high' },
  Critical: { icon: '‼', classes: 'bg-risk-critical/15 text-risk-critical' },
};
const UNKNOWN_SEVERITY = { icon: '•', classes: 'bg-risk-read/15 text-risk-read' };

@Component({
  selector: 'bops-system-messages',
  imports: [FormsModule, TranslatePipe],
  templateUrl: './system-messages.html',
})
export class SystemMessages {
  protected readonly messages = inject(SystemMessagesStore);
  protected readonly i18n = inject(I18n);
  protected readonly severities = SystemMessageSeverities;

  // What is typed; the store holds what is applied. Nothing is requested until Apply.
  protected readonly from = signal('');
  protected readonly to = signal('');
  protected readonly severity = signal<SystemMessageSeverityName | ''>('');
  protected readonly contains = signal('');
  protected readonly rangeError = signal(false);
  protected readonly expanded = signal<ReadonlySet<string>>(new Set());

  constructor() {
    void this.messages.apply({});
  }

  protected async apply(): Promise<void> {
    const fromUtc = localInputToUtcIso(this.from());
    const toUtc = localInputToUtcIso(this.to());
    if (fromUtc && toUtc && Date.parse(fromUtc) > Date.parse(toUtc)) {
      this.rangeError.set(true);
      return;
    }

    this.rangeError.set(false);
    const filter: SystemMessageFilter = {};
    if (fromUtc) filter.fromUtc = fromUtc;
    if (toUtc) filter.toUtc = toUtc;
    if (this.severity()) filter.severity = this.severity() as SystemMessageSeverityName;
    const text = this.contains().trim();
    if (text) filter.contains = text;
    this.expanded.set(new Set());
    await this.messages.apply(filter);
  }

  protected async reset(): Promise<void> {
    this.from.set('');
    this.to.set('');
    this.severity.set('');
    this.contains.set('');
    this.rangeError.set(false);
    this.expanded.set(new Set());
    await this.messages.reset();
  }

  protected async next(): Promise<void> {
    this.expanded.set(new Set());
    await this.messages.next();
  }

  protected async previous(): Promise<void> {
    this.expanded.set(new Set());
    await this.messages.previous();
  }

  protected toggle(id: string): void {
    this.expanded.update((current) => {
      const next = new Set(current);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  protected severityIcon(severity: string): string {
    return (SEVERITY_PRESENTATION[severity] ?? UNKNOWN_SEVERITY).icon;
  }

  protected severityClasses(severity: string): string {
    return (SEVERITY_PRESENTATION[severity] ?? UNKNOWN_SEVERITY).classes;
  }

  /** Metadata is data: shown as escaped, indented JSON text — never interpreted as markup, a URL or a script. */
  protected metadataText(message: SystemMessage): string {
    return JSON.stringify(message.metadata ?? {}, null, 2);
  }

  protected hasMetadata(message: SystemMessage): boolean {
    return Object.keys(message.metadata ?? {}).length > 0;
  }
}
