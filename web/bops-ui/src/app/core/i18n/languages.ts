// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { en } from './en';
import { it } from './it';
import type { Messages } from './messages';

/**
 * The languages the UI speaks. Adding one is a catalogue file typed as `Messages` and one line here; `en` stays the source of truth
 * and the fallback. `name` is the language's own name, shown on the switch, and is never translated.
 */
export const LANGUAGES = {
  en: { name: 'English', messages: en as Messages },
  it: { name: 'Italiano', messages: it },
} as const;

export type Language = keyof typeof LANGUAGES;

export const DEFAULT_LANGUAGE: Language = 'en';

export function isLanguage(value: unknown): value is Language {
  return typeof value === 'string' && Object.prototype.hasOwnProperty.call(LANGUAGES, value);
}

export const AVAILABLE_LANGUAGES = (Object.keys(LANGUAGES) as Language[]).map((code) => ({
  code,
  name: LANGUAGES[code].name,
}));
