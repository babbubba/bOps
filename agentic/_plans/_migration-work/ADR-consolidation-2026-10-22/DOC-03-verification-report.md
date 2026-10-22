# DOC-03 — verification report

Non-normative working artifact of the DOC-03 phase of `agentic/_plans/ADR-consolidation-2026-10-22.md`. It is migration evidence, not current documentation, not a contract, and must not be loaded by the bootstrap.

**Verdict: READY FOR DOC-04** (CP-03a PASS, CP-03b PASS).

| | |
|---|---|
| Task | `BOPS-0121` / `DOC-CONSOL-01` (stays `IN_PROGRESS`; Issue #125 / Project #6 stay `sync_pending`) |
| Branch | `docs/adr-consolidation-2026-10-22` |
| HEAD before DOC-03 (= DOC-03 source commit in the manifest) | `57990d26f362676624d203064b69d3ced9c3448f` (local was `6f6c870`, fast-forwarded; remote HEAD identical) |
| HEAD after DOC-03 | the single DOC-03 commit that contains this file (a file cannot contain the hash of its own commit; see the session summary or `git log -- agentic/_plans/_migration-work/ADR-consolidation-2026-10-22/DOC-03-verification-report.md`) |
| Baseline of `main` | `e0e9de7a74b8bb33e07c7b1f8c92ab87832986de` (blobs of the archive are read from here) |

## 1. Preflight (plan §0)

Repository and branch correct; working tree clean; `origin/docs/adr-consolidation-2026-10-22` = `57990d2…`, local was its strict ancestor (`git merge-base --is-ancestor`), safe `git merge --ff-only`; merge-base with `origin/main` = baseline `e0e9de7…`, all baseline blobs available; no unexpected remote commits; BOPS-0121 `IN_PROGRESS`, `authorized: true`, branch matches; `validate_register.py` 0 errors; GATE-02 APPROVED in plan §7.3; five Candidate contracts and the DOC-02 artifacts present; originals (55 ADRs, `01`, `03`, `06`) byte-identical between baseline and `57990d2` (`git diff e0e9de7 HEAD` empty for them). PR #124 and other branches were not read, fetched or used. Local identity set with repository-local `git config` (`Fabio Cavallari <fcavallari@bsoftsolutions.it>`).

## 2. CP-03a — archive integrity: PASS

Build: a deterministic Python script wrote `docs/architecture/archive/legacy-adr-and-decisions-2026-10-22.zip` from `git cat-file blob e0e9de7…:<path>` (fixed timestamps, deflate level 9), then the external `MANIFEST.csv`, `MANIFEST.md`, `.sha256` and `README.md`. Archive size 526,229 bytes; 1,358,451 bytes uncompressed. The check ran **before** any original was removed (the removal of the ADR files happened afterwards) and was re-run on the final tree.

Reproduce (from the repository root). `cp03a.py` and `cp03b.py` are **not versioned files**: they exist only as the code blocks of Appendix A and Appendix B of this report.
Recreate them in a scratch directory outside the repository (for example by copying each fenced `python` block into `cp03a.py` / `cp03b.py`, or with the extractor
shown below) and run them with the repository root as the current directory. Both scripts read only versioned inputs (the ZIP, manifests, DOC-02 artifacts, tracked files and baseline blobs via `git cat-file`).

```bash
SCRATCH=$(mktemp -d)
python3 -I - "$SCRATCH" <<'PY'
import re,sys
t=open('agentic/_plans/_migration-work/ADR-consolidation-2026-10-22/DOC-03-verification-report.md').read()
for name,ap in (('cp03a','Appendix A'),('cp03b','Appendix B')):
    i=t.index('## '+ap); m=re.search(r'```python\n(.*?)\n```',t[i:],re.S)
    open(sys.argv[1]+'/'+name+'.py','w').write(m.group(1)+'\n')
PY
python3 -I "$SCRATCH/cp03a.py"                       # exit 0 = PASS
(cd docs/architecture/archive && sha256sum -c legacy-adr-and-decisions-2026-10-22.zip.sha256)
git cat-file blob e0e9de7a74b8bb33e07c7b1f8c92ab87832986de:agentic/06-decisions.md | sha256sum   # compare with MANIFEST.csv
```

Output:

```
PASS 1 exactly 57 entries 57
PASS 1b no duplicates 
PASS 2 exact approved path set 
PASS 1c no metadata/other entries 
PASS zip integrity (testzip) 
PASS 3 each entry == git blob of baseline (git cat-file) []
PASS 4a csv has 57 rows, same path set 
PASS 4b manifest.md has 57 rows consistent with csv 
PASS 5 test extraction into empty dir reproduces 57 files byte-exact 57
PASS 6a sidecar sha256 matches zip 
PASS 6b manifests contain neither zip hash nor each other's/own hash 
PASS 7 consistent with DOC-02 ledger (sha256,status) and id-coverage (55 ADRs)
```

`sha256sum -c`: `legacy-adr-and-decisions-2026-10-22.zip: OK`.

## 3. CP-03b — identifier resolution and link destinations: PASS

Method (script in Appendix B): (1) the identifiers resolvable **before** are derived from the baseline blobs (55 ADR files, D-001…D-050 headings of `06`, A/B/C/D rules of `01`, S1–S11 of `03`); those resolvable **after** are the rows of the four compatibility indexes; every index row must carry a destination whose file and anchor exist. (2) Every `ADR-NNNN` (also `ADR NNNN`, `adr/NNNN-…` paths, `ADR-NNNN/NNNN` lists), `D-NNN`, `rule A/B/C/D…` and `rule Sn` occurrence in all tracked text files outside the indexes, the contracts, the archive and the migration artifacts is classified as *ok-before-and-after*, *PRE-EXISTING-ANOMALY* (never resolvable) or *NEWLY-BROKEN* (resolvable before, not after: not acceptable). (3) All Markdown links of the current-docs set are resolved, including `#anchor` (GitHub slug rules and `<a id>`), and compared with the baseline. Negative controls were run: removing `ADR-0045` and `S5` from the "after" sets gave 72 NEWLY-BROKEN and FAIL; appending a broken file link and a broken anchor link to `README.md` gave 2 newly broken links and FAIL (file restored).

```bash
python3 -I "$SCRATCH/cp03b.py"          # exit 0 = PASS
NEG=1 python3 -I "$SCRATCH/cp03b.py"    # negative control: must FAIL
```

Output:

```
baseline-valid  ADR 55 D 50 rules A/B/C/D 33 S 11
index-resolvable ADR 55 D 50 rules A/B/C/D 33 S 11
index docs/architecture/adr/README.md rows/links checked (55, 215)
index agentic/01-architecture-rules.md rows/links checked (33, 53)
index agentic/03-security-rules.md rows/links checked (11, 12)
index agentic/06-decisions.md rows/links checked (50, 108)
index destination problems: []

references checked (occurrences) by bucket/kind/result:
  ('code', 'ADR') {'ok-before-and-after': 1630}
  ('code', 'D') {'ok-before-and-after': 39}
  ('code', 'S') {'ok-before-and-after': 109}
  ('code', 'rule') {'ok-before-and-after': 139}
  ('current-docs', 'ADR') {'ok-before-and-after': 149}
  ('current-docs', 'D') {'ok-before-and-after': 28}
  ('current-docs', 'S') {'ok-before-and-after': 22}
  ('current-docs', 'rule') {'ok-before-and-after': 28}
  ('historical-docs', 'ADR') {'ok-before-and-after': 782, 'PRE-EXISTING-ANOMALY': 4}
  ('historical-docs', 'D') {'ok-before-and-after': 108}
  ('historical-docs', 'S') {'ok-before-and-after': 18}
  ('historical-docs', 'rule') {'ok-before-and-after': 14}
TOTAL occurrences 3070 {'ok-before-and-after': 3066, 'PRE-EXISTING-ANOMALY': 4}
unique identifiers referenced 133

PRE-EXISTING anomalies (never resolvable at baseline): 4
   ('ADR', '0003', 'agentic/obsolete/plans/piano-bops.md', 1170)
   ('ADR', '0004', 'agentic/obsolete/plans/piano-bops.md', 1171)
   ('ADR', '0007', 'agentic/obsolete/plans/piano-bops.md', 1174)
   ('ADR', '0008', 'agentic/obsolete/plans/piano-bops.md', 1175)

NEWLY BROKEN identifier references: 0

path-style ADR citations to paths that never existed (pre-existing, listed separately): 1
   ('src/core/bOps.Abstractions/Audit.cs', 673, 'agentic/architecture/adr/0013', 'path never existed at baseline')

Markdown links — current docs checked: 71 files; broken after: 0; broken at baseline: 0
NEWLY BROKEN links in current docs (existing files): 0
broken links in files added by DOC-02/DOC-03: 0
pre-existing broken links in current docs (unchanged): 0
historical docs (151 files): links to removed ADR files left unchanged by design (G1-06): 37 ; other new: 0

CP-03b RESULT: PASS
```

Reading the result:

1. **Previously valid references correctly resolved after DOC-03:** 3,066 occurrences (133 unique identifiers) in code (src, tests, web, scripts, config), current documents and historical documents; all resolve through an index row whose destination exists.
2. **Pre-existing anomalies (documented, not caused by DOC-03):** `ADR-0003/0004/0007/0008` in `agentic/obsolete/plans/piano-bops.md` (numbers that never existed); `agentic/architecture/adr/0013` cited at `src/core/bOps.Abstractions/Audit.cs:673` (path never existed; the ID itself resolves); `docs/benchmarks/bench00-maf-compatibility-report.md` missing (G1-10, not reconstructed); a relative link in ADR-0057 to `know-m1-b-fix01…` (archived unchanged).
3. **Newly broken references caused by DOC-03:** none (identifiers 0; links in current docs 0).

Links to removed ADR **files** in historical documents (packets, closed plans, CHANGELOG, benchmark and maintenance records, handoff archive, obsolete) are intentionally left unchanged (plan G1-06; the identifiers still resolve through the index, the former file names are in its second column): 76 matching lines (37 of them are Markdown links reported by the checker as broken destinations; the others are plain-text path mentions), by file:

- `CHANGELOG.md`: 1
- `agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md`: 9
- `agentic/_plans/2026-10-11-maf-knowledge-convergence.md`: 1
- `agentic/_tasks/2026-09-16-v1.1-g-secure-settings.md`: 1
- `agentic/_tasks/2026-09-16-v1.2-multi-agent.md`: 2
- `agentic/_tasks/2026-09-18-v1.2-a-adr-and-plan.md`: 1
- `agentic/_tasks/2026-09-18-v1.2-b-contracts.md`: 1
- `agentic/_tasks/2026-09-18-v1.2-c-authority-reduction.md`: 2
- `agentic/_tasks/2026-09-18-v1.2-d-orchestrator-and-roles.md`: 2
- `agentic/_tasks/2026-09-18-v1.2-e-budgets-deadlines-cancellation.md`: 1
- `agentic/_tasks/2026-09-18-v1.2-f-durable-state-and-resume.md`: 1
- `agentic/_tasks/2026-09-18-v1.2-g-independent-verification.md`: 1
- `agentic/_tasks/2026-09-18-v1.2-h-audit-correlation.md`: 1
- `agentic/_tasks/2026-09-18-v1.2-i-cli.md`: 1
- `agentic/_tasks/2026-09-18-v1.2-j-api.md`: 1
- `agentic/_tasks/2026-09-18-v1.2-k-ui.md`: 1
- `agentic/_tasks/2026-09-18-v1.2-l-documentation.md`: 1
- `agentic/_tasks/2026-09-18-v1.2-m-integration-release.md`: 1
- `agentic/_tasks/2026-09-21-v1.3-a-system-events.md`: 1
- `agentic/_tasks/2026-09-21-v1.3-b-docker-management.md`: 1
- `agentic/_tasks/2026-09-21-v1.3-c-process-diagnostics.md`: 1
- `agentic/_tasks/2026-09-21-v1.3-d-network-diagnostics.md`: 1
- `agentic/_tasks/2026-09-25-v1.3x-harden-02-model-failure-containment.md`: 1
- `agentic/_tasks/2026-09-25-v1.3x-harden-03-resume-state-machine.md`: 1
- `agentic/_tasks/2026-09-25-v1.3x-harden-07-windows-stability-evidence.md`: 2
- `agentic/_tasks/2026-09-25-v1.3x-harden-08-context-budget-economy.md`: 3
- `agentic/_tasks/2026-09-25-v1.3x-harden-09-evidence-reasoning.md`: 3
- `agentic/_tasks/2026-09-25-v1.3x-harden-10-browser-session.md`: 2
- `agentic/_tasks/2026-09-25-v1.3x-harden-11-delegation-operability.md`: 3
- `agentic/_tasks/2026-09-25-v1.3x-harden-13-provider-defaults-fallback.md`: 3
- `agentic/_tasks/2026-10-07-runtime-step-routing-replan-safety.md`: 1
- `agentic/_tasks/2026-10-08-v1.3-pre-release-stabilization.md`: 1
- `agentic/_tasks/2026-10-08-v1.3z-prerequisite-readiness-system-messages.md`: 1
- `agentic/_tasks/2026-10-08-v1.3z-windows-kernel-dump-analysis.md`: 1
- `agentic/_tasks/2026-10-09-v1.3-f25-durable-ordinary-mutation-journal.md`: 1
- `agentic/_tasks/2026-10-10-af-01-maf-foundation-governed-inference.md`: 1
- `agentic/_tasks/2026-10-10-af-02-maf-context-compaction.md`: 1
- `agentic/_tasks/2026-10-10-af-03-governed-summarization.md`: 1
- `agentic/_tasks/2026-10-10-af-04-evidence-cleanup.md`: 1
- `agentic/_tasks/2026-10-10-af-05-maf-regression-tuning-cleanup.md`: 1
- `agentic/_tasks/2026-10-11-af-05-benchmark-results.md`: 1
- `agentic/_tasks/2026-10-11-af-05h-live-regression-hardening.md`: 1
- `agentic/_tasks/2026-10-12-af-05h2-r2-implementation.md`: 1
- `agentic/_tasks/2026-10-12-af-plan-01-planning-robustness.md`: 1
- `agentic/obsolete/plans/2026-09-16-v1.0-security-hardening.md`: 1
- `agentic/obsolete/plans/2026-09-16-v1.1-skill-capability-sdk.md`: 2
- `docs/benchmarks/prompt-routing-finalization.md`: 1
- `docs/handoff-archive/HANDOFF-until-2026-10-21.md`: 6
- `docs/handoff-archive/tasks-README-until-2026-10-21.md`: 1

## 4. Final DOC-03 verification

| Check | Result |
|---|---|
| CP-03a / CP-03b | PASS / PASS |
| Exactly five contracts marked `Current` (`docs/architecture/current/01…05`), 105 binding rules (`C01` 21, `C02` 29, `C03` 24, `C04` 18, `C05` 13) | PASS; only the Status, Git base and Task rows and one archive-pointer paragraph in *Sources* changed (`git diff` of the five files: 25 insertions, 15 deletions, no rule text touched) |
| `01`, `03`, `06` are non-normative compatibility indexes; `docs/architecture/adr/README.md` is the ADR index | PASS (rows: ADR 55, A/B/C/D 33, S 11, D 50) |
| All 55 ADRs present in the archive (57 entries) | PASS |
| No active bootstrap requires the archive or ADRs | PASS (`agentic/00-bootstrap.md` forbids reading them; indexes are lookup-only) |
| No competing normative architecture document | PASS (remaining files in `docs/architecture/` are historical evidence reports or technical references) |
| No Proposed decision promoted; D1, D4, D5 open; Knowledge M1 limited by C05-R13 and D-049 | PASS (seven Proposed ADRs listed as Proposed in the ADR index; contract pending sections untouched) |
| C02-R12, C05-R13 and S1–S11 intact | PASS (contract rule text untouched; S1–S11 map unchanged) |
| Remaining current-documentation links resolve | PASS (71 files, 0 broken) |
| No change to source code, tests, CI, scripts, package manifests or production configuration | PASS (`git status` shows only `.md` files and the archive) |
| PR #124 not used | PASS |
| `validate_register.py` | PASS: 121 tasks, 0 errors; BOPS-0121 remains `IN_PROGRESS`; `register.json` not modified |
| Issue #125 / Project #6 | `sync_pending` unchanged; no remote write was made |

## 5. Phases and what changed

**Phase B** — removed the 55 `docs/architecture/adr/*.md`; created the ADR README (55 rows: former file name, title, decision status, destination; per-row anchors `#adr-NNNN`); reduced `agentic/01-architecture-rules.md`, `agentic/03-security-rules.md` (topic labels only, no safety text) and `agentic/06-decisions.md` to compatibility indexes generated from `DOC-02-id-coverage.tsv`. A12/D-012: `historical-only (expired)`.

**Phase C** — contracts 01–05 promoted `Candidate` → `Current`.

**Phase D** — `agentic/00-bootstrap.md` rewritten (precedence chain of plan §3.1, task identification, contract routing table, technical references on demand, stop conditions); `CLAUDE.md` reduced to a router (hardening plan no longer listed as "active", ADR wording removed); `AGENTS.md` created; `agentic/README.md` is a folder index without a reading order; `PROCESS.md` only the "Architectural decisions" row of §1; `00-project-spec.md` (V1.3-M merged, D-xxx/ADR pointers → contract rule ids); `02-coding-standards.md` (copy of `Directory.Build.props` replaced by a link); `04-testing-rules.md` (nonexistent `examples/`); `05-workflow.md` (no ADR obligation, no ADR tables, structural decisions = operator-approved contract change); roadmap §1, §16, §17; `agentic/_plans/2026-09-22-v1.3-coding-agent-execution.md` references; `agentic/obsolete/README.md`; `README.md`; `CONTRIBUTING.md`; `SECURITY.md`; `docs/development/claude-code-skills.md`.

**Phase E — links repaired** in 18 current documents (32 link destinations). Plain ADR links became `[Cn-Rmm (was ADR-NNNN)]` pointing to the contract rule; links to Proposed or M1-scoped ADRs (0054, 0055, 0056, 0057, 0058) point to the ADR index anchor (`adr/README.md#adr-NNNN`); directory links to the retired ADR folder point to `docs/architecture/current/`. Documents: `README.md`, `docs/deployment.md`, `docs/development/claude-code-skills.md`, `docs/docker-management.md`, `docs/knowledge/local-agent-pilot.md`, `docs/network-diagnostics.md`, `docs/operator-configuration.md`, `docs/plugins/getting-started.md`, `docs/process-diagnostics.md`, `docs/system-events.md`, `docs/system-maintenance.md`, `docs/system-stability.md`, `docs/windows-dump-analysis.md`, the four `docs/architecture/know-*.md` reports (links only), `agentic/_tasks/README.md`.

## 6. Anomalies and residual items (for DOC-04 or separate tasks)

- `agentic/_tasks/PROCESS.md` line 6 still says changing "an accepted ADR" is a normative change; only the §1 row was authorized for change. Follow-up.
- `agentic/_tasks/README.md` historical packet-map text (line "A packet with an ADR prerequisite drafts the ADR first") describes the closed hardening train; left as history.
- `agentic/_plans/2026-10-11-maf-knowledge-convergence.md` and `agentic/_plans/2026-09-25-v1.3x-reliability-hardening.md` keep their ADR file links (closed/KEEP unmodified); status-table conflict with `PROCESS.md` §1 (C10) remains a follow-up.
- Contracts keep the P5 readability debt (nine rule paragraphs above 4,000 characters), scheduled in DOC-04, not done here.
- The contracts' *Sources* sections name archived files as plain text (provenance), not links.
- Implementation-evidence `file:line` anchors in the contracts were not re-verified in DOC-03 (no code claim was added or changed).
- Nothing in DOC-03 changed runtime behaviour, resolved D1/D4/D5 or modified an architectural acceptance status.

## 7. Paths

Added (8):

- `AGENTS.md`
- `agentic/_plans/_migration-work/ADR-consolidation-2026-10-22/DOC-03-verification-report.md`
- `docs/architecture/adr/README.md`
- `docs/architecture/archive/MANIFEST.csv`
- `docs/architecture/archive/MANIFEST.md`
- `docs/architecture/archive/README.md`
- `docs/architecture/archive/legacy-adr-and-decisions-2026-10-22.zip`
- `docs/architecture/archive/legacy-adr-and-decisions-2026-10-22.zip.sha256`

Modified (41):

- `CLAUDE.md`
- `CONTRIBUTING.md`
- `HANDOFF.md`
- `README.md`
- `SECURITY.md`
- `agentic/00-bootstrap.md`
- `agentic/00-project-spec.md`
- `agentic/01-architecture-rules.md`
- `agentic/02-coding-standards.md`
- `agentic/03-security-rules.md`
- `agentic/04-testing-rules.md`
- `agentic/05-workflow.md`
- `agentic/06-decisions.md`
- `agentic/README.md`
- `agentic/_plans/2026-09-16-consolidated-roadmap.md`
- `agentic/_plans/2026-09-22-v1.3-coding-agent-execution.md`
- `agentic/_plans/ADR-consolidation-2026-10-22.md`
- `agentic/_tasks/PROCESS.md`
- `agentic/_tasks/README.md`
- `agentic/obsolete/README.md`
- `docs/architecture/current/01-maf-agent-infrastructure.md`
- `docs/architecture/current/02-tools-safety-authority.md`
- `docs/architecture/current/03-task-state-resume-mutations.md`
- `docs/architecture/current/04-evidence-and-diagnosis.md`
- `docs/architecture/current/05-interfaces-knowledge-and-hosts.md`
- `docs/architecture/know-01a-trusted-knowledge-foundation.md`
- `docs/architecture/know-01b-runtime-knowledge-read.md`
- `docs/architecture/know-disc-01-knowledge-index.md`
- `docs/architecture/know-m1-b-fix01-unicode-globalization.md`
- `docs/deployment.md`
- `docs/development/claude-code-skills.md`
- `docs/docker-management.md`
- `docs/knowledge/local-agent-pilot.md`
- `docs/network-diagnostics.md`
- `docs/operator-configuration.md`
- `docs/plugins/getting-started.md`
- `docs/process-diagnostics.md`
- `docs/system-events.md`
- `docs/system-maintenance.md`
- `docs/system-stability.md`
- `docs/windows-dump-analysis.md`

Deleted (55), all preserved byte-for-byte in the ZIP:

- `docs/architecture/adr/0001-project-owned-chat-model.md`
- `docs/architecture/adr/0002-five-risk-levels-critical-unbypassable.md`
- `docs/architecture/adr/0005-shared-openai-compatible-adapter.md`
- `docs/architecture/adr/0006-tools-os-providers-are-packages.md`
- `docs/architecture/adr/0011-local-only-remote-capable-contract.md`
- `docs/architecture/adr/0012-dynamic-loading-over-native-aot.md`
- `docs/architecture/adr/0013-model-call-audit-outcome.md`
- `docs/architecture/adr/0014-explicit-planning-and-replanning.md`
- `docs/architecture/adr/0015-policy-engine-approval-flow-and-audit-hash-chaining.md`
- `docs/architecture/adr/0016-post-action-verification.md`
- `docs/architecture/adr/0017-persistent-resumable-tasks.md`
- `docs/architecture/adr/0018-bops-api-minimal-surface.md`
- `docs/architecture/adr/0019-providers-listing-endpoint.md`
- `docs/architecture/adr/0020-plugin-loader-manifest-and-activation-boundary.md`
- `docs/architecture/adr/0021-service-package-windows-linux-strategy.md`
- `docs/architecture/adr/0022-bops-abstractions-1.0-security-boundaries.md`
- `docs/architecture/adr/0023-skill-capability-evidence-execution-plan.md`
- `docs/architecture/adr/0024-execution-plan-orchestration.md`
- `docs/architecture/adr/0025-skill-provider-and-restricted-invocation.md`
- `docs/architecture/adr/0026-bounded-filesystem-inventory-manifests.md`
- `docs/architecture/adr/0027-governed-recursive-deletion-manifests.md`
- `docs/architecture/adr/0028-outbound-web-search-and-fetch.md`
- `docs/architecture/adr/0029-encrypted-local-vault-and-master-key.md`
- `docs/architecture/adr/0030-privilege-reducing-multi-agent-delegation.md`
- `docs/architecture/adr/0031-per-role-envelope-requirements.md`
- `docs/architecture/adr/0032-bounded-cross-platform-system-events.md`
- `docs/architecture/adr/0033-governed-docker-image-build-and-volume-management.md`
- `docs/architecture/adr/0034-advanced-process-diagnostics.md`
- `docs/architecture/adr/0035-sockets-routes-and-network-diagnostics.md`
- `docs/architecture/adr/0036-product-neutral-entitlement-applicability-and-execution-time-enforcement.md`
- `docs/architecture/adr/0037-safe-local-plugin-lifecycle.md`
- `docs/architecture/adr/0038-provider-wire-contract.md`
- `docs/architecture/adr/0039-model-call-failure-containment.md`
- `docs/architecture/adr/0040-resume-state-machine.md`
- `docs/architecture/adr/0041-typed-cross-platform-stability-evidence.md`
- `docs/architecture/adr/0042-evidence-reasoning-and-limitation-disclosure.md`
- `docs/architecture/adr/0043-browser-web-session.md`
- `docs/architecture/adr/0044-request-dependent-delegation-authority-and-operability.md`
- `docs/architecture/adr/0045-execution-pinned-provider-configuration-and-fallback-chain.md`
- `docs/architecture/adr/0046-step-scoped-tool-routing-and-transactional-replanning.md`
- `docs/architecture/adr/0047-bounded-planned-step-argument-correction.md`
- `docs/architecture/adr/0048-bounded-windows-kernel-dump-analysis.md`
- `docs/architecture/adr/0049-unified-prerequisite-readiness-and-system-messages.md`
- `docs/architecture/adr/0050-semantic-planned-step-execution-and-conditional-follow-ups.md`
- `docs/architecture/adr/0051-durable-ordinary-task-mutation-intent-journal.md`
- `docs/architecture/adr/0052-microsoft-agent-framework-bops-boundary.md`
- `docs/architecture/adr/0053-maf-convergence-architecture-boundaries.md`
- `docs/architecture/adr/0054-maf-bplus-convergence-decision.md`
- `docs/architecture/adr/0055-trusted-knowledge-packs-and-provider-isolation.md`
- `docs/architecture/adr/0056-governed-knowledge-read-and-durable-resume.md`
- `docs/architecture/adr/0057-knowledge-discovery-index.md`
- `docs/architecture/adr/0058-local-agent-knowledge-pilot.md`
- `docs/architecture/adr/0059-knowledge-refusal-recovery.md`
- `docs/architecture/adr/0060-planner-facing-tool-parameter-guidance.md`
- `docs/architecture/adr/0061-system-events-source-filter-disclosure.md`

## Appendix A — `cp03a.py`

```python
import zipfile,hashlib,subprocess,csv,re,tempfile,os,sys,glob
R='/home/user/bOps'; A=R+'/docs/architecture/archive/'; B='e0e9de7a74b8bb33e07c7b1f8c92ab87832986de'
res=[]
def chk(n,ok,info=''): res.append((n,ok,info)); print(('PASS' if ok else 'FAIL'),n,info)
z=zipfile.ZipFile(A+'legacy-adr-and-decisions-2026-10-22.zip')
names=[i.filename for i in z.infolist()]
exp=sorted(subprocess.check_output(['git','-C',R,'ls-tree','-r','--name-only',B,'docs/architecture/adr'],text=True).split())
exp+=['agentic/01-architecture-rules.md','agentic/06-decisions.md']
chk('1 exactly 57 entries',len(names)==57,len(names))
chk('1b no duplicates',len(set(names))==len(names))
chk('2 exact approved path set',set(names)==set(exp) and len(exp)==57)
chk('1c no metadata/other entries',all(n.startswith('docs/architecture/adr/') or n in('agentic/01-architecture-rules.md','agentic/06-decisions.md') for n in names) and not any(re.search(r'MANIFEST|\.csv|\.sha256|README',n) for n in names))
chk('zip integrity (testzip)',z.testzip() is None)
bad=[]; man={}
for r in csv.DictReader(open(A+'MANIFEST.csv',encoding='utf-8')): man[r['path']]=r
for n in names:
    data=z.read(n); blob=subprocess.check_output(['git','-C',R,'cat-file','blob',f'{B}:{n}'])
    if data!=blob: bad.append((n,'bytes'))
    m=man.get(n)
    if not m or m['sha256_content']!=hashlib.sha256(blob).hexdigest() or int(m['size_bytes'])!=len(blob): bad.append((n,'csv'))
    if m and m['git_blob_sha']==m['sha256_content']: bad.append((n,'blobsha==sha256'))
    if m and m['git_blob_sha']!=subprocess.check_output(['git','-C',R,'rev-parse',f'{B}:{n}'],text=True).strip(): bad.append((n,'blobsha'))
chk('3 each entry == git blob of baseline (git cat-file)',not bad,bad)
chk('4a csv has 57 rows, same path set',set(man)==set(names) and len(man)==57)
md=open(A+'MANIFEST.md',encoding='utf-8').read()
mdrows=re.findall(r'^\| \d+ \| `([^`]+)` \| (\d+) \| `([0-9a-f]{64})`',md,re.M)
mdmap={p:(int(s),h) for p,s,h in mdrows}
chk('4b manifest.md has 57 rows consistent with csv',len(mdrows)==57 and all(mdmap[p]==(int(man[p]['size_bytes']),man[p]['sha256_content']) for p in man))
tmp=tempfile.mkdtemp(prefix='cp03a-'); assert not os.listdir(tmp)
z.extractall(tmp)
ext=sorted(os.path.relpath(os.path.join(d,f),tmp) for d,_,fs in os.walk(tmp) for f in fs)
ok=ext==sorted(exp) and all(open(os.path.join(tmp,p),'rb').read()==subprocess.check_output(['git','-C',R,'cat-file','blob',f'{B}:{p}']) for p in exp)
chk('5 test extraction into empty dir reproduces 57 files byte-exact',ok,len(ext))
zs=hashlib.sha256(open(A+'legacy-adr-and-decisions-2026-10-22.zip','rb').read()).hexdigest()
side=open(A+'legacy-adr-and-decisions-2026-10-22.zip.sha256').read().split()
chk('6a sidecar sha256 matches zip',side[0]==zs and side[1]=='legacy-adr-and-decisions-2026-10-22.zip')
own=[ (f,open(A+f,encoding='utf-8').read()) for f in('MANIFEST.md','MANIFEST.csv')]
chk('6b manifests contain neither zip hash nor each other\'s/own hash',all(zs not in t for f,t in own) and hashlib.sha256(open(A+'MANIFEST.csv','rb').read()).hexdigest() not in md)
# coverage consistency with DOC-02
M=R+'/agentic/_plans/_migration-work/ADR-consolidation-2026-10-22/'
cov={l.split('\t')[0]:l.split('\t') for l in open(M+'DOC-02-id-coverage.tsv',encoding='utf-8') if not l.startswith('#')}
led=[l.split('\t') for l in open(M+'DOC-02-read-ledger.tsv',encoding='utf-8') if not l.startswith('#') and not l.startswith('adr\t')]
okc=all(l[1] in names and l[4]==hashlib.sha256(z.read(l[1])).hexdigest() for l in led) and len(led)==55
okc&=all(('ADR-'+re.search(r'/(\d{4})-',p).group(1)) in cov for p in names if p.startswith('docs/'))
okc&=all(man[p]['decision_status']==next(l[5] for l in led if l[1]==p) for p in names if p.startswith('docs/'))
chk('7 consistent with DOC-02 ledger (sha256,status) and id-coverage (55 ADRs)',okc)
sys.exit(0 if all(r[1] for r in res) else 1)
```

## Appendix B — `cp03b.py`

```python
"""CP-03b: historical identifier resolution + Markdown link destinations. Usage: python3 -I cp03b.py  (run at repo root = /home/user/bOps)"""
import re,subprocess,os,sys,collections
R='/home/user/bOps'; B='e0e9de7a74b8bb33e07c7b1f8c92ab87832986de'
def git(*a,**k): return subprocess.check_output(['git','-C',R,*a],**k)
def slug(h):
    h=re.sub(r'<[^>]+>','',h).strip().lower().replace('`',''); h=re.sub(r'\[([^\]]*)\]\([^)]*\)',r'\1',h)
    return re.sub(r'[^\w\- ]','',h,flags=re.U).replace(' ','-')
def anchors_of(text):
    out=set(); seen=collections.Counter(); fence=False
    for l in text.split('\n'):
        if l.startswith('```'): fence=not fence; continue
        if fence: continue
        m=re.match(r'#{1,6}\s+(.*?)\s*#*\s*$',l)
        if m:
            s=slug(m.group(1)); n=seen[s]; seen[s]+=1; out.add(s if n==0 else f'{s}-{n}')
    out|=set(re.findall(r'<a\s+(?:id|name)="([^"]+)"',text)); return out
# ---------- file universe (tracked, current tree)
files=git('ls-files').decode().split('\n'); files=[f for f in files if f and os.path.exists(f'{R}/{f}')]
TEXT=('.cs','.md','.json','.yml','.yaml','.props','.csproj','.ts','.py','.ps1','.sh','.html','.txt','.slnx','.targets','.razor','.scss','.css','.tsv','.csv','.http')
HIST=re.compile(r'^(agentic/obsolete/|docs/handoff-archive/|docs/benchmarks/|docs/maintenance/|agentic/_tasks/20\d\d-|CHANGELOG\.md$|agentic/_plans/2026-09-25|agentic/_plans/2026-10-11|agentic/_plans/ADR-consolidation|agentic/_plans/_migration-work/|docs/architecture/archive/)')
SELF=re.compile(r'^(docs/architecture/adr/README\.md|agentic/01-architecture-rules\.md|agentic/03-security-rules\.md|agentic/06-decisions\.md|docs/architecture/current/|docs/architecture/archive/|agentic/_plans/_migration-work/|agentic/_plans/ADR-consolidation)')
def bucket(f):
    if f.endswith('.md') and not HIST.match(f) and not f.startswith(('src/','tests/','web/','scripts/')): return 'current-docs'
    if f.endswith('.md') and HIST.match(f): return 'historical-docs'
    return 'code'
# ---------- identifier universe: valid BEFORE (baseline blobs) and AFTER (index files)
def before_sets():
    adr={re.match(r'.*/(\d{4})-',p).group(1) for p in git('ls-tree','-r','--name-only',B,'docs/architecture/adr').decode().split()}
    t06=git('show',f'{B}:agentic/06-decisions.md').decode(); t01=git('show',f'{B}:agentic/01-architecture-rules.md').decode(); t03=git('show',f'{B}:agentic/03-security-rules.md').decode()
    d={m for m in re.findall(r'^#{2,4}\s+(D-\d{3})\b',t06,re.M)}
    rules=set(re.findall(r'^### ([AB]\d{1,2}) —',t01,re.M))
    sc=t01[t01.index('## C. The agent loop'):t01.index('## D.')]
    rules|={f'C{n}' for n in re.findall(r'^(\d+)\. \*\*',sc,re.M)}
    if re.search(r'^## D\. ',t01,re.M): rules.add('D')
    s={m for m in re.findall(r'^#{2,4}\s+(S\d{1,2})\b',t03,re.M)}
    return adr,d,rules,s
def after_sets():
    t=open(f'{R}/docs/architecture/adr/README.md',encoding='utf-8').read()
    adr=set(re.findall(r'<a id="adr-(\d{4})"',t))
    def ids(f,pat): return set(re.findall(pat,open(f'{R}/{f}',encoding='utf-8').read(),re.M))
    d=ids('agentic/06-decisions.md',r'^\| (D-\d{3}) \|')
    rules=ids('agentic/01-architecture-rules.md',r'^\| ([ABCD]\d{0,2}) \|'); s=ids('agentic/03-security-rules.md',r'^\| (S\d{1,2}) \|')
    return adr,d,rules,s
bA,bD,bR,bS=before_sets(); aA,aD,aR,aS=after_sets()
if os.environ.get('NEG'): aA.discard('0045'); aS.discard('S5')   # negative control: must report NEWLY-BROKEN
print('baseline-valid  ADR',len(bA),'D',len(bD),'rules A/B/C/D',len(bR),'S',len(bS))
print('index-resolvable ADR',len(aA),'D',len(aD),'rules A/B/C/D',len(aR),'S',len(aS))
# every index row must lead to existing destinations (identifier -> destination resolves)
bad_dest=[]
def check_index_links(f):
    base=os.path.dirname(f); text=open(f'{R}/{f}',encoding='utf-8').read(); n=0; rows=0
    for l in text.split('\n'):
        if l.startswith('| ') and re.match(r'\| (<a id="adr-\d{4}"></a>ADR-\d{4}|[ABCDS]\d{0,2}|D-\d{3}) \|',l):
            rows+=1; links=re.findall(r'\]\(([^)]+)\)',l)
            if not links and 'historical only' not in l: bad_dest.append((f,l[:60],'no destination'))
            for u in links:
                n+=1; p,_,a=u.partition('#'); t=os.path.normpath(os.path.join(R,base,p))
                if not os.path.exists(t): bad_dest.append((f,u,'missing file')); continue
                if a and t.endswith('.md') and a not in anchors_of(open(t,encoding='utf-8').read()): bad_dest.append((f,u,'missing anchor'))
    return rows,n
for f in ['docs/architecture/adr/README.md','agentic/01-architecture-rules.md','agentic/03-security-rules.md','agentic/06-decisions.md']:
    print('index',f,'rows/links checked',check_index_links(f))
print('index destination problems:',bad_dest)
# ---------- references in source files
PAT={'ADR':re.compile(r'\bADR[- ](\d{4})\b|\badr/(\d{4})(?=[-.)`\s/]|$)|(?<=\bADR-\d{4}[/,])(\d{4})\b|\bADRs\s+(\d{4})\b'),
     'D':re.compile(r'\bD-(\d{3})\b'),
     'rule':re.compile(r'(?:\brules?\s+|\b(?:architecture-rules\.md|01-architecture-rules\.md)[`\)\],: ]+(?:rule\s+)?)([ABCD]\d{0,2})\b(?![\w-])'),
     'S':re.compile(r'(?:\brules?\s+|security-rules\.md[`\)\],: ]+(?:rule\s+)?)(S\d{1,2})\b(?![\w-])')}
stats=collections.defaultdict(lambda: collections.Counter()); rows=[]
for f in files:
    if not f.endswith(TEXT) or SELF.match(f) or f.endswith(('.zip',)): continue
    if f.startswith('docs/architecture/adr/'): continue
    try: text=open(f'{R}/{f}',encoding='utf-8').read()
    except Exception: continue
    bk=bucket(f)
    for kind,pat in PAT.items():
        for m in pat.finditer(text):
            raw=next(g for g in m.groups() if g)
            ident={'ADR':raw,'D':f'D-{raw}','rule':raw,'S':raw}[kind]
            if kind=='D' and not (1<=int(raw)<=50): ident=None
            before={'ADR':bA,'D':bD,'rule':bR,'S':bS}[kind]; after={'ADR':aA,'D':aD,'rule':aR,'S':aS}[kind]
            if ident is None: res='out-of-range'
            else:
                vb=ident in before; va=ident in after
                res='ok-before-and-after' if vb and va else ('PRE-EXISTING-ANOMALY' if not vb and not va else ('NEWLY-BROKEN' if vb and not va else 'newly-resolvable'))
            line=text.count('\n',0,m.start())+1
            stats[(bk,kind)][res]+=1; rows.append((res,bk,kind,ident,f,line))
tot=collections.Counter(); 
print('\nreferences checked (occurrences) by bucket/kind/result:')
for k in sorted(stats): print(' ',k,dict(stats[k])); tot.update(stats[k])
print('TOTAL occurrences',sum(tot.values()),dict(tot))
print('unique identifiers referenced',len({(r[2],r[3]) for r in rows}))
anom=sorted({(r[2],r[3],r[4],r[5]) for r in rows if r[0] in('PRE-EXISTING-ANOMALY','out-of-range')})
print('\nPRE-EXISTING anomalies (never resolvable at baseline):',len(anom))
for a in anom[:60]: print('  ',a)
newly=[r for r in rows if r[0]=='NEWLY-BROKEN']; print('\nNEWLY BROKEN identifier references:',len(newly)); [print('  ',r) for r in newly[:40]]
# path-style citations of ADR files: the cited path must have existed at baseline
btree0=set(git('ls-tree','-r','--name-only',B).decode().split()); pathbad=set()
for f in files:
    if not f.endswith(TEXT) or SELF.match(f) or f.startswith('docs/architecture/adr/'): continue
    try: text=open(f'{R}/{f}',encoding='utf-8').read()
    except Exception: continue
    for m in re.finditer(r'((?:[\w.-]+/)*adr/\d{4}[\w-]*(?:\.md)?)',text):
        c=m.group(1).split('adr/')[0]; ful=m.group(1)
        if ful.startswith('adr/') or c.startswith('../') : continue
        cand=ful if ful.endswith('.md') else None
        if c.endswith('architecture/') and not c.startswith('docs/'): pathbad.add((f,text.count('\n',0,m.start())+1,ful,'path never existed at baseline'))
print('\npath-style ADR citations to paths that never existed (pre-existing, listed separately):',len(pathbad)); [print('  ',x) for x in sorted(pathbad)]
# ---------- Markdown link destinations: current docs (after) vs baseline
LINK=re.compile(r'(?<!\!)\[[^\]]*\]\(([^)\s]+)(?:\s+"[^"]*")?\)')
def broken_links(read,flist,exists,anch):
    out=set()
    for f in flist:
        text=read(f); base=os.path.dirname(f); fence=False
        for ln,l in enumerate(text.split('\n'),1):
            if l.startswith('```'): fence=not fence; continue
            if fence: continue
            l2=re.sub(r'`[^`]*`','',l)
            for u in LINK.findall(l2):
                if re.match(r'(https?:|mailto:|#|tel:|<)',u) and not u.startswith('#'): continue
                p,_,a=u.partition('#')
                t=os.path.normpath(os.path.join(base,p)) if p else f
                if t.startswith('..'): out.add((f,u,'outside repo')); continue
                if not exists(t): out.add((f,u,'missing file')); continue
                if a and t.endswith('.md') and not re.fullmatch(r'L\d+.*',a):
                    if a not in anch(t): out.add((f,u,'missing anchor'))
    return out
curr=[f for f in files if f.endswith('.md') and bucket(f)=='current-docs' and not f.startswith(('src/','tests/','web/','scripts/','samples/','node_modules'))]
hist=[f for f in files if f.endswith('.md') and bucket(f)=='historical-docs']
cache={}
def anch_after(t):
    if t not in cache: cache[t]=anchors_of(open(f'{R}/{t}',encoding='utf-8').read())
    return cache[t]
after_c=broken_links(lambda f:open(f'{R}/{f}',encoding='utf-8').read(),curr,lambda t:os.path.exists(f'{R}/{t}'),anch_after)
after_h=broken_links(lambda f:open(f'{R}/{f}',encoding='utf-8').read(),hist,lambda t:os.path.exists(f'{R}/{t}'),anch_after)
btree=set(git('ls-tree','-r','--name-only',B).decode().split()); bdirs={os.path.dirname(p) for p in btree}|{os.path.dirname(os.path.dirname(p)) for p in btree}
def bread(f): return git('show',f'{B}:{f}').decode('utf-8','replace')
bc={}
def anch_before(t):
    if t not in bc: bc[t]=anchors_of(bread(t))
    return bc[t]
def bexists(t): t=t.rstrip('/'); return t in btree or t in bdirs or t=='' or t=='.'
def dir_ok_after(t): return os.path.exists(f'{R}/{t}')
before_c=broken_links(bread,[f for f in curr if f in btree],bexists,anch_before)
before_h=broken_links(bread,[f for f in hist if f in btree],bexists,anch_before)
print(f'\nMarkdown links — current docs checked: {len(curr)} files; broken after: {len(after_c)}; broken at baseline: {len(before_c)}')
new_c=sorted(x for x in after_c if x not in before_c and (x[0] in btree))
new_c_newfiles=sorted(x for x in after_c if x[0] not in btree)
print('NEWLY BROKEN links in current docs (existing files):',len(new_c)); [print('  ',x) for x in new_c]
print('broken links in files added by DOC-02/DOC-03:',len(new_c_newfiles)); [print('  ',x) for x in new_c_newfiles]
pre=sorted(x for x in after_c if x in before_c); print('pre-existing broken links in current docs (unchanged):',len(pre)); [print('  ',x) for x in pre]
nh=sorted(x for x in after_h if x not in before_h); print(f'historical docs ({len(hist)} files): links to removed ADR files left unchanged by design (G1-06):',len(nh),'; other new:',len([x for x in nh if '/adr/00' not in x[1] and 'adr/00' not in x[1]]))
for x in nh:
    if 'adr/00' not in x[1]: print('   other',x)
fail=bool(newly or bad_dest or new_c or new_c_newfiles)
print('\nCP-03b RESULT:','FAIL' if fail else 'PASS'); sys.exit(1 if fail else 0)
```
