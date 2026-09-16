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

## Stack

.NET 10 · C# latest · `Nullable` and `TreatWarningsAsErrors` on from the first commit ·
OpenTelemetry from V0.1 · no Native AOT · Apache-2.0.

## If a rule blocks you

Stop and say which rule, what the task needed, and what you propose. Do not work around it and
mention it afterwards.
