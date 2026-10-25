"""Offline tests of the MAF CLI A/B benchmark (BOPS-0125).

No bOps process, no model, no provider and no network beyond a loopback stub: the CLI is replaced by fake_maf_cli.py (a test double).
Run: python -m unittest discover -s scripts/windows/tests -p "test_maf_cli_*.py" -v
"""
import contextlib
import http.server
import importlib.util
import io
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import zipfile
from unittest.mock import patch

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import maf_cli_analysis as an  # noqa: E402
import maf_cli_benchmark as mcb  # noqa: E402

FAKE = HERE / "fake_maf_cli.py"
REPO = HERE.parents[2]
SENTINEL = "SENTINEL-HOST-9F3A"
KEY_VALUE = "sk-test-0123456789-abcdef"


def call(duration_ms=1000, prompt=100, completion=20, finish="stop", attempt=1, provider_attempt=1, fallback=0, outcome=0, usage=True, cost=None):
    return {"Provider": "LlamaCpp", "RequestedModel": "m", "DurationMs": duration_ms, "Outcome": outcome, "FinishReason": finish,
            "Usage": {"PromptTokens": prompt, "CompletionTokens": completion, "EstimatedCostUsd": cost} if usage else None,
            "ModelAttempt": attempt, "ProviderAttempt": provider_attempt, "FallbackOrdinal": fallback, "PayloadTruncated": False,
            "RequestJson": "x" * 400, "ResponseJson": "y" * 400}


def make_state(status=1, tokens=1200, answer=f"La CPU e al 12%. {SENTINEL}. Alcuni dati sono parziali e non disponibili.",
               completeness=1, calls=None, extra_steps=None, replans=0, terminal_kind=0):
    calls = calls if calls is not None else [call(), call()]
    steps = [{"Index": 0, "ToolCall": {"ToolName": "system.cpu", "Arguments": {}}, "PlannedStepIndex": 0, "PlanRevision": replans,
              "Result": {"Outcome": 0, "Output": f"cpu 12 percent host {SENTINEL}", "Completeness": completeness, "Facts": [{"Type": "cpu", "Key": "load"}]},
              "ModelCalls": calls[:1]}]
    steps += extra_steps or []
    steps.append({"Index": len(steps), "ToolCall": None, "Result": None, "Observation": answer, "ModelCalls": calls[1:]})
    plans = [{"Revision": r, "Steps": [{"Index": 0}, {"Index": 1}], "ModelCalls": []} for r in range(replans + 1)]
    return {"Id": "11111111-1111-1111-1111-111111111111", "Status": status, "Steps": steps, "Plans": plans,
            "Accounting": {"TokensUsed": tokens, "LifetimeSteps": len(steps), "LifetimeReplans": replans}, "TerminalReason": {"Kind": terminal_kind}}


def audit_events(model_calls=2, tools=1, reads=(), approvals=0, risky=False):
    events = [{"eventType": "modelCall", "Outcome": 0} for _ in range(model_calls)]
    events += [{"eventType": "toolCall", "Tool": "system.cpu", "Risk": 1 if risky else 0, "Authorization": 0, "Duration": "00:00:01.5"} for _ in range(tools)]
    events += [{"eventType": "evidenceRead", "EvidenceId": e, "Offset": o, "RequestedLength": 100, "ResultCode": r, "StepIndex": 0} for e, o, r in reads]
    events += [{"eventType": "approval", "Approved": True} for _ in range(approvals)]
    return events


def with_audit(state, **kwargs):
    return dict(state, _audit=audit_events(**kwargs))


