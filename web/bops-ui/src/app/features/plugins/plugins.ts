// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { PackageTrustLevel, PackageTrustLevelName, PluginCatalogEntry } from '../../core/api/models';
import { I18n } from '../../core/i18n/i18n';
import type { MessageKey } from '../../core/i18n/messages';
import { TranslatePipe } from '../../core/i18n/translate.pipe';
import { RiskBadge } from '../../shared/risk-badge';
import { PluginsStore } from '../../state/plugins.store';

/** Lifecycle states this UI knows how to present. Anything else is shown generically and offers no lifecycle action. */
const KNOWN_LIFECYCLE_STATES = ['InstalledDisabled', 'Enabled', 'ActivationFailed', 'RecoveryRequired'];

/** A confirmation the operator has been shown, bound to the exact entry (id, version, ETag) they were looking at. */
interface PendingConfirmation {
  kind: 'enable' | 'recover';
  entry: PluginCatalogEntry;
}

@Component({
  selector: 'bops-plugins',
  imports: [RiskBadge, TranslatePipe],
  templateUrl: './plugins.html',
})
export class Plugins implements OnInit {
  protected readonly plugins = inject(PluginsStore);
  protected readonly i18n = inject(I18n);
  protected readonly trustLevels: PackageTrustLevel[] = [0, 1, 2, 3];
  protected readonly trustLevelName = PackageTrustLevelName;

  protected readonly selectedPlugin = computed<PluginCatalogEntry | undefined>(() =>
    this.plugins.entries().find((entry) => entry.id === this.plugins.selectedPluginId()),
  );

  protected readonly confirmation = signal<PendingConfirmation | null>(null);
  protected readonly installFile = signal<File | null>(null);
  protected readonly replaceFile = signal<File | null>(null);
  protected readonly fileError = signal<string | null>(null);

  ngOnInit(): void {
    void this.plugins.refresh();
  }

  protected select(pluginId: string): void {
    this.confirmation.set(null);
    this.replaceFile.set(null);
    this.fileError.set(null);
    this.plugins.selectPlugin(pluginId);
  }

  protected onEnabledFilterChange(value: string): void {
    this.plugins.setEnabledFilter(value === '' ? null : value === 'true');
  }

  protected onTrustFilterChange(value: string): void {
    this.plugins.setTrustFilter(value === '' ? null : (Number(value) as PackageTrustLevel));
  }

  protected formatTime(iso: string): string {
    return this.i18n.dateTime(iso);
  }

  protected stateLabel(state: string | null | undefined): string {
    if (!state) {
      return '—';
    }
    return KNOWN_LIFECYCLE_STATES.includes(state) ? this.i18n.t(`plugins.state.${state}` as MessageKey) : state;
  }

  /** Presentation gate only: a mutation needs a known state and the server-issued ETag to be constructible; the backend still decides. */
  protected canMutate(entry: PluginCatalogEntry): boolean {
    return (
      this.plugins.isAdministrator() &&
      !!entry.lifecycleETag &&
      !!entry.lifecycleState &&
      KNOWN_LIFECYCLE_STATES.includes(entry.lifecycleState)
    );
  }

  protected canRecover(entry: PluginCatalogEntry): boolean {
    return this.plugins.isAdministrator() && !!entry.lifecycleETag && entry.recoveryAvailable === true;
  }

  // ---- archive selection (advisory validation only; the backend validates the archive) ----

  private pick(event: Event): File | null {
    const file = (event.target as HTMLInputElement).files?.item(0) ?? null;
    if (file && !file.name.toLowerCase().endsWith('.zip')) {
      this.fileError.set(this.i18n.t('plugins.upload.notZip'));
      return null;
    }
    if (file && file.size === 0) {
      this.fileError.set(this.i18n.t('plugins.upload.empty'));
      return null;
    }
    this.fileError.set(null);
    return file;
  }

  protected onInstallFile(event: Event): void {
    this.installFile.set(this.pick(event));
  }

  protected onReplaceFile(event: Event): void {
    this.replaceFile.set(this.pick(event));
  }

  protected async install(input: HTMLInputElement): Promise<void> {
    const file = this.installFile();
    if (file && (await this.plugins.installArchive(file))) {
      this.installFile.set(null);
      input.value = '';
    }
  }

  protected async replace(entry: PluginCatalogEntry, input: HTMLInputElement): Promise<void> {
    const file = this.replaceFile();
    if (file && (await this.plugins.replaceArchive(entry, file))) {
      this.replaceFile.set(null);
      input.value = '';
    }
  }

  // ---- confirmed operations ----

  protected ask(kind: PendingConfirmation['kind'], entry: PluginCatalogEntry): void {
    this.plugins.clearNotice();
    this.confirmation.set({ kind, entry });
  }

  protected cancelConfirmation(): void {
    this.confirmation.set(null);
  }

  protected async confirm(): Promise<void> {
    const pending = this.confirmation();
    if (!pending) {
      return;
    }
    // The version sent is the one the operator was shown in this confirmation, never re-read from newer state.
    await (pending.kind === 'enable'
      ? this.plugins.enable(pending.entry, pending.entry.version)
      : this.plugins.recover(pending.entry));
    this.confirmation.set(null);
  }

  protected async disable(entry: PluginCatalogEntry): Promise<void> {
    await this.plugins.disable(entry);
  }
}
