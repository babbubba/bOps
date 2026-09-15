# ADR-0020 — Plugin loader, manifest shape, and the activation boundary

Status: Accepted

Written at the start of V0.10 (`piano-bops-v0.9.1-v2.0.md` §7), as required by
`agentic/05-workflow.md`'s ADR trigger list: this changes how packages are loaded, isolated and
identified. ADR-0012 (D-003) already recorded *why* dynamic loading was chosen over Native AOT;
this ADR is the design that decision made possible.

## Context

Every package through V0.9 is a project reference, composed by hand in each host's `Program.cs`
(`toolRegistry.Register(new PackageId("bops.packages.docker"), tool)` and the like). V0.10 needs
a *real* third-party package to be installed, enabled, run, disabled and removed without
recompiling or forking the host — the whole point of "everything beyond the minimal runtime is a
package" (principle 7) is that a package the project didn't write gets the same treatment as one
it did.

Four existing rules constrain the design directly:

- **A9/A10** — no package resolves a service from another package; a package is constructed with
  `ActivatorUtilities` from a container exposing only `ILoggerFactory`, `IHttpClientFactory`,
  `TimeProvider`, its own `IConfigurationSection`, and `ICapabilityProbe`.
- **A11** — a package states an id in its manifest; the *effective* `PackageId` is the host's to
  grant, not the package's to claim.
- **A12** — `bOps.Abstractions` stays `0.x`; a plugin built against one `0.x` cannot assume
  binary compatibility with another the way it could against a frozen `1.0`.
- **S8** — `AssemblyLoadContext` isolates dependencies, not permissions. A loaded package runs
  with the host's own privileges. Auto-discovery is never silent: a discovered package stays
  disabled until an operator enables it explicitly.

## Decision

### Manifest (`bOps.Abstractions.PluginManifest`)

A plugin ships a `bops-plugin.json` next to its assemblies, deserialized into:

```csharp
public sealed record PluginManifest(
    int SchemaVersion,
    string Id,
    string Publisher,
    string Version,
    string MinHostAbstractionsVersion,
    string EntryAssembly,
    string EntryType,
    IReadOnlyList<string> DeclaredCapabilities,
    IReadOnlyList<PluginDependency> Dependencies,
    RiskLevel? MaxDeclaredRisk);

public sealed record PluginDependency(string Name, string Version);
```

