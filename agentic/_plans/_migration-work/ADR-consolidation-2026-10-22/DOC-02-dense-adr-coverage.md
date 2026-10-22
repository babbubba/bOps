# DOC-02 — Dense ADR coverage (section by section)

Non-normative working artifact of the DOC-02 phase of `agentic/_plans/ADR-consolidation-2026-10-22.md`. It is migration evidence, not current documentation, not a contract, and must not be loaded by the bootstrap.

For each dense ADR of plan §7.2.3, every heading (levels 1–4, bold sub-headings used as headings are covered by their enclosing row) with the traceability-matrix row that covers its line, the disposition and the destination. Generated mechanically from the matrix and the ADR files; the `technical detail seat` is the rule (contract) or the technical reference that carries the necessary detail.

## ADR-0042 — evidence-reasoning-and-limitation-disclosure (1254 lines)

| Line | Heading | Row | Disposition | Destination / seat |
|---:|---|---|---|---|
| 1 | ADR-0042 — Evidence reasoning and limitation disclosure | A0042-001 | CONTRACT | C04-R01 |
| 32 | Context | A0042-002 | HISTORICAL | historical-only |
| 39 | What HARDEN-7 left in place (verified at `46fbf47`) | A0042-003 | HISTORICAL | historical-only |
| 69 | What "evidence reasoning" means in bOps | A0042-004 | CONTRACT | C04-R01 |
| 77 | Decision | A0042-005 | CONTRACT | C04-R01 |
| 79 | 1. Placement | A0042-005 | CONTRACT | C04-R01 |
| 95 | 2. Evidence model | A0042-006 | CONTRACT | C04-R01 |
| 111 | 3. Deterministic code vs model | A0042-007 | CONTRACT | C04-R01 |
| 134 | 4. The evidence rule (system prompt) | A0042-008 | CONTRACT | C04-R02 |
| 182 | 5. Evidence-limitations digest | A0042-009 | CONTRACT | C04-R11 |
| 258 | 6. Final-answer disclosure | A0042-010 | CONTRACT | C04-R12 |
| 314 | 7. Coverage, completeness and negative evidence | A0042-012 | CONTRACT | C04-R03 |
| 339 | 8. Timestamp semantics | A0042-013 | CONTRACT | C04-R04 |
| 376 | 9. Correlation vs causation | A0042-014 | CONTRACT | C04-R01 |
| 392 | 10. Aggregation and acquisition | A0042-015 | CONTRACT | C04-R11 |
| 400 | 11. Bounds | A0042-015 | CONTRACT | C04-R11 |
| 412 | 12. Security and privacy (rule S5) | A0042-016 | CONTRACT | C04-R11 |
| 435 | 13. Audit, persisted marker and telemetry | A0042-017 | CONTRACT | C04-R12 |
| 467 | 14. Public contract and compatibility | A0042-018 | CONTRACT | C04-R11 |
| 484 | 15. Windows and Linux | A0042-018 | CONTRACT | C04-R11 |
| 493 | 16. Boundaries with HARDEN-7 residuals and HARDEN-8 | A0042-018 | CONTRACT | C04-R11 |
| 515 | 17. Test obligations | A0042-019 | HISTORICAL | historical-only |
| 559 | Alternatives considered | A0042-020 | CONTRACT | C04-R01 |
| 621 | Consequences | A0042-021 | HISTORICAL | historical-only |
| 636 | Independent review corrections (2026-10-03) | A0042-022 | HISTORICAL | historical-only |
| 659 | Operator acceptance (2026-10-03) | A0042-022 | HISTORICAL | historical-only |
| 677 | HARDEN-9 implementation-review amendment — delegated Diagnostic structured output (Accepte | A0042-023 | CONTRACT | C04-R13 |
| 685 | Problem | A0042-023 | CONTRACT | C04-R13 |
| 702 | Scope | A0042-023 | CONTRACT | C04-R13 |
| 711 | Chosen rule | A0042-024 | CONTRACT | C04-R13 |
| 769 | Parent responsibility — and the gap it exposes | A0042-025 | CONTRACT | C04-R13 |
| 790 | Rejected alternatives | A0042-026 | HISTORICAL | historical-only |
| 808 | Test requirements | A0042-026 | HISTORICAL | historical-only |
| 824 | Implementation obligations after acceptance | A0042-026 | HISTORICAL | historical-only |
| 837 | Compatibility impact | A0042-026 | HISTORICAL | historical-only |
| 848 | Decision record | A0042-026 | HISTORICAL | historical-only |
| 855 | Implementation-review non-blocking findings (follow-up only, not part of this amendment) | A0042-026 | HISTORICAL | historical-only |
| 864 | Amendment — terminal fallback after a failed correction (PRE-3B2) | A0042-027 | CONTRACT | C04-R15 |
| 872 | Amendment — evidence-grounded final synthesis (PRE-5) | A0042-028 | CONTRACT | C04-R14 |
| 879 | Problem | A0042-028 | CONTRACT | C04-R14 |
| 887 | What still holds | A0042-028 | CONTRACT | C04-R14 |
| 894 | 1. Grounding facts (what may ground an answer) | A0042-029 | CONTRACT | C04-R14 |
| 912 | 2. Epistemic classes in the final synthesis | A0042-029 | CONTRACT | C04-R14 |
| 920 | 3. The grounding ledger and the `EvidenceGrounding/v1` block | A0042-030 | CONTRACT | C04-R14 |
| 966 | 4. What is enforceable — and what is not | A0042-031 | CONTRACT | C04-R14 |
| 997 | 5. The check (`EvidenceGroundingCheck/v1`) | A0042-032 | CONTRACT | C04-R14 |
| 1029 | 6. The correction (`EvidenceGroundingCorrection/v1`) | A0042-033 | CONTRACT | C04-R14 |
| 1042 | 7. Outcomes and failure semantics | A0042-034 | CONTRACT | C04-R14 |
| 1065 | 8. One pipeline, one hard bound | A0042-035 | CONTRACT | C04-R15 |
| 1088 | 9. Persisted markers and audit | A0042-035 | CONTRACT | C04-R15 |
| 1101 | 10. Package side: facts are the package's projection | A0042-036 | CONTRACT | C04-R14 |
| 1123 | 11. Tests (rows 43–62) | A0042-037 | HISTORICAL | historical-only |
| 1148 | Amendment — step-diverse grounding selection (AF-05G) | A0042-038 | CONTRACT | C04-R14 |
| 1153 | Problem | A0042-038 | CONTRACT | C04-R14 |
| 1164 | Rule | A0042-038 | CONTRACT | C04-R14 |
| 1184 | What does not change | A0042-038 | CONTRACT | C04-R14 |
| 1190 | Version decision | A0042-038 | CONTRACT | C04-R14 |
| 1198 | Tests | A0042-038 | CONTRACT | C04-R14 |
| 1209 | Amendment — deterministic evidence disclosure (AF-05H) | A0042-039 | CONTRACT | C04-R12 |
| 1214 | Problem | A0042-039 | CONTRACT | C04-R12 |
| 1221 | Rule | A0042-039 | CONTRACT | C04-R12 |
| 1244 | Residual gap (not solved here) | A0042-039 | CONTRACT | C04-R12 |
| 1250 | Tests | A0042-039 | CONTRACT | C04-R12 |

## ADR-0044 — request-dependent-delegation-authority-and-operability (1171 lines)

| Line | Heading | Row | Disposition | Destination / seat |
|---:|---|---|---|---|
| 1 | ADR-0044 — Request-dependent delegation authority and operability | A0044-001 | CONTRACT | C02-R25 |
| 26 | Context | A0044-002 | HISTORICAL | historical-only |
| 118 | Decision | A0044-003 | CONTRACT | C02-R25 |
| 120 | 1. The required role set depends on the request | A0044-003 | CONTRACT | C02-R25 |
| 157 | 2. The root envelope is derived only from the required roles | A0044-004 | CONTRACT | C02-R25 |
| 184 | 3. Why diagnosis-only remains read-only (the primary security invariant) | A0044-005 | CONTRACT | C02-R25 |
| 212 | 4. Absent and malformed profiles; the policy file stays strictly fail-closed | A0044-006 | CONTRACT | C02-R25 |
| 239 | 5. Readiness is a runtime function, never a second policy implementation | A0044-007 | CONTRACT | C02-R25 |
| 306 | 6. `GET /api/delegations/readiness` | A0044-008 | CONTRACT | C05-R07 |
| 308 | 6.1 Request | A0044-008 | CONTRACT | C05-R07 |
| 316 | 6.2 Response `200` | A0044-008 | CONTRACT | C05-R07 |
| 376 | 6.3 Errors | A0044-008 | CONTRACT | C05-R07 |
| 382 | 7. Readiness matrices (deterministic examples) | A0044-009 | TECH-REF | docs/agents/delegation-policy.md |
| 405 | 8. `GET /api/skills` — the activated catalog | A0044-010 | CONTRACT | C05-R07 |
| 451 | 9. Capability input: one schema, validated on the server | A0044-011 | CONTRACT | C02-R21 |
| 453 | 9.1 Schema-driven controls | A0044-011 | CONTRACT | C02-R21 |
| 473 | 9.2 Server validation is authoritative | A0044-011 | CONTRACT | C02-R21 |
| 515 | 9.3 Raw JSON fallback | A0044-013 | CONTRACT | C05-R07 |
| 536 | 9.4 Selection from the catalog | A0044-014 | CONTRACT | C05-R07 |
| 547 | 10. `bops delegate profiles init --read-only` | A0044-015 | CONTRACT | C05-R07 |
| 549 | 10.1 Command and behaviour | A0044-015 | CONTRACT | C05-R07 |
| 599 | 10.2 Generated content (finite, deterministic) | A0044-016 | CONTRACT | C05-R07 |
| 685 | 11. `bops delegate profiles check` and the drift count | A0044-017 | CONTRACT | C05-R07 |
| 713 | 12. CLI contract | A0044-018 | TECH-REF | docs/agents/delegation-policy.md |
| 727 | 13. UI readiness behaviour | A0044-019 | CONTRACT | C05-R07 |
| 760 | 14. The role-task resume guard is preserved | A0044-020 | CONTRACT | C03-R11 |
| 770 | 15. ADR-0018 amendment: API surface and authorization | A0044-021 | CONTRACT | C05-R07 |
| 782 | 16. Typed evidence-limitation metadata (closes the ADR-0042 HARDEN-11 gap) | A0044-022 | CONTRACT | C04-R13 |
| 877 | 17. Diagnostic JSON: strict, bounded, contained | A0044-023 | CONTRACT | C04-R13 |
| 915 | 18. Public contract and versioning | A0044-024 | CONTRACT | C05-R11 |
| 931 | 19. Security matrix | A0044-025 | CONTRACT | C04-R13 |
| 967 | 20. Required tests (written before the code they govern; the core is test-first) | A0044-026 | HISTORICAL | historical-only |
| 1028 | 21. End-to-end acceptance (packet scenarios preserved) | A0044-026 | HISTORICAL | historical-only |
| 1047 | 22. Platform evidence gate (Phase 1, not Phase 0) | A0044-026 | HISTORICAL | historical-only |
| 1057 | 23. Documentation impact (Phase 1) | A0044-027 | CONTRACT | C02-R25 |
| 1073 | 24. Out of scope | A0044-027 | CONTRACT | C02-R25 |
| 1080 | Alternatives considered | A0044-028 | CONTRACT | C04-R13,C05-R07 |
| 1105 | Consequences | A0044-029 | HISTORICAL | historical-only |
| 1119 | Open questions for the reviewer and the operator — resolved 2026-10-05 | A0044-030 | CONTRACT | C02-R21 |
| 1131 | Independent review and operator acceptance (2026-10-05) | A0044-030 | CONTRACT | C02-R21 |

## ADR-0057 — knowledge-discovery-index (950 lines)

| Line | Heading | Row | Disposition | Destination / seat |
|---:|---|---|---|---|
| 1 | ADR-0057 — Knowledge Discovery: indice deterministico pubblicato dal runtime (KNOW-ADR-01) | A0057-001 | PENDING | C05#pending-D5 |
| 25 | 0. Revisione KNOW-ADR-01-HARDEN — decisioni confermate e cambiate | A0057-003 | HISTORICAL | historical-only |
| 47 | A. ADR | A0057-004 | HISTORICAL | historical-only |
| 49 | A.1 Contesto | A0057-004 | HISTORICAL | historical-only |
| 91 | A.2 Decisione (sintesi) | A0057-005 | PENDING | C05#pending-D5 |
| 109 | A.3 Alternative considerate | A0057-006 | HISTORICAL | historical-only |
| 127 | A.4 Motivazioni | A0057-007 | HISTORICAL | historical-only |
| 138 | A.5 Conseguenze | A0057-008 | PENDING | C05#pending-D5 |
| 149 | A.6 Quando si calcola e cosa contiene ogni richiesta | A0057-009 | PENDING | C05#pending-D5 |
| 174 | A.7 `knowledgeRefs` nel piano | A0057-010 | PENDING | C05#pending-D5 |
| 180 | A.8 Compatibilità futura (non implementata in M1) | A0057-011 | HISTORICAL | historical-only |
| 195 | B. Percorso runtime → discovery → MAF → provider → read | A0057-012 | PENDING | C05#pending-D5 |
| 225 | B.4 Budget e limiti dell'indice | A0057-012 | PENDING | C05#pending-D5 |
| 241 | C. Contratto dati | A0057-013 | PENDING | C05#pending-D5 |
| 243 | C.1 Tipi in `bOps.Abstractions` (additivi; servono all'audit) | A0057-013 | PENDING | C05#pending-D5 |
| 281 | C.2 Tipi in `bOps.Runtime/KnowledgeRead.cs` (additivi; nessun tipo Knowledge o MAF) | A0057-013 | PENDING | C05#pending-D5 |
| 315 | C.3 Argomenti di `runtime.knowledge_read` (set chiuso esteso) | A0057-014 | PENDING | C05#pending-D5 |
| 328 | C.4 Parte D — testo del turno `User` | A0057-015 | PENDING | C05#pending-D5 |
| 350 | C.5 Parte C — coda del `SystemPrompt` | A0057-016 | PENDING | C05#pending-D5 |
| 373 | C.6 Manifest v2 (M2) | A0057-017 | HISTORICAL | historical-only |
| 378 | C.7 Codici e comportamento tipizzato | A0057-018 | PENDING | C05#pending-D5 |
| 397 | D. Trust boundary del catalogo | A0057-019 | PENDING | C05#pending-D5 |
| 399 | D.1 Autorità per sorgente | A0057-019 | PENDING | C05#pending-D5 |
| 409 | D.2 Perché `User` (H1) | A0057-020 | HISTORICAL | historical-only |
| 428 | D.3 Threat model | A0057-021 | HISTORICAL | historical-only |
| 441 | D.4 Invarianti che rendono `User` accettabile (verificati da test e mutation check §H) | A0057-022 | PENDING | C05#pending-D5 |
| 450 | D.5 Limitazioni dichiarate | A0057-023 | PENDING | C05#pending-D5 |
| 459 | R. Ref, revoche e isolamento | A0057-024 | PENDING | C05#pending-D5 |
| 461 | R.1 Proprietà dei ref | A0057-024 | PENDING | C05#pending-D5 |
| 472 | R.2 Revoca: che cosa è dimostrato | A0057-025 | PENDING | C05#pending-D5 |
| 487 | R.4 Requisito di estensibilità: authorization epoch/revision (non implementato) | A0057-026 | PENDING | C05#pending-D5 |
| 496 | R.5 Lettura per ref: autorità e integrità al momento dell'accesso | A0057-027 | PENDING | C05#pending-D5 |
| 505 | R.3 Resume, cache e stato persistito | A0057-028 | PENDING | C05#pending-D5 |
| 519 | S. Completezza della discovery | A0057-029 | PENDING | C05#pending-D5 |
| 543 | U. Validazione di nomi, descrizioni e identificatori | A0057-030 | PENDING | C05#pending-D5 |
| 545 | U.1 Vincoli di ammissione (deterministici, rifiuto e mai riscrittura) | A0057-030 | PENDING | C05#pending-D5 |
| 566 | U.2 Neutralizzazione dei marker (altrove) | A0057-031 | PENDING | C05#pending-D5 |
| 572 | U.3 Validazione ≠ istruzione al modello | A0057-032 | HISTORICAL | historical-only |
| 582 | U.4 Limiti accettati | A0057-033 | PENDING | C05#pending-D5 |
| 589 | U.5 Runtime di globalizzazione (nota di implementazione, KNOW-M1-B-FIX01) | A0057-034 | TECH-REF | docs/knowledge/local-agent-pilot.md |
| 615 | E. Piano di implementazione | A0057-035 | HISTORICAL | historical-only |
| 619 | M0 — Governance (documentale, operatore) | A0057-035 | HISTORICAL | historical-only |
| 647 | M1 — KNOW-DISC-01: indice L0 sul formato attuale | A0057-037 | HISTORICAL | historical-only |
| 651 | T1 — Abstractions e audit | A0057-037 | HISTORICAL | historical-only |
| 664 | T2 — Contratto runtime | A0057-037 | HISTORICAL | historical-only |
| 677 | T3 — Politica dei testi del publisher | A0057-037 | HISTORICAL | historical-only |
| 690 | T4 — Selettore e renderer | A0057-037 | HISTORICAL | historical-only |
| 703 | T5 — Bridge | A0057-037 | HISTORICAL | historical-only |
| 716 | T6 — Runner | A0057-037 | HISTORICAL | historical-only |
| 729 | T7 — Export osservabilità (solo test/benchmark) | A0057-037 | HISTORICAL | historical-only |
| 740 | T8 — Test d'insieme e report | A0057-037 | HISTORICAL | historical-only |
| 767 | M2 — KNOW-DISC-02: applicabilità firmata (manifest v2) | A0057-038 | HISTORICAL | historical-only |
| 773 | M3 — KNOW-DISC-03: misura | A0057-038 | HISTORICAL | historical-only |
| 777 | M4 — condizionale: `knowledgeRefs` informativi (§A.7) | A0057-038 | HISTORICAL | historical-only |
| 781 | F. Rischi | A0057-039 | HISTORICAL | historical-only |
| 807 | G. Governance | A0057-040 | CONTRACT | C02-R12 |
| 809 | G.1 Emendamento S5 (testo da inserire in `agentic/03-security-rules.md`, dopo il punto "Ty | A0057-040 | CONTRACT | C02-R12 |
| 835 | G.2 Contraddizioni con ADR-0055 e ADR-0056 e risoluzione | A0057-041 | PENDING | C05#pending-D5 |
| 849 | G.3 Altri documenti | A0057-042 | HISTORICAL | historical-only |
| 864 | H. Test discriminanti | A0057-043 | HISTORICAL | historical-only |
| 866 | H.1 Offline (M1) | A0057-043 | HISTORICAL | historical-only |
| 885 | H.2 Esecuzione | A0057-043 | HISTORICAL | historical-only |
| 891 | H.3 Mutation check (ognuno deve far fallire almeno un test) | A0057-043 | HISTORICAL | historical-only |
| 899 | H.4 Condizioni per un test live successivo (non eseguito) | A0057-043 | HISTORICAL | historical-only |
| 913 | I. Fuori da M1 | A0057-044 | HISTORICAL | historical-only |
| 932 | L. Blocchi e ipotesi | A0057-045 | PENDING | C05#pending-D5 |
| 934 | L.1 Blocchi | A0057-045 | PENDING | C05#pending-D5 |
| 942 | L.2 Ipotesi | A0057-045 | PENDING | C05#pending-D5 |

## ADR-0043 — browser-web-session (941 lines)

| Line | Heading | Row | Disposition | Destination / seat |
|---:|---|---|---|---|
| 1 | ADR-0043 — Browser web session: a bounded, revocable server-side session behind a hardened | A0043-001 | CONTRACT | C05-R04 |
| 18 | Context | A0043-002 | HISTORICAL | historical-only |
| 56 | Supported development topology — verified, not assumed | A0043-003 | CONTRACT | C05-R05 |
| 88 | Decision | A0043-004 | CONTRACT | C05-R04 |
| 90 | 1. Trust boundary | A0043-004 | CONTRACT | C05-R04 |
| 98 | 2. One credential authority | A0043-005 | CONTRACT | C05-R03 |
| 120 | 3. Session token and digest | A0043-006 | CONTRACT | C05-R04 |
| 142 | 4. Credential binding (key-version fingerprint) and roles | A0043-007 | CONTRACT | C05-R04 |
| 222 | 5. Persisted record and store | A0043-008 | CONTRACT | C05-R04 |
| 224 | 5.1 Placement | A0043-008 | CONTRACT | C05-R04 |
| 241 | 5.2 File and schema | A0043-008 | CONTRACT | C05-R04 |
| 267 | 5.3 Versioning and migration | A0043-008 | CONTRACT | C05-R04 |
| 276 | 5.4 Concurrency (fail closed) | A0043-008 | CONTRACT | C05-R04 |
| 292 | 5.5 No caching | A0043-008 | CONTRACT | C05-R04 |
| 296 | 5.6 Cleanup (bounded) | A0043-008 | CONTRACT | C05-R04 |
| 307 | 6. Lifetime | A0043-009 | CONTRACT | C05-R04 |
| 309 | 6.1 Defaults (configurable, §12) | A0043-009 | CONTRACT | C05-R04 |
| 313 | 6.2 Exact rules | A0043-009 | CONTRACT | C05-R04 |
| 329 | 6.3 Idle sliding — one rule | A0043-009 | CONTRACT | C05-R04 |
| 353 | 7. Cookie | A0043-010 | CONTRACT | C05-R04 |
| 383 | 8. Authentication composition | A0043-011 | CONTRACT | C05-R04 |
| 439 | 9. CSRF gate | A0043-012 | CONTRACT | C05-R05 |
| 476 | 10. Endpoints | A0043-012 | CONTRACT | C05-R05 |
| 481 | 10.1 `POST /api/session` — login | A0043-013 | CONTRACT | C05-R05 |
| 515 | 10.2 `GET /api/session/me` | A0043-014 | CONTRACT | C05-R05 |
| 523 | 10.3 `DELETE /api/session` — logout | A0043-014 | CONTRACT | C05-R05 |
| 536 | 11. Login rate limiting | A0043-015 | CONTRACT | C05-R05 |
| 555 | 12. Configuration | A0043-016 | TECH-REF | docs/operator-configuration.md |
| 572 | 13. Negative-security contract (secret leakage) | A0043-017 | CONTRACT | C05-R06 |
| 596 | 14. Angular | A0043-018 | CONTRACT | C05-R06 |
| 598 | 14.1 State machine (`AuthService`) | A0043-018 | CONTRACT | C05-R06 |
| 620 | 14.2 Boot without races | A0043-018 | CONTRACT | C05-R06 |
| 630 | 14.3 Login | A0043-018 | CONTRACT | C05-R06 |
| 645 | 14.4 Interceptor | A0043-018 | CONTRACT | C05-R06 |
| 657 | 14.5 Logout | A0043-018 | CONTRACT | C05-R06 |
| 662 | 14.6 One session-expired flow (with HARDEN-4) | A0043-018 | CONTRACT | C05-R06 |
| 677 | 15. Refresh and running-task watch restoration | A0043-019 | CONTRACT | C05-R06 |
| 700 | 16. Security matrix | A0043-020 | CONTRACT | C05-R04 |
| 733 | 17. Authentication / CSRF matrix (normative) | A0043-021 | CONTRACT | C05-R05 |
| 774 | 18. Required tests (implementation) | A0043-022 | HISTORICAL | historical-only |
| 828 | 18.1 E2E-8 (Playwright, executable) | A0043-022 | HISTORICAL | historical-only |
| 861 | 18.2 Manual browser evidence | A0043-022 | HISTORICAL | historical-only |
| 869 | 19. Documentation the implementation must update | A0043-023 | TECH-REF | docs/security/threat-model.md |
| 878 | Alternatives considered | A0043-024 | CONTRACT | C05-R04 |
| 909 | Consequences | A0043-025 | CONTRACT | C05-R04 |
| 923 | Residual risks | A0043-025 | CONTRACT | C05-R04 |
| 931 | Operator acceptance (2026-10-05) | A0043-026 | HISTORICAL | historical-only |

## ADR-0051 — durable-ordinary-task-mutation-intent-journal (807 lines)

| Line | Heading | Row | Disposition | Destination / seat |
|---:|---|---|---|---|
| 1 | ADR-0051 — Durable mutation intent journal for ordinary tasks, fenced orphan recovery and  | A0051-001 | CONTRACT | C03-R15 |
| 24 | 1. Context | A0051-002 | HISTORICAL | historical-only |
| 26 | 1.1 The gap, as it is in the code at `9707803` | A0051-002 | HISTORICAL | historical-only |
| 68 | 1.2 Why ADR-0040 cannot simply be relaxed | A0051-003 | CONTRACT | C03-R19 |
| 77 | 1.3 What cannot be promised | A0051-004 | CONTRACT | C03-R15 |
| 88 | 2. Decision summary | A0051-005 | CONTRACT | C03-R15 |
| 113 | 3. D1 — What is journaled, and the exact boundary | A0051-006 | CONTRACT | C03-R15 |
| 115 | 3.1 Scope | A0051-006 | CONTRACT | C03-R15 |
| 127 | 3.2 Gate-by-gate (`ExecuteStepAsync` order) | A0051-007 | CONTRACT | C03-R15 |
| 152 | 4. D4 — Outcomes and the three kinds of knowledge | A0051-008 | CONTRACT | C03-R16 |
| 154 | 4.1 Outcome kinds (new `MutationOutcomeKind`; explicit persisted values) | A0051-008 | CONTRACT | C03-R16 |
| 175 | 4.2 Role of `VerificationSpec` / `VerificationStatus` | A0051-009 | CONTRACT | C03-R16 |
| 185 | 4.3 Journal state (new `TaskMutationState`; persisted, runtime-computed, explicit values) | A0051-010 | CONTRACT | C03-R16 |
| 199 | 5. D2, D3 — Contents and identity | A0051-011 | CONTRACT | C03-R16 |
| 201 | 5.1 Identity | A0051-011 | CONTRACT | C03-R16 |
| 216 | 5.2 Intent contents (minimal) | A0051-012 | CONTRACT | C03-R16 |
| 237 | 5.3 Outcome contents | A0051-013 | CONTRACT | C03-R16 |
| 242 | 5.4 Reconciliation contents | A0051-013 | CONTRACT | C03-R16 |
| 249 | 6. D6, D13, D15 — Storage, atomicity and fencing | A0051-014 | CONTRACT | C03-R17 |
| 251 | 6.1 Storage model: **B, a separate SQLite table in `tasks.db`** (chosen) | A0051-014 | CONTRACT | C03-R17 |
| 269 | 6.2 Schema (additive, created in `SqliteTaskStore.EnsureSchema`) | A0051-015 | TECH-REF | docs/architecture/reference/task-store-and-mutation-journal.md |
| 302 | 6.3 Writes (each a single statement or one `BEGIN IMMEDIATE` transaction; never load/check | A0051-016 | CONTRACT | C03-R17 |
| 316 | 6.4 Fencing and the late writer (D15, P7) | A0051-017 | CONTRACT | C03-R17 |
| 336 | 7. D5, D21 — Crash matrix and in-process behaviour | A0051-018 | CONTRACT | C03-R18 |
| 338 | 7.1 The pipeline after F-25 (ordinary, non-`Read`) | A0051-018 | CONTRACT | C03-R18 |
| 350 | 7.2 Crash windows | A0051-018 | CONTRACT | C03-R18 |
| 370 | 7.3 In-process outcomes (no crash) | A0051-019 | CONTRACT | C03-R18 |
| 387 | 8. D8, D9 — Representing ambiguity, and the orphaned `Running` task | A0051-020 | CONTRACT | C03-R18 |
| 389 | 8.1 Representation: existing status + typed terminal reason + journal projection (Options  | A0051-020 | CONTRACT | C03-R18 |
| 404 | 8.2 Orphaned `Running`: alternatives | A0051-021 | HISTORICAL | historical-only |
| 414 | 8.3 The recovery transition | A0051-022 | CONTRACT | C03-R19 |
| 451 | 9. D7, D11, D12 — Reconciliation, resume and approvals | A0051-023 | CONTRACT | C03-R20 |
| 453 | 9.1 Who and what | A0051-023 | CONTRACT | C03-R20 |
| 489 | 9.2 Ordinary tasks adopt ADR-0030's rule — with one difference | A0051-024 | CONTRACT | C03-R20 |
| 497 | 9.3 Reconciliation verification, exactly | A0051-025 | CONTRACT | C03-R20 |
| 515 | 9.4 Resume rules (amending ADR-0040 §3) | A0051-026 | CONTRACT | C03-R20 |
| 532 | 9.5 History after reconciliation (ADR-0038) | A0051-027 | CONTRACT | C03-R20 |
| 543 | 9.6 Approvals and policy after restart (D11) | A0051-028 | CONTRACT | C03-R20 |
| 556 | 10. D10 — Process startup | A0051-029 | CONTRACT | C03-R18 |
| 564 | 11. D14 — Store capability and third-party stores | A0051-030 | CONTRACT | C03-R21 |
| 594 | 12. D16, D17 — Legacy tasks and read-only work | A0051-032 | CONTRACT | C03-R21 |
| 596 | 12.1 Legacy (`MutationJournalMode = Absent`: every ordinary row written before F-25) | A0051-032 | CONTRACT | C03-R21 |
| 611 | 12.2 Read-only work (P6) | A0051-033 | CONTRACT | C03-R21 |
| 618 | 13. ADR-0040 amendments (exact) | A0051-034 | CONTRACT | C03-R20 |
| 631 | 14. D18, D19 — Audit, API, CLI, UI (minimum contract) | A0051-035 | CONTRACT | C03-R21 |
| 633 | 14.1 Audit | A0051-035 | CONTRACT | C03-R21 |
| 657 | 14.2 API | A0051-036 | CONTRACT | C05-R02 |
| 673 | 14.3 CLI | A0051-037 | CONTRACT | C05-R02 |
| 678 | 14.4 UI (spec only; F-25B) | A0051-038 | CONTRACT | C05-R02 |
| 690 | 15. D23 — Guarantees | A0051-039 | CONTRACT | C03-R21 |
| 710 | 16. Residuals, stated plainly | A0051-040 | CONTRACT | C03-R21 |
| 725 | 17. Design review against required failure modes | A0051-041 | HISTORICAL | historical-only |
| 740 | 18. Alternatives considered | A0051-042 | HISTORICAL | historical-only |
| 761 | 19. Consequences | A0051-043 | HISTORICAL | historical-only |
| 780 | 20. Decisions (accepted 2026-10-09, D-047) | A0051-044 | CONTRACT | C03-R21 |
| 800 | 21. Implementation evidence (F-25B, 2026-10-10) | A0051-045 | HISTORICAL | historical-only |

## ADR-0032 — bounded-cross-platform-system-events (764 lines)

| Line | Heading | Row | Disposition | Destination / seat |
|---:|---|---|---|---|
| 1 | ADR-0032 — Bounded cross-platform system events (`system.events`) | A0032-001 | HISTORICAL | historical-only |
| 13 | Context | A0032-002 | CONTRACT | C04-R06,C04-R17 |
| 26 | Decision | A0032-003 | CONTRACT | C02-R03 |
| 28 | One tool, one manifest, two collectors | A0032-003 | CONTRACT | C02-R03 |
| 37 | Arguments | A0032-004 | CONTRACT | C02-R06 |
| 56 | Result | A0032-006 | CONTRACT | C04-R06 |
| 95 | Windows collection | A0032-008 | CONTRACT | C04-R06 |
| 109 | Linux collection | A0032-009 | CONTRACT | C04-R06 |
| 128 | Untrusted data and privacy | A0032-010 | CONTRACT | C04-R17 |
| 137 | No new privilege | A0032-011 | CONTRACT | C02-R15 |
| 142 | Alternatives considered | A0032-012 | CONTRACT | C02-R06 |
| 159 | Consequences | A0032-013 | CONTRACT | C04-R06 |
| 172 | HARDEN-7 amendment — Accepted 2026-10-02 | A0032-014 | HISTORICAL | historical-only |
| 186 | Context | A0032-015 | HISTORICAL | historical-only |
| 205 | Decision | A0032-016 | CONTRACT | C04-R07 |
| 207 | 1. Two output modes; a default per tool | A0032-016 | CONTRACT | C04-R07 |
| 231 | 2. Horizons: a separate day-granular argument, aggregate only | A0032-017 | CONTRACT | C02-R06,C04-R07 |
| 275 | 3. `system.events` result, schema 2 | A0032-018 | TECH-REF | docs/system-events.md |
| 329 | 4. `system.crashes` result, schema 2 | A0032-019 | CONTRACT | C04-R04 |
| 408 | 5. Temporal coverage — a separate object, never a synonym for completeness | A0032-021 | CONTRACT | C04-R07 |
| 477 | 6. Completeness and the `complete` field | A0032-022 | CONTRACT | C04-R06 |
| 500 | 7. Bounds that do not change | A0032-023 | CONTRACT | C02-R07 |
| 508 | 8. Audit | A0032-023 | CONTRACT | C02-R07 |
| 517 | 9. Schema versions and compatibility | A0032-024 | TECH-REF | docs/system-events.md |
| 541 | Alternatives considered | A0032-025 | CONTRACT | C04-R04,C04-R07 |
| 583 | Consequences | A0032-026 | CONTRACT | C04-R06 |
| 597 | Independent review corrections (2026-10-02) | A0032-026 | CONTRACT | C04-R06 |
| 605 | HARDEN-9 amendment — Accepted 2026-10-03 | A0032-027 | HISTORICAL | historical-only |
| 615 | Context | A0032-027 | HISTORICAL | historical-only |
| 623 | Decision | A0032-028 | CONTRACT | C04-R08 |
| 625 | 1. Argument and grammar | A0032-028 | CONTRACT | C04-R08 |
| 657 | 2. Matching — the canonical `source`, nothing else | A0032-029 | CONTRACT | C04-R08 |
| 687 | 3. Order and counting | A0032-030 | CONTRACT | C04-R08 |
| 697 | 4. Echo | A0032-030 | CONTRACT | C04-R08 |
| 712 | 5. Completeness and coverage | A0032-030 | CONTRACT | C04-R08 |
| 718 | 6. Schema version, manifest and audit | A0032-031 | TECH-REF | docs/system-events.md |
| 732 | Alternatives considered | A0032-032 | CONTRACT | C04-R08 |
| 747 | Tests | A0032-033 | HISTORICAL | historical-only |

## ADR-0045 — execution-pinned-provider-configuration-and-fallback-chain (664 lines)

| Line | Heading | Row | Disposition | Destination / seat |
|---:|---|---|---|---|
| 1 | ADR-0045 — Execution-pinned provider configuration and ordered fallback chain | A0045-001 | CONTRACT | C01-R17 |
| 20 | Context | A0045-002 | HISTORICAL | historical-only |
| 22 | 1. Current configuration and lifetime | A0045-002 | HISTORICAL | historical-only |
| 57 | 2. Current execution and resume boundary | A0045-002 | HISTORICAL | historical-only |
| 70 | 3. Existing retry and audit contracts | A0045-002 | HISTORICAL | historical-only |
| 84 | 4. HARDEN-13 requirement | A0045-003 | CONTRACT | C01-R17 |
| 94 | Decision | A0045-004 | CONTRACT | C01-R15 |
| 96 | 5. Three states: persisted, effective and pinned | A0045-004 | CONTRACT | C01-R15 |
| 120 | 6. Consistency and publication | A0045-005 | CONTRACT | C01-R15 |
| 157 | 7. Environment precedence | A0045-006 | CONTRACT | C01-R16 |
| 212 | 8. Pinning scope and model lifetime | A0045-007 | CONTRACT | C01-R17 |
| 239 | 9. Durable non-secret pin and late-bound credentials | A0045-008 | CONTRACT | C01-R17 |
| 285 | 10. Shipped bootstrap default — operator decision recorded | A0045-009 | CONTRACT | C01-R21 |
| 312 | 11. Ordered fallback chain | A0045-010 | CONTRACT | C01-R18 |
| 431 | 12. Sticky behavior | A0045-011 | CONTRACT | C01-R18 |
| 448 | 13. Validation and failure behavior | A0045-012 | CONTRACT | C01-R19 |
| 483 | 14. Audit and persisted call records | A0045-013 | CONTRACT | C01-R20 |
| 514 | 15. Security controls | A0045-014 | CONTRACT | C01-R18 |
| 531 | 16. Required implementation tests | A0045-015 | HISTORICAL | historical-only |
| 591 | 17. Block B implementation/test obligations (review NB-4..NB-6) | A0045-016 | CONTRACT | C01-R17 |
| 608 | Alternatives considered | A0045-017 | CONTRACT | C01-R18 |
| 635 | Consequences | A0045-018 | CONTRACT | C01-R18 |
| 657 | Review and acceptance gate | A0045-018 | CONTRACT | C01-R18 |

## ADR-0052 — microsoft-agent-framework-bops-boundary (642 lines)

| Line | Heading | Row | Disposition | Destination / seat |
|---:|---|---|---|---|
| 1 | ADR-0052 — Microsoft Agent Framework as the bOps agent infrastructure boundary | A0052-001 | CONTRACT | C01-R01 |
| 9 | 1. Context | A0052-002 | CONTRACT | C01-R01 |
| 26 | 2. Decision | A0052-003 | CONTRACT | C01-R01 |
| 63 | 3. Relationship to existing decisions | A0052-004 | CONTRACT | C01-R03 |
| 65 | ADR-0001 — project-owned `IChatModel` | A0052-004 | CONTRACT | C01-R03 |
| 80 | ADR-0014 — explicit planning and HARDEN-8 | A0052-005 | CONTRACT | C01-R05 |
| 86 | ADR-0013 / ADR-0039 — model-call audit and failure containment | A0052-005 | CONTRACT | C01-R05 |
| 90 | ADR-0017 / ADR-0040 — persistent/resumable tasks | A0052-005 | CONTRACT | C01-R05 |
| 94 | ADR-0030 / ADR-0036 | A0052-006 | CONTRACT | C01-R01 |
| 98 | ADR-0042 / ADR-0050 | A0052-006 | CONTRACT | C01-R01 |
| 102 | ADR-0045 | A0052-006 | CONTRACT | C01-R01 |
| 106 | ADR-0046 / ADR-0047 | A0052-006 | CONTRACT | C01-R01 |
| 110 | ADR-0051 | A0052-006 | CONTRACT | C01-R01 |
| 114 | 4. Dependency boundary | A0052-007 | CONTRACT | C01-R02 |
| 139 | 5. Governed model invocation | A0052-008 | CONTRACT | C01-R05 |
| 166 | 6. MAF context management | A0052-009 | CONTRACT | C01-R06 |
| 195 | 7. Protected context | A0052-010 | CONTRACT | C01-R06 |
| 213 | 8. Message mapping | A0052-011 | CONTRACT | C01-R06 |
| 231 | 9. Model context profile | A0052-012 | CONTRACT | C01-R07 |
| 256 | 10. Context budget and token estimation | A0052-013 | CONTRACT | C01-R07 |
| 285 | 11. Compaction pipeline | A0052-014 | CONTRACT | C01-R07 |
| 309 | 12. Summarization | A0052-015 | CONTRACT | C01-R08 |
| 311 | 12.1 Provider/model selection | A0052-015 | CONTRACT | C01-R08 |
| 324 | 12.2 Governance | A0052-015 | CONTRACT | C01-R08 |
| 335 | 12.3 Authority | A0052-016 | CONTRACT | C01-R08 |
| 353 | 12.4 Failure | A0052-016 | CONTRACT | C01-R08 |
| 359 | 13. EvidenceRead | A0052-017 | CONTRACT | C04-R16 |
| 389 | 14. EvidenceFact coverage | A0052-018 | CONTRACT | C04-R01 |
| 404 | 15. Tool authority | A0052-019 | CONTRACT | C01-R01 |
| 420 | 16. Harness, sessions and workflows | A0052-020 | CONTRACT | C01-R01 |
| 434 | 17. AgentRunner target decomposition | A0052-021 | HISTORICAL | historical-only |
| 459 | 18. Implementation sequence | A0052-022 | HISTORICAL | historical-only |
| 471 | 19. Telemetry | A0052-023 | CONTRACT | C01-R09 |
| 488 | 20. Failure model | A0052-024 | CONTRACT | C01-R09 |
| 501 | 21. Security invariants | A0052-025 | CONTRACT | C01-R01 |
| 519 | 22. Consequences | A0052-026 | HISTORICAL | historical-only |
| 521 | Positive | A0052-026 | HISTORICAL | historical-only |
| 534 | Negative | A0052-026 | HISTORICAL | historical-only |
| 546 | 23. Alternatives rejected | A0052-027 | HISTORICAL | historical-only |
| 548 | Keep a fully custom agent framework | A0052-027 | HISTORICAL | historical-only |
| 552 | Keep MAF and HARDEN-8 as permanent interchangeable engines | A0052-027 | HISTORICAL | historical-only |
| 556 | Replace public bOps contracts with MAF types | A0052-027 | HISTORICAL | historical-only |
| 560 | Adopt full Harness immediately | A0052-027 | HISTORICAL | historical-only |
| 564 | Increase EvidenceRead limits as the solution | A0052-027 | HISTORICAL | historical-only |
| 568 | 24. Required documentation reconciliation | A0052-028 | HISTORICAL | historical-only |
| 579 | 25. Acceptance record | A0052-029 | CONTRACT | C01-R01 |
| 591 | 26. Research basis | A0052-030 | HISTORICAL | historical-only |
| 605 | 27. AF-02 implementation evidence (2026-10-11) | A0052-031 | HISTORICAL | historical-only |
| 621 | 28. AF-03 implementation evidence (2026-10-11) | A0052-032 | HISTORICAL | historical-only |
| 625 | 29. AF-04 implementation evidence (2026-10-11) | A0052-033 | CONTRACT | C04-R16 |
| 629 | 30. AF-05 implementation evidence (2026-10-11) | A0052-034 | CONTRACT | C01-R07 |
| 634 | 31. AF-05G implementation evidence (2026-10-11) | A0052-035 | CONTRACT | C04-R16 |

## ADR-0041 — typed-cross-platform-stability-evidence (557 lines)

| Line | Heading | Row | Disposition | Destination / seat |
|---:|---|---|---|---|
| 1 | ADR-0041 — Typed cross-platform stability evidence (`system.stability`) | A0041-001 | CONTRACT | C04-R04 |
| 16 | Context | A0041-002 | CONTRACT | C04-R10 |
| 29 | Decision | A0041-003 | CONTRACT | C02-R03,C04-R09,C04-R10 |
| 31 | 1. Tool identity and placement | A0041-003 | CONTRACT | C02-R03,C04-R09,C04-R10 |
| 42 | 2. Arguments | A0041-004 | CONTRACT | C04-R10 |
| 63 | 3. Categories | A0041-005 | CONTRACT | C04-R10 |
| 77 | 3.1 Windows | A0041-006 | TECH-REF | docs/system-stability.md |
| 123 | 3.2 Linux | A0041-007 | TECH-REF | docs/system-stability.md |
| 161 | 4. Collection bounds and coverage stores | A0041-008 | CONTRACT | C04-R10 |
| 177 | 5. Result contract (`schemaVersion` 1) | A0041-009 | CONTRACT | C04-R10 |
| 285 | 6. Evidence time (normative for `system.stability` and `system.crashes`) | A0041-011 | CONTRACT | C04-R04 |
| 336 | 7. Identity, deduplication and correlation (normative for `system.stability` and `system.c | A0041-012 | CONTRACT | C04-R05 |
| 358 | 8. Aggregation and time buckets | A0041-013 | TECH-REF | docs/system-stability.md |
| 387 | 9. Coverage and completeness | A0041-014 | CONTRACT | C04-R07 |
| 400 | 10. Boundary with `system.crashes` and `system.events` | A0041-015 | CONTRACT | C04-R10 |
| 422 | 11. Security, privacy and audit | A0041-016 | CONTRACT | C04-R10 |
| 443 | 12. Public contract and compatibility | A0041-017 | CONTRACT | C02-R01 |
| 454 | Alternatives considered | A0041-018 | CONTRACT | C04-R04 |
| 506 | Consequences | A0041-019 | CONTRACT | C04-R10 |
| 522 | Evidence-driven correction discovered during real Windows validation (2026-10-02) | A0041-020 | CONTRACT | C04-R04 |
| 542 | Independent review corrections (2026-10-02) | A0041-021 | CONTRACT | C04-R04 |

## ADR-0014 — explicit-planning-and-replanning (437 lines)

| Line | Heading | Row | Disposition | Destination / seat |
|---:|---|---|---|---|
| 1 | ADR-0014 — An explicit PLAN/REPLAN phase, separate from the per-step model call | A0014-001 | CONTRACT | C03-R03 |
| 10 | Context | A0014-002 | HISTORICAL | historical-only |
| 20 | Decision | A0014-003 | CONTRACT | C03-R03 |
| 64 | Alternatives considered | A0014-010 | CONTRACT | C03-R03 |
| 98 | Consequences | A0014-011 | HISTORICAL | historical-only |
| 109 | HARDEN-8 amendment — context and budget economy (Accepted 2026-10-04) | A0014-012 | CONTRACT | C01-R09 |
| 134 | Decision summary | A0014-014 | HISTORICAL | historical-only |
| 153 | 1. Model-facing history and stable references | A0014-017 | HISTORICAL | historical-only |
| 207 | 2. Read complete source evidence through Runtime | A0014-019 | CONTRACT | C04-R16 |
| 286 | 3. HARDEN-9 and replan compatibility | A0014-022 | CONTRACT | C04-R11 |
| 317 | 4. Overflow recovery and budgets | A0014-025 | CONTRACT | C01-R09 |
| 396 | 5. E2E-13 and review obligations | A0014-029 | HISTORICAL | historical-only |

## ADR-0040 — resume-state-machine (428 lines)

| Line | Heading | Row | Disposition | Destination / seat |
|---:|---|---|---|---|
| 1 | ADR-0040 — Resume as a persisted state-machine transition: execution attempts, budgets, at | A0040-001 | CONTRACT | C03-R12 |
| 29 | Context | A0040-002 | HISTORICAL | historical-only |
| 82 | Decision | A0040-003 | CONTRACT | C03-R10 |
| 84 | 1. Task execution attempt (additive; not a new status) | A0040-003 | CONTRACT | C03-R10 |
| 96 | 2. Additive task fields | A0040-004 | CONTRACT | C03-R10 |
| 115 | 3. Resumability — one runtime function | A0040-005 | CONTRACT | C03-R12 |
| 140 | 4. The transition: an atomic persisted state change, then admission, then 202 | A0040-006 | CONTRACT | C03-R13 |
| 142 | 4.1 Store capability (additive interface) | A0040-006 | CONTRACT | C03-R13 |
| 167 | 4.2 SQLite implementation and migration | A0040-007 | TECH-REF | docs/architecture/reference/task-store-and-mutation-journal.md |
| 180 | 4.3 The resume sequence (API) | A0040-008 | CONTRACT | C03-R13,C05-R02 |
| 205 | 4.4 Ownership and fencing | A0040-009 | CONTRACT | C03-R13 |
| 226 | 4.5 Post-execution writes | A0040-010 | CONTRACT | C03-R13 |
| 236 | 5. Budgets: per execution attempt versus lifetime | A0040-011 | CONTRACT | C03-R09 |
| 238 | 5.1 Semantics | A0040-011 | CONTRACT | C03-R09 |
| 260 | 5.2 What counts | A0040-012 | CONTRACT | C03-R09 |
| 270 | 5.3 Where they are persisted | A0040-012 | CONTRACT | C03-R09 |
| 275 | 5.4 Rows without accounting (deterministic derivation, materialized once) | A0040-012 | CONTRACT | C03-R09 |
| 287 | 6. Terminal reason | A0040-013 | CONTRACT | C03-R10 |
| 295 | 7. Zero-plan resume | A0040-014 | CONTRACT | C03-R04 |
| 301 | 8. Task origin | A0040-015 | CONTRACT | C03-R11 |
| 303 | 8.1 Explicit marker | A0040-015 | CONTRACT | C03-R11 |
| 311 | 8.2 Rows written before the marker (fail closed) | A0040-015 | CONTRACT | C03-R11 |
| 319 | 9. API view and HTTP contract (amends ADR-0018) | A0040-016 | CONTRACT | C05-R02 |
| 340 | 10. Lifecycle audit | A0040-017 | CONTRACT | C03-R14 |
| 360 | 11. Resume note — withdrawn | A0040-018 | CONTRACT | C03-R12 |
| 367 | 12. Policy, approval and verification on resume | A0040-019 | CONTRACT | C03-R12 |
| 373 | Alternatives considered | A0040-020 | CONTRACT | C03-R11,C03-R12,C03-R13 |
| 395 | Consequences | A0040-021 | HISTORICAL | historical-only |
| 415 | Decisions (as accepted) | A0040-022 | CONTRACT | C03-R09 |

## ADR-0037 — safe-local-plugin-lifecycle (398 lines)

| Line | Heading | Row | Disposition | Destination / seat |
|---:|---|---|---|---|
| 1 | ADR-0037 — Safe local plugin lifecycle | A0037-001 | HISTORICAL | historical-only |
| 8 | Context | A0037-002 | CONTRACT | C02-R27 |
| 21 | Decision | A0037-003 | CONTRACT | C02-R27 |
| 23 | Authority and persisted state | A0037-003 | CONTRACT | C02-R27 |
| 47 | Intake, staging, extraction, and bounds | A0037-004 | CONTRACT | C02-R27 |
| 99 | Exact validation sequence | A0037-006 | CONTRACT | C02-R27 |
| 143 | Atomic install, generation roles, and recovery | A0037-007 | CONTRACT | C02-R27 |
| 215 | Enable, disable, and activation failure | A0037-009 | CONTRACT | C02-R27 |
| 244 | Concurrency, ETag, and idempotency | A0037-010 | CONTRACT | C05-R08 |
| 301 | Cleanup and audit | A0037-011 | CONTRACT | C02-R27 |
| 316 | M6 API responsibility | A0037-012 | CONTRACT | C05-R08 |
| 329 | M7 UI responsibility | A0037-013 | CONTRACT | C05-R08 |
| 341 | Security property and public boundary | A0037-014 | CONTRACT | C02-R15,C02-R26 |
| 351 | Threat and bypass analysis | A0037-015 | CONTRACT | C02-R27 |
| 371 | Alternatives considered | A0037-016 | CONTRACT | C02-R27 |
| 384 | Implementation contract for M5 | A0037-017 | HISTORICAL | historical-only |

## Summary

| ADR | headings | headings without a row | matrix rows |
|---|---:|---:|---:|
| 0042 | 63 | 0 | 39 |
| 0044 | 40 | 0 | 30 |
| 0057 | 68 | 0 | 45 |
| 0043 | 47 | 0 | 26 |
| 0051 | 56 | 0 | 45 |
| 0032 | 38 | 0 | 33 |
| 0045 | 23 | 0 | 18 |
| 0052 | 52 | 0 | 35 |
| 0041 | 21 | 0 | 21 |
| 0014 | 12 | 0 | 29 |
| 0040 | 29 | 0 | 22 |
| 0037 | 16 | 0 | 17 |
