# BOPS-0125 MAF-CLI-AB-01 — CLI A/B benchmark of the MAF governed turn host

| | |
|---|---|
| Task | `BOPS-0125` (register: `agentic/_tasks/register.json`), Issue and Project #6 item recorded there |
| Contract | [Contract 01](../../docs/architecture/current/01-maf-agent-infrastructure.md), rule `C01-R22` (operator decision `D12`, 2026-10-23) |
| Related | `BOPS-0122` (the host being measured, merged in PR #129), `BOPS-0067` AF-05 (offline regression and tuning), `BOPS-0073` BENCH-00 (API-only capture; its helpers are reused) |
| Priority / effort | P1 / medium — mirrored from BOPS-0122, **to be confirmed or changed by the operator** |
| Depends on | nothing in the register (BOPS-0122 is merged; its own closure gates are independent) |
| Operator runbook | [`docs/maintenance/maf-cli-ab-benchmark-runbook-01.md`](../../docs/maintenance/maf-cli-ab-benchmark-runbook-01.md) |

## Purpose

BENCH-00 measured the **API**, which does not read `Cli:Experimental:MafGovernedTurn`; it cannot compare the two CLI paths and must not be quoted as evidence for BOPS-0122.
This task builds the smallest reproducible harness that runs the **real CLI** with the switch `false` (A) and `true` (B) on the same build, model, endpoint, parameters, tools,
limits, prompts and policy, so the operator can decide whether to extend the integration to the API and which defects to fix. It adds no MAF component and changes no architecture.

## Operator authorization (2026-10-24)

Authorized: preparing the scripts and the offline tests. **Not authorized:** live runs, push, PR, merge, tag, normative changes. OpenRouter needs a separate authorization (cost and model).
The first live step is a manual pilot of B01 (A and B) by the operator; B01/B02/B03 follow only after the evidence of the pilot has been checked.

## Scope

`scripts/windows/maf_cli_benchmark.py` (collector, preflight, campaign, integrity), `scripts/windows/maf_cli_analysis.py` (metrics, A/B report, blind evaluation sheets, estimates),
`scripts/windows/run-maf-cli-benchmark.ps1` (launcher), `scripts/windows/tests/` (offline tests and a scripted CLI stand-in), the runbook above. No runtime, broker, API, CLI or `.csproj` change.

## Design rules

- Same commit, one build, one profile, one policy, one overlay for both variants; the **only** configuration difference is `Cli__Experimental__MafGovernedTurn`. A configuration fingerprint (overlay without per-run paths and without the MAF key, plus policy, shipped `appsettings.json` and CLI binary hashes) must be equal and is checked by the report.
- The effective mode is read back from the process (the `EXPERIMENTAL` line of C01-R22 on standard error: present iff B). A mismatch stops the campaign.
- `Knowledge__Enabled=false` is forced; ambient `Cli__*`, `Agent__*`, `ModelProvider__*`, `Knowledge__*`, `Policy__*` … variables are scrubbed from the child environment (their names are recorded, never their values).
- Read-only policy with every level explicit (Read automatic, Low…Critical forbidden); standard input is closed, so an approval prompt reads end-of-file and denies. The CLI hosts one provider, so there is no automatic fallback.
- Serial execution, isolated state per run, alternating A/B order by round, hard per-run timeout, campaign wall-clock, token and cost budgets; any stop preserves the partial evidence and writes the integrity manifest.
- Private output outside any Git work tree; credentials redacted from captured output and scanned for in the whole run directory; nothing is uploaded.
- Prompts are the exact BENCH-00 `B01`/`B02`/`B03` strings imported from `live_benchmark.py`.
- Diagnosis quality is not inferred from `Completed`: each run gets a **blind** evaluation sheet (claims, evidence index, rubric) to be graded by a person; the report shows PENDING until then.

## Acceptance criteria

- A1 `preflight` makes no real model call, validates the target commit, the profile, authorizations, build, policy, endpoint listing and the mode of both variants read back from the real CLI (against a loopback stub; the probe leaves a throwaway task in a probe-only store).
- A2 `run` refuses without `--allow-live`, refuses a non-loopback endpoint without `--allow-remote-endpoint`, refuses an output directory inside a Git work tree or a non-empty one.
- A3 a campaign runs the chosen scenarios and rounds serially, with isolated state, and captures task state, model calls, audit, tool output, EvidenceRead, accounting and final state in full.
- A4 timeouts and budgets stop the campaign in a controlled way with partial evidence preserved and verifiable (`verify`).
- A5 the report (JSON, CSV, Markdown) compares A and B per scenario, labels each metric certain / estimate / heuristic / qualitative, flags one or two rounds as exploratory, and contains no evidence text.
- A6 offline tests cover both paths, preflight, modes, isolation, configuration reproducibility, evidence capture, A/B comparison, timeouts and sensitive data.
- A7 (live, operator) the pilot B01 A/B and then B01/B02/B03 A/B are executed by the operator and their evidence checked; the three-round variant is prepared but not run.

## Gates

`OFFLINE_TESTS` (achieved in this session), `LIVE_REAL_TOOLS` (operator-run pilot and campaign), `CI_VERIFIED` (the 41 offline tests run in the CI step `MAF CLI A/B benchmark tests (Python, offline)` on Windows and Linux), `OPERATOR_GATE`.

## Status log

Task state lives only in the register; this section records evidence as it is produced.
