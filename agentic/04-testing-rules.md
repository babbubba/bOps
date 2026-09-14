# 04 — Testing rules

## The split

| Layer | Discipline |
|---|---|
| `bOps.Runtime`, `bOps.Policy`, `bOps.Audit`, verification | **Test first.** No exceptions. |
| Platform packages (System, Service, Network, Filesystem, Docker) | **Integration tests against real targets.** Never mock the OS. |
| Provider packages | Contract tests against recorded HTTP fixtures, plus one live smoke test per provider, opt-in. |
| Hosts (CLI, API, Worker) | Thin by construction; test the composition, not the logic — there should be none. |

## Test-first on the core

The core decides whether a privileged action happens. A bug there is a security incident, not
a defect. Write the failing test, then the code.

The reason is specific, not ceremonial: on a policy engine and on verification, tests written
*after* the implementation tend to confirm the behaviour that exists rather than define the
behaviour that was wanted. That is exactly backwards for the two components whose entire job
is to say no.

Minimum coverage of cases, per component:

**Policy engine** — each risk level; each override with conditions matching and not matching;
a `Critical` entry configured as `approval` (must be rejected at load); an unknown package;
a package ceiling lowering a decision; a package ceiling that tries to raise one (must not);
malformed YAML (must fail closed).

**Verification** — `Confirmed`, `Refuted`, `Inconclusive`; a non-`Read` tool without a
`VerificationSpec` (registration must be rejected); a non-`Read` tool not implementing
`IVerifiableTool` (same); a verification tool that itself fails.

**Agent loop** — unknown tool name; tool throwing; tool timing out; policy denial; repeated
policy denial reaching the termination threshold; approval rejected; `MaxSteps` reached;
budget exceeded; oversized tool output being truncated; a provider returning malformed JSON;
a provider returning multiple tool calls.

**Audit** — every event type serializes and round-trips; a `Sensitive` argument is redacted
before reaching the sink; concurrent writes do not interleave.

## Never mock the operating system

A mocked `/proc/meminfo` confirms that you can write a mock. It does not confirm that the
parser handles a kernel that reports fields in a different order, or a value in a different
unit, or a container where `MemAvailable` is absent.

Platform package tests run against real targets:

- Linux tools: inside a real Linux container, with the real `/proc` and a real `systemd`
  where the test needs one.
- Windows tools: on a real `windows-latest` runner.
- Docker tools: against a real Docker daemon with real containers created by the test.

From V0.5 the Aspire AppHost provides these targets, and the same composition is used locally
and in CI. Before V0.5, use `Testcontainers` directly.

Tests requiring a platform are marked `[Trait("Platform","Linux")]` / `"Windows"` and are
skipped — visibly, never silently — where they cannot run.

## The conformance suite for platform packages

Two OS packages contributing the same tool must produce the **same output shape**, because the
LLM reads that output and a difference in shape is a difference in behaviour.

`bOps.Packages.System.Conformance` is a shared test library: a set of assertions any
`system.*` implementation must satisfy, regardless of OS. Each platform package references it
and runs it against its own tools. Adding macOS later means running the same suite — which is
how you find out whether the abstraction was real.

The suite asserts structure and invariants, not values: the result parses, required fields are
present, percentages are within 0–100, memory figures are self-consistent, the manifest
matches what the tool accepts.

## Making the planner deterministic

Never call a live model in a CI test. `FakeChatModel` replays a recorded sequence of
`ModelResponse` values, which makes the loop fully deterministic and lets a test assert the
exact sequence of policy decisions, executions and verifications.

Recorded end-to-end scenarios live in `examples/` and double as planner regression tests: a
goal, the recorded model responses, the expected tool sequence, the expected audit trail. When
the loop changes, these tell you what changed in behaviour.

Live-model tests exist, are marked `[Trait("Category","LiveModel")]`, and are excluded from
the default CI run. They answer a different question — "does this provider still behave as we
assume" — and they are allowed to be flaky, which is why they must never gate a merge.

## CI

- Matrix: `windows-latest` and `ubuntu-latest`, from V0.5. Before that, `ubuntu-latest` is
  enough only while nothing platform-specific exists.
- The build runs with `TreatWarningsAsErrors`; a warning fails CI exactly as it fails locally.
- Architecture rule A1 (the core names no package) is enforced by an automated test, not by
  review.
- Contract round-trip tests (rule A2) run on every contract type, generated rather than
  hand-listed, so a new type cannot be forgotten.

## What not to test

- Do not test that .NET works. No tests asserting that `record` equality is by value.
- Do not write a test per property setter for coverage.
- Do not assert exact log message text; assert the event, its level and its structured fields.
- Do not lock in formatting of tool output beyond what the conformance suite requires —
  output wording will change as prompts improve, and a brittle assertion there costs more than
  it catches.
