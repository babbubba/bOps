# DOC-02 — Normative content not represented elsewhere before DOC-02, and where it now lives

Non-normative working artifact of the DOC-02 phase of `agentic/_plans/ADR-consolidation-2026-10-22.md`. It is migration evidence, not current documentation, not a contract, and must not be loaded by the bootstrap.

## A. Technical extractions (plan G1-04)

Rows with disposition `TECH-REF` in the matrix, grouped by destination. *Action* says whether the destination was extended or created in DOC-02 (according to `git status` at report time) or already carried the detail.

| Destination | Action | ADR rows | Why this seat |
|---|---|---|---|
| `agentic/02-coding-standards.md` | Pre-existing (no change) | ADR-0015 Decision: AnalysisLevel (90-103); agentic/06-decisions.md D-011 analyzer strictness (175-189) | Engineering standard already stated there (analysis level). |
| `docs/operator-configuration.md` | Extended in DOC-02 | ADR-0017 Decision: CLI resume (53-56); ADR-0039 Decision §7 terminal reason (193-214); ADR-0039 Decision §10 configuration (254-270); ADR-0043 Decision §12 configuration (555-570); ADR-0049 Decision §12 operator UI (205-213) | Existing guide of the tool or area; the detail is consistent with the rule it implements. |
| `docs/architecture/reference/provider-adapters-and-service-tools.md` | Created in DOC-02 | ADR-0021 Decision: Windows (27-33); ADR-0021 Consequences (77-100); ADR-0038 Decision §2 null omission (86-94) | No guide describes provider request serialization or the service tools. |
| `docs/governed-recursive-deletion.md` | Extended in DOC-02 | ADR-0027 Decision: bounds, freshness, retention (69-82) | Existing guide of the tool or area; the detail is consistent with the rule it implements. |
| `docs/system-events.md` | Extended in DOC-02 | ADR-0032 Decision: argument table detail (39-50); ADR-0032 Decision: result JSON detail (60-80); ADR-0032 HARDEN-7 §3 system.events schema 2 (275-327); ADR-0032 HARDEN-7 §9 schema versions (517-539); ADR-0032 HARDEN-9 §6 schema, manifest, audit (718-730); ADR-0061 s2.8 position and byte budget (134-146) | Existing guide of the tool or area; the detail is consistent with the rule it implements. |
| `docs/system-maintenance.md` | Extended in DOC-02 | ADR-0032 HARDEN-7 §4 crash detail tables (366-406) | Existing guide of the tool or area; the detail is consistent with the rule it implements. |
| `docs/docker-management.md` | Pre-existing (no change) | ADR-0033 Decision: tool table (24-40); ADR-0033 Decision: references and names (72-81) | Existing guide of the tool or area; the detail is consistent with the rule it implements. |
| `docs/process-diagnostics.md` | Extended in DOC-02 | ADR-0034 Decision: process.metrics (63-79); ADR-0034 Decision: process.tree (81-100); ADR-0034 Decision: process.modules (102-115); ADR-0034 Decision: Linux collection (141-169) | Existing guide of the tool or area; the detail is consistent with the rule it implements. |
| `docs/network-diagnostics.md` | Extended in DOC-02 | ADR-0035 Decision: network.sockets (57-78); ADR-0035 Decision: routes, neighbors, interface_stats (80-113) | Existing guide of the tool or area; the detail is consistent with the rule it implements. |
| `docs/architecture/reference/task-store-and-mutation-journal.md` | Created in DOC-02 | ADR-0040 Decision §4.2 SQLite (167-178); ADR-0051 Decision s6.2 schema (269-300) | No guide describes the task store or the mutation journal (ADR-0040 §4.2, ADR-0051); a reference page was the only fit. |
| `docs/system-stability.md` | Extended in DOC-02 | ADR-0041 Decision §3.1 Windows sources (77-122); ADR-0041 Decision §3.2 Linux source (123-159); ADR-0041 Decision §5 JSON example (179-231); ADR-0041 Decision §8 aggregation and buckets (358-385) | Existing guide of the tool or area; the detail is consistent with the rule it implements. |
| `docs/security/threat-model.md` | Pre-existing (no change) | ADR-0043 Decision §19 documentation obligations (869-876) | Existing guide of the tool or area; the detail is consistent with the rule it implements. |
| `docs/agents/delegation-policy.md` | Extended in DOC-02 | ADR-0044 Decision §7 readiness matrices (382-403); ADR-0044 Decision §12 CLI contract (713-725) | Existing guide of the tool or area; the detail is consistent with the rule it implements. |
| `docs/windows-dump-analysis.md` | Extended in DOC-02; stale section removed in the GATE-02 corrections | ADR-0048 Decision §10 timeout conflict (161-171) — historical, superseded by `ToolManifest.RequestedExecutionTimeout` (rule C02-R10); the guide's 'Time' section is the operator guidance | Existing guide of the tool or area; the superseded wording is no longer reproduced. |
| `docs/knowledge/local-agent-pilot.md` | Extended in DOC-02 and in the GATE-02 corrections (U1–U10 and §U.2 section) | ADR-0057 U.1 admission constraints (545-564), U.2 marker neutralization (566-570), U.4 limits (582-587), U.5 globalization runtime (589-612) | Pilot guide for Knowledge; globalization is a deployment requirement of the pilot. |
| `agentic/04-testing-rules.md` | Pre-existing (no change) | agentic/06-decisions.md D-010 TDD on the core (163-173) | Existing guide of the tool or area; the detail is consistent with the rule it implements. |
| `agentic/00-project-spec.md` | Pre-existing (no change) | agentic/00-project-spec.md What bOps is (3-9); agentic/00-project-spec.md Roadmap and scope discipline (69-111); agentic/00-project-spec.md Out of scope (112-123) | Existing guide of the tool or area; the detail is consistent with the rule it implements. |

