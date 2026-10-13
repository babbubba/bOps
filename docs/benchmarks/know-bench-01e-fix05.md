# KNOW-BENCH-01E-FIX05

The native corpus adapter produces Captured only after a successful WindowsSystemEventsTool invocation. File replay calls CanaryEventsCorpus.Read followed by Provenance without local acquisition authority, so even a valid acquisition receipt becomes Unverified. This remains intentional.

TrustedEventsCapture is a test-only, single-use process-local session. Capture runs through the existing runner execution-plan path, registry, visibility, policy, authorization, dispatch and persisted audit. The session releases replay only after matching the successful capture audit. Its private immutable corpus and provenance strings are never imported to acquire trust. Parsing those strings during replay only materializes a fresh view for SHA-256 and argument checks; it does not reconstruct acquisition authority from JSON. Only the fixed native source can select Captured; the controlled test source always selects Synthetic.

Each condition has one contextual tool invocation bound to its task and the existing tenant-a actor. A uses no Knowledge provider, pack, pin or grant. B requires the existing signed pack, exact manifest pin and authorized grant, with normal admission and read governance. The corpus enters the model context only through the authorized system.events result. Both conditions use the same minimized output and SHA-256; neither dispatches the source again. Disposal revokes the session. Imported files, casts of imported objects and JSON claims cannot create a replay capability.

result.json records the shared corpus hash, acquisition receipt and controlledCaptureId, with an explicit process-local provenance scope. These exported claims are historical observations, not persistent attestations; importing them grants no authority. Native capture audit is process-local to the temporary test world; no new durable signature, PKI or portable verification mechanism is introduced.

Exactly two offline tests passed (TrustedEventsCaptureTests): synthetic governed capture followed by identical A/B replay and export; imported native receipt, incompatible request and adversarial private-memory mutation rejection. Zero model requests or native Windows reads. The native provenance calculation was checked with controlled metadata; the shared handoff was exercised with a Synthetic source. This does not demonstrate Windows acquisition or live A/B completion. Only the selected test project and its dependencies were compiled, with --no-restore; no solution build or full suite.

## Future manual integrated command — NOT executed

Use a clean pinned checkout, an existing empty result directory and the previously prepared signed pack. Supply all existing Qwen/provider configuration, shared metadata pin, exact commit, verified-settings acknowledgement, B manifest pin and B grant authorization. In the pinned shared metadata set corpusSha256 to the literal "single-run-in-process": the actual unpredictable capture hash is observed after capture and exported identically for A/B. The rest of the existing metadata validation remains in force. Clear BOPS_RUN_KNOW_BENCH_01D, BOPS_EVENTS_CORPUS_MODE, BOPS_EVENTS_CORPUS_PATH, BOPS_EVENTS_CORPUS_SHA256 and BOPS_KNOW_BENCH_SIGN. The integrated entry point selects B's Knowledge inputs only for B; A's composition receives none.

```powershell
$env:BOPS_RUN_KNOW_BENCH_CAPTURE_AB = '1'
$env:BOPS_EVENTS_CORPUS_ACCEPT_MINIMIZATION = '1'
dotnet test tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj --no-restore --filter 'FullyQualifiedName=bOps.AgentFramework.Tests.KnowledgeBenchmarkLiveTests.CaptureThenAB' --verbosity minimal
```

This command intentionally performs native capture and real model A/B when explicitly enabled. It was not run in FIX05. No Qwen warm-up, server introspection or new metrics are added. Existing server settings remain operator-verified. Configuration failures or unsuccessful A stop the integrated sequence; exported failures retain the existing exporter behavior.
