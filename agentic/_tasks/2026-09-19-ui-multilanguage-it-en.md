# Multilingual UI (Italian and English)

Status: **implemented** (2026-09-20; the Delegations screens were migrated with the rest; native review of the Italian still open)
Recommended model effort: **medio**
Repository: `bOps`
Depends on: nothing. It touches only `web/bops-ui`; no API, SDK or persistence change is expected. If one turns
out to be necessary, stop and say why before proceeding.

## Origin

Requested by the operator on 2026-09-19. The whole UI is English text written into the templates and a few
TypeScript files, and the operator works in Italian. This is not the bilingual public README
([`post-v2.0-bilingual-readme`](2026-09-16-post-v2.0-bilingual-readme.md)), which is documentation; this is the
running interface.

## Outcome

An operator can switch the UI between Italian and English at any time, without reloading, and the choice is
remembered. English stays the default when nothing says otherwise, and the source of truth for every string.

## Size

Counted on 2026-09-19 (after the model-call "?" details were added): about 85 text nodes across the six templates
(`app`, `login`, `dashboard`, `approvals`, `plugins`, `settings`), about 15 `placeholder`/`title`/`aria-label`
attributes, about 15 `cond ? 'a' : 'b'` label pairs, about 25 strings built in TypeScript (the status, risk and
outcome name tables in `core/api/models.ts`, the "Something went wrong." fallbacks in five stores, the login and
approval messages, `Clear the stored API key for …`), and four places that format a date or number with the
browser's default locale. That is one task, not a plan.

## Decisions to confirm at the start

- [x] **Mechanism.** Recommended: a small in-house, dependency-free translation service. Two typed catalogues
      (`en.ts` is the shape, `it.ts` must satisfy it, so a missing or extra key is a compile error), a signal holding
      the active language, and a `t` pipe/function. Rejected unless the operator prefers otherwise: Angular's
      `@angular/localize` (build-time; one bundle per language, so no switching without a reload and one served
      bundle per locale, which the Aspire/`ng serve` setup does not have) and a runtime library such as Transloco or
      ngx-translate (a new dependency for about 130 strings; every dependency goes through `dependency-review`).
- [x] **What is translated and what is not.** Translated: everything the UI itself writes. Not translated: data the
      API returns and the operator wrote or a package declared (the task goal, tool names and descriptions,
      observations and tool output, plugin manifests), and the API's own error `message`s, which stay English. Say so
      in the UI docs. Making server messages translatable needs an error-code contract in the API; that is a separate
      follow-up, not part of this task.
- [x] **Where the choice is kept.** `localStorage` under `bops-ui-language`, read and written in `try/catch` like
      `core/theme.ts` (storage can be blocked), never a cookie or URL parameter.

## Design

- [x] Initial language: the stored choice, else `it` when `navigator.language` starts with `it`, else `en`. Set
      `document.documentElement.lang` whenever it changes.
- [x] A visible switch in the sidebar next to the theme toggle (for example `EN | IT`), two buttons with
      `aria-pressed`, keyboard reachable, not a hidden setting.
- [x] Keys are stable, dot-separated and grouped by screen (`dashboard.step.modelInfo.responseTime`); a value may
      hold named placeholders (`{count}`), never string concatenation of translated fragments.
- [x] Plurals use `Intl.PluralRules` for the active language, not `(s)` suffixes (`3 step(s)` becomes "3 passi" /
      "1 passo" / "3 steps" / "1 step").
- [x] Dates and numbers use `Intl` with the active language explicitly (`toLocaleTimeString`, `NumberFormat` and the
      `number` pipe in the dashboard "?" details today follow the browser locale, which can disagree with the chosen
      language). Angular's pipes read `LOCALE_ID` once at injection, so they need the locale passed in or a small
      wrapper; do not switch `LOCALE_ID` at runtime.
- [x] A missing key falls back to English and logs once in development; it never renders a raw key or throws.
- [x] Enum labels (task status, risk level, tool outcome, policy mode) live in the catalogues, keyed by the enum name,
      not by the number the API sends.