class Base(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="mafbench ")
        self.addCleanup(self.temp.cleanup)
        self.tmp = pathlib.Path(self.temp.name)
        for target, replacement in (("secure_dir", lambda p: None),
                                    ("host_snapshot", lambda *a, **k: {"capturedAtUtc": "t", "cpuBusyPercent": 5.0, "memoryAvailableMb": 8000}),
                                    ("tool_version", lambda *a, **k: None)):
            patcher = patch.object(mcb, target, replacement)
            patcher.start()
            self.addCleanup(patcher.stop)
        env = patch.dict(os.environ, {"Cli__Experimental__MafGovernedTurn": "true", "Agent__MaxSteps": "1", "Knowledge__Enabled": "true",
                                      "TEST_BENCH_KEY": KEY_VALUE}, clear=False)
        env.start()
        self.addCleanup(env.stop)

    def profile(self, **overrides):
        data = {"profileId": "test", "provider": "LlamaCpp", "baseUrl": "http://127.0.0.1:9/v1", "model": "m", "agent": {"MaxTotalTokens": 10000}}
        data.update(overrides)
        path = self.tmp / "profile.json"
        path.write_text(json.dumps(data), encoding="utf-8")
        return path

    def script(self, **content):
        path = self.tmp / "script.json"
        path.write_text(json.dumps(content), encoding="utf-8")
        return path

    def main(self, *args):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = mcb.main([str(a) for a in args])
        return code, out.getvalue(), err.getvalue()

    def double(self, script_path):
        return ["--test-double", "--cli-command-json", json.dumps([sys.executable, str(FAKE), "--script", str(script_path)])]

    def run_campaign(self, script_path, *extra, profile=None, scenarios="B01", output=None):
        output = output or self.tmp / "out"
        if "--token-budget" not in extra:
            extra = (*extra, "--token-budget", 1_000_000)
        return output, self.main("run", "--model-profile", profile or self.profile(), "--output-root", output, "--scenarios", scenarios,
                                 "--skip-endpoint-probe", *self.double(script_path), *extra)


class ProfileTests(Base):
    def test_profile_rejects_secrets_and_foreign_keys(self):
        with self.assertRaises(mcb.BenchmarkError):
            mcb.load_profile(self.profile(apiKey="sk-live"))
        with self.assertRaises(mcb.BenchmarkError):
            mcb.load_profile(self.profile(agent={"Summarization": "x"}))
        with self.assertRaises(mcb.BenchmarkError):
            mcb.load_profile(self.profile(extraConfiguration={"Knowledge:Enabled": "true"}))
        with self.assertRaises(mcb.BenchmarkError):
            mcb.load_profile(self.profile(baseUrl="http://user:pw@127.0.0.1/v1"))
        self.assertTrue(mcb.load_profile(self.profile(baseUrl="https://openrouter.ai/api/v1"))["remote"])
        self.assertFalse(mcb.load_profile(self.profile())["remote"])

    def test_overlay_differs_between_variants_only_by_the_maf_key(self):
        profile = mcb.load_profile(self.profile(apiKeyEnv="TEST_BENCH_KEY"))
        a, b = (mcb.build_overlay(profile, v, pathlib.Path("W")) for v in "AB")
        self.assertEqual({k for k in a if a[k] != b[k]}, {mcb.MAF_KEY})
        self.assertEqual((a[mcb.MAF_KEY], b[mcb.MAF_KEY]), ("false", "true"))
        self.assertEqual(a["Knowledge__Enabled"], "false")
        self.assertNotIn(KEY_VALUE, json.dumps(a))
        self.assertEqual(mcb.config_fingerprint(a, "p", "s", "c"), mcb.config_fingerprint(b, "p", "s", "c"))

    def test_child_environment_scrubs_ambient_configuration(self):
        profile = mcb.load_profile(self.profile())
        overlay = mcb.build_overlay(profile, "A", pathlib.Path("W"))
        env, scrubbed = mcb.child_environment(os.environ, overlay, None)
        self.assertEqual(env[mcb.MAF_KEY], "false")
        self.assertEqual(env["Knowledge__Enabled"], "false")
        self.assertNotEqual(env.get("Agent__MaxSteps"), "1")
        self.assertIn("AGENT__MAXSTEPS", [s.upper() for s in scrubbed])
        self.assertNotIn(KEY_VALUE, scrubbed)


