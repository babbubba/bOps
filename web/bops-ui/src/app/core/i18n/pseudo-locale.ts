// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

/** The locale name understood only by the UI test harness. It is deliberately absent from the production language switch. */
export const PSEUDO_LOCALE = 'pseudo' as const;
export type PseudoLocale = typeof PSEUDO_LOCALE;

const PROTECTED_PART = /(\{\w+\}|<[^>]*>)/g;
const VISIBLE_CHARACTER = /[\p{L}\p{N}]/u;
const ACCENTS: Record<string, string> = {
  a: 'å', A: 'Å', e: 'ë', E: 'Ë', i: 'ï', I: 'Ï', o: 'ö', O: 'Ö',
  u: 'ü', U: 'Ü', c: 'ç', C: 'Ç', n: 'ñ', N: 'Ñ', y: 'ÿ', Y: 'Ÿ',
};

function expandText(text: string): string {
  let visible = 0;
  let expanded = '';

  for (const character of text) {
    expanded += ACCENTS[character] ?? character;
    if (VISIBLE_CHARACTER.test(character) && ++visible % 5 === 0) {
      expanded += '···';
    }
  }

  return expanded;
}

/**
 * Deterministically accents and expands visible copy by roughly 40%, while leaving interpolation placeholders and markup intact.
 * The surrounding brackets make it obvious in screenshots that the test locale, rather than English, is active.
 */
export function pseudoLocalize(message: string): string {
  const parts = message.split(PROTECTED_PART);
  return `⟦${parts.map((part) => (/^(\{\w+\}|<[^>]*>)$/.test(part) ? part : expandText(part))).join('')}⟧`;
}
