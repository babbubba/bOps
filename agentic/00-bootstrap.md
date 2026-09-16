# 00 — Agent bootstrap

This file is the stable entry point for coding agents. It routes to the existing normative
documents; it does not duplicate or supersede their rules.

## Project context

bOps is a .NET 10 runtime that operates Windows and Linux machines through declarative tools,
policy, approval, post-action verification and complete audit. The LLM proposes; the runtime
decides and executes. The current development milestone is V1.1, the generic Skill/Capability
SDK, Evidence model and immutable execution plan. V1.2 multi-agent work is not in scope until the
V1.1 gate closes.

## Required read order

Before changing project state, read:

1. `CLAUDE.md`.
2. `agentic/00-project-spec.md`.
3. `agentic/01-architecture-rules.md`.
4. `agentic/03-security-rules.md`.
5. The subject-specific files in this directory.
6. `agentic/06-decisions.md` before proposing an architectural alternative.
7. The active plan and task files under `agentic/_plans/` and `agentic/_tasks/`.
8. `HANDOFF.md` and the relevant accepted ADRs.

`piano-bops-v0.9.1-v2.0.md` is the authoritative backlog from V0.9.1 onward. The older
`piano-bops.md` is historical and must be interpreted only through
`agentic/07-plan-corrections.md`.

## Stack and architecture

- .NET 10 and latest C#.
- Nullable, analyzers and warnings-as-errors are enforced.
- The dependency-free public contract lives in `bOps.Abstractions`.
- Runtime, Policy, Memory and Audit never name a package, provider or concrete tool.
- Packages depend only on `bOps.Abstractions` and load through the package boundary.
- There is no generic execution tool, arbitrary SQL path or unfiltered environment dump.
- Policy is enforced where execution happens; every side effect is verified; every outcome is
  audited.

## Documentation and workflow

- Non-trivial work requires a plan in `agentic/_plans/` and matching task tracking in
  `agentic/_tasks/`.
- Update task checklists as real work completes, not in advance.
- Keep README, CHANGELOG, HANDOFF, plans, tasks and code-adjacent documentation aligned.
- Write an ADR before any change covered by `agentic/05-workflow.md`.
- Use short-lived branches and Conventional Commits in English; never rewrite shared history to
  cosmetically repair old commit messages.
- Never push, create a release tag or open a PR unless the operator explicitly asks.

## Testing and debugging

- Core Runtime, Policy, Audit and verification changes are test-first.
- Platform packages use real target tests; provider packages use recorded HTTP contracts and
  opt-in live smoke tests.
- Diagnose root cause before changing behavior. Keep application, infrastructure,
  configuration and documentation causes distinct.
- Developer credentials and machine configuration must not influence deterministic tests.

## Final validation

Validate the affected surface and, for a milestone closeout, run at minimum:

- `dotnet build bOps.slnx --configuration Release`
- `dotnet test bOps.slnx --configuration Release --no-build --filter "Category!=LiveModel"`
- Angular production build and headless tests when API/UI behavior changes
- the relevant architecture, secret and documentation-alignment checks

Windows and Linux CI are authoritative for real-platform behavior. A release milestone also
requires its tag, release workflow, reproducible artifacts, SBOMs, checksums and attestations.

## Naming and file discipline

- Code, identifiers, comments, commit messages and normative `agentic/` files are English.
- Namespaces use `bOps.*`; tools use lowercase dotted names.
- One top-level type per file; no `Helper`, `Manager`, `Util` or `Common` dumping grounds.
- Do not create temporary copies, backup files, placeholder documents or unused scaffolding.
- Treat `specifiche-pendenti.md` as a non-normative working document. Preserve unrelated local
  edits and never use it as roadmap authority.

