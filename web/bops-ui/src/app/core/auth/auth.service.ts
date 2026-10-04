// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpClient, HttpContext, HttpContextToken, HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom, timeout } from 'rxjs';
import { backoffMs } from '../streaming/task-events';

export interface CurrentIdentity {
  id: string;
  displayName: string;
  roles: string[];
}

/**
 * Where the browser session stands (ADR-0043 §14.1). `initializing` until the boot `GET /api/session/me` answers;
 * `unavailable` when the API could not be asked (no answer, 429, 5xx, timeout) — never confused with a refused credential.
 */
export type AuthStatus = 'initializing' | 'authenticated' | 'unauthenticated' | 'session-expired' | 'unavailable';

/** Marks a request whose 401 its caller handles itself (boot, sign-out), so the interceptor never calls it an expiry. */
export const OWN_UNAUTHORIZED = new HttpContextToken<boolean>(() => false);

/** How long the boot identity request may take before the API counts as unavailable. */
export const RESTORE_TIMEOUT_MS = 10_000;

/** Statuses that mean "the API could not answer now", not "the credential is wrong". */
function isUnavailable(err: unknown): boolean {
  if (!(err instanceof HttpErrorResponse)) return true;
  return err.status === 0 || err.status === 429 || err.status >= 500;
}

/**
 * The browser's authentication state. The API key is exchanged once for a server-side session held in an `HttpOnly` cookie the
 * page cannot read (ADR-0043): the key is only ever a function argument, never a field, a signal or browser storage, and no
 * request carries `Authorization`. A reload restores the session through `GET /api/session/me`.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);

  readonly status = signal<AuthStatus>('initializing');
  readonly identity = signal<CurrentIdentity | null>(null);
  readonly authenticated = computed(() => this.status() === 'authenticated');

  /** True after the API refused the session while the operator was signed in, until they sign in again or sign out on purpose. */
  readonly sessionExpired = computed(() => this.status() === 'session-expired');
  readonly unavailable = computed(() => this.status() === 'unavailable');

  /** A sign-out the server did not confirm: the session may still be valid, so the UI stays signed in and says so. */
  readonly signOutFailed = signal(false);

  /** A deliberate sign-out is in flight: a 401 answering a request sent before it is part of signing out, never an expiry. */
  readonly signingOut = signal(false);

  private retryTimer: ReturnType<typeof setTimeout> | undefined;
  private retryFailures = 0;
  private restoring: Promise<void> | null = null;
  private readonly wake = (): void => {
    if (this.status() === 'unavailable' && (typeof document === 'undefined' || document.visibilityState !== 'hidden')) {
      void this.restore();
    }
  };

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stopRetrying());
  }

  /** Boot (and retry while unavailable): asks the API who the browser session belongs to. Never rejects. */
  restore(): Promise<void> {
    this.restoring ??= this.ask().finally(() => (this.restoring = null));
    return this.restoring;
  }

  /**
   * Exchanges `apiKey` for a browser session. The key is sent once in a JSON body and not kept anywhere; the caller clears its own
   * field. Rejects with the `HttpErrorResponse` on failure, leaving the current status as it was.
   */
  async signIn(apiKey: string, keepSignedIn = false): Promise<void> {
    const key = apiKey.trim();
    if (!key) {
      throw new Error('An API key is required.');
    }

    const identity = await firstValueFrom(
      this.http.post<CurrentIdentity>('/api/session', { apiKey: key, keepSignedIn }),
    );
    this.becomeAuthenticated(identity);
  }

  /** Ends the browser session on the server. 204 or 401 mean signed out; anything else leaves the operator signed in with an error. */
  async signOut(): Promise<void> {
    this.signOutFailed.set(false);
    this.signingOut.set(true);
    try {
      await firstValueFrom(
        this.http.delete<void>('/api/session', { context: new HttpContext().set(OWN_UNAUTHORIZED, true) }),
      );
    } catch (error) {
      if (!(error instanceof HttpErrorResponse && error.status === 401)) {
        this.signOutFailed.set(true);
        return;
      }
    } finally {
      this.signingOut.set(false);
    }

    this.identity.set(null);
    this.status.set('unauthenticated');
  }

  /** The API refused the session while signed in: back to sign-in, with the reason kept. A no-op in any other status. */
  expireSession(): void {
    if (this.status() !== 'authenticated' || this.signingOut()) return;
    this.identity.set(null);
    this.status.set('session-expired');
  }

  private async ask(): Promise<void> {
    try {
      const identity = await firstValueFrom(
        this.http
          .get<CurrentIdentity>('/api/session/me', { context: new HttpContext().set(OWN_UNAUTHORIZED, true) })
          .pipe(timeout(RESTORE_TIMEOUT_MS)),
      );
      this.becomeAuthenticated(identity);
    } catch (error) {
      if (this.status() === 'authenticated') return;
      if (isUnavailable(error)) {
        this.becomeUnavailable();
      } else {
        this.stopRetrying();
        this.identity.set(null);
        this.status.set(this.status() === 'session-expired' ? 'session-expired' : 'unauthenticated');
      }
    }
  }

  private becomeAuthenticated(identity: CurrentIdentity): void {
    this.stopRetrying();
    this.signOutFailed.set(false);
    this.identity.set(identity);
    this.status.set('authenticated');
  }

  /** Unavailable is retried on the HARDEN-4 backoff schedule, and at once when the browser comes back online or the tab is shown. */
  private becomeUnavailable(): void {
    this.identity.set(null);
    this.status.set('unavailable');
    clearTimeout(this.retryTimer);
    this.retryTimer = setTimeout(() => void this.restore(), backoffMs(this.retryFailures++));
    if (typeof window !== 'undefined') {
      window.addEventListener('online', this.wake);
      document.addEventListener('visibilitychange', this.wake);
    }
  }

  private stopRetrying(): void {
    clearTimeout(this.retryTimer);
    this.retryTimer = undefined;
    this.retryFailures = 0;
    if (typeof window !== 'undefined') {
      window.removeEventListener('online', this.wake);
      document.removeEventListener('visibilitychange', this.wake);
    }
  }
}
