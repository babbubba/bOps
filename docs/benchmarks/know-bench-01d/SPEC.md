# KNOW-BENCH-01D specification (preparation only)

Base: `codex/know-bench-01a`, `88aebdf`. No live benchmark, real event capture,
provider request, server configuration change or historical-result edit.

## Signed delivery

`signed-pack/acme.windows-event-runbooks/1.0.0` contains exactly two payload files,
the existing manifest and RSA-PSS/SHA-256 signature envelope. Kind is Knowledge.
`test-trust.json` contains only the public test key. Content hash:
`ef8bd50ece5aee5930158bba9c1dea7e44b49d5f35285d13a1f26ff4e6ba065d`.
It covers procedures for System/Application, 41, 6008, application/.NET errors,
WHEA, chronology, partial evidence and necessary follow-up. No corpus conclusion.

The isolated `KnowledgeBenchmark.SignDelivery` reuses TestPack/TestPublisher,
KnowledgePackFormat and their manifest hashing/signing. Private RSA material lives
only in test-process memory; it is never exported. This delivery was produced by
setting `BOPS_KNOW_BENCH_SIGN=1` for the first offline check. Repeating signing
refuses an existing directory. For a deliberate new test delivery, archive it
manually first, then sign and review the new public artifacts and hashes; changing
a key invalidates the old signature's trust relationship. Ordinary tests and B
load the committed signed bytes through DirectoryKnowledgePackSource and the
existing trust store, verifier, admission, tenant snapshot and read bridge. No
production signer or security contract was added.

## A/B contract

Both conditions use Qwen3.6-35B, the exact UTF-8 scenario from 01A, its original
bounded arguments (raw, 1440 minutes, warning, 500 records, 65536 output bytes),
ActorA/tenant-a, the same native system.events manifest and governed replay.
Runtime options, operational policy, registry surface, authorization and the
five-call cap are identical. Available operational tools and their schemas are
compared after the broker projection. The sole extra capability in B is the
existing runtime.knowledge_read; its schema necessarily cannot be present in A.
If “identical tool visibility” meant including that capability, it would contradict
the required intervention. No operational capability changes between conditions.

A uses withHost:false, empty source/grants/bindings/trust and removes the test
provider registration before composition. No snapshot is materialized. Offline
checks inspect the real projected ModelRequests, the registry and audit, including
an attempted absent Knowledge call. B registers only tenant-a and the one exact
Knowledge grant/reference; its snapshot binding and hashes are explicit. The
source contains only this delivery. Body paragraphs are absent from all A context
and pre-read B context; successful B content arrives as a Tool turn with Knowledge
provenance, never as System/Developer instructions. No diagnostic body enters the
goal, tool arguments, scripted responses or standing prompt. The existing common
system.events manifest already has diagnostic guidance, shared by both arms.

Each arm pins the same absolute local corpus path and independently retained
64-character lowercase SHA-256. The envelope is revalidated at preflight and again
on each governed tool replay; arguments must exactly match. No capture or synthetic
fallback exists in the future A/B entrypoints. A corrupted/incompatible replay can
return only a refused tool result. No real corpus was created or pinned here. The
offline corpus has explicit synthetic origin, minimized content and partial
coverage; its hash is computed once and shared by A/B, and identical tool evidence
and file bytes are asserted. Treat real-capture hashes as integrity, not authenticity.

## New Qwen8192 baseline and execution metadata

BENCH-00 with n-predict=4096 is an immutable historical baseline. Preserve all its
results, timings and metadata. Define a distinct `Qwen8192` baseline for future
measures: Qwen3.6-35B, n-predict=8192, reasoning enabled. The user's server change
is recorded as supplied information, not verified by a provider call here.
Never pool 4096/8192 or different bOps/runtime revisions as equivalent results.
A/B within Qwen8192 must share the same effective model, generation and runtime
configuration, not just a display name. Comparison across revisions is a separately
labelled experiment; retain original artifacts.

Before either future arm, prepare ONE operator-verified immutable UTF-8 JSON
metadata file outside tracked files. Both arms require its same independently
pinned SHA-256. Mandatory fields: baseline="Qwen8192", llamaCppVersion (release and
commit/build), model="Qwen3.6-35B", modelFileSha256, quantization, nPredict=8192,
ctxSize (effective per slot), reasoning=true, sampling (all effective sampler
settings/order, seed and defaults), bopsCommit (full git SHA), corpusSha256,
providerProfileSha256, runtimeLimits and retryPolicy. Record backend/build flags,
chat template, concurrency, cold/warm loading state and request overrides as well.
Unknown effective settings are a preflight blocker, not an assumed default.
The common corpus hash, pinned metadata, exact model, clean tracked checkout and
actual Git HEAD are checked. Server metadata requires the operator attestation
`BOPS_KNOW_BENCH_SETTINGS_VERIFIED=1`; this test host does not introspect server
settings or prove that they remain unchanged. Reverify them for each arm and
invalidate the pair if the server changes. This is a residual manual dependency,
not a modification of the security contracts.

