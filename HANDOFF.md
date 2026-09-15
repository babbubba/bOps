# Handoff — V0.9 complete: `bOps.Api` + Angular UI, including its originally-deferred Settings scope

Written at the end of the session that implemented the Angular UI, the deferred half of V0.9, and
a follow-up session in the same day that closed out the "task-detail/settings deferred" question
this file itself raised. Everything below is exact, not a summary — follow it literally to resume.

## State right now

**`dotnet build bOps.slnx` builds clean end to end — 0 warnings, 0 errors.** `dotnet test
bOps.slnx --filter "Category!=LiveModel"`: every suite passes — **`bOps.Api.Tests`** is now **12**
tests (2 SSE regression tests from the first UI session, 2 new `GET /api/providers` tests from the
Settings follow-up); **`bOps.Runtime.Tests`** gained 3 new tests for
`ChatModelRegistry.RegisteredProviderIds`.

**`web/bops-ui` builds and tests clean**: `ng build` succeeds, `ng test --watch=false` passes
(2/2 — still only the shell smoke test, see "does NOT have yet" below). Manually exercised against
the real, running `bOps.Api` in the Browser pane for both the original dashboard/approvals pass and
the new Settings page — in both dark and light mode, at both desktop and mobile viewport widths.

**⚠️ Security finding carried forward from V0.7, still not resolved: the real OpenRouter API key
in `src/core/bOps.Cli/appsettings.json` is committed to git history** (commit `7ac2901`). Neither
`bOps.Api/appsettings.json` (V0.9, `ApiKey: ""`) nor anything in `web/bops-ui` touches this file.

## What this session did

Built the Angular UI — the half of V0.9 explicitly deferred in the previous session's handoff —
after two scoping conversations with the operator, both worth restating:

1. **Client generation and feature scope**: hand-written TypeScript client against `bOps.Api`'s
   real endpoints (no OpenAPI pipeline this pass — deferred, `piano-bops.md` §16.4/§17.4 describe
   the eventual generated-client approach for whenever that session happens); MVP feature scope is
   **dashboard + approvals** only (the two SignalStores most directly tied to the backend,
   `piano-bops.md` §17.3) — `task-detail` as its own route and `settings`/`ProvidersStore` are
   deferred, though the dashboard ended up including an inline task-detail panel (see below).
2. **Style**: Tailwind CSS, minimal and functional — an internal ops panel, not a consumer
   product — but explicitly asked to be *both* good-looking and practical for sysadmins and less
   experienced operators alike. Dark/light mode (OS-preference default, manual toggle, `localStorage`
   persistence) and the repo's real `logo.png` (not a placeholder) were both deliberate asks.

### Follow-up: closing V0.9's Settings scope (same day, ADR-0019)

This handoff's own "Next steps" asked, rather than assumed, whether to finish V0.9's deferred UI
scope before V0.10. The operator answered three specific questions:

- **`task-detail` as its own route**: **no** — the dashboard's inline panel already covers "watch
  a task run"; not worth the extra route/maintenance surface. Confirmed as genuinely done, not
  reopened.
- **Settings scope**: **add `GET /api/providers`**, not just a read of the existing `GET
  /api/tools`. This reverses part of ADR-0018 (which explicitly deferred a provider-listing
  endpoint), so it got its own ADR — **ADR-0019** — rather than a silent implementation change,
  per `agentic/05-workflow.md`'s rule that amending a prior ADR or altering a
  `bOps.Abstractions` type needs one.
- **API client**: **stays hand-written** — no OpenAPI pipeline introduced.

**What ADR-0019 actually added**:
- `IChatModelRegistry.RegisteredProviderIds` (`bOps.Abstractions`) — every provider id at least
  one registered package supports, deduplicated. `ChatModelRegistry` implements it off the same
  dictionary `Register` already populates. TDD: `tests/bOps.Runtime.Tests/ChatModelRegistryTests.cs`
  written before the interface member existed.
- `GET /api/providers` (`src/core/bOps.Api/ProvidersEndpoints.cs`) — returns
  `registeredProviderIds` plus an `active` summary (`provider`, `model`, `baseUrl`, `hasApiKey`)
  read from the same `ModelProvider` configuration section `Program.cs` already binds to build the
  singleton `IChatModel`. **Never returns the API key's value** — `bOps.Api` still has no
  authentication (ADR-0018), so anything reachable by any caller must not leak a live credential;
  a `hasApiKey: false` is exactly what let the Settings page render "Missing" for the shipped
  `appsettings.json` (`ApiKey: ""`) without exposing anything sensitive. Tests:
  `tests/bOps.Api.Tests/ProvidersEndpointsTests.cs`.
