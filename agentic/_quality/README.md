# Milestone quality measurement

This directory contains the deterministic quality contract for milestone baselines. The manifest
describes normative requirements and expected evidence; `Test-MilestoneQuality.ps1` executes local
build/test evidence, reads TRX outcomes, and projects a JSON report plus Markdown summary.

Quality and efficiency are independent. Packet count, quota, elapsed time, human intervention and
rework are metadata only. A platform test that is skipped or not executed is `PENDING`, never a
quality `PASS`; `-AllowPendingExternal` permits a `PROVISIONAL` local result but never `VERIFIED`.

Run from `repos/bOps`:

```powershell
./scripts/Test-MilestoneQuality.ps1 -TestSelf
./scripts/Test-MilestoneQuality.ps1 -Manifest agentic/_quality/manifests/v1.3-g.json -RunTests -AllowPendingExternal
./scripts/Test-MilestoneQuality.ps1 -Manifest agentic/_quality/manifests/v1.3-g.json -EvidenceDirectory artifacts/quality/v1-3-g -VerifyKnownFailures -AllowPendingExternal
```

`known-failures.json` records only repository-documented pre-existing failures. A failure observed
in the full suite is exempted from milestone regression only after one exact-FQN isolated
verification pass; failed, skipped or infrastructure-broken verification remains a hard failure or
blocked result respectively. Repository health and milestone regression are reported separately.

The automated lifecycle is:

`local report -> PR CI -> main CI -> verified quality PR -> manual merge`.

CI writes platform artifacts and a separate `.ci.json`/`.ci.md` report. A verified report is created
only when all required evidence is complete and the aggregate status is `VERIFIED`; `PROVISIONAL`,
`FAIL` and `BLOCKED` never create verified files. Normal CI is not weakened by the known-failure
registry. The post-merge finalizer runs only for successful `push` CI on `main`, checks out the
trusted CI commit, never executes artifact-provided scripts, and uses least-privilege write access.
It creates a deterministic `quality/<slug>-verified-<short-sha>` branch and opens a non-auto-merged
PR. If repository policy blocks PR creation, the branch and verified artifact remain available for
manual PR creation.

The committed report is intentionally deterministic: no machine name, username, absolute path,
random identifier, or scoring timestamp is used.

Repository source checks used as traceability evidence are static assertions only. They confirm that
expected source patterns are present; they do not establish runtime behavior or semantic correctness.
