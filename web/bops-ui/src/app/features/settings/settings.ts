// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { SettingsProviderView } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { SettingsStore } from '../../state/settings.store';

interface ProfileDraft {
  baseUrl: string;
  model: string;
  supportsNativeToolCalling: boolean;
}

@Component({
  selector: 'bops-settings',
  imports: [FormsModule],
  templateUrl: './settings.html',
})
export class Settings implements OnInit {
  protected readonly settings = inject(SettingsStore);
  private readonly auth = inject(AuthService);

  protected readonly isAdministrator = () => this.auth.identity()?.roles.includes('administrator') ?? false;

  protected readonly apiKeyDrafts = new Map<string, string>();
  protected readonly profileDrafts = new Map<string, ProfileDraft>();
  protected readonly expandedProviderId = signal<string | null>(null);

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
    if (!confirm(`Clear the stored API key for '${providerId}'? This cannot be undone from here.`)) {
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
}