class PreflightTests(Base):
    def test_preflight_passes_with_the_test_double_and_sends_no_task_to_a_model(self):
        out = self.tmp / "pf"
        code, stdout, _ = self.main("preflight", "--model-profile", self.profile(), "--output-root", out, "--skip-endpoint-probe",
                                    *self.double(self.script()))
        self.assertEqual(code, 0, stdout)
        document = json.loads((out / "preflight.json").read_text())
        self.assertTrue(document["ok"] and document["noTaskSentToAModel"] and document["noRealModelCall"] and document["throwawayProbeTasksAgainstLoopbackStub"])
        probe = next(c for c in document["checks"] if c["name"] == "mode-probe")
        self.assertEqual({v: p["markerSeen"] for v, p in probe["probes"].items()}, {"A": False, "B": True})
        self.assertEqual(list(out.rglob("tasks.db")), [])  # the probe records nothing in a task store

    def test_the_mode_probe_stops_the_cli_at_its_first_model_request_so_it_never_sees_a_failure(self):
        # The pilots found the probe leaving crash records in the Application log of the diagnosed machine: the CLI reached its first
        # model call, got HTTP 400 and failed. The stub now holds that request (60 s) and the probe kills the process when it arrives.
        out = self.tmp / "pf"
        started = time.monotonic()
        code, stdout, _ = self.main("preflight", "--model-profile", self.profile(), "--output-root", out, "--skip-endpoint-probe",
                                    *self.double(self.script(probeConnect=True)))
        self.assertEqual(code, 0, stdout)
        self.assertLess(time.monotonic() - started, 40)
        probe = next(c for c in json.loads((out / "preflight.json").read_text())["checks"] if c["name"] == "mode-probe")
        self.assertEqual(probe["stubRequests"], 2)
        self.assertEqual({v: (p["stoppedEarly"], p["status"], p["markerSeen"]) for v, p in probe["probes"].items()},
                         {"A": (True, "pass", False), "B": (True, "pass", True)})

    def test_a_probe_that_ends_by_itself_is_not_reported_as_stopped(self):
        out = self.tmp / "pf"
        code, _, _ = self.main("preflight", "--model-profile", self.profile(), "--output-root", out, "--skip-endpoint-probe",
                               *self.double(self.script()))
        self.assertEqual(code, 0)
        probe = next(c for c in json.loads((out / "preflight.json").read_text())["checks"] if c["name"] == "mode-probe")
        self.assertFalse(any(p["stoppedEarly"] for p in probe["probes"].values()))

    def test_preflight_fails_when_mode_b_does_not_report_itself(self):
        code, stdout, _ = self.main("preflight", "--model-profile", self.profile(), "--output-root", self.tmp / "pf", "--skip-endpoint-probe",
                                    *self.double(self.script(omitMarker={"B": True})))
        self.assertEqual(code, 2)
        self.assertIn("[FAIL", stdout)

    def test_remote_endpoint_needs_a_separate_authorization(self):
        remote = self.profile(baseUrl="https://openrouter.ai/api/v1", apiKeyEnv="TEST_BENCH_KEY")
        args = ["preflight", "--model-profile", remote, "--skip-endpoint-probe", "--skip-mode-probe", *self.double(self.script())]
        self.assertEqual(self.main(*args, "--output-root", self.tmp / "a")[0], 2)
        self.assertEqual(self.main(*args, "--output-root", self.tmp / "b", "--allow-remote-endpoint")[0], 0)

    def test_missing_api_key_variable_fails_without_printing_a_value(self):
        with patch.dict(os.environ, {}, clear=False):
            os.environ.pop("TEST_BENCH_KEY", None)
            code, stdout, stderr = self.main("preflight", "--model-profile", self.profile(apiKeyEnv="TEST_BENCH_KEY"), "--output-root", self.tmp / "pf",
                                             "--skip-endpoint-probe", "--skip-mode-probe", *self.double(self.script()))
        self.assertEqual(code, 2)
        self.assertNotIn(KEY_VALUE, stdout + stderr)

    def test_output_inside_a_git_work_tree_is_refused(self):
        inside = REPO / ".bench-test-output"
        self.addCleanup(shutil.rmtree, inside, True)
        code, _, stderr = self.main("preflight", "--model-profile", self.profile(), "--output-root", inside, *self.double(self.script()))
        self.assertEqual(code, 2)
        self.assertFalse(inside.exists())

    def test_endpoint_probe_lists_models_and_never_posts(self):
        seen = []

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                seen.append(("GET", self.path))
                body = b'{"data":[{"id":"m"}]}'
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def do_POST(self):
                seen.append(("POST", self.path))
                self.send_response(500)
                self.end_headers()

            def log_message(self, *a):
                pass

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        profile = mcb.load_profile(self.profile(baseUrl=f"http://127.0.0.1:{server.server_address[1]}/v1"))
        result = mcb.probe_endpoint(profile, None)
        self.assertEqual((result["status"], result["modelListed"]), ("pass", True))
        self.assertEqual(seen, [("GET", "/v1/models")])

    def test_live_run_requires_allow_live(self):
        code, _, stderr = self.main("run", "--model-profile", self.profile(), "--output-root", self.tmp / "o", "--cli-command-json", "[]")
        self.assertEqual(code, 3)
        self.assertFalse((self.tmp / "o").exists())


