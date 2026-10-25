# MAF CLI A/B benchmark — operator runbook (BOPS-0125)

Measures the real `bops` CLI with the MAF governed turn host **off (A)** and **on (B)** (`Cli:Experimental:MafGovernedTurn`, Contract 01 C01-R22).
Nothing here has been run live. Every step below that talks to a model needs `-AllowLive`, which only you give.

> BENCH-00 measured the **API**, which ignores this switch. Never quote an API measurement as evidence for the CLI MAF host.

> The four-condition extension (A/B plus C/D with the signed Windows Knowledge pack, Qwen3.8 Smart 128K) is in
> [`maf-knowledge-abcd-campaign-runbook-01.md`](maf-knowledge-abcd-campaign-runbook-01.md). A and B keep the meaning described here.

## What the tooling guarantees

- Same build, profile, endpoint, parameters, tools, limits, prompts (the exact BENCH-00 B01/B02/B03), policy and effective configuration in both variants; the configuration fingerprint must match and the report says whether it did.
- Mode proof: the harness reads the mode back from the process (the `EXPERIMENTAL` line on standard error appears iff B). A mismatch stops the campaign.
- Knowledge is forced off. Ambient `Cli__*`, `Agent__*`, `ModelProvider__*`, `Knowledge__*`, `Policy__*`, `Audit__*`, `Memory__*` … variables are removed from the child environment.
- Policy: Read automatic; Low, Medium, High, Critical forbidden. Standard input is closed, so no approval can be given. The CLI hosts one provider: there is no automatic fallback.
- Serial runs, isolated state per run, hard timeouts, budgets, partial evidence kept. Output is private, outside the repository, never uploaded.
- Credentials are redacted from captured output and the whole run directory is scanned for them (a hit stops the campaign).

## 0. One-time set-up (no model involved)

Use the worktree where this tooling lives, or any checkout that contains it.

```powershell
$repo   = 'C:\source\repos\babbubba\bOps-mafab'
$bench  = Join-Path $repo 'scripts\windows\run-maf-cli-benchmark.ps1'
$home2  = Join-Path $env:USERPROFILE 'Documents\bOps-MafBenchmarks'
New-Item -ItemType Directory -Force (Join-Path $home2 'profiles') | Out-Null
Copy-Item (Join-Path $repo 'scripts\windows\maf-cli-benchmark-profile.qwen-local.example.json') (Join-Path $home2 'profiles\qwen-local.json')
$profile = Join-Path $home2 'profiles\qwen-local.json'
```

Edit `qwen-local.json` (outside the repository): `baseUrl` and `model` must name your llama.cpp server and the model it serves (`GET <baseUrl>/models`).
The file holds **no secret**; a provider that needs a key names the environment variable in `apiKeyEnv` (llama.cpp normally needs none).
Start the llama.cpp server yourself, with the settings you want to measure, and leave it unchanged for the whole campaign. Do not enable any fallback or router in front of it.

To keep the privileges the same as BENCH-00 (event logs, stability data), start PowerShell **as Administrator** once and run every step below from it; add `-RequireElevated` to be refused otherwise. Privilege level is recorded in the manifest.

## 1. Estimate (no CLI, no model)

```powershell
& $bench -Estimate -ModelProfile $profile -Scenarios 'B01,B02,B03' -Rounds 1 -TaskTimeoutSeconds 1800 -HistoryDir (Join-Path $env:USERPROFILE 'Documents\bOps-Benchmarks')
```

Reference output on this PC (BENCH-00 history, 13/12/12 runs of B01/B02/B03, **API path, several models: indicative only**):

| Campaign | Runs | Expected tokens | Worst-case tokens | Expected model time | Worst-case duration |
|---|---|---|---|---|---|
| B01 A/B, 1 round (pilot, `-PerRunTokenCap 120000`) | 2 | ≈ 30 k (≈ 15 k per run) | 240 k (2 × 120 k cap) | ≈ 3 min | 30 min (2 × 900 s timeout) |
| B01 A/B, 1 round (shipped cap 350 k) | 2 | ≈ 30 k | 700 k | ≈ 3 min | 1 h |
| B01+B02+B03 A/B, 1 round | 6 | ≈ 460 k (15 k / 126 k / 90 k per run) | 2.1 M | ≈ 0.4 h | 3 h |
| B01+B02+B03 A/B, 3 rounds | 18 | ≈ 1.4 M | 6.3 M | ≈ 1.1 h | 9 h |