- `web/bops-ui/src/app/state/providers.store.ts` (`ProvidersStore`) — unlike `TasksStore`/
  `ApprovalsStore`, this **loads once on init and does not poll**: host-level provider
  configuration is not live state that changes while the app is open.
- `web/bops-ui/src/app/features/settings/` (`Settings` component + template) — two cards: the
  active provider's configuration (with the API-key presence badge) and every registered provider
  id, the active one visually marked. Routed at `/settings`, added to the sidebar nav in
  `app.html`/`app.routes.ts`. Read-only — no control to switch the active provider at runtime
  exists yet (ADR-0019 explicitly scoped that out; see "does NOT have yet" below).

Verified live in the Browser pane against the real backend at both desktop and mobile widths: the
Settings page correctly showed all six registered providers (`OpenAI`, `Anthropic`, `LlamaCpp`,
`Ollama`, `OpenRouter`, `DeepSeek`), `OpenRouter` marked active, and an orange "Missing" API-key
badge matching the shipped empty `ApiKey` in `appsettings.json`.

### Second follow-up, same day: `bOps.AppHost` now starts `bOps.Api` + the Angular UI

The operator asked to run the API and UI through Aspire instead of two separate shells. D-002
already named this explicitly ("from V0.9, the API and UI"), so this needed no new ADR — it is the
AppHost catching up to a decision already on record, not a new one.

`src/bOps.AppHost/Program.cs` now has, alongside the existing `linux-test-target` container:

```csharp
var api = builder.AddProject<Projects.bOps_Api>("bops-api")
    .WithHttpEndpoint(port: 5080, name: "http");

builder.AddJavaScriptApp("bops-ui", "../../web/bops-ui", "start")
    .WithHttpEndpoint(port: 4200, isProxied: false)
    .WaitFor(api);
```

