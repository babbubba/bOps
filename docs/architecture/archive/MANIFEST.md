# Archive manifest — legacy ADRs and decision documents

> **Non-normative.** This manifest describes a historical archive. It carries no architectural authority; the current authority is the five contracts in [`docs/architecture/current/`](../current/). Bootstrap and agents must not load it.

| | |
|---|---|
| Archive | `legacy-adr-and-decisions-2026-10-22.zip` (exactly 57 entries, original repository-relative paths, uncompressed content byte-identical to the Git blobs of the baseline) |
| Baseline commit (blobs read from here) | `e0e9de7a74b8bb33e07c7b1f8c92ab87832986de` (`main`) |
| DOC-03 source commit (verified HEAD at the start of DOC-03) | `57990d26f362676624d203064b69d3ced9c3448f` |
| Task | `BOPS-0121` / `DOC-CONSOL-01` |
| Date | 2026-10-22 |
| Outside the ZIP | this file, `MANIFEST.csv`, `legacy-adr-and-decisions-2026-10-22.zip.sha256`, `README.md` |

The SHA-256 below is the hash of the **content** of each original (not the Git blob SHA, listed in `MANIFEST.csv`). This manifest does not contain its own hash, the hash of the CSV or the hash of the ZIP (the ZIP hash is only in the `.sha256` sidecar), and does not name the commit that contains it: use `git log -- docs/architecture/archive/MANIFEST.md`.

## 1. Archived originals (57)

Rows: 55 ADRs, plus the complete previous versions of `agentic/01-architecture-rules.md` and `agentic/06-decisions.md`. `agentic/03-security-rules.md` is **not** archived here (plan G1-05): its previous version stays recoverable from Git history. Destination `pending-Dn` means the content is non-binding until decision Dn is taken; D1, D4 and D5 remain open. Seven ADRs are Proposed: 0050, 0053, 0054, 0055, 0056, 0058, 0059.

