# Private fixtures — what is and is not reproducible from a clean clone

The Qwen live baseline and the replay harnesses (`tests/bOps.QwenLiveBaseline`, `tests/bOps.ReplayFixtureCapture`, and the campaign
scripts under `docs/benchmarks/qwen-live-baseline-01/` and `docs/benchmarks/replay-default-equivalence-01/`) replay tool results that
were captured from a real Windows workstation. Those captures (`qwen-live-baseline-01/fixtures/`), the raw per-run results and final
texts of the campaigns, and the server properties files contain machine, account and network identifiers, so they are **not
distributed** with the repository.

Consequences on a clean clone:

- The harness projects build, but the campaigns that load `fixtures/` (QWEN-LIVE-BASELINE-01, REPLAY-DEFAULT-EQUIVALENCE-01, the
  T02 replays) cannot be run, and `checks.py` / `analyze.py` need those fixtures and the raw run directories.
- `CampaignFixtureTests` and `RealShapeTests`, which assert against the real captured fixtures, are not part of the repository; the
  other harness tests use synthetic data only.
- The published reports and the summaries under `results/` (metrics, manifests, scenario statistics) are kept as the record of what
  was measured. Identifiers inside them are redacted (`<domain>\<operator>`, `<llm-host>`, `S-1-5-21-<redacted>`); the measurements
  themselves are unchanged. The hashes in the manifests refer to the private originals.
- The scripted baselines (PA-1 goldens, AE-1) do not depend on these fixtures and run anywhere.

To reproduce a campaign, capture fresh fixtures on your own machine with `bOps.ReplayFixtureCapture` and review them for sensitive
data first; never commit raw captures.