class BudgetTests(Base):
    def run_main(self, *extra, profile=None):
        return self.main("run", "--model-profile", profile or self.profile(), "--output-root", self.tmp / "o", "--scenarios", "B01",
                         "--skip-endpoint-probe", *self.double(self.script(state=make_state())), "--allow-live", *extra)

    def test_live_run_refuses_without_a_token_budget(self):
        code, _, stderr = self.run_main()
        self.assertEqual(code, 2)
        self.assertIn("token-budget", stderr)
        self.assertFalse((self.tmp / "o").exists())

    def test_live_run_refuses_a_budget_below_one_run_cap(self):
        code, _, _ = self.run_main("--token-budget", 5000)  # profile cap is 10000
        self.assertEqual(code, 2)
        self.assertFalse((self.tmp / "o").exists())

    def test_per_run_token_cap_overrides_the_profile_and_is_recorded(self):
        code, _, _ = self.run_main("--token-budget", 40000, "--per-run-token-cap", 20000)
        self.assertEqual(code, 0)
        manifest = json.loads((self.tmp / "o" / "campaign.json").read_text())
        self.assertEqual((manifest["limits"]["perRunTokenCap"], manifest["limits"]["campaignTokenBudget"]), (20000, 40000))
        self.assertEqual(manifest["agentOverrides"]["MaxTotalTokens"], 20000)
        self.assertEqual(set(manifest["harnessSha256"]), {"maf_cli_benchmark.py", "maf_cli_analysis.py", "live_benchmark.py"})
        self.assertEqual(len(manifest["profileSha256"]), 64)

    def test_task_timeout_must_fit_the_campaign_timeout(self):
        code, _, _ = self.run_main("--token-budget", 40000, "--task-timeout", 600, "--campaign-timeout", 300)
        self.assertEqual(code, 2)

    def test_estimate_honours_the_per_run_cap(self):
        code, stdout, _ = self.main("estimate", "--model-profile", self.profile(), "--per-run-token-cap", 50000)
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(stdout)["worstCaseTokens"], 100000)


