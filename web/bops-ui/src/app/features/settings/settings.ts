// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { FallbackEntry, SettingsProviderView } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { I18n } from '../../core/i18n/i18n';
import type { TranslationKey } from '../../core/i18n/messages';
import { TranslatePipe } from '../../core/i18n/translate.pipe';
import { SettingsStore } from '../../state/settings.store';

/** ADR-0045: the fallback chain holds at most this many candidates. */
export const MAX_FALLBACKS = 3;

/** The one router alias bOps ships as its bootstrap default (ADR-0045 section 10). Not generic router detection. */
const ROUTER_PROVIDER = 'openrouter';
const ROUTER_MODEL = 'openrouter/free';

const SHADOWED_FIELD_LABELS: Record<string, TranslationKey> = {
  baseUrl: 'settings.baseUrl',
  model: 'settings.model',
  supportsNativeToolCalling: 'settings.nativeToolCalling',
};

interface ProfileDraft {
  baseUrl: string;
  model: string;
  supportsNativeToolCalling: boolean;
}

@Component({
  selector: 'bops-settings',
  imports: [FormsModule, TranslatePipe],
  templateUrl: './settings.html',
})
export class Settings implements OnInit {
  protected readonly settings = inject(SettingsStore);
  private readonly auth = inject(AuthService);
  private readonly i18n = inject(I18n);

  protected readonly isAdministrator = () => this.auth.identity()?.roles.includes('administrator') ?? false;

  protected readonly apiKeyDrafts = new Map<string, string>();
  protected readonly profileDrafts = new Map<string, ProfileDraft>();
  protected readonly expandedProviderId = signal<string | null>(null);

  /** The unsaved fallback-chain edit, or `null` when the editor simply mirrors the stored list. */
  private readonly fallbackDraft = signal<FallbackEntry[] | null>(null);
  protected readonly maxFallbacks = MAX_FALLBACKS;

  /** The editable rows: the draft when one exists, otherwise the stored (persisted) list in its stored order. */
  protected readonly fallbackRows = computed<FallbackEntry[]>(
    () => this.fallbackDraft() ?? this.settings.view()?.persistedFallbacks ?? [],
  );

  protected readonly fallbackDirty = computed(() => this.fallbackDraft() !== null);

  protected readonly fallbackComplete = computed(() =>
    this.fallbackRows().every((row) => row.provider.trim() !== '' && row.model.trim() !== ''),
  );

  /**
   * ADR-0045 section 10: the router notice is driven by the EFFECTIVE primary only. A stored OpenRouter/openrouter-free profile that
   * a host override has shadowed is not the router the next execution will use, so it never raises this warning.
   */
  protected readonly routerEffective = computed(() => {
    const view = this.settings.view();
    return (
      view !== null &&
      view.activeProviderId?.toLowerCase() === ROUTER_PROVIDER &&
      view.effectiveModel.toLowerCase() === ROUTER_MODEL
    );
  });

  ngOnInit(): void {
    if (this.isAdministrator()) {
      void this.settings.refresh();
    }
  }

  protected toggleExpanded(providerId: string): void {
    this.expandedProviderId.set(this.expandedProviderId() === providerId ? null : providerId);
    if (!this.profileDrafts.has(providerId)) {
      const existing = this.settings.view()?.providers.find((provider) => provider.providerId === providerId);
      this.profileDrafts.set(providerId, {
        baseUrl: existing?.baseUrl ?? '',
        model: existing?.model ?? '',
        supportsNativeToolCalling: existing?.supportsNativeToolCalling ?? true,
      });
    }
  }

  protected apiKeyDraft(providerId: string): string {
    return this.apiKeyDrafts.get(providerId) ?? '';
  }

  protected setApiKeyDraft(providerId: string, value: string): void {
    this.apiKeyDrafts.set(providerId, value);
  }

  protected profileDraft(providerId: string): ProfileDraft {
    return this.profileDrafts.get(providerId) ?? { baseUrl: '', model: '', supportsNativeToolCalling: true };
  }

  protected async saveKey(providerId: string): Promise<void> {
    const apiKey = this.apiKeyDraft(providerId);
    if (!apiKey) {
      return;
    }

    const succeeded = await this.settings.setProviderKey(providerId, apiKey);
    if (succeeded) {
      this.apiKeyDrafts.delete(providerId);
    }
  }

  protected async clearKey(providerId: string): Promise<void> {
    if (!confirm(this.i18n.t('settings.key.confirmClear', { provider: providerId }))) {
      return;
    }

    await this.settings.clearProviderKey(providerId);
  }

  protected async saveProfile(providerId: string): Promise<void> {
    const draft = this.profileDraft(providerId);
    if (!draft.baseUrl || !draft.model) {
      return;
    }

    await this.settings.setProviderProfile(providerId, {
      baseUrl: draft.baseUrl,
      model: draft.model,
      supportsNativeToolCalling: draft.supportsNativeToolCalling,
      extraParameters: null,
    });
  }

  protected async makeActive(providerId: string): Promise<void> {
    await this.settings.setActiveProvider(providerId);
  }

  protected canActivate(provider: SettingsProviderView): boolean {
    return !provider.isActive && (provider.baseUrl !== null || provider.providerId === this.settings.view()?.activeProviderId);
  }

  protected shadowedFieldLabelKeys(provider: SettingsProviderView): TranslationKey[] {
    return provider.shadowedFields.flatMap((field) => (field in SHADOWED_FIELD_LABELS ? [SHADOWED_FIELD_LABELS[field]] : []));
  }

  // ---- Fallback chain (ADR-0045) ----

  protected providerOptions(): string[] {
    return this.settings.view()?.providers.map((provider) => provider.providerId) ?? [];
  }

  protected addFallback(): void {
    const rows = this.fallbackRows();
    if (rows.length >= MAX_FALLBACKS) {
      return;
    }

    this.fallbackDraft.set([...rows, { provider: this.providerOptions()[0] ?? '', model: '' }]);
  }

  protected removeFallback(index: number): void {
    this.fallbackDraft.set(this.fallbackRows().filter((_, i) => i !== index));
  }

  /** Moves a candidate one place earlier (`-1`) or later (`+1`). Order is the fallback order, so it is never reshuffled implicitly. */
  protected moveFallback(index: number, delta: -1 | 1): void {
    const rows = [...this.fallbackRows()];
    const target = index + delta;
    if (target < 0 || target >= rows.length) {
      return;
    }

    [rows[index], rows[target]] = [rows[target], rows[index]];
    this.fallbackDraft.set(rows);
  }

  protected updateFallback(index: number, field: keyof FallbackEntry, value: string): void {
    this.fallbackDraft.set(this.fallbackRows().map((row, i) => (i === index ? { ...row, [field]: value } : row)));
  }

  /** Saves the complete ordered list. An empty list is the clear operation, so the API sees one intent per gesture. */
  protected async saveFallbacks(): Promise<void> {
    if (!this.fallbackComplete()) {
      return;
    }

    const rows = this.fallbackRows().map((row) => ({ provider: row.provider.trim(), model: row.model.trim() }));
    const succeeded = rows.length === 0 ? await this.settings.clearFallbacks() : await this.settings.setFallbacks(rows);
    if (succeeded) {
      this.fallbackDraft.set(null);
    }
  }

  protected async clearFallbacks(): Promise<void> {
    if (await this.settings.clearFallbacks()) {
      this.fallbackDraft.set(null);
    }
  }

  protected discardFallbackEdits(): void {
    this.fallbackDraft.set(null);
  }
}