## B. Mechanical check of rule text against the source ADR rows

For every `CONTRACT` row of an ADR, identifiers (code spans, CamelCase names, dotted names) and measured values (numbers with units) were extracted from the ADR lines of the row and searched in the text of the mapped rules (case-insensitive). The tokens missing from the rules were then searched in the repository (source, tests, docs outside the ADRs and the new contracts, web UI source, governance files); tokens present there are implementation detail that the code or a guide holds. The remainder after a word-level re-check is listed in part C.

- CONTRACT rows with at least one token: 502; tokens: 6892; not in the mapped rules: 3630 (mostly class, file and member names that are verifiable in code); absent from the rules and from the repository: 361; remaining after the word-level re-check: 77 (part C).

## C. Residual tokens (77) and their disposition

| Row | Rules | Token | Disposition |
|---|---|---|---|
| A0006-004 | C02-R01, C02-R03 | `IsMacOS` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0006-004 | C02-R01, C02-R03 | `OperatingSystem.IsMacOS` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0006-004 | C02-R01, C02-R03 | `if (OperatingSystem.IsMacOS` | Notation, example or code expression quoted in the ADR; not a rule. |
| A0014-015 | C04-R16 | `16 characters` | False positive: part of "UTF-16 characters". |
| A0016-007 | C02-R09 | `VerificationOutcomeAuditEvent` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0020-003 | C02-R26 | `PluginValidator` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0020-009 | C02-R04 | `AppDomain` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0021-006 | C02-R19 | `Tmds.DBus` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0021-006 | C02-R19 | `org.freedesktop.systemd1` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0024-009 | C02-R21 | `SkillRunner` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0025-009 | C03-R23 | `ExecuteSkillAsync` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0029-004 | C02-R13 | `MaskedLength` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0029-008 | C02-R13 | `IConfigurationSource` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0029-010 | C02-R13 | `MaskedLength` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0032-017 | C02-R06, C04-R07 | `[now − N × 1440 minutes, now]` | Notation, example or code expression quoted in the ADR; not a rule. |
| A0032-019 | C04-R04 | `10 seconds` | Gap found: the per-read 10-second cap inside the shared 20-second budget was in no guide; added to `docs/system-events.md` (technical specification). Code: `WindowsCrashEvidenceTool.cs:32` (`EventLogReadCap`). |
| A0032-021 | C04-R07 | `10 seconds` | Gap found: the per-read 10-second cap inside the shared 20-second budget was in no guide; added to `docs/system-events.md` (technical specification). Code: `WindowsCrashEvidenceTool.cs:32` (`EventLogReadCap`). |
| A0032-028 | C04-R08 | `1.1 KB` | Historical sizing remark in ADR-0032. |
| A0032-030 | C04-R08 | `1,060 bytes` | Historical estimate in ADR-0032; the byte budget is in `docs/system-events.md`/`docs/system-stability.md`. |
| A0034-012 | C02-R19 | `CreateToolhelp32Snapshot` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0035-009 | C02-R20 | `LI == 3` | Notation, example or code expression quoted in the ADR; not a rule. |
| A0035-014 | C02-R19 | `DnsClient` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0035-014 | C02-R19 | `DnsClient.NET` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0038-003 | C01-R10 | `fs_2Edelete_5Ftree` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0039-008 | C01-R14 | `32 characters` | Present in code (`StabilityEvidence.CodeCharacters = 32`, `src/packages/bOps.Packages.System.Core/StabilityEvidence.cs:93`); other 32-character figures are secret-shape thresholds in the sanitizer. |
| A0039-008 | C01-R14 | `sk-…` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0040-009 | C03-R13 | `TryTransitionAsync(state, Running, itsAttempt` | Notation, example or code expression quoted in the ADR; not a rule. |
| A0040-020 | C03-R11, C03-R12, C03-R13 | `json_extract` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0041-004 | C04-R10 | `[now − windowDays × 1440 minutes, now]` | Notation, example or code expression quoted in the ADR; not a rule. |
| A0041-009 | C04-R10 | `32 characters` | Present in code (`StabilityEvidence.CodeCharacters = 32`, `src/packages/bOps.Packages.System.Core/StabilityEvidence.cs:93`); other 32-character figures are secret-shape thresholds in the sanitizer. |
| A0041-020 | C04-R04 | `0.14 s` | Historical performance measurement in ADR-0041. |
| A0042-001 | C04-R01 | `02e3e19` | Commit hash (provenance). |
| A0042-009 | C04-R11 | `j > i` | Notation, example or code expression quoted in the ADR; not a rule. |
| A0042-015 | C04-R11 | `180 days × events × relations` | Complexity remark, not a rule. |
| A0042-017 | C04-R12 | `bops.evidence_disclosure_reask` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0042-017 | C04-R12 | `result_not_used` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0042-024 | C04-R13 | `16 characters` | False positive: part of "UTF-16 characters". |
| A0042-024 | C04-R13 | `bops.evidence_disclosure_reask` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0042-030 | C04-R14 | `7,168 characters` | Present as "7,168 UTF-16 code units" in C04-R14; equals 12 × 512 + 1,024 (`EvidenceGrounding.MaxCharacters`, `src/core/bOps.Runtime/EvidenceGrounding.cs:97`). |
| A0042-032 | C04-R14 | `Agent:EvidenceGroundingChecks` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0042-032 | C04-R14 | `EvidenceGroundingChecks` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0042-038 | C04-R14 | `step 7: C1 C2` | Notation, example or code expression quoted in the ADR; not a rule. |
| A0042-038 | C04-R14 | `step 8: B1 B2 B3` | Notation, example or code expression quoted in the ADR; not a rule. |
| A0042-038 | C04-R14 | `step 9: A1 A2 A3 A4` | Notation, example or code expression quoted in the ADR; not a rule. |
| A0043-001 | C05-R04 | `f347ab4` | Commit hash (provenance). |
| A0043-003 | C05-R05 | `changeOrigin` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0043-003 | C05-R05 | `changeOrigin: true` | Notation, example or code expression quoted in the ADR; not a rule. |
| A0043-007 | C05-R04 | `E.resolvedSecret` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0043-009 | C05-R04 | `IdleTimeout − 60 s` | Notation, example or code expression quoted in the ADR; not a rule. |
| A0043-011 | C05-R04 | `HandleForbiddenAsync` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0043-018 | C05-R06 | `401s` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0043-019 | C05-R06 | `queryParamsHandling: 'merge'` | Notation, example or code expression quoted in the ADR; not a rule. |
| A0043-024 | C05-R04 | `AddCookie` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0043-024 | C05-R04 | `ITicketStore` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0044-007 | C02-R25 | `P ∋ p_r` | Notation, example or code expression quoted in the ADR; not a rule. |
| A0044-007 | C02-R25 | `t₂ > t₁` | Notation, example or code expression quoted in the ADR; not a rule. |
| A0044-020 | C03-R11 | `bops resume <roleTaskId>` | Notation, example or code expression quoted in the ADR; not a rule. |
| A0045-006 | C01-R16 | `ConfiguredPrimaryProviderId` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0045-007 | C01-R17 | `IExecutionChatModelFactory` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0045-010 | C01-R18 | `ConfiguredPrimaryProviderId` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0045-012 | C01-R19 | `ConfiguredPrimaryProviderId` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0045-013 | C01-R20 | `ActualProvider` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0045-014 | C01-R18 | `ConfiguredPrimaryProviderId` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0046-004 | C03-R05 | `AuthorizedToolView` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0048-006 | C02-R20 | `ntsd.ini` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0049-007 | C05-R10 | `user:pass` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0049-010 | C05-R10 | `600 characters` | Present in code: `PrerequisiteTransitionRecorder.MaxRemediationTextLength = 600` (`src/core/bOps.Runtime/PrerequisiteTransitionRecorder.cs:21`); the system-message rule C05-R10 states the bound. |
| A0051-006 | C03-R15 | `Risk == Read` | Notation, example or code expression quoted in the ADR; not a rule. |
| A0060-001 | C03-R24 | `ups.md` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0060-007 | C03-R24 | `1,052 characters` | Historical measurement of the catalog line (744 → 1,052); the enforced bound is the 320-character cap and the 1…330 test (C03-R24). |
| A0060-007 | C03-R24 | `330 characters` | Test bound for the measured growth (ADR-0060 §10); not a runtime rule. |
| A0061-008 | C04-R08 | `3,058 bytes` | Present in `docs/system-events.md` as `3,058 B` (form differs). |
| A0061-008 | C04-R08 | `LogLinks` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0061-008 | C04-R08 | `ProviderMetadata` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0061-008 | C04-R08 | `ProviderMetadata.LogLinks` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |
| A0061-011 | C04-R08 | `203 bytes` | Measured size of a constant (`SourceFilterEvidence.Semantics`); the constant, not its size, is the rule. |
| A0061-018 | C04-R08 | `LogLinks` | Name used in the ADR that does not occur in the repository code, docs or tests: renamed, planned, rejected-alternative or example (REVIEW-04). The contracts state the behaviour without this name. |

## D. Gaps found and closed in this phase

- `10 seconds` per-read cap of the Windows Event Log inside the 20-second shared budget (ADR-0032 HARDEN-7): added to `docs/system-events.md`.
- Everything else in part A was identified by a per-ADR token check against the destination guides (`techchk`) before the sections were written; the sections listed there are the result.
