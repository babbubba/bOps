// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, inject } from '@angular/core';
import { I18n } from './i18n';
import { TranslatePipe } from './translate.pipe';

/** The language switch: one button per language, `aria-pressed` on the active one, always visible and reachable by keyboard. */
@Component({
  selector: 'bops-language-switch',
  imports: [TranslatePipe],
  template: `
    <div role="group" [attr.aria-label]="'app.language.label' | t" class="flex gap-1">
      @for (option of i18n.available; track option.code) {
        <button
          type="button"
          [attr.aria-pressed]="i18n.language() === option.code"
          [attr.aria-label]="'app.language.switchTo' | t: { language: option.name }"
          [attr.lang]="option.code"
          (click)="i18n.setLanguage(option.code)"
          class="rounded-md border px-2 py-1 text-xs font-semibold uppercase focus-visible:outline-2 focus-visible:outline-accent"
          [class]="
            i18n.language() === option.code
              ? 'border-accent bg-accent/10 text-accent'
              : 'border-default text-muted hover:bg-accent/5 hover:text-text'
          "
        >
          {{ option.code }}
        </button>
      }
    </div>
  `,
})
export class LanguageSwitch {
  protected readonly i18n = inject(I18n);
}
