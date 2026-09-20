// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Injectable, isDevMode, signal } from '@angular/core';
import { en } from './en';
import { AVAILABLE_LANGUAGES, DEFAULT_LANGUAGE, Language, LANGUAGES, isLanguage } from './languages';
import type { MessageKey, MessageParams, TranslationKey } from './messages';

const STORAGE_KEY = 'bops-ui-language';

/** The enum groups whose labels live in the catalogues (`enum.<group>.<name>`). */
export type EnumGroup =
  | 'risk'
  | 'taskStatus'
  | 'trust'
  | 'delegationStatus'
  | 'delegationRole'
  | 'roleStatus'
  | 'verification'
  | 'stepOutcome'
  | 'reconciliation'
  | 'evidenceKind'
  | 'blastRadius';

/**
 * The UI's own text, in the language the operator chose. A small in-house service rather than a library: two typed catalogues, a
 * signal holding the active language and `t`. Templates use the `t` pipe; TypeScript uses `t` directly. The choice is a per-viewer
 * convenience kept in `localStorage` (like the theme), and storage that is blocked only means it is not remembered.
 *
 * Only what the UI itself writes is translated. What the API returns and a person or a package wrote (a task's goal, a tool's name
 * and output, a plugin's manifest) and the API's own error messages are shown as they are.
 */
@Injectable({ providedIn: 'root' })
export class I18n {
  readonly available = AVAILABLE_LANGUAGES;
  readonly language = signal<Language>(this.readInitial());

  private readonly reported = new Set<string>();

  constructor() {
    this.apply(this.language());
  }

  setLanguage(language: Language): void {
    if (!isLanguage(language)) {
      return;
    }

    this.language.set(language);
    this.apply(language);
    try {
      localStorage.setItem(STORAGE_KEY, language);
    } catch {
      // Best-effort only: the choice then lasts for this session.
    }
  }

  /**
   * The message for `key` in the active language, with `{name}` placeholders filled from `params` (numbers are formatted for the
   * language). A prefix with `.one`/`.other` variants and a numeric `count` is resolved by the language's plural rules. A key the
   * active language lacks falls back to English and is reported once in development; a raw key is never rendered.
   */
  t(key: TranslationKey, params?: MessageParams): string {
    const template = this.resolve(key, params);
    return template.replace(/\{(\w+)\}/g, (whole, name: string) => {
      const value = params?.[name];
      if (value === undefined) {
        return whole;
      }

      return typeof value === 'number' ? this.number(value) : value;
    });
  }

  /**
   * The label of an enum value, by its name. A value the catalogues do not know (a newer server, a status a package invented) is
   * shown as the server sent it, which is the honest fallback for data rather than for the UI's own text.
   */
  label(group: EnumGroup, name: string | null | undefined): string {
    if (name === null || name === undefined) {
      return '';
    }

    const normalized = group === 'blastRadius' ? name.toLowerCase() : name;
    const key = `enum.${group}.${normalized}`;
    return this.has(key) ? this.t(key as MessageKey) : name;
  }

  /** A time of day in the active language, not the browser's. */
  time(iso: string): string {
    return new Date(iso).toLocaleTimeString(this.language());
  }

  /** A date and time in the active language. */
  dateTime(iso: string | null): string {
    return iso ? new Date(iso).toLocaleString(this.language()) : '';
  }

  number(value: number, options?: Intl.NumberFormatOptions): string {
    return new Intl.NumberFormat(this.language(), options).format(value);
  }

  /** A size in the binary units the API's manifests are counted in (the unit symbols are not words and are not translated). */
  bytes(value: number): string {
    if (value < 1024) {
      return `${this.number(value)} B`;
    }

    const units = ['KiB', 'MiB', 'GiB', 'TiB'];
    let amount = value / 1024;
    let index = 0;
    while (amount >= 1024 && index < units.length - 1) {
      amount /= 1024;
      index++;
    }

    return `${this.number(amount, { minimumFractionDigits: 1, maximumFractionDigits: 1 })} ${units[index]}`;
  }

  /** How long a call took, in the unit that reads best: "820 ms", "4.3 s", "1 min 5 s" (decimal separator and unit words follow the language). */
  duration(ms: number): string {
    if (!Number.isFinite(ms) || ms < 0) {
      return '—';
    }

    if (ms < 1000) {
      return this.t('common.duration.ms', { value: Math.round(ms) });
    }

    const seconds = ms / 1000;
    if (seconds < 60) {
      return this.t('common.duration.s', { value: this.number(seconds, { minimumFractionDigits: 1, maximumFractionDigits: 1 }) });
    }

    const whole = Math.round(seconds);
    const minutes = Math.floor(whole / 60);
    const rest = whole % 60;
    return rest === 0
      ? this.t('common.duration.min', { minutes })
      : this.t('common.duration.minS', { minutes, seconds: rest });
  }

  private resolve(key: TranslationKey, params?: MessageParams): string {
    const count = params?.['count'];
    if (typeof count === 'number' && this.has(`${key}.other`)) {
      const category = new Intl.PluralRules(this.language()).select(count);
      return this.lookup(`${key}.${category}`) ?? this.lookup(`${key}.other`) ?? '';
    }

    return this.lookup(key) ?? '';
  }

  private has(key: string): boolean {
    return Object.prototype.hasOwnProperty.call(en, key);
  }

  private lookup(key: string): string | undefined {
    const active = LANGUAGES[this.language()].messages as Record<string, string>;
    const own = Object.prototype.hasOwnProperty.call(active, key) ? active[key] : undefined;
    if (own !== undefined) {
      return own;
    }

    const fallback = (en as Record<string, string>)[key];
    if (fallback !== undefined && isDevMode() && !this.reported.has(`${this.language()}:${key}`)) {
      this.reported.add(`${this.language()}:${key}`);
      console.warn(`[i18n] '${key}' is missing from the '${this.language()}' catalogue; showing English.`);
    }

    return fallback;
  }

  private readInitial(): Language {
    try {
      const stored = localStorage.getItem(STORAGE_KEY);
      if (isLanguage(stored)) {
        return stored;
      }
    } catch {
      // Fall through to the browser's language.
    }

    const preferred = typeof navigator === 'undefined' ? '' : (navigator.language ?? '').toLowerCase();
    return preferred.startsWith('it') ? 'it' : DEFAULT_LANGUAGE;
  }

  private apply(language: Language): void {
    document.documentElement.lang = language;
  }
}
