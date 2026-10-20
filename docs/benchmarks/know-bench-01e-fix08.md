1. Files: KnowledgeBenchmarkExport.cs, KnowledgeBenchmarkExportTests.cs, KnowledgeBenchmarkSecureExportTests.cs and this report; base codex/know-bench-01a at 3a9150e.
2. Boundary: WriteAsync projects result.json and SafeAudit projects audit.jsonl before staging publication; no execution/corpus changes.
3. Reused controls: payload-free audit projection, allowlisted configuration/fingerprints and corpus minimization; reviewed ModelFailureText/ProviderFailures regex redaction, which cannot certify arbitrary prose and is therefore not used as an export guarantee.
4. Non-null final text is replaced with [redacted: untrusted benchmark text withheld]; unapproved tool/exception identifiers and textual audit Stage/$type values receive the same marker; error messages/payloads remain excluded.
5. Export reads TaskState without writing it; tests verify reference identity, serialized state and source audit-event equality; original failures are rethrown unchanged.
6. Schema v2, string/null field types, failure classification and failure-safe publication remain; freeTextRepresentation adds an explicit explanation. A/B share the same projection.
7. Validation: project-only dotnet build --no-restore passed with 0 warnings/errors; dotnet test --no-build --no-restore --filter FullyQualifiedName~KnowledgeBenchmarkSecureExportTests passed exactly 2/2 offline tests (sensitive synthetic data; benign structured diagnostics).
8. Limits: arbitrary benign prose is withheld too; structured status/accounting/usage/tool outcomes remain. Source operational task/audit data intentionally remain unchanged; corpus hashes/provenance/attestations are untouched. No Roslyn AD0001 occurred.
9. Local commit only: fix(benchmarks): withhold untrusted export text; no push or merge.
10. No live LLM, Event Log read, benchmark, full suite/solution build, WSL, subagent, NuGet update, product/configuration/policy/HTTP-timeout change or follow-on task.
