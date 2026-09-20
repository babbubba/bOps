// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../core/auth/auth.service';
import { LanguageSwitch } from '../../core/i18n/language-switch';
import type { MessageKey } from '../../core/i18n/messages';
import { TranslatePipe } from '../../core/i18n/translate.pipe';

@Component({
  selector: 'bops-login',
  imports: [FormsModule, LanguageSwitch, TranslatePipe],
  templateUrl: './login.html',
})
export class Login {
  private readonly auth = inject(AuthService);

  protected readonly token = signal('');
  protected readonly submitting = signal(false);
  protected readonly error = signal<MessageKey | null>(null);

  protected async submit(): Promise<void> {
    this.submitting.set(true);
    this.error.set(null);
    try {
      await this.auth.signIn(this.token());
      this.token.set('');
    } catch {
      this.error.set('login.error.failed');
    } finally {
      this.submitting.set(false);
    }
  }
}
