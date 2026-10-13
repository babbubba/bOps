# KNOW-BENCH-01C: local system.events corpus

Implemented on `codex/know-bench-01a`, starting at `aa1e1bd`. No real capture,
LLM call, Knowledge pack/signature or A/B benchmark was performed for this task.

`KnowledgeProcess.ComposeCanary` decorates only the explicitly selected native
`WindowsSystemEventsTool` when an internal `CanaryEventsActivation` is supplied.
`KnowledgeHostCanary.Compose` forwards this optional activation. The manifest,
package, risk, platform visibility, schema and ordinary runtime authorization
remain in the existing registry/runner. The model-driven canary still uses its
MAF broker and expectedArguments checks; no MAF session is stored or replaced.
The manual entry point uses the existing `ExecuteExecutionPlanAsync` deterministic
runtime dispatch, which needs no model. This is an existing supported route,
not a new executor. It does not exercise the model planning/broker loop.

Capture requires Enabled and an explicit minimization acknowledgement. The
native implementation runs only after normal runtime validation/policy/authorization
and after a CreateNew reservation succeeds. It accepts only Read requests for
System/Application (or their native default), with native bounded arguments.
Source, text and excludeSources filters are deliberately unsupported to prevent
free-text query metadata from being retained or audited. Invalid event IDs are
rejected before native collection. One capture instance can collect at most once.

Before writing, minimization removes message/sampleMessage, provider/source,
unit, process IDs/names and source/coverage details by setting their values to null.
No substitute records or identifiers are invented. Original counts, severity,
event IDs, timestamps, coverage, completeness and truncation flags are retained.
Output includes a benchmarkCorpus marker identifying the original capture or
synthetic origin and explicitly describing stored rather than fresh observations.
The persisted sanitized output is exactly the output returned by capture/replay;
the unsanitized native output is never persisted. This reduces the diagnostic
content available for a later benchmark and must be accepted by its operator.
Remaining event IDs/timing/counts may still be sensitive. Protect the directory
with local access controls; this format is not encrypted.

The UTF-8 canonical JSON envelope is `{corpus, sha256}`. Object keys sort ordinally;
array order and the output string are preserved. SHA-256 covers every byte of the
canonical corpus, excluding the envelope hash itself. Corpus v1 records schema 2,
manifest parameter contract, native assembly version/module identity, submitted
arguments, effective query and UTC window, mode/limits, capture UTC, source origin,
minimization version and completeness/truncation/status. The snapshot has a 2 MiB
read ceiling. Hash is integrity evidence, not a signature, authenticity assertion
or proof of Windows acquisition; synthetic snapshots are labelled synthetic.

Replay requires a separately pinned expected hash, validates the entire canonical
envelope and reconstructs compatible metadata against the current tool contract.
Exact canonical submitted arguments must match, including omitted vs explicit
defaults. Missing files, invalid JSON, unsupported format/schema, altered hashes,
metadata inconsistency and incompatible requests fail closed. Replay has no call
to the native collector. Independent A/B compositions can use the same path/hash;
each invocation rereads and verifies it under a read-only share, so changes cannot
silently affect a subsequent invocation. Treat this pin as immutable for both runs.

Runtime evidence facts state executionMode=capture/replay and corpusSha256; the
audit summary independently records these, minimization and unattested-local-corpus.
Native operational facts are not retained as fresh replay attestations. The
deterministic entry point also labels its evidence description capture/replay.
No event text/path/exception details are emitted by the adapter or manual test.
Standard runtime argument audit remains unchanged; use only non-sensitive filters.

Only absolute local paths in a pre-existing `.bops-event-corpus` directory with a
`.bops-events.snapshot.json` suffix are accepted. UNC, network drives and reparse
points are refused. Both directory and suffix are ignored by Git, including nested
copies; CreateNew prevents accidental overwrite. Never force-add snapshots. A failed
capture may leave an empty/partial reservation; replay rejects it. Inspect/remove
it manually or select a new filename before retrying; the tool never deletes it.

## Offline verification already performed

Only the test project was built with `--no-restore` (zero warnings/errors), not the
solution. Exactly two filtered offline tests passed:

- Synthetic corpus: canonical serialization/hash validation and two independent
  governed replays return identical sanitized data without a native collection.
  Original synthetic content is not represented as Windows acquisition. Checks
  also cover expectedArguments validation/equality and replay audit attribution.
- Fail closed: missing/malformed/tampered corpus, wrong hash, rehashed unsupported
  format (including an independently pinned unsupported format), incompatible
  arguments, forbidden channel, schema failure, missing opt-in/minimization,
  capture overwrite and a disabled/unavailable tool through governed dispatch.

Zero model-call audit events and no Knowledge admission in either test. Native
capture success, real OS channel access, model-driven A/B and cross-version
compatibility remain untested. No production configuration was modified.

## Manual commands — examples only, not executed

From this worktree, after building this test project, choose an explicit local
directory outside tracked files. The examples use a neutral operator-supplied
`$corpusPath`; its parent must already exist and be named `.bops-event-corpus`.
The fixed manual request is System, raw, five minutes, ten records, 8192 bytes.
Application/default or other bounded requests require an explicit internal caller;
the manual entry point intentionally exposes no arbitrary query/command interface.

```powershell
$env:BOPS_EVENTS_CORPUS_PATH = $corpusPath
$env:BOPS_EVENTS_CORPUS_ACCEPT_MINIMIZATION = '1'
$env:BOPS_EVENTS_CORPUS_MODE = 'capture'
dotnet test tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~KnowledgeEventsCorpusTests.SyntheticCorpus_TwoGovernedReplaysAreIdentical
```

After a successful intentional capture, independently retain/pin the envelope's
sha256 value as `$pinnedSha256` (64 lowercase hexadecimal characters). Do not
derive a new pin from a possibly changed file on every replay.

```powershell
$env:BOPS_EVENTS_CORPUS_PATH = $corpusPath
$env:BOPS_EVENTS_CORPUS_ACCEPT_MINIMIZATION = '1'
$env:BOPS_EVENTS_CORPUS_SHA256 = $pinnedSha256
$env:BOPS_EVENTS_CORPUS_MODE = 'replay'
dotnet test tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~KnowledgeEventsCorpusTests.SyntheticCorpus_TwoGovernedReplaysAreIdentical
```

The manual branch never creates synthetic input. Missing/mismatched snapshots
fail; it never falls back to capture or an LLM. Clear these four environment
variables after the intentional manual operation before ordinary offline testing.
Do not proceed to KNOW-BENCH-01D from this task.