`AddJavaScriptApp` comes from the new `Aspire.Hosting.JavaScript` 13.5.3 package (the current
successor to the older `Aspire.Hosting.NodeJs`, matching the AppHost's own Aspire SDK version) —
it runs an `npm run <scriptName>` in the given directory, `npm install` first by default
(`WithNpm`'s default), which is exactly `web/bops-ui`'s existing `start` script (`ng serve`).
`bOps.AppHost.csproj` also gained a `ProjectReference` to `bOps.Api.csproj`, which is what makes
`Projects.bOps_Api` exist (Aspire's source generator emits one such type per referenced project).

**Both ports are pinned to match the existing non-Aspire workflow exactly** — `5080` for the API
(matching `bOps.Api/Properties/launchSettings.json` and `web/bops-ui/proxy.conf.json`) and `4200`
for the UI (matching `.claude/launch.json`). This is additive, not a replacement: `dotnet run
--project src/core/bOps.Api` + `npm run start --prefix web/bops-ui` in two shells still works
exactly as before; `dotnet run --project src/bOps.AppHost` is now a third, single-command way to
start the same two processes together, with Aspire's dashboard for logs/telemetry.

**`isProxied: false` on the UI's endpoint is load-bearing, not cosmetic** — found by actually
running this, not by reading docs. With Aspire's default proxying (`isProxied: true`), DCP itself
tries to own port 4200 for its front-end proxy and expects the underlying process to bind a
*different* port that Aspire injects (typically via a `PORT` env var); `ng serve` does not read
that env var and always binds 4200 directly, so proxied mode failed outright with "Port 4200 is
already in use" — confirmed in `resource-executable-*.log` under the DCP temp session directory
when this was first tried without `isProxied: false`. Also tried and rejected: passing both `port:
4200` and `targetPort: 4200` explicitly — DCP refuses that combination outright for a non-container
resource ("Non-container resources cannot be proxied when both TargetPort and Port are specified
with the same value").

Verified by actually running `dotnet run --project src/bOps.AppHost`: both `bops-api` and
`bops-ui` came up, `curl http://localhost:5080/api/providers` and a live Browser-pane load of
`http://localhost:4200/settings` both worked exactly as they do outside Aspire — the UI's Settings
page rendered against the API's real response, proxied through `ng serve`'s own dev-server proxy
exactly as it does when started manually.

### Structure (`web/bops-ui/src/app/`)

```
core/
  api/bops-api-client.ts   — hand-written HttpClient wrapper, one method per bOps.Api endpoint
  api/models.ts            — TS interfaces mirroring bOps.Api's JSON exactly (camelCase; enums as their wire ints, not TS enums)
  streaming/task-events.ts — wraps EventSource for GET /api/agents/tasks/{id}/events
  theme.ts                 — dark/light toggle, localStorage-backed, OS-preference fallback
state/
  tasks.store.ts           — SignalStore: running tasks, selected task (live via SSE), start/resume/select
  approvals.store.ts       — SignalStore: pending approvals (2s poll — no SSE stream exists for these), approve/reject, tool→risk lookup
  providers.store.ts       — SignalStore: registered/active LLM provider (load-once, no poll — host config, not live state)
features/
  dashboard/               — start-task form, running-task list, inline live task-detail timeline
  approvals/                — pending-approval cards with risk badge, note field, approve/reject
  settings/                 — active-provider card (with API-key presence badge) + registered-provider list
shared/
  risk-badge.ts, status-badge.ts — small presentational components, color-coded from CSS custom properties in styles.css
```

Routing: `/dashboard` (default), `/approvals` and `/settings`, all lazy-loaded (`loadComponent`). `app.html` is
the shell: a sidebar on desktop that collapses to a top row on narrow viewports (plain Tailwind
responsive classes, no separate mobile component), the real logo (cropped to just the icon glyph
via a fixed-size `overflow: hidden` container — the source PNG has the full "bOps" wordmark
below it), nav links, and the theme toggle.

### Two real bugs this session's manual browser testing caught in `bOps.Api` — both fixed, both now regression-tested

Neither was caught by any of the 8 `bOps.Api.Tests` written in the previous session, because none
of them read the SSE stream's raw bytes — every earlier test asserted on plain GET/POST JSON only.

1. **The SSE endpoint serialized with PascalCase, silently disagreeing with every other endpoint.**
   `StreamTaskEventsAsync` called a bare `JsonSerializer.Serialize(task)` — the BCL default
   (`Id`, `Goal`, `Status`, ...) — while `Results.Ok(task)` elsewhere in the same file gets
   ASP.NET Core's Web defaults (camelCase) automatically. The Angular client's TS interfaces are
   all camelCase (matching the real REST responses, verified by curl before writing them), so
   every field read off an SSE snapshot came back `undefined` — the task detail panel rendered
   with an empty goal, empty id, and a `TypeError` on `task.steps.length` inside an `@if` block, on
   a tight loop as the polling/refresh cycles kept re-triggering it. Fixed: `AgentsEndpoints.cs`
   now serializes SSE payloads with `new JsonSerializerOptions(JsonSerializerDefaults.Web)`
   explicitly. Regression test: `TaskEvents_StreamsCamelCaseJsonSnapshots_LikeEveryOtherEndpoint`.
2. **`GET .../events` gave up permanently on the first read if the task didn't exist yet.**
   `POST /api/agents/tasks` returns `202` the instant it has generated an id (ADR-0018) — before
   the detached background run has saved anything. The dashboard opens the SSE stream for that id
   immediately on receiving the `202`, which can and did race the first `ITaskStore.SaveAsync`:
   the very first `LoadAsync` inside the SSE loop returned `null`, and the endpoint's original
   logic treated any `null` as permanently "not found," writing an `error` event and closing —
   with no way for that specific connection to ever recover. The browser's `EventSource` then
   auto-reconnected (since the client only calls `.close()` itself on a real snapshot, never
   having received one) — repeatedly, forever, hitting the same losing race every time, which is
   what produced the hundreds of `TIME_WAIT` connections and console errors observed while
   debugging this. Fixed: the endpoint now tolerates up to a 5-second grace period of `null` reads
   before declaring a task genuinely not found. Regression test:
   `TaskEvents_ToleratesOpeningTheStream_BeforeTheBackgroundRunHasSavedAnything` — opens the stream
   with **no** pre-poll at all, unlike every other test in the file, specifically to reproduce the
   race deterministically.

Both fixes live in `src/core/bOps.Api/AgentsEndpoints.cs`; no ADR update needed — these are
implementation bugs against ADR-0018's already-decided design, not new decisions with alternatives
to record.

### Client-side detail worth knowing

- **`watchTaskEvents` closes its own `EventSource` on the first terminal-status snapshot**,
  specifically so the browser's default auto-reconnect never engages once a task is actually done —
  this is what makes bug #2 above so damaging when it fires: a stream that never gets an *actual*
  snapshot never reaches the code path that would have stopped it from reconnecting.
- **`ApprovalsStore` loads `GET /api/tools` once at startup to build a tool→risk lookup**, because
  `PendingApproval` (bOps.Api's DTO) carries no risk field, and an operator deciding approve/reject
  benefits from seeing it. Best-effort: a failed load just means no risk badge, not a broken queue.
- **`TasksStore` and `ApprovalsStore` are both `providedIn: 'root'`, each running its own polling
  loop from the moment the app boots** (3s for the running-task list, 2s for pending approvals) —
  simple, works for an MVP with one operator per browser tab; would need real thought (backoff,
  visibility-based pausing) before this UI is left open unattended for long stretches.

## Design choices worth knowing before extending this further

- **`web/bops-ui` targets Angular 20, not literally "Angular 21"** as `piano-bops.md` names — 21
  is not GA; 20 is the latest version compatible with this environment's Node (`v20.20.2`; Angular
  22 requires Node ≥22). Everything `piano-bops.md` §17 describes (standalone components, Signals,
  `@ngrx/signals`) is present in 20 — revisit the exact version only if it actually matters later,
  not preemptively.
- **`.claude/launch.json` and `web/bops-ui/proxy.conf.json`** wire `ng serve` (port 4200) to proxy
  `/api/*` to `bOps.Api` on `http://localhost:5080` (a new `Properties/launchSettings.json` gives
  `bOps.Api` that stable port — it had none before this session, defaulting to Kestrel's own pick).
  No CORS configured anywhere — the proxy makes it unnecessary for local dev; a real deployment
  topology (same-origin static hosting vs. separate origins) is undecided, deferred alongside auth.
- **Browser-tool coordinate clicks were unreliable while verifying this UI** (viewport-scaling
  mismatches specific to this session's tooling) — every check that mattered was ultimately done
  by calling the component's own methods directly via `window.ng.getComponent(el)` from
  `javascript_tool`, which exercises the exact same code path a real click would (the click handler
  bodies, not a simulation of them). Worth knowing if a future session hits the same friction.

## What this session deliberately does NOT have yet

- **No `task-detail` route.** Confirmed, not just deferred — the operator explicitly chose to keep
  only the dashboard's inline panel rather than add a dedicated route (see the ADR-0019 follow-up
  above). This is now a closed decision, not an open item to revisit.
- **No way to change the active provider from Settings.** `GET /api/providers` (ADR-0019) is
  read-only; switching `ModelProvider` at runtime would need a new write endpoint plus a decision
  about where that configuration is persisted (file? database?) — explicitly out of scope for this
  pass, flagged in ADR-0019's alternatives-considered section.
- **No OpenAPI-generated client.** Hand-written, deliberately, reconfirmed this session too.
- **No unit tests for the SignalStores or feature components** — only the pre-existing `App`
  smoke test (updated for the new shell) and the .NET-side `bOps.Api.Tests`. Verified by hand
  against a real running backend instead; worth adding real component/store tests before this UI
  grows much further, especially now that it has caught two real backend bugs no earlier .NET test
  did — a UI-level test suite would likely catch the *next* one earlier still.
- **No authentication** — same standing gap as `bOps.Api` itself (ADR-0018), unrelated to this
  session.
- **`bOps.AppHost` now orchestrates `bOps.Api` + the Angular UI** (see the follow-up section
  above) — no longer "not run," this carried-forward item is closed. The `linux-test-target`
  container inside it still has not been exercised this session (unrelated to this change; that
  container is only relevant to `bOps.Packages.System.Linux.Tests`/`bOps.Packages.Filesystem.Tests`
  on a non-Linux dev machine).
- **No dynamic plugin loading** — V0.10, unrelated.
- **No CLI command to run `AuditChainVerifier` on demand** — carried forward again.

## Next steps

**V0.9 is now genuinely, fully complete** — backend, UI, and the Settings follow-up all built,
tested, and manually verified against a real (if unauthenticated, by design) `bOps.Api` instance.
There is no more "should we finish V0.9 first" question left to ask; the next session should go
straight to V0.10 scoping.

**Before any further roadmap work**, the committed API key finding from V0.7 is still open —
carried forward yet again; it has now survived three full roadmap versions unresolved.

**V0.10 — "Dynamic package loading, `bops plugin install`, published plugin SDK" — has not been
started.** Per the scope-discipline rule, the next session should begin by reading
`agentic/00-project-spec.md`'s roadmap entry for V0.10 and `06-decisions.md` (D-003, which already
settled *why* dynamic loading over Native AOT, but not yet *how*) before writing code.

Three smaller, non-urgent items carried forward again from every prior handoff:

1. The six pre-existing ADRs `agentic/05-workflow.md` lists as "the first ADRs to exist" (0001,
   0002, 0005, 0006, 0011, 0012) are still unwritten.
2. No CLI subcommand runs `AuditChainVerifier`. Small, real, not done.
3. No unit tests for the Angular SignalStores/feature components (`TasksStore`, `ApprovalsStore`,
   `ProvidersStore`, `Dashboard`, `Approvals`, `Settings`) — real gap, not hypothetical, given what
   manual testing alone already caught in the first UI session (two genuine backend bugs, see
   `docs/architecture/adr/0018-bops-api-minimal-surface.md`'s history and the SSE fixes in
   `src/core/bOps.Api/AgentsEndpoints.cs`).
