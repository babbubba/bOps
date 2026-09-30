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

  /** True after the API refused the credential while the operator was signed in, until they sign in again or sign out on purpose. */
  readonly sessionExpired = signal(false);

  async signIn(token: string): Promise<void> {
    const trimmed = token.trim();
    if (!trimmed) {
      throw new Error('An API key is required.');
    }

    this.token.set(trimmed);
    try {
      this.identity.set(await firstValueFrom(this.http.get<CurrentIdentity>('/api/session/me')));
      this.sessionExpired.set(false);
    } catch (error) {
      this.clear();
      throw error;
    }
  }

  signOut(): void {
    this.clear();
    this.sessionExpired.set(false);
  }

  /** The API refused the credential: back to sign-in, with the reason kept so the sign-in screen can say it. */
  expireSession(): void {
    this.clear();
    this.sessionExpired.set(true);
  }

  private clear(): void {
    this.token.set(null);
    this.identity.set(null);
  }
}
