# Multilingual UI (Italian and English)

Status: **planned** (independent of the V1.2 chain; do it before V1.2-K so the delegation screens are born translated)
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

- [ ] **Mechanism.** Recommended: a small in-house, dependency-free translation service. Two typed catalogues
      (`en.ts` is the shape, `it.ts` must satisfy it, so a missing or extra key is a compile error), a signal holding
      the active language, and a `t` pipe/function. Rejected unless the operator prefers otherwise: Angular's
      `@angular/localize` (build-time; one bundle per language, so no switching without a reload and one served
      bundle per locale, which the Aspire/`ng serve` setup does not have) and a runtime library such as Transloco or
      ngx-translate (a new dependency for about 130 strings; every dependency goes through `dependency-review`).
- [ ] **What is translated and what is not.** Translated: everything the UI itself writes. Not translated: data the
      API returns and the operator wrote or a package declared (the task goal, tool names and descriptions,
      observations and tool output, plugin manifests), and the API's own error `message`s, which stay English. Say so
      in the UI docs. Making server messages translatable needs an error-code contract in the API; that is a separate
      follow-up, not part of this task.
- [ ] **Where the choice is kept.** `localStorage` under `bops-ui-language`, read and written in `try/catch` like
      `core/theme.ts` (storage can be blocked), never a cookie or URL parameter.

## Design

- [ ] Initial language: the stored choice, else `it` when `navigator.language` starts with `it`, else `en`. Set
      `document.documentElement.lang` whenever it changes.
- [ ] A visible switch in the sidebar next to the theme toggle (for example `EN | IT`), two buttons with
      `aria-pressed`, keyboard reachable, not a hidden setting.
- [ ] Keys are stable, dot-separated and grouped by screen (`dashboard.step.modelInfo.responseTime`); a value may
      hold named placeholders (`{count}`), never string concatenation of translated fragments.
- [ ] Plurals use `Intl.PluralRules` for the active language, not `(s)` suffixes (`3 step(s)` becomes "3 passi" /
      "1 passo" / "3 steps" / "1 step").
- [ ] Dates and numbers use `Intl` with the active language explicitly (`toLocaleTimeString`, `NumberFormat` and the
      `number` pipe in the dashboard "?" details today follow the browser locale, which can disagree with the chosen
      language). Angular's pipes read `LOCALE_ID` once at injection, so they need the locale passed in or a small
      wrapper; do not switch `LOCALE_ID` at runtime.
- [ ] A missing key falls back to English and logs once in development; it never renders a raw key or throws.
- [ ] Enum labels (task status, risk level, tool outcome, policy mode) live in the catalogues, keyed by the enum name,
      not by the number the API sends.

## Implementation

- [ ] Add the service, the two catalogues and the pipe; register them in `app.config.ts`.
- [ ] Migrate the six templates and the TypeScript strings listed above. Include the model-call "?" details and the
      duration formatter (`shared/model-call-format.ts` returns `ms`, `s`, `min` units; they move into the catalogue
      too).
- [ ] Add the language switch and `<html lang>`.
- [ ] Italian text is written for an operator, not word for word: keep the technical terms operators already use
      (task, plugin, policy, token, audit) and translate the sentences around them.

## Tests and documentation

- [ ] Parity: both catalogues have exactly the same keys and the same placeholder names (a spec, in addition to the
      compile-time check).
- [ ] No English left behind: a spec or script that fails when a template has a text node, `placeholder`, `title` or
      `aria-label` that does not go through the translation, so a later screen cannot regress it silently. The V1.2-K
      delegation screens must pass it.
- [ ] Switching language re-renders the dashboard, approvals, plugins and settings in Italian and back, without a
      reload; the choice survives a reload; blocked storage still works for the session.
- [ ] Plural and date/number formatting for `en` and `it`, including 0, 1, 2 and 1000.
- [ ] The existing UI specs keep passing with English as the default.
- [ ] Angular production build and headless tests; checked once in a real browser, in both languages and both themes.
- [ ] `web/bops-ui/README.md` says how to add a string, how to add a language and what is deliberately not
      translated; `CHANGELOG.md` records the change.
- [ ] A native Italian speaker reviews the Italian catalogue before it is called done.

## Out of scope

- The public README and the `agentic/` documents (see the post-V2.0 bilingual README task).
- `bOps.Cli` output and the API's messages.
- More than two languages. The mechanism should make a third a catalogue file and a registration line, but no third
  language is added here.
- Right-to-left layout.

## Definition of Done

- [ ] An operator can use every screen in Italian and in English and switch without reloading.
- [ ] No user-visible string is hard-coded outside the catalogues, enforced by a test.
- [ ] No new runtime dependency, or the one added is justified in the task and passes `dependency-review`.
- [ ] No API, `bOps.Abstractions` or persistence change; the operator-facing README, CHANGELOG and this task agree.
