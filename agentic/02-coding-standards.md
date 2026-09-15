# 02 — Coding standards

## Enforcement, not advice

Rules that a compiler can enforce are enforced by the compiler. Everything below that a build
setting can express lives in `Directory.Build.props` at the repository root, and applies to
every project without exception.

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest-all</AnalysisLevel>
    <InvariantGlobalization>true</InvariantGlobalization>
    <Deterministic>true</Deterministic>
    <!-- Noise, not signal, on a greenfield codebase. Revisit at V0.3. -->
    <NoWarn>$(NoWarn);CA1303;CA1304;CA1305;CA1310;CA1848</NoWarn>
    <!-- CA2007 (ConfigureAwait(false)) is a library-authoring guideline for code that might run
         under a capturing SynchronizationContext. bOps has no such context anywhere in the
         solution — see docs/architecture/suppressions.md. -->
    <NoWarn>$(NoWarn);CA2007</NoWarn>
  </PropertyGroup>
</Project>
```

`bOps.Abstractions` additionally sets `GenerateDocumentationFile=true` and does **not**
suppress `CS1591`: it is a published SDK, so every public member carries XML documentation.

### Escalation schedule

| | Through V0.2 | From V0.3 (current) |
|---|---|---|
| `Nullable`, `TreatWarningsAsErrors` | on | on |
| `AnalysisLevel` | `latest-recommended` | `latest-all` |
| Suppression list | `CA1303;CA1304;CA1305;CA1310;CA1848` | the same list, **plus `CA2007`** — see below |

`TreatWarningsAsErrors` is on from the first commit because on an empty repository each
warning appears alone, as it is written, and costs seconds to fix. The expensive scenario —
inheriting hundreds of warnings at once — cannot happen here. `AnalysisLevel=latest-all` waited
until V0.3 because it is the setting that generates volume without proportional value early on.

Escalating to `latest-all` surfaced real, fixable issues (missing `ArgumentNullException.ThrowIfNull`
guards on public entry points across `bOps.Abstractions`, `bOps.Runtime` and the `System.Core`/
`OpenAiCompatible` packages; missing standard exception constructors per CA1032; a P/Invoke
missing `[DefaultDllImportSearchPaths]`; test-only types that could be `internal`; one genuinely
dead test double) — all fixed, not suppressed. The one new suppression, `CA2007`, was a
deliberate exception rather than "reviewed and shortened" as this table originally predicted:
`ConfigureAwait(false)` protects against deadlocking a capturing `SynchronizationContext`
(WinForms, WPF, classic ASP.NET), and nothing in this solution ever runs under one — a console
host and libraries consumed only by that host and its own tests. Suppressing 66 call sites of a
diagnostic that cannot fire a real bug here is the documented exception the suppression policy
below expects, not a shortcut.

### Suppressions must stay visible

Silencing a warning is allowed. Hiding that you silenced it is not.

- Prefer fixing over suppressing. Always.
- A `#pragma warning disable` must be scoped to the smallest possible region and carry a
  one-line justification.
- A project-level `<NoWarn>` beyond the shared list requires an entry in
  `docs/architecture/suppressions.md` with rule id, scope, reason and the condition under
  which it can be removed.
- Never add a rule to the shared `NoWarn` list to make a task compile. That is a change to
  project policy and belongs in its own commit with its own justification.

## Language and style

- File-scoped namespaces. One top-level type per file, named after the file.
- `sealed` by default on classes. Unseal deliberately, never by habit.
- `record` for everything crossing a boundary (contracts, DTOs, events). Immutable, with
  value equality, which is what makes the audit trail and the tests trustworthy.
- Primary constructors for dependency injection. Explicit constructors when there is
  validation or non-trivial initialization.
- `var` only when the type is evident from the right-hand side.
- Collection expressions (`["windows", "linux"]`), pattern matching and switch expressions
  over `if`/`else` chains and `is`/cast pairs.
- `required` members instead of constructors with six optional parameters.
- Target .NET 10 idioms. This project has no legacy to carry — do not write 2015 C#.

## Error model

**Exceptions signal programmer error. Results signal expected outcomes.**

- A tool returns `ToolCallResult` for everything a caller could reasonably expect: the file
  does not exist, the service is not installed, the daemon is unreachable, the argument is out
  of range. **A tool that throws is a bug in the tool.** The runtime catches anyway
  (rule C1) — and audits the fact that it had to.