class CampaignTests(Base):
    def states(self, a=None, b=None):
        a = a or with_audit(make_state(tokens=1000), model_calls=2)
        return {"A": a, "B": b or a}

    def test_ab_campaign_isolation_mode_verification_and_report(self):
        script = self.script(states=self.states(b=with_audit(make_state(tokens=1300, calls=[call(prompt=140), call()]), model_calls=2)),
                             echoEnv=["Cli__Experimental__MafGovernedTurn", "Knowledge__Enabled", "Agent__MaxSteps"])
        out, (code, stdout, stderr) = self.run_campaign(script, "--allow-live")
        self.assertEqual(code, 0, stdout + stderr)
        manifest = json.loads((out / "campaign.json").read_text())
        self.assertTrue(manifest["testDouble"])
        self.assertEqual(manifest["configFingerprintByVariant"]["A"], manifest["configFingerprintByVariant"]["B"])
        runs = {r["variant"]: r for r in manifest["plannedRuns"]}
        infos = {v: json.loads((out / "runs" / r["runDir"] / "run.json").read_text()) for v, r in runs.items()}
        self.assertTrue(all(i["modeVerified"] for i in infos.values()))
        self.assertEqual((infos["A"]["mafMarkerSeen"], infos["B"]["mafMarkerSeen"]), (False, True))
        stdout_a = (out / "runs" / runs["A"]["runDir"] / "stdout.txt").read_text()
        stdout_b = (out / "runs" / runs["B"]["runDir"] / "stdout.txt").read_text()
        self.assertIn("Cli__Experimental__MafGovernedTurn=false", stdout_a)  # explicit, and not the ambient "true"
        self.assertIn("Cli__Experimental__MafGovernedTurn=true", stdout_b)
        self.assertIn("Knowledge__Enabled=false", stdout_a + stdout_b)
        self.assertNotIn("Agent__MaxSteps=1", stdout_a + stdout_b)
        self.assertIn("PRE_EXISTING_TASKS=0", stdout_a + stdout_b)  # isolated state per variant and run
        self.assertNotEqual(infos["A"]["environmentOverlay"]["Memory__FilePath"], infos["B"]["environmentOverlay"]["Memory__FilePath"])
        diff = {k for k in infos["A"]["environmentOverlay"] if infos["A"]["environmentOverlay"][k] != infos["B"]["environmentOverlay"][k]}
        self.assertEqual(diff - mcb.PATH_KEYS, {mcb.MAF_KEY})
        self.assertEqual(infos["A"]["configFingerprint"], infos["B"]["configFingerprint"])
        for r in runs.values():
            run_dir = out / "runs" / r["runDir"]
            self.assertTrue((run_dir / "capture" / "tasks").is_dir() and (run_dir / "audit-full.jsonl").is_file())
        report = json.loads((out / "report" / "ab-report.json").read_text())
        self.assertTrue(report["exploratory"] and report["testDouble"] and report["configuration"]["identicalAcrossVariants"])
        self.assertEqual(report["scenarios"]["B01"]["metrics"]["accountedTokens"]["delta"], 300)
        self.assertEqual(report["scenarios"]["B01"]["metrics"]["promptTokens"]["delta"], 40)
        self.assertTrue(all(s["ok"] for s in report["safety"]))
        markdown = (out / "report" / "ab-report.md").read_text()
        self.assertIn("TEST DOUBLE", markdown)
        self.assertIn("exploratory", markdown)
        self.assertTrue((out / "report" / "ab-report.csv").read_text().startswith("scenario,metric,class"))
        self.assertEqual(mcb.verify_integrity(out), [])

    def test_report_and_csv_contain_no_evidence_text(self):
        out, (code, *_) = self.run_campaign(self.script(state=with_audit(make_state())), "--allow-live")
        self.assertEqual(code, 0)
        evidence = "".join(p.read_text(errors="replace") for p in (out / "runs").rglob("state_json.utf8"))
        self.assertIn(SENTINEL, evidence)
        for name in ("ab-report.json", "ab-report.csv", "ab-report.md"):
            self.assertNotIn(SENTINEL, (out / "report" / name).read_text())

    def test_blind_sheets_hide_the_variant_and_summarise_judgements(self):
        out, (code, *_) = self.run_campaign(self.script(state=with_audit(make_state())), "--allow-live")
        self.assertEqual(code, 0)
        manifest = json.loads((out / "campaign.json").read_text())
        codes = {r["blindCode"] for r in manifest["plannedRuns"]}
        sheets = {p.stem: json.loads(p.read_text()) for p in (out / "evaluation").glob("*.json")}
        self.assertEqual(set(sheets), codes)
        for sheet in sheets.values():
            self.assertNotIn("variant", json.dumps(sheet).lower().replace("verdictvocabulary", ""))
            self.assertTrue(sheet["claims"])
        self.assertEqual(an.evaluation_summary(next(iter(sheets.values())))["status"], "PENDING")
        sheet = next(iter(sheets.values()))
        for claim in sheet["claims"]:
            claim["verdict"] = "supported"
        sheet["overall"]["diagnosisCorrectness"] = 2
        self.assertEqual(an.evaluation_summary(sheet)["status"], "COMPLETE")
        self.assertEqual(mcb.verify_integrity(out), [])  # grading never invalidates the evidence hashes

    def test_alternating_order_and_rounds(self):
        out, (code, *_) = self.run_campaign(self.script(state=with_audit(make_state())), "--allow-live", "--rounds", 2)
        self.assertEqual(code, 0)
        order = [r["variant"] for r in json.loads((out / "campaign.json").read_text())["plannedRuns"]]
        self.assertEqual(order, ["A", "B", "B", "A"])
        report = json.loads((out / "report" / "ab-report.json").read_text())
        self.assertEqual(report["roundsPerCell"], 2)
        self.assertEqual(report["scenarios"]["B01"]["metrics"]["modelCalls"]["A"]["n"], 2)

    def test_timeout_preserves_partial_evidence_and_stops_the_campaign(self):
        running = with_audit(make_state(status=0), model_calls=1)
        out, (code, stdout, _) = self.run_campaign(self.script(state=running, sleep=60), "--allow-live", "--task-timeout", 2)
        self.assertEqual(code, 1, stdout)
        state = json.loads((out / "campaign-state.json").read_text())
        self.assertIn("timeout", state["stopReason"])
        first = json.loads((out / "campaign.json").read_text())["plannedRuns"][0]
        info = json.loads((out / "runs" / first["runDir"] / "run.json").read_text())
        self.assertTrue(info["timedOut"])
        self.assertTrue(list((out / "runs" / first["runDir"] / "capture" / "tasks").rglob("state_json.utf8")))
        self.assertEqual(len(list((out / "runs").iterdir())), 1)  # B was never started
        self.assertEqual(mcb.verify_integrity(out), [])

    def test_token_budget_stops_between_runs(self):
        out, (code, *_) = self.run_campaign(self.script(state=with_audit(make_state(tokens=900))), "--allow-live", "--token-budget", 1500,
                                            profile=self.profile(agent={"MaxTotalTokens": 1000}))
        self.assertEqual(code, 1)
        state = json.loads((out / "campaign-state.json").read_text())
        self.assertEqual(state["runsDone"], 1)
        self.assertIn("token-budget", state["stopReason"])

    def test_mode_mismatch_stops_and_is_reported(self):
        out, (code, *_) = self.run_campaign(self.script(state=with_audit(make_state()), omitMarker={"B": True}), "--skip-mode-probe", "--allow-live")
        self.assertEqual(code, 1)
        self.assertIn("mode-mismatch", json.loads((out / "campaign-state.json").read_text())["stopReason"])
        report = json.loads((out / "report" / "ab-report.json").read_text())
        self.assertFalse(next(s for s in report["safety"] if s["variant"] == "B")["ok"])

    def test_provider_failure_stops_early(self):
        failing = with_audit(make_state(status=6, calls=[call(outcome=1), call(outcome=1)]))
        out, (code, *_) = self.run_campaign(self.script(state=failing), "--allow-live")
        self.assertEqual(code, 1)
        self.assertIn("provider unavailable", json.loads((out / "campaign-state.json").read_text())["stopReason"])

    def test_secret_is_redacted_from_output_and_detected_in_evidence(self):
        profile = self.profile(apiKeyEnv="TEST_BENCH_KEY")
        out, (code, *_) = self.run_campaign(self.script(state=with_audit(make_state()), leakEnv="BOPS_MODEL_API_KEY"), "--allow-live", profile=profile)
        self.assertEqual(code, 1)
        first = json.loads((out / "campaign.json").read_text())["plannedRuns"][0]
        run_dir = out / "runs" / first["runDir"]
        self.assertNotIn(KEY_VALUE, (run_dir / "stdout.txt").read_text())
        self.assertIn("[REDACTED", (run_dir / "stdout.txt").read_text())
        self.assertEqual(json.loads((run_dir / "run.json").read_text())["secretLeakScan"], "leak")
        self.assertNotIn(KEY_VALUE, (out / "campaign.json").read_text())

    def test_non_empty_output_directory_is_refused(self):
        out = self.tmp / "out"
        out.mkdir()
        (out / "x").write_text("x")
        _, (code, *_) = self.run_campaign(self.script(state=make_state()), "--allow-live", output=out)
        self.assertEqual(code, 2)

    def test_integrity_detects_tampering(self):
        out, (code, *_) = self.run_campaign(self.script(state=with_audit(make_state())), "--allow-live")
        self.assertEqual(code, 0)
        victim = next((out / "runs").rglob("state_json.utf8"))
        victim.write_text(victim.read_text() + " ")
        self.assertEqual(self.main("verify", "--campaign-dir", out)[0], 1)

    def test_scenario_prompts_are_the_exact_bench00_prompts(self):
        self.assertEqual(mcb.GOALS, {s["id"]: s["goal"] for s in mcb.lb.SCENARIOS})
        self.assertEqual(mcb.SCENARIO_IDS, ["B01", "B02", "B03"])
        self.assertTrue(mcb.GOALS["B03"].startswith("trova la causa dei continui crash"))
        out, (code, *_) = self.run_campaign(self.script(state=with_audit(make_state())), "--allow-live", scenarios="B03,B01")
        self.assertEqual(code, 0)
        planned = json.loads((out / "campaign.json").read_text())["plannedRuns"]
        self.assertEqual([p["scenario"] for p in planned], ["B03", "B03", "B01", "B01"])  # operator order is kept


