# BopsUi

This project was generated using [Angular CLI](https://github.com/angular/angular-cli) version 20.3.37.

## Language (English and Italian)

The UI speaks English and Italian and switches between them without a reload: the `EN | IT` buttons sit in the sidebar (and on the
sign-in screen). The choice is kept in `localStorage` under `bops-ui-language` (a per-viewer convenience, never a cookie or a URL
parameter; blocked storage only means it is not remembered). With nothing stored the UI is Italian when the browser's language starts
with `it` and English otherwise, and `<html lang>` follows the active language. English is the source of truth and the fallback.

The mechanism is small and has no dependency: `src/app/core/i18n/` holds `en.ts` (the shape), `it.ts` (typed against it, so a missing
or extra key is a compile error), the `I18n` service (a signal holding the language, `t`, and locale-aware time, number, size and
duration formatting) and the `t` pipe.

### What is translated, and what is not

Translated: everything the UI writes itself, including the labels of enum values (risk, task status, delegation status and role,
trust level, verification verdict, ...), the dates, numbers, sizes and durations it formats, and its own error fallbacks.

Not translated, on purpose: what the API returns and a person, a model or a package wrote (a task's goal, tool names, descriptions and
output, findings, plugin manifests, provider names), and the API's own error `message`s, which stay English. Making those translatable
needs an error-code contract in the API and is a separate follow-up. The product name `bOps` and the size units (`KiB`) are not words
and are not translated. A value the catalogues do not know (a status a newer server sends) is shown as the server sent it.

### Adding a string

1. Add the key to `en.ts` with the English text, and the same key to `it.ts`. Keys are dot-separated and grouped by screen
   (`dashboard.step.modelInfo.responseTime`). Use named placeholders (`{count}`), never a concatenation of translated fragments.
2. In a template use the pipe, `{{ 'dashboard.title' | t }}`, `{{ 'dashboard.list.showingLatest' | t: { shown: a, total: b } }}`,
   or for an attribute `[attr.aria-label]="'dashboard.list.statusFilter' | t"` and `[placeholder]="'...' | t"`. In TypeScript,
   `inject(I18n).t('...')`. A key that is not in `en.ts` does not compile.
3. A message that depends on a number has `.one` and `.other` variants (`dashboard.task.meta.one` / `.other`) and is asked for by the
   shared prefix with a numeric `count`; the plural rule is the active language's (`Intl.PluralRules`).
4. An enum label is `enum.<group>.<Name>`, keyed by the enum's name and never by the number the API sends; use `i18n.label(group, name)`.
5. Dates, numbers and durations go through `i18n.time`, `i18n.dateTime`, `i18n.number`, `i18n.bytes` and `i18n.duration`, not the
   browser's default locale.

### Adding a language

Add a catalogue file typed as `Messages` (`export const fr: Messages = { ... }`, which the compiler checks against `en.ts`) and one line
in `src/app/core/i18n/languages.ts`. The switch lists it and the specs check it for the same keys and placeholders.

### Checks

- `npm run lint:i18n` fails when a template has a text node, a `placeholder`, `title`, `aria-label` or `alt`, or an interpolation with a
  string literal that does not go through the translation (it runs in CI before the tests, and proves itself with `--self-test`). A new
  screen has to pass it.
- The specs check that both catalogues have exactly the same keys and placeholders, plural and date/number formatting for `en` and `it`,
  and that each screen re-renders in Italian and back, that the choice survives a reload and that blocked storage still works.

## Development server

To start a local development server, run:

```bash
ng serve
```

Once the server is running, open your browser and navigate to `http://localhost:4200/`. The application will automatically reload whenever you modify any of the source files.

## Code scaffolding

Angular CLI includes powerful code scaffolding tools. To generate a new component, run:

```bash
ng generate component component-name
```

For a complete list of available schematics (such as `components`, `directives`, or `pipes`), run:

```bash
ng generate --help
```

## Building

To build the project run:

```bash
ng build
```

This will compile your project and store the build artifacts in the `dist/` directory. By default, the production build optimizes your application for performance and speed.

## Running unit tests

To execute unit tests with the [Karma](https://karma-runner.github.io) test runner, use the following command:

```bash
ng test
```

## Running end-to-end tests

For end-to-end (e2e) testing, run:

```bash
ng e2e
```

Angular CLI does not come with an end-to-end testing framework by default. You can choose one that suits your needs.

## Additional Resources

For more information on using the Angular CLI, including detailed command references, visit the [Angular CLI Overview and Command Reference](https://angular.dev/tools/cli) page.
