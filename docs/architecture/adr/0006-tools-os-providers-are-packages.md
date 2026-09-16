# ADR-0006 — Tools, operating systems and LLM providers are all packages behind the same contract

Status: Accepted

**Backfill note.** Written at V0.9.1 to record the decision underlying `agentic/00-project-spec.md`
principle 7 and `agentic/01-architecture-rules.md` rule A8, in force since V0.1.
`agentic/05-workflow.md` has listed this ADR as owed since the project's first commit.

## Context

The archived original plan §2/§11 placed `LinuxSystemProvider` — an `ISystemProvider` — inside a package
literally named `bOps.Packages.Service.Linux`, with a `service.*` package also expected to exist
separately. Following that layout would have meant the core needing to know about
`ISystemProvider` as a distinct extension point from `IToolProvider`, and would have made adding
macOS (or any new OS) mean forking and republishing whichever package happened to hold the
`ISystemProvider` implementation for it (`agentic/07-plan-corrections.md`; `agentic/06-decisions.md`
D-005). The same question applies identically to LLM providers: is "which provider" a property
the runtime switches on, or a package boundary?

## Decision

There are exactly two extension points a package implements — `IToolProvider` (tools) and
`IModelProviderPackage` (LLM providers) — both defined in `bOps.Abstractions`, both consumed
identically whether the package is first-party or third-party (principle 7: "the only difference
... is *where it is loaded from*, never *how it is built*"). Operating systems are not a third
extension point; each OS is itself a package (`bOps.Packages.System.Windows`,
`.System.Linux`, and eventually a macOS package) contributing its own complete `ITool`
implementations, selected automatically by the registry's platform filter (rule A4). There is no
`ISystemProvider`, no `IServiceProvider2` (the plan's own numeric-suffix workaround for a naming
collision — removed entirely, not renamed). Shared code between OS packages for the same tool
family lives in a plain class library (`bOps.Packages.System.Core`), never in a cross-package
service contract (rule A9: packages never resolve services from other packages).

## Alternatives considered

- **A package contributing a platform *service* another package consumes.** Rejected: requires
  cross-plugin service resolution, load ordering with dependencies, and a versioned
  `ISystemProvider` contract living somewhere — exactly the complexity rule A9 exists to avoid.
- **Providers internal to one `System` package, selected by a runtime switch.** This was the
  plan's structure. Rejected because it means operating systems are not actually packages:
  adding macOS means editing and republishing the existing `System` package rather than writing
  a new one, and the core would need an `if (OperatingSystem.IsMacOS())`-shaped switch somewhere,
  violating rule A1.
- **The same question, applied to LLM providers: a switch statement over provider name in the
  runtime.** Rejected identically — `ChatModelRegistry` resolves by provider id from whatever
  `IModelProviderPackage`s were registered, with zero hardcoded provider names in `bOps.Runtime`.

## Consequences

`bOps.Packages.System.Windows`/`.Linux` each ship complete, independent tool implementations
sharing only inert formatting/manifest code from `.System.Core`. Six LLM provider packages exist
today (OpenRouter, Ollama, llama.cpp, OpenAI, DeepSeek, Anthropic) as proof the pattern holds
under real variety, not just the two OS packages it was designed for.
