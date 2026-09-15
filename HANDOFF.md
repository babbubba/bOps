# Handoff — V0.9.1 closed and pushed; CI green; hand off to V0.10

This closes out V0.9.1 (repository integrity and licensing readiness — no functional change),
per `piano-bops-v0.9.1-v2.0.md` §11 checklist item 6 ("stop at the first unmet gate; do not
anticipate v0.10, v0.11 or later"). **V0.9.1's gate is now fully met, including the part the
previous version of this file said still needed confirming: CI is green on both OSes on GitHub's
own runners**, not just locally. Everything is committed and pushed to `origin/main`.

## What this session did

Picked up mid-task from a prior session's `HANDOFF.md` (itself written by an agent that ran out
of budget while adding Angular unit tests, the one piece of V0.9.1 left undone). Verified that
work, then closed out and committed everything in eight commits (`509eb75..HEAD`):

1. `docs(v0.9.1): reconcile agentic/ rules with the v0.9.1-v2.0 plan` — roadmap pointer, D-013–
   D-015, README tool-table correction, six backfilled ADRs.
2. `build(v0.9.1): centralize NuGet packaging metadata and legal files` — `Directory.Build.props`,
   `LICENSE` copyright fix, `NOTICE`, `CONTRIBUTING.md`, `SECURITY.md`.