Cost: **0 USD** with a local llama.cpp model (no price in the profile). For a remote model fill `pricing` (`promptUsdPerMTok`, `completionUsdPerMTok`); the estimate then prices every token at the higher rate (an upper bound). Worst case = every run uses its whole token cap (`Agent:MaxTotalTokens`, default 350 000) and its whole timeout.
**Limits are enforced, not optional.** `-TokenBudget` is mandatory for a live run and must cover at least one run's cap (otherwise the run is refused before any directory or task is created); it is checked before every run, so the campaign stops between runs when the next one could exceed it. `-PerRunTokenCap` lowers `Agent:MaxTotalTokens` for every run of both variants (recorded in the manifest as `perRunTokenCap`; the same for A and B). `-TaskTimeoutSeconds` kills the CLI process of a run (evidence kept, campaign stops unless `-ContinueOnTimeout`), `-CampaignTimeoutSeconds` bounds the whole campaign and must not be below the task timeout. The manifest also records the SHA-256 of the harness scripts and of the profile.

## 2. Preflight (no real model call)

```powershell
$commit = (git -C $repo rev-parse HEAD).Trim()
& $bench -PreflightOnly -ModelProfile $profile -ExpectedTargetCommit $commit -OutputRoot (Join-Path $home2 "preflight-$(Get-Date -Format yyyyMMdd-HHmmss)")
```

It checks: target commit and clean runtime sources; output outside any Git work tree; profile and authorizations (a non-loopback endpoint fails unless `-AllowRemoteEndpoint`); builds the CLI in Release (the `bops.dll` hash is recorded); the read-only policy; the endpoint (`GET <baseUrl>/models` — a listing, never an inference request; `-SkipEndpointProbe` skips it); and the **mode probe**.
The mode probe runs the real CLI once per variant against a **loopback stub**: no model is contacted, the `EXPERIMENTAL` line must appear for B only. The stub holds the CLI's first model request and the probe **stops the process the moment it arrives** (`stoppedEarly: true` in `preflight.json`): the mode is decided at startup, and a CLI that never sees a model failure leaves no crash record in the Application log of the machine under diagnosis. The probe leaves one throwaway task per variant in `preflight\mode-probe-*\tasks.db`, never in a campaign. Use `-SkipModeProbe` if you want no task of any kind (the mode is then still verified on every real run, just not before).
Exit code 0 = passed; the details are in `preflight.json`.

## 3. Pilot: B01, MAF OFF and ON, one round (live — your go-ahead)

```powershell
& $bench -AllowLive -ModelProfile $profile -ExpectedTargetCommit $commit -Scenarios 'B01' -Variants 'A,B' -Rounds 1 `
    -PerRunTokenCap 120000 -TokenBudget 240000 -TaskTimeoutSeconds 900 -CampaignTimeoutSeconds 2400 `
    -OutputRoot (Join-Path $home2 "pilot-B01-$(Get-Date -Format yyyyMMdd-HHmmss)")
```

Then, before anything else: `& $bench -Verify <that directory>`; open `report\ab-report.md`; read the Safety table (mode verified, mutations 0, approvals 0, audit chain, secret scan); skim `runs\*\stdout.txt` and `run.json`.

## 4. Full first round: B01, B02, B03, A and B (live — only after the pilot evidence was checked)

```powershell
& $bench -AllowLive -ModelProfile $profile -ExpectedTargetCommit $commit -Scenarios 'B01,B02,B03' -Variants 'A,B' -Rounds 1 `
    -TaskTimeoutSeconds 1800 -CampaignTimeoutSeconds 14400 -TokenBudget 2100000 `
    -OutputRoot (Join-Path $home2 "round1-$(Get-Date -Format yyyyMMdd-HHmmss)")
```

