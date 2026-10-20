# KNOW-BENCH-01E-FIX03 — Corpus provenance and replay integrity

Scope: only the 01C test capture/replay adapter and 01E benchmark entry/export
points; base `8615017`. No operational preflight or FIX04 is implemented.

## Available evidence and limits

The existing corpus records the minimized output, submitted bounded arguments,
effective query/window, UTC recording time, schema/parameter contract, native
assembly version/module ID, completeness, truncation, status and canonical SHA-256.
Replay checks the separately pinned hash, exact envelope bytes and reconstructed
metadata against the current tool contract at every invocation. These are integrity
and compatibility checks; an attacker can rehash an artificial corpus and supply
its matching pin. Neither the pin nor the Knowledge pack signature proves Windows
acquisition. Knowledge admission and corpus provenance remain separate guarantees.

The successful native capture branch additionally records a random capture ID,
the actual invoked tool type, SHA-256 of its manifest and parameter schema, and
only OS family/architecture for the source environment. No hostname, username,
machine ID, path or environment variable is added. Its audit reference is a
correlation selector on `ToolCallAuditEvent.Summary.corpusProvenance.captureId`;
the governed runtime writes the ordinary audit event after tool execution. This
reference is not a signed receipt and does not prove successful audit persistence.
The capture ID, invocation facts and metadata are generated after the actual
native call; the corpus hash covers the receipt and all existing corpus metadata.

## Classification and enforcement

- **Synthetic**: an artificial test corpus declares synthetic origin. This label
  never claims Windows acquisition; the replay output, facts and audit retain it.
- **Captured**: only the successful native invocation in the governed adapter can
  emit this classification. It means locally observed execution, without a
  cryptographic attestation. The persisted capture metadata remains self-asserted
  to a later importer, even if structurally consistent and independently pinned.
- **Unverified**: imported `windows-capture` and explicitly unverified corpora.
  Replay replaces the output's origin claim with `unverified`, and records the
  resolved classification in facts/audit. Unknown/invalid origin values fail closed.

There is no independent persistent acquisition verifier in this infrastructure.
Consequently **all disk-imported captures are Unverified and the real A/B entry
points refuse them**, as well as Synthetic corpora, before creating a provider or
making a model call. A capture-shaped JSON object cannot enable the real benchmark.
This deliberately leaves real persisted-capture A/B execution unavailable until
an independently verifiable acquisition capability exists; no keys, PKI,
process-local trust registry or operator JSON attestation are substituted for it.

Offline A/B preparation pins the same content and resolved provenance; each replay
rechecks both and has no native fallback. Identical corpus hashes include identical
receipt metadata; classification is deterministic for imports. Existing visibility,
authorization, argument validation, minimization and audit dispatch are preserved.
Export schema v2 receives only an additive `corpusProvenance` member from the
validated corpus, or an explicit Unverified/not-validated default on earlier failure.
Captured provenance never comes from the shared operator configuration file.
The evidence payload/window remain stored observations, with origin corrected on
Unverified replay. Existing v1 snapshots without receipts remain readable and
cannot gain Captured status.

## Offline verification

Only the test project is built with `--no-restore`; no solution build or upgrades.
Exactly two filtered tests in `KnowledgeCorpusProvenanceTests` use artificial data:
1. Actual A/B compositions with deterministic governed execution plans verify
   identical output, SHA-256, Synthetic provenance and unchanged corpus bytes.
   No model method is invoked, including for condition B.
2. A coherent rehashed fake capture with every receipt field remains Unverified
   and is refused for a real benchmark. Wrong pins, rehashed inconsistent
   completeness and mutation after composition fail closed through replay.

Native capture, Windows Event Log access, live A/B and LLM calls are not exercised.
No push, merge, appsettings or product runtime/policy changes are part of this task.

Result: test-project build passed with zero warnings/errors; the filtered run
executed exactly two tests, both passed. `git diff --check` is clean.
