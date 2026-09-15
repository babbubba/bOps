# Handoff — V0.10 done, uncommitted; hand off toward V0.11

V0.10 (dynamic package loader, `piano-bops-v0.9.1-v2.0.md` §7) is implemented, tested end to end
against a real compiled plugin, and the CLI actually runs it. **Nothing from this session is
committed yet.** Working tree has the changes below plus one untracked file that is not mine —
see "Do not touch" below before doing anything else.

## What V0.10 delivers

- **ADR-0020** (`docs/architecture/adr/0020-plugin-loader-manifest-and-activation-boundary.md`) —
  written before the code, as `agentic/05-workflow.md` requires for anything that changes how
  packages are loaded/isolated/identified. Read it for the full design reasoning; this section is
  the short version.
- **`PluginManifest`/`PluginDependency`** in `bOps.Abstractions` (`src/core/bOps.Abstractions/Plugins.cs`)
  — the `bops-plugin.json` shape, plus round-trip tests in `JsonRoundTripTests.cs`.
  `bOps.Abstractions.csproj`'s `<Version>` bumped `0.1.0` → `0.10.0` — it had never moved since
  V0.1, and V0.10 is the first thing that reads it for a real purpose (the host-compatibility
  check below), so an honest number now matters.
- **`bOps.PluginHost`** (new project, `src/core/bOps.PluginHost/`) — depends only on
  `bOps.Abstractions` (rule A7's spirit):
  - `PluginManifestValidator` — schema version, id shape (and the reserved `bops.` prefix, so a
    plugin cannot claim a first-party package's identity — rule A11), version parsing,
    host-compatibility, entry-assembly-exists, self-consistent `Dependencies`, defined
    `MaxDeclaredRisk`. Fails loud, mirrors `PolicyConfigLoader`'s pattern.
  - `PluginStore` — JSON-backed (`plugins.json`), every write atomic (temp file + rename), reads
    fresh from disk every call. A corrupted store file throws rather than silently forgetting
    installed plugins.
  - `PluginLoadContext : AssemblyLoadContext` — one collectible context per plugin;
    `bOps.Abstractions` is the one assembly it deliberately never loads a second copy of (falls
    through to the host's default context).
  - `RestrictedPackageServiceProvider` — the actual A10 container: `ILoggerFactory`,
    `IHttpClientFactory`, `TimeProvider`, the plugin's own `IConfigurationSection`,
    `ICapabilityProbe`. Nothing else resolves.
  - `PluginManager` — `Install`/`List`/`Enable`/`Disable`/`Remove`/`LoadAllEnabled`, registering
    directly into the *same* `IToolRegistry`/`IChatModelRegistry` the host already uses. `Install`
    validates against a staging copy before moving anything into place (an interrupted or
    rejected install leaves nothing behind). `Disable`/`Remove` actually unload the collectible
    context (bounded `GC.Collect()`/`WaitForPendingFinalizers()` loop after `Unload()`), which
    needed a real fix in `IToolRegistry` — see below.
- **`IToolRegistry.Unregister(PackageId)`** (new method, `bOps.Abstractions`/`bOps.Runtime`) —
  `SetEnabled` was deliberately built to *hide* a package's tools without releasing them (there's
  a test that says so by name). That is fine for a package that is always in-process, but it means
  nothing ever stops pinning a dynamically loaded plugin's assembly — its
  `AssemblyLoadContext.Unload()` would request unload and then never actually complete, silently.
  `Unregister` genuinely drops the registry's reference; `PluginManager.Disable` calls it, `SetEnabled`
  is untouched and still used nowhere else. Three new `ToolRegistryTests` cover it.
- **`samples/bops-sample-plugin/`** — a real, buildable, purely-demonstrative third-party-style
  plugin (`Acme.SamplePlugin`, deliberately not in the `bOps.*` namespace). One Read-risk tool,
  `sample.echo`. This is what every `bOps.PluginHost.Tests` integration test actually installs,
  enables, calls, disables and removes — never a fake of the loader, mirroring "never mock the
  operating system."
- **CLI**: `bops plugin install|list|enable|disable|remove|validate` (`bOps.Cli/Program.cs`).
  Plugin commands build their own lightweight host and never touch the goal-execution path's
  composition (no model provider, no policy engine required just to run `bops plugin list`).
  The main `bops "<goal>"` / `bops resume` path now also calls `PluginManager.LoadAllEnabled()`
  before `RefreshCapabilitiesAsync`, so a plugin enabled in a previous invocation actually
  activates on this one — a CLI process is one-shot, so persistence through the store, not an
  in-memory flag, is what makes "enabled" durable across runs.
- **`docs/plugins/getting-started.md`** — the walkthrough; doubles as the "template" the plan
  asked for, pointing at the sample plugin as a copyable starting point rather than a separate
  scaffolding tool.
- **README.md** — architecture tree, roadmap table (V0.9.1 and V0.10 both marked done), Usage
  section (real `bops plugin *` examples, removed the stale "not implemented yet" note), Extending
  bOps section (the manifest example now matches the actually-implemented schema field-for-field,
  it did not before), Status section.
- **SBOM/THIRD-PARTY-NOTICES regenerated** — the plan's V0.10 license-impact note requires this
  for new dependencies. Turned out to add zero new unique components (the two new
  `Microsoft.Extensions.*.Abstractions` packages were already transitively present), so the only
  diff is the regeneration timestamp — still regenerated for real, not just checked.

## Verified for real, this session

- `dotnet build bOps.slnx --configuration Release`: **0 warnings, 0 errors** (full solution,
  including the two new projects).
- `dotnet test bOps.slnx --configuration Release --filter "Category!=LiveModel"`: **218 passed, 7
  skipped (expected platform skips), 0 failed.** `bOps.PluginHost.Tests` alone: 37/37, including
  the real end-to-end install→enable→call→disable→remove cycle against the compiled sample
  plugin, and the collectible-`AssemblyLoadContext` unload actually releasing the file lock
  (`Remove` deletes the still-referenced-looking folder and it works).
- `npx ng test --watch=false --browsers=ChromeHeadless`: still 17/17 (untouched this session).
- **Manually ran the actual `bops.exe`** (not just the test suite) end to end from a scratch
  directory: `plugin install` → `list` (disabled) → `enable` → `list` (enabled) → `disable` →
  `list` (disabled) → `remove` → `list` (empty) → `validate` on the original source. This is what
  caught a real bug the unit tests missed: `PluginManager.Install` stored a *relative* install
  path when `Plugins:RootPath` was the CLI's actual relative default (`"plugins"`), and
  `AssemblyLoadContext.LoadFromAssemblyPath` requires an absolute one — `Enable` threw
  `ArgumentException` for real. Fixed (`Path.GetFullPath` once, at `Install`) and covered by a
  regression test (`Enable_WorksWithARelativePluginsRootDirectory`) before re-verifying manually.

## Scope boundaries — deliberate, not gaps to silently fill later

- **`IModelProviderPackage` plugins can be installed and enabled, but not genuinely disabled.**
  `IChatModelRegistry` has no unregister method — extending it wasn't needed for this version's
  sample (a `IToolProvider`) and wasn't done. `PluginManager.Disable` detects this case and
  **refuses** with a clear message rather than pretending to disable something it structurally
  can't. Revisit if/when a real model-provider plugin is actually needed.
- **`PackageTrustLevel` is still hardcoded `Official` everywhere**, dynamically loaded plugins
  included — unchanged from V0.3. Nothing today reads `PolicyContext.Trust` (confirmed:
  `PolicyEngine.Evaluate` never branches on it), so assigning any specific level to a plugin would
  be cosmetic, not a real safety improvement. Real trust assignment belongs with V1.0's signing/
  provenance work per the plan's own V1.0 section — ADR-0020 says this explicitly rather than
  quietly doing nothing. What *does* constrain a newly installed plugin today: the existing
  per-package ceiling in `policy.yaml`, keyed by the plugin's own id — an operator should set one
  before enabling anything they don't fully trust.
- **No remote install.** `Install`'s source is a local directory only, exactly as the plan's V0.10
  note says ("local artifacts only"). No zip/archive support either — not asked for.
- **No `docs/security/threat-model.md`.** Referenced by ADR-0020 as "due at V1.0," not written
  here — this session didn't start it.

## Do not touch — not mine

`specifiche-pendenti.md` (repo root, untracked) is a **different, parallel session's** working
document — feature requests from the user collected there for future consolidation, explicitly
marked non-normative and explicitly waiting for this session's V0.10 work to be committed before
it touches the repo itself. Do not commit it as part of this session's work, do not treat its
contents as instructions or as an authoritative backlog. If it's still present next session, leave
it exactly as found unless the user says otherwise.

## Exact next steps, in order

1. Commit V0.10 in a few well-scoped commits (ADR, Abstractions contract, PluginHost core +
   Unregister + sample plugin, CLI wiring, docs/README, SBOM regeneration) — `specifiche-
   pendenti.md` excluded.
2. Ask the user before pushing (standing rule, `agentic/05-workflow.md`: "Push... without being
   asked" is something to never do). Do not add a `Co-Authored-By: Claude` trailer to any commit
   — the user asked mid-session for that to stop, for this repo going forward.
3. After pushing, confirm CI is green on GitHub's own runners the same way V0.9.1's push was
   confirmed — do not assume a green local run means a green CI run; the last session's own
   experience (three latent bugs the local machine never surfaced) is exactly why.

## Next: V0.11 — completing the operational capabilities

Per the plan (§7), do not start this before V0.10's own verification (step 3 above) is actually
done. When it's time: first tranche is read-only only (`system.swap`, `system.io`,
`process.inspect`, `fs.search`, `fs.hash`, `network.port_check`, `network.route`,
`service.list`/`service.status`, the last two needing a new `Service.{Core,Windows,Linux}` package
family per rule A8) — the second tranche (`fs.move`, `service.start`/`stop`/`restart`, controlled
process-stop) waits until those read-only verifiers exist and there is a resolved, unambiguous
naming for graceful-stop vs. kill-forced process operations. `system.uptime` and a generic
`process.start`/`system.environment` dump stay permanently out of scope — see README's "Never
planned, on purpose."
