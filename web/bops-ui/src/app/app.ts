// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ThemeService } from './core/theme';
import { ApprovalsStore } from './state/approvals.store';
import { DelegationsStore } from './state/delegations.store';
import { AuthService } from './core/auth/auth.service';
import { Login } from './features/login/login';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, Login],
  templateUrl: './app.html',
})
export class App {
  protected readonly theme = inject(ThemeService);
  protected readonly approvals = inject(ApprovalsStore);
  protected readonly delegations = inject(DelegationsStore);
  protected readonly auth = inject(AuthService);

  protected toggleTheme(): void {
    this.theme.toggle();
  }
}