This is a *shape*, not an instance (A1's exception for `bOps.Abstractions`) — it names no
concrete package, only the fields any package's manifest must have. `SchemaVersion` is checked
against a supported set so a future breaking manifest change fails loudly (`PluginValidator`,
not a silent best-effort parse) instead of guessing. `DeclaredCapabilities`, `Dependencies` and
`MaxDeclaredRisk` are informational, exactly as rule S3 already says a package's own declared
ceiling is informational — they exist so an operator can read what a plugin *claims* before
enabling it, never to auto-configure policy from them.

### Loading (`bOps.PluginHost`, a new core-adjacent project)

A new project, depending only on `bOps.Abstractions` (rule A7's spirit, even though it is not one
of the four projects A1 names) plus `Microsoft.Extensions.DependencyInjection.Abstractions` for
`ActivatorUtilities`. It never names a tool or provider either.

- **`PluginStore`** — a JSON file (`plugins.json` by default) recording every installed plugin's
  id, install path, manifest snapshot and enabled flag. Every write goes to a temp file and is
  atomically renamed into place, so a process killed mid-write leaves the previous, valid state
  — never a half-written file the next start-up would fail to parse.
- **`PluginManifestValidator`** — schema version, required-field, self-consistent-dependency
  (two `Dependencies` entries naming the same package at different versions is rejected as an
  internally inconsistent manifest) and host-compatibility checks (the running
  `bOps.Abstractions` version must be `>=` `MinHostAbstractionsVersion`). Fails loud with a
  specific reason, mirroring `PolicyConfigurationException`'s pattern — never coerces.
- **`PluginLoadContext : AssemblyLoadContext`** — one collectible context per plugin.
  `AssemblyDependencyResolver` resolves the plugin's *own* dependencies from its folder; the one
  explicit exception is `bOps.Abstractions` itself, which `Load(AssemblyName)` deliberately
  returns `null` for, so the CLR falls through to the copy already loaded in the default context.
  This is "share a single copy of `bOps.Abstractions`" (plan §7, V0.10 note 3), made structural:
  a plugin cannot end up with its own, second, incompatible copy of the contract types the host
  already uses to talk to it.
- **`PluginManager`** — `Install`/`List`/`Enable`/`Disable`/`Remove`, each atomic:
  - `Install(sourceDirectory)` copies into a staging subfolder first, validates the manifest
    there, then moves the whole folder into place only once validation passed — an interrupted
    or rejected install leaves nothing behind, not a half-copied plugin the store could see.
    Rejects an `Id` collision with an already-installed plugin (A11: the host is the arbiter of
    whether a claimed id is accepted, not a rubber stamp). Recorded **disabled** — installing is
    not enabling (rule S8).
  - `Enable(id)` loads the plugin's `EntryType` into a fresh `PluginLoadContext`, constructs it
    with `ActivatorUtilities.CreateInstance` against a container exposing *exactly* the A10 list
    (a small `RestrictedPackageServiceProvider`, not the host's real `IServiceProvider`), and
    registers its tools (`IToolProvider`) or chat model factory (`IModelProviderPackage`) into
    the *same* `IToolRegistry`/`IChatModelRegistry` the host already uses — no parallel registry,
    no special case in `AgentRunner`. An entry type implementing neither interface, or both, is
    rejected before anything is registered.
  - `Disable(id)` calls the registry's existing `SetEnabled(package, false)` (unchanged since
    V0.1 — this ADR needed no change there) and unloads the collectible `AssemblyLoadContext`.
  - `Remove(id)` disables first, then deletes the installed folder and the store record — in
    that order, so a failure partway leaves the plugin disabled-but-present rather than
    deleted-but-still-registered.
- No remote install. `Install`'s source is a local directory only (plan §7, V0.10 note 4: "local
  artifacts only" in this version). A zip/archive source or a remote registry is not this ADR's
  concern and is not implied by anything here.

### What this ADR deliberately does not change

`PolicyContext.Trust` stays exactly as it already was — hardcoded `Official` in
`AgentRunner.ExecuteStepAsync`, a known simplification the code already comments on. Making trust
level actually change a policy *decision* needs the real thing V1.0's plan section calls for —
signing, provenance, a trust store — not a label this loader could assign itself with a straight
face today (rule S8: do not imply verification that does not exist). The existing per-package
ceiling in `policy.yaml`, keyed by the plugin's own `PackageId` string, is how an operator
constrains a newly-installed plugin's risk *today*: nothing here requires waiting for trust
levels to do that. Assigning and enforcing `PackageTrustLevel` for dynamically loaded packages is
explicitly deferred to V1.0, where it belongs alongside signing (`piano-bops-v0.9.1-v2.0.md`,
V1.0 note 4).

## Alternatives considered

- **A shared, ambient DI container packages resolve services from directly** — rejected outright
  by rule A9; would let a plugin discover and depend on another plugin's internals with no
  contract, versioning or isolation story at all.
- **`Activator.CreateInstance` instead of `ActivatorUtilities`** — rejected by rule A10 and
  02-coding-standards.md's forbidden list; gives a package no injected services at all or forces
  it to reach for ambient state (`AppDomain`, static singletons) to get anything, which is worse.
- **A non-collectible `AssemblyLoadContext`** — simpler, but makes `Disable` a lie: the assemblies
  stay loaded and referenced forever, so "disabled" would only mean "not called," not "unloaded."
  Collectible costs a little extra care (nothing outside the context may hold a live reference
  past `Unload()`) for a real disable.
- **Assigning `PackageTrustLevel.Community` instead of leaving trust untouched** — considered, but
  rejected for this ADR: nothing downstream reads `PolicyContext.Trust` today (confirmed —
  `PolicyEngine.Evaluate` never branches on it), so assigning *any* specific level here would be
  cosmetic at best and a false signal of a maturity the trust model does not have yet at worst.
  Better to say plainly "this is still `Official` everywhere, and that is a known gap closed at
  V1.0" than to invent a value nothing acts on.

## Consequences

A third-party package can be built against nothing but the published `bOps.Abstractions` NuGet
package, packaged with a `bops-plugin.json`, and installed/enabled/disabled/removed through
`bops plugin *` without the host being rebuilt — V0.10's Definition of Done. `bOps.Abstractions`
staying `0.x` (A12) means a plugin's `MinHostAbstractionsVersion` check is a floor, not a promise
of forward compatibility across `0.x` releases; that promise only exists from `1.0`. Every
dynamically loaded package still runs with the host's own OS privileges (S8) — this loader closes
none of that gap, and `docs/security/threat-model.md` (due at V1.0) is where it gets its full
treatment, not here.