class MetricTests(unittest.TestCase):
    def metrics(self, state, events=None, **info):
        events = events if events is not None else audit_events()
        return an.compute_metrics(state, events, {"ok": True}, {"exitCode": 0, "wallSeconds": 100, "timedOut": False, **info})

    def test_counts_retries_truncation_fallback_and_usage(self):
        state = make_state(calls=[call(finish="length", attempt=2), call(fallback=1, provider_attempt=2), call(usage=False)])
        m = self.metrics(state)
        self.assertEqual((m["modelCalls"], m["modelRetries"], m["truncatedResponses"], m["fallbacks"], m["providerRetries"], m["usageMissingCalls"]),
                         (3, 1, 1, 1, 1, 1))
        self.assertEqual((m["promptTokens"], m["completionTokens"]), (200, 40))
        self.assertEqual(m["estimatedTokens"], 3 * 200)
        self.assertEqual(m["modelSeconds"], 3.0)

    def test_pending_planned_steps_and_replans(self):
        m = self.metrics(make_state(replans=2))
        self.assertEqual((m["plannedSteps"], m["plannedStepsExecuted"], m["plannedStepsPending"], m["replans"]), (2, 1, 1, 2))

    def test_evidence_reads_duplicates_and_partial_source_coverage(self):
        events = audit_events(reads=[("e1", 0, 0), ("e1", 0, 0), ("e1", 100, 1), ("e2", 0, 3)])
        m = self.metrics(make_state(completeness=an.COMPLETENESS_PARTIAL), events)
        self.assertEqual((m["evidenceReadAudited"], m["evidenceReadServed"], m["evidenceReadRejected"], m["evidenceReadDuplicates"]), (4, 3, 1, 1))
        self.assertEqual((m["partialSources"], m["partialSourcesWithRead"]), (1, 1))

    def test_safety_counters(self):
        m = self.metrics(make_state(), audit_events(risky=True, approvals=1))
        self.assertEqual((m["mutationsExecuted"], m["approvalEvents"]), (1, 1))
        self.assertEqual(self.metrics(make_state())["mutationsExecuted"], 0)

    def test_answer_checks_flag_unsupported_numbers_and_undisclosed_limits(self):
        state = make_state(completeness=an.COMPLETENESS_PARTIAL, answer="Il carico e al 99% per 4821 secondi.")
        m = self.metrics(state)
        self.assertEqual((m["answerDisclosesLimitation"], m["undisclosedPartialSources"]), (False, 1))
        self.assertEqual(m["answerNumbersNotInEvidence"], 2)
        ok = self.metrics(make_state(completeness=an.COMPLETENESS_PARTIAL, answer="CPU 12 percent. Dati parziali."))
        self.assertEqual((ok["answerDisclosesLimitation"], ok["undisclosedPartialSources"], ok["answerNumbersNotInEvidence"]), (True, 0, 0))

    def test_terminal_status_is_not_diagnosis_quality(self):
        m = self.metrics(make_state(status=1, answer="Nessuna causa trovata, ma e certamente il driver video."))
        self.assertEqual(m["terminalStatus"], "Completed")
        pending = an.evaluation_summary(an.build_evaluation_sheet("R01", "B03", make_state(answer="La causa e il driver video. Potrebbe essere la RAM.")))
        self.assertEqual(pending["status"], "PENDING")

    def test_causal_claims_are_separated_from_hedged_ones(self):
        sheet = an.build_evaluation_sheet("R01", "B03", make_state(answer="La causa dei crash e il driver video. Potrebbe dipendere dalla RAM, a causa di errori. Dati parziali."))
        kinds = [c["autoKind"] for c in sheet["claims"]]
        self.assertEqual(kinds, ["causal", "causal-hedged", "limitation"])

    def test_audit_chain_break_is_detected(self):
        path = pathlib.Path(tempfile.mkdtemp()) / "audit.jsonl"
        self.addCleanup(shutil.rmtree, path.parent, True)
        lines = [json.dumps({"Seq": s, "PrevHash": p, "Hash": h, "EventJson": json.dumps({"eventType": "x"})}) for s, p, h in ((1, "0", "a"), (2, "a", "b"), (3, "WRONG", "c"))]
        path.write_text("\n".join(lines) + "\n")
        self.assertFalse(an.load_audit(path)[1]["ok"])
        path.write_text("\n".join(lines[:2]) + "\n")
        self.assertTrue(an.load_audit(path)[1]["ok"])

    def test_missing_state_yields_unknowns_not_zeros(self):
        m = an.compute_metrics(None, [], {"ok": True}, {"exitCode": 3, "wallSeconds": 1.0, "timedOut": False})
        self.assertEqual(m["terminalStatus"], "NoTaskState")
        self.assertIsNone(m["modelCalls"])

    def test_cost_uses_reported_cost_first_then_configured_price(self):
        priced = {"promptUsdPerMTok": 1.0, "completionUsdPerMTok": 2.0}
        self.assertEqual(an.compute_metrics(make_state(calls=[call(cost=0.5), call()]), [], {"ok": True}, {}, priced)["estimatedCostUsd"], 0.5)
        self.assertEqual(an.compute_metrics(make_state(), [], {"ok": True}, {}, priced)["estimatedCostUsd"], round(200 / 1e6 + 40 * 2 / 1e6, 6))
        self.assertIsNone(an.compute_metrics(make_state(), [], {"ok": True}, {})["estimatedCostUsd"])


