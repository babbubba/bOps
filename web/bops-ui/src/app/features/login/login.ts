// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../core/auth/auth.service';
import { LanguageSwitch } from '../../core/i18n/language-switch';
import type { MessageKey, MessageParams } from '../../core/i18n/messages';
import { TranslatePipe } from '../../core/i18n/translate.pipe';

const UNREACHABLE = [0, 502, 503, 504];

@Component({
  selector: 'bops-login',
  imports: [FormsModule, LanguageSwitch, TranslatePipe],
  templateUrl: './login.html',
})
export class Login {
  protected readonly auth = inject(AuthService);

  /** The key field. Cleared when the sign-in request completes, whatever its outcome: the key lives only for that request. */
  protected readonly apiKey = signal('');
  /** "Keep me signed in on this device": off by default and never remembered. */
  protected readonly keepSignedIn = signal(false);
  protected readonly submitting = signal(false);
  /** The refusal, as a message key translated at display time (so switching language re-translates it). */
  protected readonly error = signal<{ key: MessageKey; params?: MessageParams } | null>(null);

  protected async submit(): Promise<void> {
    this.submitting.set(true);
    this.error.set(null);
    try {
      await this.auth.signIn(this.apiKey(), this.keepSignedIn());
    } catch (err) {
      this.error.set(this.describe(err));
    } finally {
      this.apiKey.set('');
      this.submitting.set(false);
    }
  }

  protected retry(): void {
    void this.auth.restore();
  }

  /** ADR-0043 §14.3 step 5: one text per refusal, never the server's raw payload or the key. */
  private describe(err: unknown): { key: MessageKey; params?: MessageParams } {
    if (!(err instanceof HttpErrorResponse)) {
      return { key: 'login.error.failed' };
    }

    const code = (err.error as { code?: unknown } | null)?.code;
    if (err.status === 401) return { key: 'login.error.failed' };
    if (err.status === 403 && code === 'viewer_role_required') return { key: 'login.error.viewerRequired' };
    if (err.status === 403 && code === 'csrf_rejected') return { key: 'login.error.origin' };
    if (err.status === 429) {
      const seconds = Number(err.headers?.get('Retry-After'));
      return { key: 'login.error.rateLimited', params: { seconds: Number.isFinite(seconds) && seconds > 0 ? Math.ceil(seconds) : 60 } };
    }

    if (UNREACHABLE.includes(err.status)) return { key: 'common.error.unreachable' };
    return { key: 'common.error.http', params: { status: err.status } };
  }
}
