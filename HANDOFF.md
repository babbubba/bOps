# Handoff — V1.1-F complete; V1.1-G active

V1.1-A through V1.1-F are complete on public `bOps` `main`. V1.1-F adds a read-only plugin catalog:
`GET /api/plugins` / `GET /api/plugins/{id}` in `bOps.Api`, and a lazy-loaded Angular **Plugins**
page. Enable, disable, install and remove stay `bops plugin *`-only — no mutation path exists
through the API in this batch.

Exploration during this batch surfaced a real, pre-existing gap: `bOps.Api` never activated plugins
at all — only `bOps.Cli` did (`CreatePluginManager` + `LoadAllEnabled()`). Since `bOps.Api` runs its
own `AgentRunner` for every task started from the dashboard (`AgentsEndpoints`/`AgentTaskLauncher`),
a catalog with nothing to query would have been permanently empty for any host that only ever runs
the API. `bOps.Api/Program.cs` now wires `PluginManager` the same way the CLI already does —
extending the existing ADR-0020 trust boundary to a second host process, not introducing a new one.

A second real gap closed alongside it: `PluginManager.LoadAllEnabled()` had no per-plugin isolation
— one plugin failing to reactivate (a revoked trust key, a corrupted install directory, a digest
that no longer matches what was recorded at install) crashed the *entire* host at start-up, on both
CLI and API. `LoadAllEnabled()` now isolates each activation, returns `{id: sanitized reason}` for
every failure (the plugin's own install path is scrubbed from the message before it is stored), and
exposes `StartupLoadErrors`/`IsActivated` so the catalog can show a failed plugin's diagnostic
instead of the whole process refusing to start.

The formal V1.0 release gate remains independently operator-gated. No tag, package publication,
release workflow or commercial-repository product change was made.

## V1.1-F delivered

- `PluginCatalogEntry` (`PluginCatalogModels.cs`) is a narrow, hand-projected DTO — never the raw
  `PluginRecord`. It never carries `InstallPath`, a raw exception stack, or trust-store content;
  verified by a test asserting the serialized response never contains the plugin's install path.
- `enabled` (persisted operator intent) and `loaded` (actually registered in this process right
  now, via the new `PluginManager.IsActivated`) are two distinct fields — never merged into one
  "status". Likewise `declaredMaxRisk` (from the manifest) and `effectiveMaxRisk` (the maximum
  `Risk` observed among that plugin's currently-registered tools, `null` when not loaded) stay
  separate — a declaration is not enforcement.
- `compatible` re-runs `PluginManifestValidator.Validate` live against the plugin's installed
  manifest, so a plugin that becomes incompatible after a host upgrade (without reinstalling) is
  caught instead of silently assumed fine forever.
- `GET /api/plugins` supports `enabled`/`trust` filters and a clamped `limit`/`offset`, ordered
  deterministically by id; `GET /api/plugins/{id}` returns 404 for an unknown id. Both require only
  the existing viewer role (`ApiAuthorization.ViewerPolicy`), same as `GET /api/tools`.
- The Angular **Plugins** page shows installed/enabled/loaded/compatible as four separate badges,
  declared capabilities as a chip list, dependencies as a plain name@version list, a load-error
  banner only when one exists, and declared-vs-effective risk as two labeled `bops-risk-badge`
  values. No enable/disable/upload control anywhere on the page (asserted by a dedicated test).

See `agentic/_tasks/2026-09-16-v1.1-f-plugin-catalog-ui.md` for the full checklist.

## Validation on 2026-09-17

- `dotnet build bOps.slnx --configuration Release` — 0 warnings, 0 errors.
- `dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"` — every
  assembly in the solution passed locally (full-suite regression, not just the touched projects),
  including the new `bOps.PluginHost.Tests` isolation test (49/49) and the new
  `bOps.Api.Tests.PluginCatalogEndpointsTests` (26/26 in `bOps.Api.Tests`).
- `npm run build` — Angular production build succeeded; the new `plugins` route lazy-chunks
  correctly.
- `npm test -- --watch=false` — 32/32, including `plugins.store.spec.ts` and `plugins.spec.ts`.
- GitHub Actions run `<pending — fill in after push>` — Windows and Ubuntu, pending.

## Next action and boundaries

Implement `agentic/_tasks/2026-09-16-v1.1-g-secure-settings.md` once authorized: writable Settings
backed by an encrypted local vault. Its own ADR is due at V1.1-G per `agentic/05-workflow.md`'s
pre-committed subject table — master-key handling and rotation must be designed before code, not
discovered while writing it.

Do not start V1.1-H or later work until V1.1-G closes. Do not create a release tag or publish
packages without separate operator authorization. The private commercial repository remains
product-gated and unchanged; only the workspace submodule pin advances after this public closure.