class EstimateTests(unittest.TestCase):
    def test_defaults_and_worst_case(self):
        est = an.estimate_campaign(["B01", "B02", "B03"], ["A", "B"], 1, 350000, 1800)
        self.assertEqual(est["runs"], 6)
        self.assertEqual(est["worstCaseTokens"], 6 * 350000)
        self.assertEqual(est["worstCaseHours"], 3.0)
        self.assertEqual(est["cost"]["worstCaseUsd"], 0.0)

    def test_history_from_bench00_archives_and_priced_cost(self):
        directory = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, directory, True)
        (directory / "run1").mkdir()
        with zipfile.ZipFile(directory / "run1" / "a.zip", "w") as z:
            z.writestr("run-roster.json", json.dumps({"tasks": [{"taskId": "t1", "scenarioId": "B01"}]}))
            z.writestr("tasks/t1/state_json.utf8", json.dumps(make_state(tokens=40000, calls=[call(duration_ms=60000), call(duration_ms=60000)])))
        history = an.history_from_bench00(directory)
        self.assertEqual(history["B01"]["tokensMean"], 40000)
        est = an.estimate_campaign(["B01"], ["A", "B"], 1, 100000, 600, history, {"promptUsdPerMTok": 1.0, "completionUsdPerMTok": 4.0})
        self.assertEqual(est["expectedTokens"], 80000)
        self.assertEqual(est["cost"]["expectedUsd"], round(80000 * 4 / 1e6, 4))
        self.assertEqual(an.history_from_bench00(directory / "missing"), {})


