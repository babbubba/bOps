// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Pipe, PipeTransform, inject } from '@angular/core';
import { I18n } from './i18n';
import type { MessageParams, TranslationKey } from './messages';

/**
 * `{{ 'dashboard.title' | t }}` and `{{ 'dashboard.task.meta' | t: { count: n, time: x } }}`. Impure on purpose: the result depends on
 * the active language, which is not one of its inputs, so it is re-evaluated on each change detection and a switch takes effect
 * without a reload. The key is typed, so a key that is not in the catalogue does not compile.
 */
@Pipe({ name: 't', pure: false })
export class TranslatePipe implements PipeTransform {
  private readonly i18n = inject(I18n);

  transform(key: TranslationKey, params?: MessageParams): string {
    return this.i18n.t(key, params);
  }
}
