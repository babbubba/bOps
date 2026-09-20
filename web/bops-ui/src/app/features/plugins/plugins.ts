// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, computed, inject, OnInit } from '@angular/core';
import { PackageTrustLevel, PackageTrustLevelName, PluginCatalogEntry } from '../../core/api/models';
import { I18n } from '../../core/i18n/i18n';
import { TranslatePipe } from '../../core/i18n/translate.pipe';
import { RiskBadge } from '../../shared/risk-badge';
import { PluginsStore } from '../../state/plugins.store';

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

  ngOnInit(): void {
    void this.plugins.refresh();
  }

  protected select(pluginId: string): void {
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
}
