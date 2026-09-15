# Contributing to bOps

Thank you for considering a contribution. Please read this alongside
[`agentic/`](agentic/) (the binding technical rules) and [`docs/licensing.md`](docs/licensing.md)
(what Apache-2.0 does and doesn't cover, and why this project uses a CLA) before opening a pull
request.

## Before you write code

1. Read [`agentic/00-project-spec.md`](agentic/00-project-spec.md),
   [`agentic/01-architecture-rules.md`](agentic/01-architecture-rules.md) and
   [`agentic/03-security-rules.md`](agentic/03-security-rules.md).
2. Check [`agentic/06-decisions.md`](agentic/06-decisions.md) — a settled question is not
   re-opened by a new PR without new information; propose a change via an ADR that supersedes
   the existing decision, not a silent diff.
3. Check the active backlog, [`piano-bops-v0.9.1-v2.0.md`](piano-bops-v0.9.1-v2.0.md), for which
   version a change belongs to. A PR that anticipates a later version than the one currently
   being built will be asked to wait, not merged early (`agentic/05-workflow.md`, scope
   discipline).
4. If your change touches `bOps.Runtime`, `bOps.Policy`, `bOps.Audit` or `bOps.Memory`, write the
   failing test first (`agentic/04-testing-rules.md`).

## The Contributor License Agreement (CLA)

**No external contribution is merged before the CLA process is operational and your CLA is on
file.** This project uses a CLA rather than a bare `Signed-off-by` (Developer Certificate of
Origin) line, so that contributions can be relied on across the project's Apache-2.0 public
repository and, per the open-core decision recorded in `agentic/06-decisions.md` (D-013, D-015),
its commercial offerings — without a separate negotiation for every accepted contribution.

What the CLA does **not** change: the license the public repository grants everyone downstream
stays Apache-2.0. Signing a CLA does not transfer copyright, and it does not make your
contribution any less available to the community than anyone else's.

- If you hold the rights to your contribution yourself, an **individual CLA** applies.
- If your employer holds the rights to code you write (common for anything written as part of
  your job), a **corporate CLA path** applies as well — check with whoever manages IP
  agreements at your employer before submitting.
- The CLA's actual text is drafted and reviewed by IP/software counsel before it is binding —
  this repository does not treat this document, or any other engineering-authored text, as that
  text. Until the signing process is live, contributions are welcome as discussion and review,
  but merging is on hold.

## Pull requests

- Trunk-based, short-lived branches off `main`. Conventional Commits, imperative mood, in
  English (`agentic/05-workflow.md` has the exact scopes and examples).
- The PR description explains *why*, not just what — the diff already shows what changed.
- CI must pass: .NET build/test under `TreatWarningsAsErrors`, and (from V0.9.1) the Angular
  build/test for `web/bops-ui`.
- No secret, credential, or anything matching a key pattern — ever, even in a test fixture.
- Nothing that would put commercially sensitive material (official Skill knowledge, playbooks,
  entitlement logic, Control Plane or Portal code) into this public repository — that content
  belongs in the private `bOps.Commercial` repository by design (`docs/licensing.md`), not here
  under any circumstance.

## What contributions are especially welcome

- Third-party packages (tools or LLM providers) built against the public `bOps.Abstractions`
  contract — see [`docs/plugins/`](docs/plugins/) once the plugin loader (V0.10) ships.
  A third-party package is never required to be open source itself.
- Bug reports and fixes for the runtime, packages, CLI, API and UI already shipped through V0.9.
- Documentation and test coverage improvements, especially where `agentic/` already flags a gap.

## Reporting a security issue

Do not open a public issue for a security vulnerability. See [`SECURITY.md`](SECURITY.md) for
the private disclosure process.