class LauncherTests(unittest.TestCase):
    SCRIPT = HERE.parent / "run-maf-cli-benchmark.ps1"

    def powershell(self):
        return shutil.which("pwsh") or shutil.which("powershell")

    def test_launcher_parses(self):
        shell = self.powershell()
        if not shell:
            self.skipTest("PowerShell not available")
        command = ("$e=$null;[void][System.Management.Automation.Language.Parser]::ParseFile('%s',[ref]$null,[ref]$e);"
                   "if($e.Count){$e|%%{$_.Message};exit 1}" % str(self.SCRIPT).replace("'", "''"))
        self.assertEqual(subprocess.run([shell, "-NoProfile", "-Command", command], capture_output=True, text=True).returncode, 0)

    def test_launcher_estimate_runs_offline(self):
        shell = self.powershell()
        if not shell:
            self.skipTest("PowerShell not available")
        profile = pathlib.Path(tempfile.mkdtemp()) / "p.json"
        self.addCleanup(shutil.rmtree, profile.parent, True)
        profile.write_text(json.dumps({"provider": "LlamaCpp", "baseUrl": "http://127.0.0.1:8080/v1", "model": "m"}))
        result = subprocess.run([shell, "-NoProfile", "-File", str(self.SCRIPT), "-Estimate", "-ModelProfile", str(profile), "-Scenarios", "B01,B02,B03"],
                                capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["runs"], 6)


if __name__ == "__main__":
    unittest.main()
