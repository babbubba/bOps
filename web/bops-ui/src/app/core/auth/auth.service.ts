// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export interface CurrentIdentity {
  id: string;
  displayName: string;
  roles: string[];
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);

  readonly token = signal<string | null>(null);
  readonly identity = signal<CurrentIdentity | null>(null);
  readonly authenticated = computed(() => this.identity() !== null);

  async signIn(token: string): Promise<void> {
    const trimmed = token.trim();
    if (!trimmed) {
      throw new Error('An API key is required.');
    }

    this.token.set(trimmed);
    try {
      this.identity.set(await firstValueFrom(this.http.get<CurrentIdentity>('/api/session/me')));
    } catch (error) {
      this.signOut();
      throw error;
    }
  }

  signOut(): void {
    this.token.set(null);
    this.identity.set(null);
  }
}