## Implementation

- [x] Add the service, the two catalogues and the pipe; register them in `app.config.ts`.
- [x] Migrate the six templates and the TypeScript strings listed above. Include the model-call "?" details and the
      duration formatter (`shared/model-call-format.ts` returns `ms`, `s`, `min` units; they move into the catalogue
      too).
- [x] Add the language switch and `<html lang>`.
- [x] Italian text is written for an operator, not word for word: keep the technical terms operators already use
      (task, plugin, policy, token, audit) and translate the sentences around them.

## Tests and documentation

- [x] Parity: both catalogues have exactly the same keys and the same placeholder names (a spec, in addition to the
      compile-time check).
- [x] No English left behind: a spec or script that fails when a template has a text node, `placeholder`, `title` or
      `aria-label` that does not go through the translation, so a later screen cannot regress it silently. The V1.2-K
      delegation screens must pass it.
- [x] Switching language re-renders the dashboard, approvals, plugins and settings in Italian and back, without a
      reload; the choice survives a reload; blocked storage still works for the session.
- [x] Plural and date/number formatting for `en` and `it`, including 0, 1, 2 and 1000.
- [x] The existing UI specs keep passing with English as the default.
- [x] Angular production build and headless tests.
- [ ] Checked once in a real browser against a live API, in both languages and both themes (not done).
- [x] `web/bops-ui/README.md` says how to add a string, how to add a language and what is deliberately not
      translated; `CHANGELOG.md` records the change.
- [ ] A native Italian speaker reviews the Italian catalogue before it is called done (the catalogue was written by the assistant).

## Out of scope

- The public README and the `agentic/` documents (see the post-V2.0 bilingual README task).
- `bOps.Cli` output and the API's messages.
- More than two languages. The mechanism should make a third a catalogue file and a registration line, but no third
  language is added here.
- Right-to-left layout.

## Definition of Done

- [x] An operator can use every screen in Italian and in English and switch without reloading.
- [x] No user-visible string is hard-coded outside the catalogues, enforced by a test.
- [x] No new runtime dependency, or the one added is justified in the task and passes `dependency-review`.
- [x] No API, `bOps.Abstractions` or persistence change; the operator-facing README, CHANGELOG and this task agree.

## Evidence

Verified on this Windows machine on 2026-09-20.

| Check | Result |
|---|---|
| Production build | `ng build`: succeeds, strict templates (a key that is not in `en.ts` does not compile). |
| Headless tests | 129 passed, 0 failed (91 before): catalogue parity (keys, placeholders, plural variants), language choice and persistence, blocked storage, plurals for 0, 1, 2 and 1000 in `en` and `it`, dates, numbers, sizes and durations, and each screen switching to Italian and back. |
| No English left behind | `npm run lint:i18n` (also in CI): 10 templates scanned, 22 self-test cases; putting English text back in `plugins.html` was reported with its line. |
| Mutation | 17 mutants of the service, pipe and switch: 13 killed, 1 does not build, 3 equivalent (English and Italian plural rules agree for the tested counts; a raw-key fallback that is unreachable because English has every key; the dashboard time mutant is equivalent only on a machine whose default locale is Italian, and is expected to die on the en-US CI runner). |

## Interpretations made

1. **Mechanism and storage** as recommended: in-house service, `localStorage` `bops-ui-language`, no dependency.
2. **Provided at start-up** by `provideAppInitializer` rather than by registration, since the service is `providedIn: 'root'`.
3. **English labels are unchanged** where they were raw enum names (`MaxStepsReached`); only Italian reads as words, so the existing specs pass as they were.
4. **Inline code in a sentence is dropped** (the settings source line named `ModelProvider__Provider` in `<code>`), because a pipe returns text.
5. **The language switch is also on the sign-in screen.**
6. **Not done:** a browser run against a live API and the native review of the Italian; both are open above.
