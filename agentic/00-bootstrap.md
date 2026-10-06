# 00 — Agent bootstrap

This file is the stable entry point for coding agents. It routes to the existing normative
documents; it does not duplicate or supersede their rules.

## Project context

bOps is a .NET 10 runtime that operates Windows and Linux machines through declarative tools,
policy, approval, post-action verification and complete audit. The LLM proposes; the runtime
decides and executes. V1.2 multi-agent orchestration is complete and released as
`v1.2.0-preview.8`. V1.3 senior-operator local diagnostics is merged. The active work is the
V1.3.x reliability hardening train (`agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md`),
executed one HARDEN packet at a time; HARDEN-1 through HARDEN-10 are complete (HARDEN-10 merged via PR #76), HARDEN-11
is completed and merged via PR #77 (`12a03165ef458ecb3b36625d955731e34671c6f8`) after its independent
implementation authority/security review PASS, and HARDEN-12 is completed and merged via PR #79
(`86bf74857a2435daee6177cd5cf78aae4e6198eb`) after its independent UI review PASS. HARDEN-13 is completed and merged
to `main` via PR #83 (`3bed13e3274570a3928ac64706be6246c0d9a978`): ADR-0045/D-042 are Accepted; B1, B2a
(PR #80), B2b (PR #81) and B3 (PR #82) passed independent review; all executable final-review gates passed; blockers 0,
non-blockers 0. HARDEN-14 is the next executable packet and is not started. All gates are defined only by the consolidated
roadmap.

## Required read order

Before changing project state, read:

1. `CLAUDE.md`.
2. `agentic/00-project-spec.md`.
3. `agentic/01-architecture-rules.md`.
4. `agentic/03-security-rules.md`.
5. The subject-specific files in this directory.
6. `agentic/06-decisions.md` before proposing an architectural alternative.
7. `agentic/_plans/2026-09-16-consolidated-roadmap.md` and only the selected active task under
   `agentic/_tasks/`.
8. `HANDOFF.md` and the relevant accepted ADRs.

Never include `agentic/obsolete/` in routine discovery or bootstrap. It contains superseded inputs
and snapshots with no authority.

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
- Treat everything under `agentic/obsolete/` as historical and non-normative. Do not read it unless
  the active task explicitly requests historical research.
