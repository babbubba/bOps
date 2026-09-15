// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, inject } from '@angular/core';
import { ProvidersStore } from '../../state/providers.store';

@Component({
  selector: 'bops-settings',
  imports: [],
  templateUrl: './settings.html',
})
export class Settings {
  protected readonly providers = inject(ProvidersStore);

  protected isActive(providerId: string): boolean {
    return this.providers.active()?.provider === providerId;
  }
}
