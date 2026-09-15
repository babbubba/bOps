# ADR-0012 — Dynamic package loading over Native AOT

Status: Accepted

**Backfill note.** Written at V0.9.1 to record the decision made at V0.1
(`agentic/06-decisions.md` D-003) in ADR form. `agentic/05-workflow.md` has listed this ADR as
owed since the project's first commit. The loader itself does not exist yet — it is `V0.10`'s
deliverable per `piano-bops-v0.9.1-v2.0.md` §7 — this ADR records why AOT was ruled out before
V0.10 even starts, not the loader's design (a separate ADR is due when V0.10 begins).

## Context

`piano-bops.md` §13 asked for `PublishAot=true` for startup speed and a small binary, while §5.2
separately described a plugin loader based on dynamically loading third-party assemblies at
runtime. The two are technically incompatible: an ahead-of-time-compiled binary cannot load
assemblies it did not know about at publish time. Principle 7 — "everything beyond the minimal
runtime is a package," with third-party packages built exactly like first-party ones — depends
on the second. AOT would also have constrained `Docker.DotNet` and any future ORM the way EF
Core is commonly constrained under AOT.

## Decision

Drop `PublishAot`. Ship self-contained with partial trimming instead. Dynamic package loading
(via `AssemblyLoadContext`, the specific mechanism V0.10 will design in its own ADR) is the
capability the project keeps; a few tens of milliseconds of extra startup time and a binary in
the tens of MB are the cost, paid gladly, because the package ecosystem is this project's
identity, not an optional feature bolted on afterward.

## Alternatives considered

- **Two hosts: `bops-lite` (AOT, no plugins) and `bops` (full, dynamic loading).** Rejected: two
  build matrices, two test matrices, and "why won't my plugin load in this build" as the
  permanent top support question — a worse outcome than picking one host and being honest about
  its tradeoff.
- **AOT with only statically-compiled, first-party packages; no third-party plugin story at
  all.** Rejected: kills closed-source third-party distribution outright, which is most of what
  makes the open-core commercial plan (`piano-bops-v0.9.1-v2.0.md` §2, D-013) viable — official
  Skills need to load as packages into a core they don't need to fork.

## Consequences

Startup lands around 80 ms instead of roughly 15 ms under AOT — irrelevant for a tool that then
waits seconds for a model response. `bOps.Abstractions` stays fully reflection-loadable, which
V0.10's loader will depend on. This decision is what makes V0.10 possible at all, not merely
easier.