3. `build(v0.9.1): generate SBOM and THIRD-PARTY-NOTICES from real inventories` —
   `scripts/Generate-Sbom.ps1`, `scripts/Generate-ThirdPartyNotices.ps1`, the generated
   `THIRD-PARTY-NOTICES`, `.gitignore` (`sbom/` ignored, it's a build artifact), the local
   `dotnet-CycloneDX` tool manifest.
4. `ci(v0.9.1): fix stale bOps.sln reference, add Angular UI stage` — `bOps.sln` → `bOps.slnx`,
   added `npm ci`/`build`/`ng test` to the CI matrix.
5. `chore(v0.9.1): apply SPDX headers to every source file` — `scripts/Add-SpdxHeaders.ps1` plus
   the mechanical header-only diff to all 122 `.cs` and 17 `.ts` files (verified no other change
   snuck in: every one of those files' diff was exactly a 2-line header + blank line before
   staging).
6. `fix(security): remove live API key committed to appsettings.json` — blanks
   `ModelProvider.ApiKey` in both `bOps.Api` and `bOps.Cli` `appsettings.json`. **Rule S6.** See
   "Standing constraint" below — this commit does not rotate the real key.
7. `test(v0.9.1): add unit tests for SignalStores and feature components` — the six new spec
   files (`TasksStore`, `ApprovalsStore`, `ProvidersStore`, `Dashboard`, `Approvals`, `Settings`).
   This was the one substantive V0.9.1 item still outstanding; it is now done.
8. `chore: track launchSettings.json for bOps.Api.Tests` — minor, matches the existing convention
   of committing this file for the other two runnable projects.
9. `docs: close out V0.9.1 handoff, point next session at V0.10` — this file, first version.

Pushed after that (`df3e354..origin/main`), and the very first real CI run on GitHub's runners
(the workflow had referenced the deleted `bOps.sln` all through v0.9, so nothing had actually run
there before) surfaced three genuine, pre-existing bugs that local runs never caught. Fixed and
pushed as four more commits:

10. `fix(security): resolve symlinks in intermediate path segments, not just the leaf` —
    `FilesystemPathPolicy.ResolveLinkChain` only resolved a symlink at the path's final node;
    a symlinked *ancestor* directory (`allowed/link/data.txt`, where `link` not `data.txt` is the
    link) passed through unresolved. Rule S11's exact gap. This dev machine cannot create
    symlinks without elevation, so the test that catches this always skipped locally — never
    exercised until a hosted runner (which can) actually ran it.
11. `fix(ci): add the WindowsOnlyFactAttribute the CI comment already assumed existed` —
    `WindowsSystemToolsTests.Cpu_Conforms`/`Memory_Conforms` ran unguarded on `ubuntu-latest` and
    threw for real (`PerformanceCounter` is genuinely Windows-only). `ci.yml`'s own comment
    already claimed a "Windows counterpart" to `LinuxOnlyFactAttribute` existed; it didn't. Added
    it, applied to the same six OS-touching methods `LinuxSystemToolsTests` guards.
12. `fix(ci): skip Docker tests visibly when the daemon can't run Linux containers` —
    GitHub's `windows-latest` runner's Docker Desktop defaults to Windows containers, so the
    `alpine` image `TestContainer` needs can never start there. `DockerAvailableFactAttribute` now
    checks `docker version --format {{.Server.Os}}` and skips, naming the reason, when it isn't
    `linux`.
13. `ci: bump setup-node to 22, silencing the Node 20 deprecation warning`.

**None of these three bugs were introduced by this session's own changes** — they were latent in
code from V0.5/V0.9, invisible because CI never actually ran until commit 4 in this list fixed the
solution-file reference. Confirmed CI green (both `windows-latest` and `ubuntu-latest`, including
the Angular stage and SBOM generation/upload) on run `35005244090` after all fixes.

## Verified, right before committing (this session, not inherited claims)

- `dotnet build bOps.slnx --configuration Release`: **0 warnings, 0 errors**.
- `dotnet test bOps.slnx --configuration Release --filter "Category!=LiveModel"`: **175 passed,
  7 skipped (expected platform skips), 0 failed.**
- `npx ng test --watch=false --browsers=ChromeHeadless` (in `web/bops-ui`): **17/17 passed** —
  includes the six new spec files. The one earlier flaky assertion (asserting a DOM input's value
  immediately after an async `onStart` instead of the component's own `goal()` signal) was already
  fixed on disk when this session picked the work up; no further change was needed there.
- `npm run build` (in `web/bops-ui`): production build succeeds.
- `./scripts/Generate-Sbom.ps1` then `./scripts/Generate-ThirdPartyNotices.ps1`: regenerated from
  scratch, reproduced the exact same counts the prior session reported — **142 .NET components**,
  **610 npm components (10 runtime, 600 development)** — with no unresolved-license errors. Both
  outputs land under the gitignored `artifacts/sbom/`; `THIRD-PARTY-NOTICES` at the repo root is
  the committed, human-readable result.
- `grep ApiKey` on both committed `appsettings.json` files: confirmed blank (`""`), not the real
  key value.

## Standing constraint — still in force, do not violate it

**Never rotate or otherwise touch the real OpenRouter API key's value.** The user explicitly said,
earlier in this project: *"ignora la key...tanto poi la dismetto e la rifaccio ma non ora"*
(ignore the key, I'll deprecate and redo it later, myself, not now). Commit `ada4463` in this
session only blanked the *committed config field* — a separate, already-approved action — it does
not rotate the key. The key's real value is still in git history (`509eb75`); that is a decision
for the user to act on when they choose to, not something any future session should do
proactively.

## V0.9.1 Definition of Done (plan §7) — status

"CI verde sui file corretti, GitHub riconosce Apache-2.0, package OSS con metadati coerenti,
NOTICE e inventario inclusi negli artefatti, processo contributivo documentato."

- CI verde: **confirmed**, both OSes, on GitHub's own runners (run `35005244090`).
- GitHub recognizes Apache-2.0: the standard, unmodified license text is at `LICENSE`; GitHub's
  own license detector will pick it up on the repo page (not independently re-verified by this
  session beyond the text being canonical — check the repo's "License" badge next time you're on
  the GitHub page, it's a few-second glance, not worth a dedicated step).
- Package metadata / NOTICE+inventory in artifacts / contributor process: **verified** in the
  prior session's `dotnet pack` + `.nupkg` inspection (§ above), unaffected by anything in this
  session's CI-fix commits.

**V0.9.1's gate is closed.**

## Next: V0.10 — package loader and Plugin SDK

Per the plan (§7), this can now start — V0.9.1's gate above is closed. When you do:

1. Write an ADR on the loader after re-evaluating candidate libraries; prefer a project-owned
   `AssemblyLoadContext` if the previously-considered dependency is archived/abandoned.
2. Define and version the plugin manifest (`bops-plugin.json`): identity, publisher, version,
   host/SDK compatibility, declared capabilities, config, dependencies, informational max risk.
3. Share a single copy of `bOps.Abstractions` across loaded packages; isolate other dependencies;
   gate activation through the restricted container rule A10 requires.
4. Every discovered package stays disabled until explicitly enabled. No unverified remote install
   in v0.10 — local artifacts only.
5. Implement `plugin install/list/enable/disable/remove` as atomic, recoverable operations.
6. Publish `bOps.Abstractions` still as `0.x`, a template, a manifest analyzer/validator, and one
   purely-demonstrative Apache-2.0 sample Skill.

Definition of done for v0.10: an external sample package can be built, packaged, installed,
enabled, run, verified, disabled and removed without touching the core.