- Exceptions are for broken invariants: a manifest that passed validation and is still
  malformed, a registry returning a tool it just said it had. These should crash loudly in
  development and are never caught to be ignored.
- Never `catch (Exception)` and continue silently. Either handle a specific exception type,
  or convert it into an outcome that is logged and audited.
- Never swallow `OperationCanceledException`. Cancellation propagates.

## Async

- `async`/`await` end to end. No `.Result`, no `.Wait()`, no `.GetAwaiter().GetResult()`.
- Every I/O-bound public method takes a `CancellationToken ct = default` and honours it.
- `ValueTask` only on measured hot paths that frequently complete synchronously. Default to
  `Task`.
- `IAsyncEnumerable<T>` for streams of results. Never accumulate an unbounded stream into a
  `List<T>` because it is easier.
- `ConfigureAwait` is unnecessary in this codebase (no synchronization context in a worker or
  console host). Do not add it as cargo cult.

## Dependency injection

- Constructor injection only. No service locator, no `IServiceProvider` injected into
  business types.
- The composition root is `Program.cs` of each host, and nowhere else.
- Packages are constructed with `ActivatorUtilities` from the restricted host container
  (architecture rule A10).
- Register `TimeProvider` and use it. Never call `DateTimeOffset.UtcNow` directly in code that
  needs to be testable — which is most of it, given every audit event is timestamped.

## Serialization

- `System.Text.Json` with **source generation** (`JsonSerializerContext`) for every contract
  type. No reflection-based serialization on the contract surface.
- Explicit `JsonSerializerOptions`, never the defaults.
- Every contract type has a round-trip test (architecture rule A2).

## Performance

The project aims to be efficient; it does not aim to be clever.

- Default to the clear implementation. Optimize what a benchmark shows to be hot — nothing
  else.
- The genuinely hot paths are: `/proc` parsing (called in sampling loops), JSON
  serialization of the audit stream, and history truncation on large tool output. These are
  the places where `Span<T>`, pooled buffers and allocation-free parsing earn their
  complexity.
- Everywhere else, including the entire agent loop, the dominant cost is a network call to an
  LLM measured in seconds. Micro-optimizing around it is wasted effort and lost clarity.
- No LINQ inside a sampling loop that runs at sub-second intervals. LINQ everywhere else is
  fine and preferred.

## Comments

Default to none. The code says what it does; names carry the meaning.

Write a comment only when the *why* is not recoverable from the code: a hidden constraint, a
non-obvious invariant, a workaround for a specific upstream bug, behaviour that would surprise
a reader. Never explain *what* a line does. Never reference the task, the PR or the person who
asked — that context belongs in the commit message and rots in a comment.

XML documentation is required on every public member of `bOps.Abstractions`, because third
parties consume it, and optional elsewhere.

## Naming

- `PascalCase` for types and members, `_camelCase` for private fields, `camelCase` for locals
  and parameters.
- Namespaces are `bOps.*` — lowercase `b`. This is a deliberate deviation from .NET convention
  so that the brand spelling is consistent everywhere in the code. Do not "fix" it.
- Tool names are `lowercase.dotted`: `system.cpu`, `docker.logs`, `service.restart`. The
  prefix is the domain, not the package.
- Interfaces named for the role, not the mechanism: `IToolRegistry`, not `IToolDictionary`.
  Never a numeric suffix to dodge a collision — `IServiceProvider2` is exactly the naming that
  this project does not do.
- No `Helper`, `Manager`, `Util` or `Common` types. If a class cannot be named for what it is,
  it does not yet know what it is.

## Forbidden

- A generic "execute this command" tool, in any form, under any name. Not negotiable.
- String concatenation of anything model-produced into a shell command, a SQL query or a path.
- `Assembly.LoadFrom` for packages. Loading goes through the plugin loader with an isolated
  `AssemblyLoadContext` (V0.10).
- Hardcoded paths, machine names, ports or credentials. Configuration or constants with a
  documented default.
- `Activator.CreateInstance` for package types (rule A10).
- Backwards-compatibility shims, `_unused` renames, re-exports, or `// removed` comments.
  Nothing has shipped yet; delete code that is not used.
