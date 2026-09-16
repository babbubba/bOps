// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../core/auth/auth.service';

@Component({
  selector: 'bops-login',
  imports: [FormsModule],
  templateUrl: './login.html',
})
export class Login {
  private readonly auth = inject(AuthService);

  protected readonly token = signal('');
  protected readonly submitting = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async submit(): Promise<void> {
    this.submitting.set(true);
    this.error.set(null);
    try {
      await this.auth.signIn(this.token());
      this.token.set('');
    } catch {
      this.error.set('Authentication failed. Check the local API key and try again.');
    } finally {
      this.submitting.set(false);
    }
  }
}