| # | path | bytes | sha256 (content) | status | destination |
|---|---|---|---|---|---|
| 1 | `docs/architecture/adr/0001-project-owned-chat-model.md` | 3368 | `e27d88baafe561489dfaf24f4d0319b79ad62c80c98905b5b83e4674881c25d5` | Accepted | C01-R03 |
| 2 | `docs/architecture/adr/0002-five-risk-levels-critical-unbypassable.md` | 3632 | `8566bcb749346d47ae3585aec64321b793574529f4eb5ddab89193a1576eb151` | Accepted | C02-R05 |
| 3 | `docs/architecture/adr/0005-shared-openai-compatible-adapter.md` | 3006 | `1e9f4bf46d8aea440dbff99e09ddba74b058bf01ca4f9a24a3752f3223ef13cc` | Accepted | C01-R04 |
| 4 | `docs/architecture/adr/0006-tools-os-providers-are-packages.md` | 3510 | `74be981c4081a65f3713c67f38a0f5bf06d64cdb26f42bd33e913f9570d14703` | Accepted | C02-R03; C02-R01 |
| 5 | `docs/architecture/adr/0011-local-only-remote-capable-contract.md` | 3083 | `3fb5a63f5818298b7db80f9dfe9fb7e4b7627d1eb4fee136f041614bdeb2e1fc` | Accepted | C02-R02; C02-R19 |
| 6 | `docs/architecture/adr/0012-dynamic-loading-over-native-aot.md` | 2599 | `c43e5335686c86d11d7ee461b8350bfa7b6dc5be73f992a60d286e54e907c18f` | Accepted | C05-R01 |
| 7 | `docs/architecture/adr/0013-model-call-audit-outcome.md` | 4311 | `74643f994621f944b0d358fddf23e6a18134f9412c9e1e427039920de83bb218` | Accepted | C01-R14 |
| 8 | `docs/architecture/adr/0014-explicit-planning-and-replanning.md` | 34813 | `fb293a2af845b2b6be39b0256d2b2724f3dac6403650b2ebba9b12a73cf7fe1c` | Accepted | C03-R03; C03-R01; C01-R09; C03-R09; C04-R16; C04-R11 |
| 9 | `docs/architecture/adr/0015-policy-engine-approval-flow-and-audit-hash-chaining.md` | 11728 | `407ee804a123b0ea9ffce2cd999bf934a06e5ff6cf719e8771fe5b162b7bef4e` | Accepted | C02-R05; C02-R08; C02-R14; agentic/02-coding-standards.md; C03-R03; C02-R03 |
| 10 | `docs/architecture/adr/0016-post-action-verification.md` | 10466 | `573a129cd1d10ad480439e556b996bb0bfd838767f3d7196229d0bf19067382c` | Accepted | C02-R09 |
| 11 | `docs/architecture/adr/0017-persistent-resumable-tasks.md` | 6724 | `fff86cf0db979e8a58ffe5c078982e723d0443c80c2b785396f03450e50ddbfe` | Accepted | C03-R02; C03-R12; docs/operator-configuration.md; C02-R01 |
| 12 | `docs/architecture/adr/0018-bops-api-minimal-surface.md` | 10056 | `ed13aaf9b5ac73fcfac54ba16f61d88d30bd7c284e739616404b3ae668f77439` | Accepted | C05-R01; C05-R02; C02-R08 |
| 13 | `docs/architecture/adr/0019-providers-listing-endpoint.md` | 3828 | `8ba61e213f8d2d9883a60cc8995a3c3cb0172c8e850c800fed56ad733871ddac` | Accepted | C05-R03 |
| 14 | `docs/architecture/adr/0020-plugin-loader-manifest-and-activation-boundary.md` | 9607 | `bcc182a2ca81f77388c3d71abfd49e7d4cd271fbe93599a231c581a33ea34fa6` | Accepted | C02-R04; C02-R26; C02-R15 |
| 15 | `docs/architecture/adr/0021-service-package-windows-linux-strategy.md` | 7200 | `e4b29824b6323eb577043d2cbbd1bc9055b9e84bb7181b51469eedb236f97295` | Accepted | docs/architecture/reference/provider-adapters-and-service-tools.md; C02-R19 |
| 16 | `docs/architecture/adr/0022-bops-abstractions-1.0-security-boundaries.md` | 7387 | `ee07c2752104a20a9845d83e6bbe46ff3169bf56046cac1230fd67ee8227c848` | Accepted | C02-R15; C05-R11; C02-R13; C05-R03; C02-R26; C02-R08; C02-R06; C04-R06 |
| 17 | `docs/architecture/adr/0023-skill-capability-evidence-execution-plan.md` | 13855 | `0d7da07e30ee0c6c64135a9d38c6db145c1c6b4bdc8c628add781cc7229d6fe9` | Accepted | C02-R21; C04-R18; C02-R05 |
| 18 | `docs/architecture/adr/0024-execution-plan-orchestration.md` | 7245 | `a5aca87d010be9c719a22fc08c36d9367c6463c7b38d83272a0ee30c24b72a31` | Accepted | C02-R21; C02-R08; C04-R18 |
| 19 | `docs/architecture/adr/0025-skill-provider-and-restricted-invocation.md` | 6826 | `60c3c1501832fec56f7d5e8819d396262527f032d676e911c9e84bccebffef1e` | Accepted | C02-R21; C02-R05; C02-R14; C03-R23 |
| 20 | `docs/architecture/adr/0026-bounded-filesystem-inventory-manifests.md` | 6703 | `c6f7328c8602d26a01e34a2bbef089d8b685974cffd2744cac78d270f01b72da` | Accepted | C02-R07; C02-R16 |
| 21 | `docs/architecture/adr/0027-governed-recursive-deletion-manifests.md` | 10916 | `fa28d12f1f99fac3ac560ec26019fb41574c9918152e8eabe4775e7f267505b8` | Accepted | C02-R07; C02-R06; C02-R17; docs/governed-recursive-deletion.md; C05-R09 |
| 22 | `docs/architecture/adr/0028-outbound-web-search-and-fetch.md` | 12375 | `20324565df40236b2a00a1b88c3d25dfbe4cd741aa25158fb7ee6e10f5b892ef` | Accepted | C02-R18; C04-R17; C02-R01 |
| 23 | `docs/architecture/adr/0029-encrypted-local-vault-and-master-key.md` | 20375 | `83840c6be4d3b4a621fdd6d2a4b7dc178bc3958385bdc34168b96ff974b9d420` | Accepted | C01-R16; C05-R03; C02-R13 |
| 24 | `docs/architecture/adr/0030-privilege-reducing-multi-agent-delegation.md` | 15082 | `a816e696563b7e64f54246be7a1366ff6f30039c050ba8db3331cbc1bc249a48` | Accepted | C02-R23; C02-R24; C04-R17; C03-R22; C05-R07; C05-R11; C02-R15 |
| 25 | `docs/architecture/adr/0031-per-role-envelope-requirements.md` | 10446 | `3385cee729783fe2876a59172b049d7c244e9d57f653b495c79a3c8455052c92` | Accepted | C02-R24; C03-R22; C05-R11 |
| 26 | `docs/architecture/adr/0032-bounded-cross-platform-system-events.md` | 56619 | `5cc631fb4266678c6cace2a5ffb0410f1ff21c1892a197651bfa1fd7494ef6a3` | Accepted | C04-R06; C04-R17; C02-R03; C02-R06; docs/system-events.md; C02-R15; C04-R07; C04-R04; docs/system-maintenance.md; C02-R07; C04-R08 |
| 27 | `docs/architecture/adr/0033-governed-docker-image-build-and-volume-management.md` | 12417 | `05b380a3e7d692a20e8cd013b4606292f4d00125c1437204d22ed8b4c24f9fcf` | Accepted | C02-R20; docs/docker-management.md; C02-R06; C04-R17 |
| 28 | `docs/architecture/adr/0034-advanced-process-diagnostics.md` | 13605 | `6cf4e6010b6aca3f14ef5fb1f5be9e7e33f1b58a06e07a70e9f4bd68c2fa8c67` | Accepted | C02-R19; C02-R03; C02-R06; C04-R17; docs/process-diagnostics.md |
| 29 | `docs/architecture/adr/0035-sockets-routes-and-network-diagnostics.md` | 17118 | `c50b97c7dda5288d4343d9c48da952032ff12d3971fbf6d8c115a0d1e11d8fc4` | Accepted | C02-R03; C02-R06; docs/network-diagnostics.md; C02-R20; C02-R19; C02-R15 |
| 30 | `docs/architecture/adr/0036-product-neutral-entitlement-applicability-and-execution-time-enforcement.md` | 24035 | `507c59fe0377e8d9bd7944a05fdfac27d31ad59bdbe74755056b48cc9df54ef9` | Accepted | C02-R22 |
| 31 | `docs/architecture/adr/0037-safe-local-plugin-lifecycle.md` | 26440 | `40152225e68ea20adff688b1a09f7dde46b297a48baf9743010e2ab1cc642b2d` | Accepted | C02-R27; C05-R08; C02-R15; C02-R26 |
| 32 | `docs/architecture/adr/0038-provider-wire-contract.md` | 15965 | `b7dd688e4e23f3942598239e1b30dc3aee974b21c9ccefa0529517cf66f188b6` | Accepted | C01-R10; docs/architecture/reference/provider-adapters-and-service-tools.md; C01-R11; C03-R03 |
| 33 | `docs/architecture/adr/0039-model-call-failure-containment.md` | 26107 | `358a7f29f40a6bdd733a2e4ec4ae960fb90adf2666dbda5dec7193c9a82b3cfd` | Accepted | C01-R13; C01-R12; C01-R14; docs/operator-configuration.md; C03-R04; C03-R13 |
| 34 | `docs/architecture/adr/0040-resume-state-machine.md` | 35321 | `a3c86f0c93ec8915e8aa786568290f3c8140922b2b25b1ecf4fd53797f71f248` | Accepted | C03-R12; C03-R10; C03-R13; docs/architecture/reference/task-store-and-mutation-journal.md; C05-R02; C03-R09; C03-R04; C03-R11; C03-R14 |
| 35 | `docs/architecture/adr/0041-typed-cross-platform-stability-evidence.md` | 44421 | `8d0850981d4d96607a6a0743622e9ecb64dc1d120a9530eaae40f289c9e75930` | Accepted | C04-R04; C04-R10; C02-R03; C04-R09; docs/system-stability.md; C04-R05; C04-R07; C02-R01 |
| 36 | `docs/architecture/adr/0042-evidence-reasoning-and-limitation-disclosure.md` | 108219 | `96d6c1aa76e3e5d97ac84962804bcf1f106817fd04bb0501df24fcbe573c41af` | Accepted | C04-R01; C04-R02; C04-R11; C04-R12; C04-R03; C04-R04; C04-R13; C04-R15; C04-R14 |
| 37 | `docs/architecture/adr/0043-browser-web-session.md` | 72285 | `0b2bc697b6a77d8067d603f032106eeb2330df192be4dcf7797f954b41e3a380` | Accepted | C05-R04; C05-R05; C05-R03; docs/operator-configuration.md; C05-R06; docs/security/threat-model.md |
| 38 | `docs/architecture/adr/0044-request-dependent-delegation-authority-and-operability.md` | 101256 | `19a4d1008711e6f1e683771e0a580a05dbf7920191c52a5c8717b503582e6d07` | Accepted | C02-R25; C05-R07; docs/agents/delegation-policy.md; C02-R21; C03-R11; C04-R13; C05-R11 |
| 39 | `docs/architecture/adr/0045-execution-pinned-provider-configuration-and-fallback-chain.md` | 44951 | `072bdcb621dd88184403ab17dac94d158bd2fca983c5c6e31c80f4351d58b7c8` | Accepted | C01-R17; C01-R15; C01-R16; C01-R21; C01-R18; C01-R19; C01-R20 |
| 40 | `docs/architecture/adr/0046-step-scoped-tool-routing-and-transactional-replanning.md` | 28954 | `b70163537436b7c6f55b34a6919da79362db32d180280f0385e6e03da072bacb` | Accepted | C03-R05; C01-R11; C03-R04; C04-R16; C04-R15; C03-R07; C03-R06; C03-R08 |
| 41 | `docs/architecture/adr/0047-bounded-planned-step-argument-correction.md` | 7884 | `599ea0935f60dbe6ffe200de3e28c715f8b9e7d7a1fea78dceb22a6c96c62fb9` | Accepted | C03-R06; C03-R05 |
| 42 | `docs/architecture/adr/0048-bounded-windows-kernel-dump-analysis.md` | 13024 | `722238d2ccc6de41d316f3311c3176e65e1706a3ba296efa7595bbc435dcefae` | Accepted | C02-R20; C04-R09; C02-R19 |
| 43 | `docs/architecture/adr/0049-unified-prerequisite-readiness-and-system-messages.md` | 19429 | `ec2d617da9099b447c145039ae1991ad4580ca78d3e4a5548fa113d37346a6d7` | Accepted | C02-R28; C05-R10; docs/operator-configuration.md |
| 44 | `docs/architecture/adr/0050-semantic-planned-step-execution-and-conditional-follow-ups.md` | 19294 | `7104fcfee0b0ab482cad91d3d1919cf0b5aa7926adebad83b1379fec46f61adb` | Proposed | C03#pending-D4; C04#pending-D4 |
| 45 | `docs/architecture/adr/0051-durable-ordinary-task-mutation-intent-journal.md` | 66849 | `394fafc050d6263561043e46d35aa318ec5992ac5e7ad56b56da31cb6c20fb80` | Accepted | C03-R15; C03-R19; C03-R16; C03-R17; docs/architecture/reference/task-store-and-mutation-journal.md; C03-R18; C03-R20; C03-R21; C05-R02 |
| 46 | `docs/architecture/adr/0052-microsoft-agent-framework-bops-boundary.md` | 28944 | `12ed9b8054e925affe642502b1ed2bc8600736087154f3201386afd49acf3a2d` | Accepted | C01-R01; C01-R03; C01-R05; C01-R02; C01-R06; C01-R07; C01-R08; C04-R16; C04-R01; C01-R09 |
| 47 | `docs/architecture/adr/0053-maf-convergence-architecture-boundaries.md` | 18278 | `aaa6b898cafadcf02284bfa3d14211917f259ca1ecd7bdf862f46869ddc18439` | Proposed | C01#pending-D1; C05#pending-D5 |
| 48 | `docs/architecture/adr/0054-maf-bplus-convergence-decision.md` | 15786 | `b190bfa338aa1b3eb44ccddf75b58f65fb445912c360d38200cab245975af30b` | Proposed | C01#pending-D1; C05#pending-D5 |
| 49 | `docs/architecture/adr/0055-trusted-knowledge-packs-and-provider-isolation.md` | 8067 | `3958f67d90e3d9d6b632feefecb2a4e0fd0e185149cfa94346a039575d80c7e0` | Proposed | C05#pending-D5 |
| 50 | `docs/architecture/adr/0056-governed-knowledge-read-and-durable-resume.md` | 8832 | `2f4ca626ebf83ef1ec13923107722882c19bb33f2527872ba5f76d64a0528071` | Proposed | C05#pending-D5 |
| 51 | `docs/architecture/adr/0057-knowledge-discovery-index.md` | 83376 | `43a20dc85139a576f9e702b0db82cf597d9c442f3633ad66e9d31ddd65f72032` | Accepted | C05#pending-D5; C05-R13; docs/knowledge/local-agent-pilot.md; C02-R12 |
| 52 | `docs/architecture/adr/0058-local-agent-knowledge-pilot.md` | 17718 | `1b3f36dfb2ff1ed9b9c7c4e43e8f4a352941d050fac4eaf9ad10867bcff92b6d` | Proposed | C05#pending-D5 |
| 53 | `docs/architecture/adr/0059-knowledge-refusal-recovery.md` | 8980 | `2eac986ceca742bec5e2b43f73bf9afec726375adc0a2b31b042ca91507099c4` | Proposed | C05#pending-D5 |
| 54 | `docs/architecture/adr/0060-planner-facing-tool-parameter-guidance.md` | 19143 | `d02744912f170c3bddaa15ca83c05477f22bebee2126558d6a8e9f42cc263d6c` | Accepted | C03-R24 |
| 55 | `docs/architecture/adr/0061-system-events-source-filter-disclosure.md` | 38519 | `853d7aafdc84b4513195f52bcabb013e4527387616d6b00ae1d12c00e0d1c920` | Accepted | C04-R08; docs/system-events.md |
| 56 | `agentic/01-architecture-rules.md` | 33215 | `89603261187534873b5dec6f7fe88ac6047b5448c4cdeb09145c0c5807f772c5` | Accepted (rules A/B/C/D) | compatibility index (kept in place, non-normative) |
| 57 | `agentic/06-decisions.md` | 94259 | `589ccdf5741edcee390214396ee1f5da51fff1aa4ab42de5b45e2d7311bf532e` | Accepted (decision log D-001..D-050) | compatibility index (kept in place, non-normative) |

