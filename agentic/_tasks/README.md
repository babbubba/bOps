# Task packets

This directory holds executable public-repository work: one packet per task. **Task state is not recorded here.**
Status, validation, dependencies, priority and the next READY task live in [`register.json`](register.json); the rules
(progressive `BOPS-NNNN` ids, status vocabulary, mandatory synchronization with the GitHub Issue, Project #6 and
`HANDOFF.md`) are in [`PROCESS.md`](PROCESS.md). Read the consolidated roadmap, then the register, and open only the task
you are told to do. Do not use task snapshots under `agentic/obsolete/`.

```bash
python scripts/governance/validate_register.py --summary
```

The previous per-task status table is preserved verbatim in
[`docs/handoff-archive/tasks-README-until-2026-10-21.md`](../../docs/handoff-archive/tasks-README-until-2026-10-21.md);
it was out of date for several packets (for example V1.3.y, AF-02..AF-05, the pre-release stabilization packet).

New packets start their title with the progressive id (`# BOPS-0111 | DIAG-ASSURE-01 — ...`); existing files keep their
path and are not renamed.

Private companion implementation from V1.3 onward belongs in `bOps.Commercial`. The private coordination root
tracks cross-repository sequencing after it exists; it must not duplicate these task bodies.

## Execution rules

- Never start a later ordered task while an earlier dependency remains open.
- Status checkboxes describe verified work only; do not check them prospectively.
- A required ADR is written and accepted before the implementation it governs.
- External mutations — repository creation, push, tag, publication or release — require explicit
  operator authorization at execution time.
- Keep README, CHANGELOG, HANDOFF and the selected task aligned with validated results.

## ADR-0052 Microsoft Agent Framework migration train

AF-01 through AF-05 are an ordered architecture migration governed by
[ADR-0052](../../docs/architecture/adr/0052-microsoft-agent-framework-bops-boundary.md). MAF adoption
is already decided (D-048): AF-05 benchmarks tune the implementation and authorize legacy cleanup,
not a return to a custom agent framework. Execute AF-01 first; AF-04 may overlap AF-03 only after
AF-02's shared context interfaces are stable. Each packet contains its own stop conditions and
validation gate.

## V1.3 coding-agent packet map

From V1.3-G onward, use the conservative execution strategy in
[`../_plans/2026-09-22-v1.3-coding-agent-execution.md`](../_plans/2026-09-22-v1.3-coding-agent-execution.md).
The functional task remains authoritative for scope; its execution-packet companion removes local
design/exploration work so a smaller coding model can execute one bounded unit at a time.

| Batch | Execution packets | Default model guidance |
|---|---|---|
| G | [G1–G7](2026-09-22-v1.3-g-execution-packets.md) | Luna Medium; Low for bounded shared/docs work |
| H | [H1–H5](2026-09-22-v1.3-h-execution-packets.md) | Luna Low/Medium |
| I | [I1–I5](2026-09-22-v1.3-i-execution-packets.md) | Luna Medium |
| J | [J1–J5](2026-09-22-v1.3-j-execution-packets.md) | Luna Medium; optional Sol Low security review |
| K | [K1–K8](2026-09-22-v1.3-k-execution-packets.md) | Luna Medium |
| L | [L0–L9](2026-09-22-v1.3-l-execution-packets.md) | Luna Medium; Sol Low only for final unresolved review |
| M | [M1–M8](2026-09-22-v1.3-m-execution-packets.md) | Terra Medium while available for ADR design, Luna Medium implementation, Sol Low review |

Model escalation is never automatic. Stop after two reasoned attempts on the same blocker and hand
off precise evidence rather than consuming the remainder of a usage window.

## V1.3.x reliability hardening packet map

Governed by [`../_plans/2026-09-25-v1.3x-reliability-hardening.md`](../_plans/2026-09-25-v1.3x-reliability-hardening.md),
which holds the full review, finding IDs, dependency gates and Definition of Done. **The train is closed;
the order below is historical.** A packet with an ADR prerequisite drafts the ADR first and stops for
operator acceptance. New commits use the identity in the plan's §19 (`Fabio Cavallari`).

| Order | Packet | Priority | Implementation | Review |
|---:|---|---|---|---|
| 1 | [HARDEN-1 provider wire contract](2026-09-25-v1.3x-harden-01-provider-wire-contract.md) | MUST | Opus High | Opus High |
| 2 | [HARDEN-2 model-call failure containment](2026-09-25-v1.3x-harden-02-model-failure-containment.md) | MUST | Opus High | Opus High |
| 3 | [HARDEN-3 resume state machine](2026-09-25-v1.3x-harden-03-resume-state-machine.md) | MUST | Opus High | Opus highest |
| 4 | [HARDEN-4 task lifecycle UI](2026-09-25-v1.3x-harden-04-task-lifecycle-ui.md) | MUST | Sonnet High | Opus Medium/High |
| 5 | [HARDEN-5 storage.health fix](2026-09-25-v1.3x-harden-05-storage-health.md) | MUST | Sonnet Medium | Sonnet Medium |
| 6 | [HARDEN-6 tool contract constraints](2026-09-25-v1.3x-harden-06-tool-contract-constraints.md) | MUST | Opus High | Opus High |
| 7 | [HARDEN-7 Windows stability evidence](2026-09-25-v1.3x-harden-07-windows-stability-evidence.md) | MUST | Opus High | Opus High |
| 8 | [HARDEN-9 evidence reasoning](2026-09-25-v1.3x-harden-09-evidence-reasoning.md) | SHOULD | Opus Medium/High | Opus Medium |
| 9 | [HARDEN-8 context and budget economy](2026-09-25-v1.3x-harden-08-context-budget-economy.md) | SHOULD | Opus High | Opus High |
| 10 | [HARDEN-10 browser session](2026-09-25-v1.3x-harden-10-browser-session.md) | MUST | Opus High | Opus highest |
| 11 | [HARDEN-11 delegation operability](2026-09-25-v1.3x-harden-11-delegation-operability.md) | MUST | Opus High | Opus highest |
| 12 | [HARDEN-12 UI layout and localization](2026-09-25-v1.3x-harden-12-ui-layout-localization.md) | SHOULD | Sonnet Medium | Sonnet Medium |
| 13 | [HARDEN-13 provider defaults and fallback](2026-09-25-v1.3x-harden-13-provider-defaults-fallback.md) | SHOULD | Sonnet High | Opus Medium |
| 14 | [HARDEN-14 end-to-end composition gate](2026-09-25-v1.3x-harden-14-e2e-composition-gate.md) | GATE | Sonnet High | Opus Medium/High |
