// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// "No English left behind": fails when a template has text the UI wrote that does not go through the translation, so a later screen
// cannot regress it silently. It reads every `src/app/**/*.html` and every inline `template:` in a component, and reports:
//   - a text node with letters in it (anything that is not `{{ ... }}` or Angular control flow);
//   - a static `placeholder`, `title`, `aria-label` or `alt` attribute with letters in it;
//   - a bound one (`[placeholder]`, `[attr.title]`, ...) or an interpolation that holds a string literal with letters and does not
//     go through the `t` pipe or `i18n.`.
// The product name (bOps) is a brand, not a word. Data a screen shows (`{{ task.goal }}`) is an interpolation with no literal and passes. Run `node scripts/check-i18n.mjs`; the
// same script proves itself with `--self-test`.

import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = fileURLToPath(new URL('../src/app', import.meta.url));
const TEXT_ATTRIBUTES = ['placeholder', 'title', 'aria-label', 'alt'];
const LETTER = /\p{L}/u;

/** Blanks a matched segment but keeps its newlines, so what is reported still has the right line number. */
const blank = (segment) => segment.replace(/[^\n]/g, ' ');

const lineOf = (source, index) => source.slice(0, index).split('\n').length;

/** True when an expression is translated: it goes through the pipe or the service. */
const translated = (expression) => /\|\s*t\b/.test(expression) || /\bi18n\./.test(expression) || /\bt\(/.test(expression);

/** True when an expression holds a quoted literal with a letter in it (English written into the template). */
const hasWordLiteral = (expression) => {
  for (const match of expression.matchAll(/'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)"/g)) {
    if (LETTER.test(match[1] ?? match[2] ?? '')) return true;
  }
  return false;
};

/** Returns the violations of one template as `{ line, message }`. `lineOffset` is where the template starts in its file. */
export function scanTemplate(template, lineOffset = 0) {
  const found = [];
  const report = (index, message) => found.push({ line: lineOffset + lineOf(template, index), message });
  let rest = template;

  // Comments.
  rest = rest.replace(/<!--[\s\S]*?-->/g, blank);

  // Tags: check their text attributes, then take them out.
  const tag = /<\/?[a-zA-Z][\w-]*(?:"[^"]*"|'[^']*'|[^>"'])*>/g;
  for (const match of rest.matchAll(tag)) {
    const source = match[0];
    for (const attr of match[0].matchAll(/(?<=\s)([\w.:@()[\]-]+)\s*=\s*(?:"([^"]*)"|'([^']*)')/g)) {
      const [, name, doubleQuoted, singleQuoted] = attr;
      const value = doubleQuoted ?? singleQuoted ?? '';
      const at = match.index + source.indexOf(attr[0]);
      const plain = TEXT_ATTRIBUTES.includes(name);
      const bound = TEXT_ATTRIBUTES.some((a) => name === `[${a}]` || name === `[attr.${a}]`);

      if (plain && LETTER.test(value.replace(/\{\{[\s\S]*?\}\}/g, ''))) {
        report(at, `attribute ${name}="${value}" is not translated`);
      } else if (bound && !translated(value) && hasWordLiteral(value)) {
        report(at, `bound ${name}="${value}" holds a literal that is not translated`);
      }
    }
  }
  rest = rest.replace(tag, blank);

  // Interpolations: a literal with letters that does not go through the translation is English in the template.
  for (const match of rest.matchAll(/\{\{([\s\S]*?)\}\}/g)) {
    if (!translated(match[1]) && hasWordLiteral(match[1])) {
      report(match.index, `interpolation {{${match[1]}}} holds a literal that is not translated`);
    }
  }
  rest = rest.replace(/\{\{[\s\S]*?\}\}/g, blank);

  // Angular control flow and entities are syntax, not text.
  rest = rest
    .replace(/@(?:if|for|switch|case|defer)\s*\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)/g, blank)
    .replace(/@let\s+[^;]+;/g, blank)
    .replace(/@(?:else if\s*\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)|else|default|empty)/g, blank)
    .replace(/&#?\w+;/g, blank);

  // What is left is text a person reads.
  rest.split('\n').forEach((line, index) => {
    if (LETTER.test(line.replace(/\bbOps\b/g, ''))) {
      found.push({ line: lineOffset + index + 1, message: `text "${line.trim()}" is not translated` });
    }
  });

  return found;
}

/** The templates of a file: the whole of an `.html`, or each inline `template: \`...\`` of a `.ts`. */
export function templatesOf(path, source) {
  if (path.endsWith('.html')) return [{ template: source, lineOffset: 0 }];
  return [...source.matchAll(/template:\s*`([\s\S]*?)`/g)].map((m) => ({
    template: m[1],
    lineOffset: lineOf(source, m.index + m[0].indexOf('`')) - 1,
  }));
}

function* files(directory) {
  for (const name of readdirSync(directory)) {
    const path = join(directory, name);
    if (statSync(path).isDirectory()) yield* files(path);
    else if ((name.endsWith('.html') || name.endsWith('.ts')) && !name.endsWith('.spec.ts')) yield path;
  }
}

function scanTree() {
  const violations = [];
  let scanned = 0;
  for (const path of files(ROOT)) {
    for (const { template, lineOffset } of templatesOf(path, readFileSync(path, 'utf8'))) {
      scanned++;
      for (const { line, message } of scanTemplate(template, lineOffset)) {
        violations.push(`${relative(process.cwd(), path)}:${line}: ${message}`);
      }
    }
  }
  return { violations, scanned };
}

function selfTest() {
  const cases = [
    ['plain text', '<p>Hello there</p>', 1],
    ['text after an interpolation', '<p>{{ name }} logged in</p>', 1],
    ['a translated text', "<p>{{ 'a.key' | t }}</p>", 0],
    ['a translated ternary', "<p>{{ (ok ? 'a.yes' : 'a.no') | t }}</p>", 0],
    ['data only', '<p>{{ task.goal }} · {{ count }}</p>', 0],
    ['a literal without letters', "<p>{{ value ?? '—' }}</p>", 0],
    ['an English literal in an interpolation', "<p>{{ ok ? 'Yes' : 'No' }}</p>", 1],
    ['a static placeholder', '<input placeholder="Type here" />', 1],
    ['a static title', '<button title="Close">{{ x }}</button>', 1],
    ['a static aria-label', '<div aria-label="Roles"></div>', 1],
    ['a translated bound attribute', `<input [placeholder]="'a.key' | t" />`, 0],
    ['a translated attr binding', `<div [attr.aria-label]="'a.key' | t"></div>`, 0],
    ['an untranslated bound attribute', `<input [placeholder]="'Type here'" />`, 1],
    ['an untranslated attr binding', `<div [attr.title]="'Close'"></div>`, 1],
    ['a data-bound attribute', '<input [placeholder]="hint" />', 0],
    ['an empty alt', '<img src="a.png" alt="" />', 0],
    ['control flow is not text', '@if (a) {\n  <p>{{ x }}</p>\n} @else {\n  <p>{{ y }}</p>\n}', 0],
    ['English inside control flow', '@if (a) {\n  Not signed in\n}', 1],
    ['a case with a string', "@switch (s) {\n  @case ('A') { {{ 'a.k' | t }} }\n  @default { {{ 'a.d' | t }} }\n}", 0],
    ['an entity is not text', '<p>{{ a }}&#64;{{ b }}&nbsp;</p>', 0],
    ['the product name is not translated', '<span>bOps</span>', 0],
    ['a comment is not text', '<!-- Not shown -->\n<p>{{ a }}</p>', 0],
  ];

  let failed = 0;
  for (const [name, template, expected] of cases) {
    const got = scanTemplate(template).length;
    if (got !== expected) {
      failed++;
      console.error(`self-test FAILED: ${name}: expected ${expected} violation(s), got ${got}`);
    }
  }

  if (failed > 0) process.exit(1);
  console.log(`check-i18n self-test: ${cases.length} cases ok`);
}

if (process.argv.includes('--self-test')) {
  selfTest();
} else if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const { violations, scanned } = scanTree();
  if (violations.length > 0) {
    console.error(`check-i18n: ${violations.length} string(s) written into a template without going through the translation:\n`);
    for (const violation of violations) console.error(`  ${violation}`);
    console.error('\nAdd the string to src/app/core/i18n/en.ts and it.ts and use the t pipe (see web/bops-ui/README.md).');
    process.exit(1);
  }
  console.log(`check-i18n: ${scanned} template(s) scanned, no untranslated text.`);
}