Archive reason for every row: the content was consolidated into the canonical contracts, technical references or the pending section of a contract (DOC-02, GATE-02 approved 2026-10-22), and keeping several normative copies would create competing authorities. The files are preserved unmodified for provenance only. Where the destination column says `historical-only` the text has no current counterpart by design (rationale in `DOC-02-traceability-matrix.tsv`).

## 2. Historical identifier map

Source: `DOC-02-id-coverage.tsv` (GATE-02 approved). Destination is a rule of a current contract (`Cn-Rmm`), a current technical reference, `Cn#pending-Dn` (non-binding pending section) or `historical-only`. Missing ADR numbers (never existed): 0003, 0004, 0007, 0008, 0009, 0010.

| id | source | destination | decision status |
|---|---|---|---|
| A1 | 01-architecture-rules.md | C02-R01 | Accepted (rule A1) |
| A2 | 01-architecture-rules.md | C02-R02 | Accepted (rule A2) |
| A3 | 01-architecture-rules.md | C02-R02 | Accepted (rule A3) |
| A4 | 01-architecture-rules.md | C02-R02 | Accepted (rule A4) |
| A5 | 01-architecture-rules.md | C02-R02 | Accepted (rule A5) |
| A6 | 01-architecture-rules.md | C02-R02 | Accepted (rule A6) |
| A7 | 01-architecture-rules.md | C02-R03 | Accepted (rule A7) |
| A8 | 01-architecture-rules.md | C02-R03 | Accepted (rule A8) |
| A9 | 01-architecture-rules.md | C02-R03, C02-R04 | Accepted (rule A9) |
| A10 | 01-architecture-rules.md | C02-R04 | Accepted (rule A10) |
| A11 | 01-architecture-rules.md | C02-R04 | Accepted (rule A11) |
| A12 | 01-architecture-rules.md | historical-only | Expired (G1-09) |
| A13 | 01-architecture-rules.md | C01-R01, C01-R02, C01-R05 | Accepted (rule A13, ADR-0052) |
| B1 | 01-architecture-rules.md | C02-R02, C02-R06 | Accepted (rule B1) |
| B2 | 01-architecture-rules.md | C02-R06, C02-R07, C02-R05 | Accepted (rule B2, ADR-0026, ADR-0027) |
| B3 | 01-architecture-rules.md | C02-R09 | Accepted (rule B3, D-006) |
| B4 | 01-architecture-rules.md | C02-R29, C02-R28 | Accepted (rule B4 as amended by ADR-0049) |
| B5 | 01-architecture-rules.md | C01-R03, C01-R11 | Accepted (rule B5, D-007) |
| B6 | 01-architecture-rules.md | C01-R04 | Accepted (rule B6) |
| B7 | 01-architecture-rules.md | C02-R05, C02-R08, C02-R04 | Accepted (rule B7, ADR-0015) |
| B8 | 01-architecture-rules.md | C02-R14, C01-R14 | Accepted (rule B8, ADR-0013, ADR-0015) |
| B9 | 01-architecture-rules.md | C03-R03 | Accepted (rule B9, ADR-0014) |
| B10 | 01-architecture-rules.md | C02-R21, C03-R23 | Accepted (rule B10, ADR-0025) |
| C1 | 01-architecture-rules.md | C03-R01, C01-R14 | Accepted (rule C1, ADR-0013) |
| C2 | 01-architecture-rules.md | C02-R10 | Accepted (rule C2, S7) |
| C3 | 01-architecture-rules.md | C01-R06, C01-R07, C01-R08 | Accepted (rule C3, ADR-0052) |
| C4 | 01-architecture-rules.md | C03-R01 | Accepted (rule C4) |
| C5 | 01-architecture-rules.md | C03-R01, C03-R09 | Accepted (rule C5) |
| C6 | 01-architecture-rules.md | C03-R01 | Accepted (rule C6) |
| C7 | 01-architecture-rules.md | C02-R11, C02-R12 | Accepted (rule C7, S5) |
| C8 | 01-architecture-rules.md | C03-R03 | Accepted (rule C8, ADR-0014) |
| C9 | 01-architecture-rules.md | C04-R02, C04-R11, C04-R12, C04-R13 | Accepted (rule C9, ADR-0042) |
| D | 01-architecture-rules.md | C02-R14 | Accepted (rule D, D-009) |
| S1 | 03-security-rules.md | C02-R19 | Accepted (S1) |
| S2 | 03-security-rules.md | C02-R06 | Accepted (S2) |
| S3 | 03-security-rules.md | C02-R05 | Accepted (S3) |
| S4 | 03-security-rules.md | C02-R09 | Accepted (S4) |
| S5 | 03-security-rules.md | C02-R11, C02-R12 | Accepted (S5; ADR-0057 amendment) |
| S6 | 03-security-rules.md | C02-R13 | Accepted (S6) |
| S7 | 03-security-rules.md | C02-R10 | Accepted (S7) |
| S8 | 03-security-rules.md | C02-R15 | Accepted (S8) |
| S9 | 03-security-rules.md | C02-R14 | Accepted (S9) |
| S10 | 03-security-rules.md | C02-R15 | Accepted (S10) |
| S11 | 03-security-rules.md | C02-R16 | Accepted (S11) |
| D-001 | 06-decisions.md | C05-R01, C02-R02 | Accepted (D-001) |
| D-002 | 06-decisions.md | C05-R01 | Accepted (D-002) |
| D-003 | 06-decisions.md | C05-R01 | Accepted (D-003) |
| D-004 | 06-decisions.md | C05-R12 | Accepted (D-004) |
| D-005 | 06-decisions.md | C02-R03 | Accepted (D-005) |
| D-006 | 06-decisions.md | C02-R09 | Accepted (D-006) |
| D-007 | 06-decisions.md | C01-R03, C01-R11 | Accepted (D-007) |
| D-008 | 06-decisions.md | C02-R14 | Accepted (D-008) |
| D-009 | 06-decisions.md | C02-R14 | Accepted (D-009) |
| D-010 | 06-decisions.md | agentic/04-testing-rules.md | Accepted (D-010) |
| D-011 | 06-decisions.md | agentic/02-coding-standards.md | Accepted (D-011) |
| D-012 | 06-decisions.md | historical-only | Expired (G1-09) |
| D-013 | 06-decisions.md | C05-R12 | Accepted (D-013) |
| D-014 | 06-decisions.md | C05-R12 | Accepted (D-014) |
| D-015 | 06-decisions.md | C05-R12 | Accepted (D-015) |
| D-016 | 06-decisions.md | C05-R12 | Accepted (D-016) |
| D-017 | 06-decisions.md | C02-R13 | Accepted (D-017) |
| D-018 | 06-decisions.md | C02-R17 | Accepted (D-018) |
| D-019 | 06-decisions.md | C02-R18 | Accepted (D-019) |
| D-020 | 06-decisions.md | C02-R21, C03-R23 | Accepted (D-020) |
| D-021 | 06-decisions.md | C02-R16, C02-R07 | Accepted (D-021) |
| D-022 | 06-decisions.md | C02-R07 | Accepted (D-022) |
| D-023 | 06-decisions.md | C02-R18 | Accepted (D-023) |
| D-024 | 06-decisions.md | C02-R26 | Accepted (D-024) |
| D-025 | 06-decisions.md | C05-R03, C02-R13, C01-R15 | Accepted (D-025) |
| D-026 | 06-decisions.md | C02-R23, C03-R22 | Accepted (D-026) |
| D-027 | 06-decisions.md | C02-R24, C05-R11 | Accepted (D-027) |
| D-028 | 06-decisions.md | C04-R06, C04-R09, C02-R06, C02-R19 | Accepted (D-028) |
| D-029 | 06-decisions.md | C02-R20 | Accepted (D-029) |
| D-030 | 06-decisions.md | C02-R19, C02-R20 | Accepted (D-030) |
| D-031 | 06-decisions.md | C02-R03, C02-R19, C02-R20 | Accepted (D-031) |
| D-032 | 06-decisions.md | C02-R22 | Accepted (D-032) |
| D-033 | 06-decisions.md | C02-R27, C05-R08, C02-R15 | Accepted (D-033) |
| D-034 | 06-decisions.md | C01-R10, C01-R11 | Accepted (D-034) |
| D-035 | 06-decisions.md | C01-R12, C01-R13, C01-R14, C03-R13 | Accepted (D-035) |
| D-036 | 06-decisions.md | C03-R09, C03-R10, C03-R11, C03-R12, C03-R13, C03-R14 | Accepted (D-036) |
| D-037 | 06-decisions.md | C04-R04, C04-R05, C04-R06, C04-R07, C04-R10 | Accepted (D-037) |
| D-038 | 06-decisions.md | C04-R01, C04-R02, C04-R03, C04-R08, C04-R11, C04-R12 | Accepted (D-038) |
| D-039 | 06-decisions.md | C04-R13 | Accepted (D-039) |
| D-040 | 06-decisions.md | C05-R04, C05-R05, C05-R06 | Accepted (D-040) |
| D-041 | 06-decisions.md | C02-R25, C05-R07, C04-R13, C02-R21 | Accepted (D-041) |
| D-042 | 06-decisions.md | C01-R15, C01-R16, C01-R17, C01-R18, C01-R19, C01-R20, C01-R21 | Accepted (D-042) |
| D-043 | 06-decisions.md | C03-R05, C03-R04 | Accepted (D-043) |
| D-044 | 06-decisions.md | C03-R06 | Accepted (D-044) |
| D-045 | 06-decisions.md | C02-R20, C04-R09 | Accepted (D-045) |
| D-046 | 06-decisions.md | C02-R28, C05-R10 | Accepted (D-046) |
| D-047 | 06-decisions.md | C03-R15, C03-R16, C03-R17, C03-R18, C03-R19, C03-R20, C03-R21 | Accepted (D-047) |
| D-048 | 06-decisions.md | C01-R01, C01-R02, C01-R05, C01-R06, C01-R08, C04-R16 | Accepted (D-048) |
| D-049 | 06-decisions.md | C05-R13 | Accepted (D-049) |
| D-050 | 06-decisions.md | C05#pending-D5 | Proposed (D-050) |
| ADR-0001 | adr/0001-project-owned-chat-model.md | C01-R03, historical-only | Accepted (ADR-0001) |
| ADR-0002 | adr/0002-five-risk-levels-critical-unbypassable.md | historical-only, C02-R05 | Accepted (ADR-0002) |
| ADR-0005 | adr/0005-shared-openai-compatible-adapter.md | historical-only, C01-R04 | Accepted (ADR-0005) |
| ADR-0006 | adr/0006-tools-os-providers-are-packages.md | historical-only, C02-R03, C02-R01 | Accepted (ADR-0006) |
| ADR-0011 | adr/0011-local-only-remote-capable-contract.md | historical-only, C02-R02, C02-R19 | Accepted (ADR-0011) |
| ADR-0012 | adr/0012-dynamic-loading-over-native-aot.md | historical-only, C05-R01 | Accepted (ADR-0012) |
| ADR-0013 | adr/0013-model-call-audit-outcome.md | C01-R14, historical-only | Accepted (ADR-0013) |
| ADR-0014 | adr/0014-explicit-planning-and-replanning.md | C03-R03, historical-only, C03-R01, C01-R09, C03-R09, C04-R16, C04-R11 | Accepted (ADR-0014) |
| ADR-0015 | adr/0015-policy-engine-approval-flow-and-audit-hash-chaining.md | historical-only, C02-R05, C02-R08, C02-R14, agentic/02-coding-standards.md, C03-R03, C02-R03 | Accepted (ADR-0015) |
| ADR-0016 | adr/0016-post-action-verification.md | historical-only, C02-R09 | Accepted (ADR-0016) |
| ADR-0017 | adr/0017-persistent-resumable-tasks.md | C03-R02, historical-only, C03-R12, docs/operator-configuration.md, C02-R01 | Accepted (ADR-0017) |
| ADR-0018 | adr/0018-bops-api-minimal-surface.md | C05-R01, historical-only, C05-R02, C02-R08 | Accepted (ADR-0018) |
| ADR-0019 | adr/0019-providers-listing-endpoint.md | historical-only, C05-R03 | Accepted (ADR-0019) |
| ADR-0020 | adr/0020-plugin-loader-manifest-and-activation-boundary.md | historical-only, C02-R04, C02-R26, C02-R15 | Accepted (ADR-0020) |
| ADR-0021 | adr/0021-service-package-windows-linux-strategy.md | historical-only, docs/architecture/reference/provider-adapters-and-service-tools.md, C02-R19 | Accepted (ADR-0021) |
| ADR-0022 | adr/0022-bops-abstractions-1.0-security-boundaries.md | historical-only, C02-R15, C05-R11, C02-R13, C05-R03, C02-R26, C02-R08, C02-R06, C04-R06 | Accepted (ADR-0022) |
| ADR-0023 | adr/0023-skill-capability-evidence-execution-plan.md | historical-only, C02-R21, C04-R18, C02-R05 | Accepted (ADR-0023) |
| ADR-0024 | adr/0024-execution-plan-orchestration.md | historical-only, C02-R21, C02-R08, C04-R18 | Accepted (ADR-0024) |
| ADR-0025 | adr/0025-skill-provider-and-restricted-invocation.md | historical-only, C02-R21, C02-R05, C02-R14, C03-R23 | Accepted (ADR-0025) |
| ADR-0026 | adr/0026-bounded-filesystem-inventory-manifests.md | historical-only, C02-R07, C02-R16 | Accepted (ADR-0026) |
| ADR-0027 | adr/0027-governed-recursive-deletion-manifests.md | historical-only, C02-R07, C02-R06, C02-R17, docs/governed-recursive-deletion.md, C05-R09 | Accepted (ADR-0027) |
| ADR-0028 | adr/0028-outbound-web-search-and-fetch.md | historical-only, C02-R18, C04-R17, C02-R01 | Accepted (ADR-0028) |
| ADR-0029 | adr/0029-encrypted-local-vault-and-master-key.md | historical-only, C01-R16, C05-R03, C02-R13 | Accepted (ADR-0029) |
| ADR-0030 | adr/0030-privilege-reducing-multi-agent-delegation.md | C02-R23, C02-R24, C04-R17, C03-R22, C05-R07, C05-R11, C02-R15 | Accepted (ADR-0030) |
| ADR-0031 | adr/0031-per-role-envelope-requirements.md | C02-R24, historical-only, C03-R22, C05-R11 | Accepted (ADR-0031) |
| ADR-0032 | adr/0032-bounded-cross-platform-system-events.md | historical-only, C04-R06, C04-R17, C02-R03, C02-R06, docs/system-events.md, C02-R15, C04-R07, C04-R04, docs/system-maintenance.md, C02-R07, C04-R08 | Accepted (ADR-0032) |
| ADR-0033 | adr/0033-governed-docker-image-build-and-volume-management.md | historical-only, C02-R20, docs/docker-management.md, C02-R06, C04-R17 | Accepted (ADR-0033) |
| ADR-0034 | adr/0034-advanced-process-diagnostics.md | historical-only, C02-R19, C02-R03, C02-R06, C04-R17, docs/process-diagnostics.md | Accepted (ADR-0034) |
| ADR-0035 | adr/0035-sockets-routes-and-network-diagnostics.md | historical-only, C02-R03, C02-R06, docs/network-diagnostics.md, C02-R20, C02-R19, C02-R15 | Accepted (ADR-0035) |
| ADR-0036 | adr/0036-product-neutral-entitlement-applicability-and-execution-time-enforcement.md | C02-R22 | Accepted (ADR-0036) |
| ADR-0037 | adr/0037-safe-local-plugin-lifecycle.md | historical-only, C02-R27, C05-R08, C02-R15, C02-R26 | Accepted (ADR-0037) |
| ADR-0038 | adr/0038-provider-wire-contract.md | C01-R10, historical-only, docs/architecture/reference/provider-adapters-and-service-tools.md, C01-R11, C03-R03 | Accepted (ADR-0038) |
| ADR-0039 | adr/0039-model-call-failure-containment.md | C01-R13, historical-only, C01-R12, C01-R14, docs/operator-configuration.md, C03-R04, C03-R13 | Accepted (ADR-0039) |
| ADR-0040 | adr/0040-resume-state-machine.md | C03-R12, historical-only, C03-R10, C03-R13, docs/architecture/reference/task-store-and-mutation-journal.md, C05-R02, C03-R09, C03-R04, C03-R11, C03-R14 | Accepted (ADR-0040) |
| ADR-0041 | adr/0041-typed-cross-platform-stability-evidence.md | C04-R04, C04-R10, C02-R03, C04-R09, docs/system-stability.md, C04-R05, C04-R07, C02-R01 | Accepted (ADR-0041) |
| ADR-0042 | adr/0042-evidence-reasoning-and-limitation-disclosure.md | C04-R01, historical-only, C04-R02, C04-R11, C04-R12, C04-R03, C04-R04, C04-R13, C04-R15, C04-R14 | Accepted (ADR-0042) |
| ADR-0043 | adr/0043-browser-web-session.md | C05-R04, historical-only, C05-R05, C05-R03, docs/operator-configuration.md, C05-R06, docs/security/threat-model.md | Accepted (ADR-0043) |
| ADR-0044 | adr/0044-request-dependent-delegation-authority-and-operability.md | C02-R25, historical-only, C05-R07, docs/agents/delegation-policy.md, C02-R21, C03-R11, C04-R13, C05-R11 | Accepted (ADR-0044) |
| ADR-0045 | adr/0045-execution-pinned-provider-configuration-and-fallback-chain.md | C01-R17, historical-only, C01-R15, C01-R16, C01-R21, C01-R18, C01-R19, C01-R20 | Accepted (ADR-0045) |
| ADR-0046 | adr/0046-step-scoped-tool-routing-and-transactional-replanning.md | historical-only, C03-R05, C01-R11, C03-R04, C04-R16, C04-R15, C03-R07, C03-R06, C03-R08 | Accepted (ADR-0046) |
| ADR-0047 | adr/0047-bounded-planned-step-argument-correction.md | historical-only, C03-R06, C03-R05 | Accepted (ADR-0047) |
| ADR-0048 | adr/0048-bounded-windows-kernel-dump-analysis.md | historical-only, C02-R20, C04-R09, C02-R19 | Accepted (ADR-0048); Accepted (ADR-0048) - section 10 wording superseded |
| ADR-0049 | adr/0049-unified-prerequisite-readiness-and-system-messages.md | C02-R28, historical-only, C05-R10, docs/operator-configuration.md | Accepted (ADR-0049) |
| ADR-0050 | adr/0050-semantic-planned-step-execution-and-conditional-follow-ups.md | C03#pending-D4, C04#pending-D4 | Proposed (ADR-0050) |
| ADR-0051 | adr/0051-durable-ordinary-task-mutation-intent-journal.md | C03-R15, historical-only, C03-R19, C03-R16, C03-R17, docs/architecture/reference/task-store-and-mutation-journal.md, C03-R18, C03-R20, C03-R21, C05-R02 | Accepted (ADR-0051) |
| ADR-0052 | adr/0052-microsoft-agent-framework-bops-boundary.md | C01-R01, C01-R03, C01-R05, C01-R02, C01-R06, C01-R07, C01-R08, C04-R16, C04-R01, historical-only, C01-R09 | Accepted (ADR-0052) |
| ADR-0053 | adr/0053-maf-convergence-architecture-boundaries.md | C01#pending-D1, historical-only, C05#pending-D5 | Proposed (ADR-0053) |
| ADR-0054 | adr/0054-maf-bplus-convergence-decision.md | C01#pending-D1, C05#pending-D5, historical-only | Proposed (ADR-0054) |
| ADR-0055 | adr/0055-trusted-knowledge-packs-and-provider-isolation.md | C05#pending-D5, historical-only | Proposed (ADR-0055) |
| ADR-0056 | adr/0056-governed-knowledge-read-and-durable-resume.md | C05#pending-D5, historical-only | Proposed (ADR-0056) |
| ADR-0057 | adr/0057-knowledge-discovery-index.md | C05#pending-D5, historical-only, C05-R13, docs/knowledge/local-agent-pilot.md, C02-R12 | Accepted (ADR-0057) - scope limited to M1 development; mechanism depends on Proposed 0055/0056; Accepted (ADR-0057); Accepted (ADR-0057) - M1 development scope only (D-049); the Proposed ADR-0054/0055/0056 are not accepted by it |
| ADR-0058 | adr/0058-local-agent-knowledge-pilot.md | C05#pending-D5, historical-only | Proposed (ADR-0058) |
| ADR-0059 | adr/0059-knowledge-refusal-recovery.md | C05#pending-D5, historical-only | Proposed (ADR-0059) |
| ADR-0060 | adr/0060-planner-facing-tool-parameter-guidance.md | C03-R24, historical-only | Accepted (ADR-0060) |
| ADR-0061 | adr/0061-system-events-source-filter-disclosure.md | C04-R08, historical-only, docs/system-events.md | Accepted (ADR-0061) |
| P1-P7 | 00-project-spec.md | C02-R05, C02-R09, C02-R14, C02-R01, C05-R01, C02-R03, C01-R01 | Accepted |

## 3. Known anomalies and limits

- `docs/benchmarks/bench00-maf-compatibility-report.md` is referenced by historical documents but does not exist in the repository. It is not reconstructed (plan G1-10); it is also an open anomaly for DOC-04.
- ADR-0057 contains a relative link to `know-m1-b-fix01…` that does not resolve from the ADR folder. The ADR is archived unchanged.
- `src/core/bOps.Abstractions/Audit.cs:673` cites `agentic/architecture/adr/0013`, a path that never existed (pre-existing; code is out of scope for DOC-03).
- Rule A12 (`bOps.Abstractions` stays `0.x` until V1.0) and D-012 are `historical-only (expired)` (plan G1-09); no new versioning policy is introduced.
- The seven Proposed ADRs stay Proposed; ADR-0057 and ADR-0060 do not promote the Proposed decisions they refine. Knowledge M1 stays limited by C05-R13 and D-049.
