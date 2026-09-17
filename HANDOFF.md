# Handoff — V1.1-B complete; V1.1-C active

V1.1-A is complete on public `bOps` `main`; GitHub Actions run `35178863698` passed on Windows and
Linux. V1.1-B now implements bounded application/device inventories and additive hardware-model
reporting on public `main`. GitHub Actions run `35180430313` passed on Windows and Linux. V1.1-B is
complete and V1.1-C bounded filesystem size and immutable inventory is now active.

The formal V1.0 release gate remains independently operator-gated. No tag, package publication,
release workflow or commercial-repository change was made.

## V1.1-B delivered

- `system.apps` and `system.devices` are `Read` tools in both System OS packages and expose the
  same bounded JSON envelope: schema version, overall status, observed/returned counts,
  truncation, per-source status and items.
- Shared formatting deduplicates stable platform identities, sorts deterministically, retains
  explicit nullable fields and enforces default/hard item and UTF-8 output limits. Caller
  cancellation propagates through collectors.
- Windows applications come from machine/user Uninstall registry locations in deliberate 32/64
  views. Windows devices come from a bounded three-level Plug and Play registry walk.
- Linux applications are streamed directly from the dpkg status database. Detected RPM/APK
  databases are reported as unsupported instead of guessed. Linux devices come from bounded PCI
  and USB sysfs sources.
- `system.info` retains its existing text fields and adds `Hardware model`, using BIOS registry
  data on Windows and DMI/device-tree sources on Linux; missing data is explicitly `unknown`.
- Source outcomes distinguish available, partial, unavailable, unsupported and not-applicable.
  An unreadable source is never represented as an observed-empty inventory.
- No shell, package-manager process, generic execution path, driver update or side effect was
  introduced. No new runtime dependency or public abstraction boundary required an ADR.

See `docs/system-inventory.md` for the operator-facing schema, limits, sources and limitations.

## Validation on 2026-09-17

- `dotnet restore bOps.slnx --locked-mode` — passed.
- `dotnet build bOps.slnx --configuration Release --no-restore` — 0 warnings, 0 errors.
- `dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"` — all
  executed assemblies passed. Relevant counts: System Core 12/12, Windows System 22/22, Runtime
  125/125, PluginHost 48/48, Policy 23/23, API 17/17 and Architecture 4/4.
- Linux parser/source-fixture tests passed locally; Linux-only system tests then passed on the
  Ubuntu CI runner against real dpkg and sysfs sources.
- GitHub Actions run `35180430313` passed: Ubuntu in 2m57s and Windows in 5m22s. Both completed
  restore, Release build, non-live tests and Angular build/tests; Linux also generated/uploaded SBOMs.

No Angular source, API endpoint contract or browser behavior changed. The repository CI will still
run its existing UI build/test matrix after the implementation reaches `main`.

## Next action and boundaries

Implement `agentic/_tasks/2026-09-16-v1.1-c-filesystem-inventory.md` with effort **alto**. Start
with the existing Filesystem path policy and symlink behavior, then decide through the documented
ADR gate whether exact immutable manifests create a public contract or durable runtime state.

Do not create a tag, publish packages, pin the workspace submodule or begin private commercial work
without separate authorization. The workspace root will correctly show `repos/bOps` at a newer
commit until the submodule pin is intentionally updated.
