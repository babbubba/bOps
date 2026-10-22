# Security rules — compatibility index

> **Non-normative compatibility index.** It contains no rule text and no authority. Binding rules live only in the five current contracts under [`docs/architecture/current/`](../docs/architecture/current/). The original full text is preserved unmodified in [`docs/architecture/archive/`](../docs/architecture/archive/) (provenance only; do not load it unless the operator asks for historical research).

This file used to state the runtime safety rules S1–S11. Those rules, with their full strength and without any weakening, are now the binding rules of [Contract 02 — Tools, safety invariants and authority](../docs/architecture/current/02-tools-safety-authority.md); its last section ("Invariant map (S1–S11)") is the authoritative map. This index only resolves the identifiers still cited by code, tests and documents (S1–S11 appear in comments and in architecture tests).

**Safety requirements are not restated here.** The previous text of this file is in the Git history; read the contract, not this table.

| ID | Topic | Contract rule |
|---|---|---|
| S1 | Generic execution tools | [C02-R19](../docs/architecture/current/02-tools-safety-authority.md#c02-r19--no-generic-execution-tool-fixed-process-invocations) |
| S2 | Argument validation | [C02-R06](../docs/architecture/current/02-tools-safety-authority.md#c02-r06--tool-contract-manifests-validation-before-execution-no-clamping) |
| S3 | Policy decision default | [C02-R05](../docs/architecture/current/02-tools-safety-authority.md#c02-r05--risk-model-and-policy-deterministic-fail-closed-critical-never-executable) |
| S4 | Post-action verification | [C02-R09](../docs/architecture/current/02-tools-safety-authority.md#c02-r09--post-action-verification-fails-closed) |
| S5 | Tool output and external reference data | [C02-R11](../docs/architecture/current/02-tools-safety-authority.md#c02-r11--tool-output-is-data-never-instruction), [C02-R12](../docs/architecture/current/02-tools-safety-authority.md#c02-r12--external-reference-data-is-not-an-instruction) |
| S6 | Secrets and logs | [C02-R13](../docs/architecture/current/02-tools-safety-authority.md#c02-r13--secrets-never-reach-a-log-secret-resolution-and-the-encrypted-vault) |
| S7 | Timeouts | [C02-R10](../docs/architecture/current/02-tools-safety-authority.md#c02-r10--every-action-runs-under-a-timeout) |
| S8 | In-process package trust | [C02-R15](../docs/architecture/current/02-tools-safety-authority.md#c02-r15--in-process-packages-are-trusted-code-least-privilege) |
| S9 | Audit completeness | [C02-R14](../docs/architecture/current/02-tools-safety-authority.md#c02-r14--audit-is-complete-local-append-only-and-tamper-evident) |
| S10 | Host process privilege | [C02-R15](../docs/architecture/current/02-tools-safety-authority.md#c02-r15--in-process-packages-are-trusted-code-least-privilege) |
| S11 | Path policy timing | [C02-R16](../docs/architecture/current/02-tools-safety-authority.md#c02-r16--path-policy-is-checked-immediately-before-the-operation) |

Related: the external-data amendment of S5 is [C02-R12](../docs/architecture/current/02-tools-safety-authority.md#c02-r12--external-reference-data-is-not-an-instruction); its development-scope Knowledge guarantees are [C05-R13](../docs/architecture/current/05-interfaces-knowledge-and-hosts.md#c05-r13--knowledge-discovery-m1-guarantees-approved-for-m1-development-and-testing).