## 5. Three rounds per variant (prepared, never started by itself)

Same command with `-Rounds 3` and the budgets of the 18-run row above (for example `-TokenBudget 6300000 -CampaignTimeoutSeconds 32400`). The order alternates by round (A,B / B,A / A,B) to spread host drift over both variants; `-Order fixed` disables that.

## 6. Grade, analyse, verify

`evaluation\R01.json`, `R02.json` … are **blind** sheets: they carry a code, never the variant. For each claim set `verdict` (`supported`, `partially-supported`, `unsupported`, `contradicted`, `hypothesis-flagged`, `limitation-disclosed`), the supporting `evidenceSteps`, and fill `overall` (`diagnosisCorrectness` 0–3, `limitationDisclosure` 0–2, `unsupportedClaims`). Judge against the **acquired evidence** (`evidenceIndex` and the run's `capture\tasks\*\state_json.utf8`), not against what you know of the machine. For B03 grade every causal claim explicitly. Do not open `campaign.json` until you finish.

```powershell
& $bench -Analyze <campaign dir>     # regenerates report\ab-report.json, .csv, .md (pending sheets stay PENDING)
& $bench -Verify  <campaign dir>     # re-hashes the evidence; grading and reports are excluded from the hashes
```

## Results: location and format

`%USERPROFILE%\Documents\bOps-MafBenchmarks\<name>\` (private ACL, outside the repository, not uploaded):

| Path | Content |
|---|---|
| `campaign.json` | manifest: commit, model/endpoint (no credentials), limits, config fingerprints, policy/CLI hashes, environment, planned runs with blind codes |
| `campaign-state.json` | progress, tokens/cost used, stop reason |
| `preflight.json`, `preflight\` | checks and the mode-probe state |
| `runs\NN-Bxx-V-rR\run.json` | per run: timings, exit code, mode proof, host load before/after, environment overlay (non-secret), scrubbed variable names, leak scan |
| `runs\…\capture\`, `audit-full.jsonl` | full task state, model calls, tool output, mutation journal, audit (unredacted evidence) |
| `runs\…\stdout.txt`, `stderr.txt` | process output, credentials redacted |
| `evaluation\Rnn.json` | blind grading sheets |
| `report\ab-report.json / .csv / .md` | comparison; contains counts and ids only, never evidence text |
| `integrity.json` | SHA-256 of all evidence files |

**Do not commit, upload or share these directories**: they contain host data (paths, users, events, logs). Share only the Markdown report after reading it.

## Metrics: what is certain, estimated, heuristic or unavailable

Classes in the report: **C** certain (persisted state, audit, provider usage, process), **E** estimate (characters/4 tokens; cost from a configured price; non-model overhead), **H** heuristic (planned-step coverage, limitation wording, numbers not found in tool output, compaction wording), **Q** qualitative (your grades).

Not available or limited:

- Correctness of the diagnosis: only through your blind grades (Q); `Completed` is a runtime outcome, never a quality statement.
- Importance-weighted fact coverage: there is no golden fact list; `partialSourcesWithRead` is a structural proxy only.
- Provider token usage for a call that reports none: counted in `usageMissingCalls`; the characters/4 estimate is then the only figure.
- Cost for a local model: 0 by construction.
- Context compaction: no dedicated event exists; detected from context-provenance wording (H).
- Resume safety: not exercised live. Mode-switch resume is covered by the offline BOPS-0122 suite.
- MAF internal telemetry: the host adds no tag or event (A6 of BOPS-0122); the mode proof is the `EXPERIMENTAL` line.
- Run-to-run model variance: one round per cell cannot separate a MAF effect from sampling noise; three rounds only bound it. A single round is **exploratory** and the report says so.
- Environment drift: host CPU/memory before and after each run are recorded; a large A/B difference in one scenario may be the machine, not the mode.

## Offline tests

```powershell
python -I -m unittest discover -s "$repo\scripts\windows\tests" -p "test_maf_cli_*.py" -v
```

They use a scripted stand-in for the CLI (a test double, labelled as such in any report it produces) and touch neither a model nor the network beyond loopback.
