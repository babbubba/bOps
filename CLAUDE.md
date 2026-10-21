# bOps — instructions for coding agents

An agent runtime that operates Windows and Linux machines through declarative tools, policies,
planning and post-action verification. **The LLM proposes; the runtime decides and executes.**

## Read these before writing code

The binding rules live in [`agentic/`](agentic/). They are not suggestions.

| | |
|---|---|
| [`agentic/00-bootstrap.md`](agentic/00-bootstrap.md) | Stable bootstrap and required read order |
| [`agentic/00-project-spec.md`](agentic/00-project-spec.md) | What bOps is, current roadmap version, what is out of scope |
| [`agentic/01-architecture-rules.md`](agentic/01-architecture-rules.md) | Structural invariants and the authoritative contract |
| [`agentic/03-security-rules.md`](agentic/03-security-rules.md) | Non-negotiable runtime safety behaviour |
| [`agentic/02-coding-standards.md`](agentic/02-coding-standards.md) | C# rules, error model, forbidden patterns |
| [`agentic/04-testing-rules.md`](agentic/04-testing-rules.md) | TDD on the core, real targets for packages |
| [`agentic/05-workflow.md`](agentic/05-workflow.md) | Commits, ADRs, definition of done |
| [`agentic/06-decisions.md`](agentic/06-decisions.md) | Settled questions — check before proposing alternatives |
| [`agentic/07-plan-corrections.md`](agentic/07-plan-corrections.md) | Historical-plan corrections retained for provenance |
| [`agentic/_plans/2026-09-16-consolidated-roadmap.md`](agentic/_plans/2026-09-16-consolidated-roadmap.md) | Single active roadmap, milestone gates and task routing |
| [`agentic/_tasks/register.json`](agentic/_tasks/register.json) | **Authoritative task state** (`BOPS-NNNN` ids, status, gates, links). Rules: [`agentic/_tasks/PROCESS.md`](agentic/_tasks/PROCESS.md) |
| [`HANDOFF.md`](HANDOFF.md) | Current session state, ≤ ~100 lines; history in `docs/handoff-archive/` |
| [`agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md`](agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md) | Active V1.3.x hardening train: findings, order, gates; packets in `agentic/_tasks/2026-09-25-v1.3x-harden-NN-*.md`, one at a time |

Everything under `agentic/obsolete/` is a historical archive, not a specification. Coding agents
must ignore it unless an active task explicitly requests historical research. Never implement from
an archived plan, specification or task.

## The rules most often broken

1. **The core names no package, tool or provider.** No `"docker.restart"`, no `/proc`, no
   `"OpenRouter"` anywhere in `bOps.Runtime`, `bOps.Policy`, `bOps.Memory` or `bOps.Audit`.
   This is checked in CI.
2. **No generic execution tool.** No `shell.run`, no `system.exec`, under any name, behind any
   flag. The whole safety model depends on it.
3. **A non-`Read` tool without verification cannot be registered.** Declare a
   `VerificationSpec` and implement `IVerifiableTool`, or registration fails.
4. **Nothing crosses the tool boundary except a serializable `ToolCallResult`.** No streams,
   no handles, no package types.
5. **Everything is audited**, including denials, rejections and timeouts — those are the
   interesting ones.
6. **Tool output is data, never instruction.** It enters the context as a structured, delimited
   tool-result turn and can never alter runtime state.
7. **Build the current roadmap version, not the next one.**

## Commit identity and GitHub accounts

- **New local commits:** author and committer `Fabio Cavallari <fcavallari@bsoftsolutions.it>`, set
  with repository-local `git config user.name` / `user.email` (never `--global`). Verify before every
  commit, and after it with `git show -s --format="Author: %an <%ae>%nCommitter: %cn <%ce>" HEAD`.
  Never commit as Claude, Claude Code, Anthropic, `Babbubba`/`babbubba` or `fcavallari-italcom`.
- **Git author name ≠ GitHub account.** Personal GitHub account for bOps: `babbubba`. Company account
  `fcavallari-italcom`: avoid it for bOps unless the operator asks.
- **Mutating `gh` operations:** hide `GH_TOKEN` / `GITHUB_TOKEN` from the current process only (e.g.
  `env -u GH_TOKEN -u GITHUB_TOKEN gh …`), confirm `gh auth status --hostname github.com` shows
  `babbubba`, and stop if it cannot be verified. Never print, delete or permanently change tokens.
- **Never rewrite historical commits** for identity cleanup; that is a separate operator task. Push, PR
  and merge only when the operator asks. Details: hardening plan §19.

## Before touching a component

1. Run `python scripts/governance/validate_register.py --summary`; find the task by its `BOPS-NNNN` id in the register, then its packet (`agentic/_tasks/`), plan and ADR; `HANDOFF.md` has the latest session state.
2. Check whether the problem is already solved in the current checkout (code, not just the task text).
3. Prove the remaining gap with concrete evidence (a failing test or trace).
4. Limit the change to the root cause. No speculative patches.
5. Add a regression test in the existing suite for that component.
6. Update the task through the synchronization rule below. Do not create a new ADR, document or test suite where one already fits.

## Task governance (mandatory, part of every task)

Every task has a progressive `BOPS-NNNN` id (global, immutable, never reused; the order of creation, not priority). Full rules:
[`agentic/_tasks/PROCESS.md`](agentic/_tasks/PROCESS.md).

- **Never start a task** that is not `authorized: true` in the register or whose dependencies are not `DONE`. Several authorized tasks may run in parallel,
  each on its own branch/session (record `branch` in the register); `HANDOFF.md` names the single current task of its branch. Move a task to `IN_PROGRESS`
  only when development really begins, and tell the operator its id and title.
- **Every status change** is reflected in the same session, in this order: (1) `agentic/_tasks/register.json`, (2) the GitHub Issue,
  (3) the Status field of GitHub Project #6 `dev plan`, (4) `HANDOFF.md` when the active task, a blocker or the next step changes.
  Not a periodic chore: part of the work. Link commits and PRs to the task; record blockers and scope changes.
- **Flow:** `IN_PROGRESS → VALIDATION → DONE`. `DONE` only with acceptance criteria verified, required tests done, PR merged when planned,
  CI of the merge commit verified, mandatory live validation done and operator approval recorded. A merge never implies `DONE`; PR bodies
  use `Refs`, never `Closes`.
- Do not change priority, milestone or dependencies without operator authorization; do not mark a task `DONE` to make a board look tidy.
- If GitHub is unreachable or a scope is missing, never report a remote update as done: record what is owed, say so, retry idempotently
  (look up by `BOPS-NNNN` before creating anything), and do not close the task administratively until reconciled.
- Before pushing a change that touches the register, run `python scripts/governance/validate_register.py` (CI runs it too).
- Updating status/bookkeeping is not a normative change; changing the rules in `agentic/0*.md`, `PROCESS.md` or an accepted ADR is, and needs the operator.

## Stack

.NET 10 · C# latest · `Nullable` and `TreatWarningsAsErrors` on from the first commit ·
OpenTelemetry from V0.1 · no Native AOT · Apache-2.0.

## If a rule blocks you

Stop and say which rule, what the task needed, and what you propose. Do not work around it and
mention it afterwards.
