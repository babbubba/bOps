// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BOpsApiClient } from '../../core/api/bops-api-client';
import {
  DeletionManifestPage,
  DeletionManifestReady,
  DeletionManifestSummary,
  PendingApproval,
  RiskLevel,
} from '../../core/api/models';
import { I18n } from '../../core/i18n/i18n';
import { TranslatePipe } from '../../core/i18n/translate.pipe';
import { RiskBadge } from '../../shared/risk-badge';
import { ApprovalsStore } from '../../state/approvals.store';

interface DeletionPreview {
  summary: DeletionManifestSummary | null;
  page: DeletionManifestPage | null;
  cursors: Array<string | undefined>;
  cursorIndex: number;
  search: string;
  loading: boolean;
  error: string | null;
}

@Component({
  selector: 'bops-approvals',
  imports: [FormsModule, RiskBadge, TranslatePipe],
  templateUrl: './approvals.html',
})
export class Approvals {
  protected readonly approvals = inject(ApprovalsStore);
  private readonly api = inject(BOpsApiClient);
  private readonly i18n = inject(I18n);
  protected readonly notes = signal<Record<string, string>>({});
  protected readonly acknowledgements = signal<Record<string, boolean>>({});
  protected readonly previews = signal<Record<string, DeletionPreview>>({});
  private readonly previewRequests = new Set<string>();

  constructor() {
    effect(() => {
      const pending = this.approvals.pending();
      untracked(() => {
        const ids = new Set(pending.map((approval) => approval.id));
        this.previews.update((previews) =>
          Object.fromEntries(Object.entries(previews).filter(([id]) => ids.has(id))),
        );
        for (const approval of pending) {
          if (approval.permanentDeletion) void this.refreshDeletionPreview(approval.id);
        }
      });
    });
  }

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
    const request = this.approvals.pending().find((approval) => approval.id === id);
    await this.approvals.approve(
      id,
      this.noteFor(id) || undefined,
      request?.permanentDeletion ? this.acknowledgements()[id] === true : false,
    );
  }

  protected async reject(id: string): Promise<void> {
    await this.approvals.reject(id, this.noteFor(id) || undefined);
  }

  protected formatTime(iso: string): string {
    return this.i18n.time(iso);
  }

  protected previewFor(id: string): DeletionPreview | undefined {
    return this.previews()[id];
  }

  protected setAcknowledged(id: string, acknowledged: boolean): void {
    this.acknowledgements.update((values) => ({ ...values, [id]: acknowledged }));
  }

  protected canApprove(request: PendingApproval): boolean {
    if (!request.permanentDeletion) return true;
    const preview = this.previews()[request.id];
    const argumentHash = request.arguments['approvalHash'];
    return this.acknowledgements()[request.id] === true
      && preview?.summary?.status === DeletionManifestReady
      && preview.summary.approvalHash === argumentHash
      && Date.parse(preview.summary.expiresAtUtc) > Date.now()
      && !preview.error;
  }

  protected formatCount(value: number): string {
    return this.i18n.number(value);
  }

  protected formatBytes(value: number): string {
    return this.i18n.bytes(value);
  }

  protected setSearch(id: string, search: string): void {
    this.updatePreview(id, (preview) => ({ ...preview, search }));
  }

  protected async applySearch(id: string): Promise<void> {
    this.updatePreview(id, (preview) => ({ ...preview, cursors: [undefined], cursorIndex: 0 }));
    await this.loadPage(id);
  }

  protected async nextPage(id: string): Promise<void> {
    const preview = this.previews()[id];
    if (!preview?.page?.nextCursor) return;
    this.updatePreview(id, (current) => ({
      ...current,
      cursors: [...current.cursors.slice(0, current.cursorIndex + 1), current.page!.nextCursor!],
      cursorIndex: current.cursorIndex + 1,
    }));
    await this.loadPage(id);
  }

  protected async previousPage(id: string): Promise<void> {
    const preview = this.previews()[id];
    if (!preview || preview.cursorIndex === 0) return;
    this.updatePreview(id, (current) => ({ ...current, cursorIndex: current.cursorIndex - 1 }));
    await this.loadPage(id);
  }

  protected downloadUrl(id: string): string {
    return this.api.approvalDeletionDownloadUrl(id);
  }

  private async refreshDeletionPreview(id: string): Promise<void> {
    if (this.previewRequests.has(id)) return;
    this.previewRequests.add(id);
    const firstLoad = !this.previews()[id];
    if (firstLoad) {
      this.previews.update((previews) => ({
        ...previews,
        [id]: {
          summary: null,
          page: null,
          cursors: [undefined],
          cursorIndex: 0,
          search: '',
          loading: true,
          error: null,
        },
      }));
    }

    try {
      const summary = await this.api.getApprovalDeletionManifest(id);
      this.updatePreview(id, (preview) => ({ ...preview, summary, loading: firstLoad, error: null }));
      if (firstLoad) await this.loadPage(id);
    } catch (error) {
      this.updatePreview(id, (preview) => ({
        ...preview,
        loading: false,
        error: error instanceof Error ? error.message : this.i18n.t('approvals.deletion.error.preview'),
      }));
    } finally {
      this.previewRequests.delete(id);
    }
  }

  private async loadPage(id: string): Promise<void> {
    const preview = this.previews()[id];
    if (!preview) return;
    this.updatePreview(id, (current) => ({ ...current, loading: true, error: null }));
    try {
      const page = await this.api.getApprovalDeletionEntries(
        id,
        preview.cursors[preview.cursorIndex],
        200,
        preview.search || undefined,
      );
      this.updatePreview(id, (current) => ({ ...current, page, loading: false }));
    } catch (error) {
      this.updatePreview(id, (current) => ({
        ...current,
        loading: false,
        error: error instanceof Error ? error.message : this.i18n.t('approvals.deletion.error.entries'),
      }));
    }
  }

  private updatePreview(id: string, update: (preview: DeletionPreview) => DeletionPreview): void {
    this.previews.update((previews) => {
      const preview = previews[id];
      return preview ? { ...previews, [id]: update(preview) } : previews;
    });
  }
}