providerProfileSha256 hashes UTF-8 System.Text.Json bytes of the non-secret
projection `{Provider,BaseUrl,Model,SupportsNativeToolCalling,RequestTimeout}`
(in that order), read through the existing local configuration helper. No key or
Vault material is serialized. Record the default AgentRunnerOptions without
changing them: runtime token/step/time/retry limits remain the existing values;
Knowledge reads retain their existing 4/4/8000 limits; both arms cap actual provider
invocations at five, including planning/retry. Transport timeout remains 120s.
Missing settings are never repaired by increasing a budget.

Future artifacts go into explicit pre-existing BOPS_KNOW_BENCH_RESULTS/A or /B;
an existing condition directory is refused. The metadata copy, final observation,
accounting, model-attempt outcomes/durations and audit are exported before world
disposal, including a failed loop. KnowledgeUsed=false invalidates the B intervention.
Keep corpus and results protected as local diagnostic artifacts.

Timing fields must distinguish model loading, generation, timeout and retry.
`totalBrokerLoopMs` measures the complete loop; model-attempt DurationMs is an
aggregate that can include server loading/transport/retry, not generation time.
The export leaves modelLoadMs/generationMs null until independent server telemetry
can establish them; never use null as zero. Preserve per-attempt outcome, failure
kind and model/provider attempt number; record timeout duration and every retry's
wait/attempt timing separately from server loading/generation in companion telemetry.
Cold and warm model runs cannot be treated as equivalent. No timing was measured
against a server in this preparation.

## Offline verification

Only the test project was built with --no-restore; no solution build or full suite.
Two distinct targeted checks pass: SameReplay_OnlyAuthorizedBReceivesKnowledgeThroughBroker
and MissingGrantRevocationTenantTamperingAndIncompatibleCorpus_FailClosed.
They use an in-memory provider probe after the real MAF context projection, not an
LLM. Real provider calls, Windows collection, diagnostic quality and live entrypoints
are untested. The first check was repeated after fixing a test-host registration
error; no additional test cases/suites were run. Revocation, missing grant, tenant
change, incorrect pack bytes, altered snapshot, wrong corpus hash and incompatible
request refuse access. These are isolation tests, not benchmark measurements.

## Manual future capture and A/B commands — NOT EXECUTED

The 01C manual capture command is fixed to a different five-minute System-only
request and cannot feed this pair. For a useful A/B corpus, an explicit internal
caller must compose withWindowsSystemEvents:true and CanaryEventsActivation
(Enabled:true, Capture:true, Path:operatorPath, ExpectedHash:null,
AcceptMinimization:true), withHost:false, then call the existing
ExecuteExecutionPlanAsync for the exact KnowledgeBenchmark.Arguments above.
Use a pre-existing absolute local `.bops-event-corpus` directory and fresh
`.bops-events.snapshot.json` filename. Capture once under ordinary runtime
permissions with deliberate minimization acknowledgement; inspect coverage, protect
the file, retain its envelope hash independently, and never force-add it to Git.
No native capture was performed here; the adapter reserves CreateNew before
collection. A failed reservation/file requires manual inspection/new filename.

Build the test project manually before a future run. Fill the shared path/hash,
metadata path/hash, commit, verified-settings acknowledgement and result directory
in one unchanged environment (or identically in separate sessions). In particular:
`BOPS_EVENTS_CORPUS_PATH`, `BOPS_EVENTS_CORPUS_SHA256`,
`BOPS_EVENTS_CORPUS_ACCEPT_MINIMIZATION=1`, `BOPS_KNOW_BENCH_METADATA`,
`BOPS_KNOW_BENCH_METADATA_SHA256`, `BOPS_KNOW_BENCH_COMMIT`,
`BOPS_KNOW_BENCH_SETTINGS_VERIFIED=1`, `BOPS_KNOW_BENCH_RESULTS`.
Do not set BOPS_EVENTS_CORPUS_MODE or the old live-canary switch.

A (operator only):
```powershell
$env:BOPS_RUN_KNOW_BENCH_01D = 'A'
try { dotnet test tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName=bOps.AgentFramework.Tests.KnowledgeBenchmarkLiveTests.WithoutKnowledge'; if ($LASTEXITCODE -ne 0) { throw 'A failed' } } finally { Remove-Item Env:BOPS_RUN_KNOW_BENCH_01D -ErrorAction SilentlyContinue }
```
B (operator only):
```powershell
$env:BOPS_RUN_KNOW_BENCH_01D = 'B'
try { dotnet test tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName=bOps.AgentFramework.Tests.KnowledgeBenchmarkLiveTests.WithKnowledge'; if ($LASTEXITCODE -ne 0) { throw 'B failed' } } finally { Remove-Item Env:BOPS_RUN_KNOW_BENCH_01D -ErrorAction SilentlyContinue }
```
Clear the shared switches after use. Do not automatically start KNOW-BENCH-01E.
