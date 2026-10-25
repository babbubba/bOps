"""Offline tests of the four-condition (A/B/C/D) extension of the MAF CLI benchmark.

A = MAF off / Knowledge off, B = MAF on / Knowledge off, C = MAF off / Knowledge on, D = MAF on / Knowledge on. No bOps process, no model and
no network: the CLI is the scriptable fake_maf_cli.py (a test double). Run:
python -m unittest discover -s scripts/windows/tests -p "test_maf_cli_*.py" -v
"""
import json
import pathlib
import sys
import unittest
import unittest.mock

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))
import maf_cli_analysis as an  # noqa: E402
import maf_cli_benchmark as mcb  # noqa: E402
from test_maf_cli_benchmark import Base, make_state, with_audit  # noqa: E402

PACK_ID, PACK_VERSION = "bsoft.windows-event-runbooks", "2.0.0"


def knowledge_events(reads=2, refused=0, index=True):
    events = [{"eventType": "knowledgeIndex", "Status": 0}] if index else []
    events += [{"eventType": "knowledgeRead", "Operation": "read_resource", "Skill": "windows-crash-classification",
                "Resource": f"references/r{i}.md", "ResultCode": 0, "ReturnedLength": 900, "EstimatedTokens": 300, "AllowedTokens": 20000}
               for i in range(reads)]
    events += [{"eventType": "knowledgeRead", "Operation": "read_resource", "Skill": "windows-event-log-sources",
                "Resource": "references/big.md", "ResultCode": 13, "ReturnedLength": 0, "EstimatedTokens": 9000, "AllowedTokens": 100}
               for _ in range(refused)]
    return events


def scripted(**per_variant):
    """A fake-CLI script whose task state and audit differ per variant."""
    states = {}
    for variant in "ABCD":
        knowledge = variant in "CD"
        audit = (knowledge_events() if knowledge else []) + [
            {"eventType": "modelCall", "Outcome": 0, "Usage": {"PromptTokens": 5000}, "Context": {"ContextUsagePercent": 3.8, "CompactedToolGroups": 0, "DroppedTurns": 0}}]
        states[variant] = with_audit(make_state(answer=per_variant.get(variant, "Fatti osservati. Ipotesi da verificare. Dati parziali dichiarati.")),
                                     model_calls=0, tools=1)
        states[variant]["_audit"] = audit + states[variant]["_audit"]
    return states


class KnowledgeBase(Base):
    def make_pack(self, *, tamper=None):
        packs = self.tmp / "packs"
        directory = packs / PACK_ID / PACK_VERSION
        directory.mkdir(parents=True)
        manifest = {"schemaVersion": 1, "packId": PACK_ID, "version": PACK_VERSION, "kind": "Knowledge", "publisher": "acme", "keyId": "acme-2026",
                    "contentSha256": "ab" * 32, "files": [{"path": "s/SKILL.md", "sha256": "cd" * 32}]}
        if tamper == "wrong-id":
            manifest["packId"] = "other.pack"
        (directory / "bops-pack.json").write_text(json.dumps(manifest), encoding="utf-8")
        (directory / "bops-pack.sig.json").write_text(json.dumps({"schemaVersion": 1, "signature": "AAAA"}), encoding="utf-8")
        trust = self.tmp / "trust" / "publisher-trust.json"
        trust.parent.mkdir()
        trust.write_text(json.dumps({"schemaVersion": 1, "keys": [{"publisher": "acme", "keyId": "acme-2026", "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nAA\n-----END PUBLIC KEY-----"}],
                                     "revokedPublishers": []}) + ("\n-----BEGIN PRIVATE KEY-----" if tamper == "private-key" else ""), encoding="utf-8")
        return packs, trust

    def knowledge_args(self, packs, trust):
        return ["--knowledge-packs-dir", packs, "--knowledge-trust-store", trust, "--knowledge-pack-id", PACK_ID, "--knowledge-pack-version", PACK_VERSION]

    def preflight(self, script_path, *extra, variants="A,B,C,D", output=None, profile=None):
        packs, trust = self.make_pack() if "--no-pack" not in extra else (None, None)
        extra = [e for e in extra if e != "--no-pack"]
        knowledge = self.knowledge_args(packs, trust) if packs else []
        return self.main("preflight", "--model-profile", profile or self.profile(), "--output-root", output or self.tmp / "pf", "--skip-endpoint-probe",
                         "--variants", variants, *knowledge, *self.double(script_path), *extra)


class KnowledgeSetupTests(KnowledgeBase):
    def args(self, packs, trust, **override):
        values = {"knowledge_packs_dir": str(packs), "knowledge_trust_store": str(trust), "knowledge_pack_id": PACK_ID, "knowledge_pack_version": PACK_VERSION}
        values.update(override)
        return type("Args", (), values)()

    def test_the_pack_files_are_validated_and_hashed(self):
        packs, trust = self.make_pack()
        setup = mcb.load_knowledge(self.args(packs, trust))
        self.assertEqual((setup["packId"], setup["version"], setup["publisher"]), (PACK_ID, PACK_VERSION, "acme"))
        self.assertEqual(len(setup["manifestSha256"]), 64)
        self.assertEqual(len(setup["trustStoreSha256"]), 64)

    def test_no_pack_means_no_setup_and_a_partial_setup_is_refused(self):
        packs, trust = self.make_pack()
        self.assertIsNone(mcb.load_knowledge(type("Args", (), {})()))
        with self.assertRaises(mcb.BenchmarkError):
            mcb.load_knowledge(self.args(packs, trust, knowledge_pack_version=None))

    def test_a_missing_file_a_wrong_id_and_a_private_key_are_refused(self):
        packs, trust = self.make_pack()
        with self.assertRaises(mcb.BenchmarkError):
            mcb.load_knowledge(self.args(packs, trust, knowledge_pack_version="9.9.9"))
        with self.assertRaises(mcb.BenchmarkError):
            mcb.load_knowledge(self.args(packs, self.tmp / "missing.json"))
        self.tmp = self.tmp / "second"
        self.tmp.mkdir()
        packs, trust = self.make_pack(tamper="wrong-id")
        with self.assertRaises(mcb.BenchmarkError):
            mcb.load_knowledge(self.args(packs, trust))
        self.tmp = self.tmp / "third"
        self.tmp.mkdir()
        packs, trust = self.make_pack(tamper="private-key")
        with self.assertRaises(mcb.BenchmarkError):
            mcb.load_knowledge(self.args(packs, trust))

    def test_c_and_d_share_one_knowledge_configuration_and_differ_from_a_b_only_by_their_switches(self):
        packs, trust = self.make_pack()
        setup = mcb.load_knowledge(self.args(packs, trust))
        profile = mcb.load_profile(self.profile(knowledge={"maxReads": 6, "contextShare": 0.4}))
        work = pathlib.Path("W")
        overlays = {v: mcb.build_overlay(profile, v, work, setup) for v in "ABCD"}

        self.assertEqual([overlays[v]["Knowledge__Enabled"] for v in "ABCD"], ["false", "false", "true", "true"])
        self.assertEqual([overlays[v][mcb.MAF_KEY] for v in "ABCD"], ["false", "true", "false", "true"])
        c, d = overlays["C"], overlays["D"]
        self.assertEqual({k for k in c if c[k] != d.get(k)}, {mcb.MAF_KEY})
        self.assertEqual((c["Knowledge__Budget__MaxReads"], c["Knowledge__Budget__ContextShare"], c["Knowledge__Budget__MaxCharactersPerRead"]), ("6", "0.4", "12000"))
        self.assertEqual((c["Knowledge__Grants__0__Kind"], c["Knowledge__Binding__Version"]), ("Knowledge", PACK_VERSION))
        self.assertTrue(c["Knowledge__SnapshotsDirectory"].endswith("knowledge-snapshots"))
        self.assertEqual(mcb.knowledge_fingerprint(c, setup), mcb.knowledge_fingerprint(d, setup))
        self.assertIsNone(mcb.knowledge_fingerprint(overlays["A"], setup))
        fingerprints = {v: mcb.config_fingerprint(overlays[v], "p", "s", "c") for v in "ABCD"}
        self.assertEqual(len(set(fingerprints.values())), 1)  # the base configuration is identical across the four

    def test_a_knowledge_variant_without_a_pack_cannot_be_built(self):
        with self.assertRaises(mcb.BenchmarkError):
            mcb.build_overlay(mcb.load_profile(self.profile()), "C", pathlib.Path("W"), None)


class PlanTests(Base):
    def test_rotation_gives_every_variant_every_position_and_36_runs(self):
        runs = mcb.plan_runs(["B01", "B02", "B03"], ["A", "B", "C", "D"], 3, "rotate", "seed")
        self.assertEqual(len(runs), 36)
        cells = {}
        for run in runs:
            cells.setdefault((run["scenario"], run["variant"]), []).append(run["round"])
        self.assertEqual(len(cells), 12)
        self.assertTrue(all(sorted(rounds) == [1, 2, 3] for rounds in cells.values()))
        firsts = {runs[i]["variant"] for i in range(0, 36, 4)}
        lasts = {runs[i]["variant"] for i in range(3, 36, 4)}
        self.assertEqual((firsts, lasts), (set("ABCD"), set("ABCD")))
        self.assertEqual(len({run["blindCode"] for run in runs}), 36)

    def test_the_historical_orders_are_unchanged(self):
        alternate = mcb.plan_runs(["B01"], ["A", "B"], 2, "alternate", "s")
        self.assertEqual([r["variant"] for r in alternate], ["A", "B", "B", "A"])
        self.assertEqual([r["variant"] for r in mcb.plan_runs(["B01"], ["A", "B"], 2, "fixed", "s")], ["A", "B", "A", "B"])


class PreflightKnowledgeTests(KnowledgeBase):
    def test_all_four_conditions_pass_when_the_cli_admits_the_pack(self):
        code, stdout, _ = self.preflight(self.script())
        self.assertEqual(code, 0, stdout)
        document = json.loads((self.tmp / "pf" / "preflight.json").read_text())
        probe = next(c for c in document["checks"] if c["name"] == "mode-probe")
        self.assertEqual({v: (p["markerSeen"], p["knowledgeAdmitted"]) for v, p in probe["probes"].items()},
                         {"A": (False, False), "B": (True, False), "C": (False, True), "D": (True, True)})
        self.assertEqual(next(c for c in document["checks"] if c["name"] == "knowledge-pack")["status"], "pass")
        self.assertTrue(document["noRealModelCall"])

    def test_preflight_fails_when_the_pack_is_not_admitted(self):
        code, stdout, _ = self.preflight(self.script(admissionCode=5))
        self.assertEqual(code, 2)
        document = json.loads((self.tmp / "pf" / "preflight.json").read_text())
        probe = next(c for c in document["checks"] if c["name"] == "mode-probe")
        self.assertEqual({v: p["status"] for v, p in probe["probes"].items()}, {"A": "pass", "B": "pass", "C": "fail", "D": "fail"})

    def test_preflight_fails_when_a_knowledge_variant_has_no_pack(self):
        code, stdout, _ = self.preflight(self.script(), "--no-pack", variants="A,C")
        self.assertEqual(code, 2)
        self.assertIn("knowledge-pack", stdout)

    def test_the_server_configuration_must_match_the_profile(self):
        profile = self.profile(expectedServer={"nCtx": 131072, "cacheTypeK": "f16", "cacheTypeV": "f16", "nPredict": 32768, "parallel": 1})
        good = {"nCtxPerSlot": 131072, "cacheTypeK": "f16", "cacheTypeV": "f16", "nPredict": "32768", "totalSlots": 1}
        with unittest.mock.patch.object(mcb, "probe_endpoint", return_value={"status": "pass"}), \
                unittest.mock.patch.object(mcb, "read_llama_props", return_value=None):
            with unittest.mock.patch.object(mcb, "probe_server_facts", return_value=good):
                pass_code = self.preflight_with_probe(profile, self.tmp / "ok")
            with unittest.mock.patch.object(mcb, "probe_server_facts", return_value={**good, "nCtxPerSlot": 65536, "cacheTypeK": "q8_0"}):
                old_code = self.preflight_with_probe(profile, self.tmp / "old")
            with unittest.mock.patch.object(mcb, "probe_server_facts", return_value={"nCtxPerSlot": 131072}):
                partial = self.preflight_with_probe(profile, self.tmp / "partial")
                accepted = self.preflight_with_probe(profile, self.tmp / "accepted", "--allow-unverified-server")
        self.assertEqual((pass_code, old_code, partial, accepted), (0, 2, 2, 0))

    def preflight_with_probe(self, profile, output, *extra):
        packs, trust = self.make_pack() if not (self.tmp / "packs").exists() else (self.tmp / "packs", self.tmp / "trust" / "publisher-trust.json")
        return self.main("preflight", "--model-profile", profile, "--output-root", output, "--skip-mode-probe", "--variants", "A,B,C,D",
                         *self.knowledge_args(packs, trust), *self.double(self.script()), *extra)[0]


class ServerFactsTests(unittest.TestCase):
    def test_facts_come_from_props_slots_and_router_arguments(self):
        facts = mcb.extract_server_facts(
            {"default_generation_settings": {"n_ctx": 131072}, "total_slots": 1, "model_alias": "Qwen3.8-Flash-Next-Smart", "build_info": "b1"},
            {"data": [{"id": "Qwen3.8-Flash-Next-Smart", "status": {"value": "loaded", "args": ["llama-server", "--ctx-size", "131072", "--cache-type-k", "f16",
                                                                                                "-ctv", "f16", "--n-predict", "32768", "--parallel", "1"]}},
                      {"id": "Other", "status": {"value": "unloaded", "args": ["--ctx-size", "999"]}}]},
            [{"n_ctx": 131072}], "Qwen3.8-Flash-Next-Smart")
        self.assertEqual((facts["nCtxPerSlot"], facts["totalSlots"], facts["ctxSize"], facts["cacheTypeK"], facts["cacheTypeV"], facts["nPredict"]),
                         (131072, 1, "131072", "f16", "f16", "32768"))
        self.assertEqual(facts["routerStatus"], "loaded")

    def test_nothing_is_invented_when_the_server_says_nothing(self):
        self.assertEqual(mcb.extract_server_facts(None, None, None, "m"), {})
        verdict = mcb.check_expected_server({"nCtx": 131072}, {}, allow_unverified=False)
        self.assertEqual(verdict["status"], "fail")
        self.assertEqual(mcb.check_expected_server({"nCtx": 131072}, {}, allow_unverified=True)["status"], "warn")

    def test_an_old_configuration_is_a_failure_even_if_unverified_values_are_accepted(self):
        verdict = mcb.check_expected_server({"nCtx": 131072, "cacheTypeK": "f16"}, {"nCtxPerSlot": 65536, "cacheTypeK": "q8_0"}, allow_unverified=True)
        self.assertEqual(verdict["status"], "fail")
        self.assertEqual({k: v["status"] for k, v in verdict["values"].items()}, {"nCtx": "fail", "cacheTypeK": "fail"})


class CampaignTests(KnowledgeBase):
    def run_abcd(self, script_path, *extra, variants="A,B,C,D", order="rotate", output=None):
        packs, trust = self.make_pack() if not (self.tmp / "packs").exists() else (self.tmp / "packs", self.tmp / "trust" / "publisher-trust.json")
        output = output or self.tmp / "out"
        extra = (*extra,) if "--token-budget" in extra else (*extra, "--token-budget", 1_000_000)
        return output, self.main("run", "--model-profile", self.profile(), "--output-root", output, "--scenarios", "B01", "--variants", variants,
                                 "--order", order, "--skip-endpoint-probe", *self.knowledge_args(packs, trust), *self.double(script_path), *extra)

    def test_the_four_conditions_run_are_verified_and_compared_pairwise(self):
        out, (code, stdout, stderr) = self.run_abcd(self.script(states=scripted()))
        self.assertEqual(code, 0, stdout + stderr)
        manifest = json.loads((out / "campaign.json").read_text())
        self.assertEqual(manifest["variants"], ["A", "B", "C", "D"])
        self.assertEqual(manifest["knowledge"]["packId"], PACK_ID)
        self.assertEqual(manifest["knowledgeFingerprintByVariant"]["C"], manifest["knowledgeFingerprintByVariant"]["D"])
        infos = [json.loads(p.read_text()) for p in sorted((out / "runs").glob("*/run.json"))]
        self.assertEqual(sorted(i["variant"] for i in infos), list("ABCD"))
        self.assertTrue(all(i["modeVerified"] and i["knowledgeVerified"] for i in infos))
        self.assertEqual({i["variant"]: i["knowledgeAdmitted"] for i in infos}, {"A": False, "B": False, "C": True, "D": True})
        env = {i["variant"]: i["environmentOverlay"] for i in infos}
        self.assertEqual([env[v]["Knowledge__Enabled"] for v in "ABCD"], ["false", "false", "true", "true"])
        self.assertEqual(len({env[v]["Knowledge__SnapshotsDirectory"] for v in "CD"}), 2)  # an isolated snapshot directory per run

        report = json.loads((out / "report" / "ab-report.json").read_text())
        self.assertEqual(sorted(report["pairs"]), ["A-B", "A-C", "B-D", "C-D"])
        self.assertTrue(report["configuration"]["identicalAcrossVariants"])
        cell = report["matrix"]["B01"]
        self.assertEqual((cell["A"]["knowledgeUsedRuns"], cell["C"]["knowledgeUsedRuns"], cell["D"]["knowledgeAvailableRuns"]), (0, 1, 1))
        self.assertEqual(cell["D"]["metrics"]["knowledgeReadsServed"]["mean"], 2)
        self.assertIn("windows-crash-classification/references/r0.md", cell["C"]["knowledgeResources"])
        self.assertEqual(report["failures"], [])
        self.assertEqual(report["missingRuns"], [])
        self.assertTrue(all(row["ok"] for row in report["safety"]))
        markdown = (out / "report" / "abcd-report.md").read_text(encoding="utf-8")
        for heading in ("Pairwise comparisons", "A-B", "C-D", "A-C", "B-D", "knowledge available / used", "Failed and missing runs"):
            self.assertIn(heading, markdown)
        self.assertEqual((out / "report" / "ab-report.md").read_text(encoding="utf-8"), markdown)
        self.assertTrue((out / "report" / "ab-report.csv").read_text().startswith("scenario,metric,class"))

    def test_a_pack_that_is_not_admitted_stops_the_campaign(self):
        out, (code, stdout, stderr) = self.run_abcd(self.script(states=scripted()), variants="C,D")
        self.assertEqual(code, 0)  # control: admitted
        out2, (code2, _, _) = self.run_abcd(self.script(states=scripted(), admissionCode=3), variants="C,D", output=self.tmp / "out2")
        self.assertEqual(code2, 2)  # the preflight mode probe refuses it before any run

    def test_a_knowledge_variant_that_shows_no_admission_is_a_mismatch(self):
        packs, trust = self.make_pack()
        # The preflight sees an admission (probe), the run does not: the run must stop and say why.
        script = self.script(states=scripted())
        original = mcb.knowledge_admission
        calls = {"n": 0}

        def flaky(path):
            calls["n"] += 1
            return original(path) if "mode-probe" in str(path) else {"events": 0, "codes": [], "admitted": False}

        with unittest.mock.patch.object(mcb, "knowledge_admission", flaky):
            out, (code, stdout, stderr) = self.run_abcd(script, variants="C")
        self.assertEqual(code, 1)
        self.assertIn("knowledge-mismatch", json.loads((out / "campaign-state.json").read_text())["stopReason"])

    def test_available_is_reported_apart_from_used(self):
        states = scripted()
        states["D"]["_audit"] = [e for e in states["D"]["_audit"] if e.get("eventType") != "knowledgeRead"]
        out, (code, _, _) = self.run_abcd(self.script(states=states), variants="C,D")
        report = json.loads((out / "report" / "ab-report.json").read_text())
        d = report["matrix"]["B01"]["D"]
        self.assertEqual((d["knowledgeAvailableRuns"], d["knowledgeUsedRuns"]), (1, 0))
        self.assertEqual(d["metrics"]["knowledgeReadsServed"]["mean"], 0)

    def test_an_interrupted_campaign_resumes_with_the_same_identity(self):
        script = self.script(states=scripted())
        first_budget = 10_000 + 1  # one run's cap is 10000: a second run does not fit under this budget
        out, (code, stdout, _) = self.run_abcd(script, "--token-budget", first_budget)
        self.assertEqual(code, 1)
        self.assertIn("token-budget", json.loads((out / "campaign-state.json").read_text())["stopReason"])
        done = sorted(p.parent.name for p in (out / "runs").glob("*/run.json"))
        self.assertEqual(len(done), 1)
        first_hash = mcb.sha_file(out / "runs" / done[0] / "run.json")

        code2, stdout2, stderr2 = self.main("run", "--model-profile", self.profile(), "--output-root", out, "--scenarios", "B01", "--variants", "A,B,C,D",
                                            "--order", "rotate", "--skip-endpoint-probe", "--resume", "--token-budget", 1_000_000,
                                            *self.knowledge_args(self.tmp / "packs", self.tmp / "trust" / "publisher-trust.json"), *self.double(script))
        self.assertEqual(code2, 0, stdout2 + stderr2)
        finished = sorted(p.parent.name for p in (out / "runs").glob("*/run.json"))
        self.assertEqual(len(finished), 4)
        self.assertEqual(mcb.sha_file(out / "runs" / done[0] / "run.json"), first_hash)  # not run again
        self.assertEqual(json.loads((out / "campaign.json").read_text())["campaignId"], out.name)
        report = json.loads((out / "report" / "ab-report.json").read_text())
        self.assertEqual(report["missingRuns"], [])

    def test_resume_is_refused_when_the_campaign_changed(self):
        script = self.script(states=scripted())
        out, (code, _, _) = self.run_abcd(script, "--token-budget", 10_001)
        self.assertEqual(code, 1)
        changed = self.profile(agent={"MaxTotalTokens": 10000, "MaxSteps": 9})
        code2, _, stderr2 = self.main("run", "--model-profile", changed, "--output-root", out, "--scenarios", "B01", "--variants", "A,B,C,D", "--order", "rotate",
                                      "--skip-endpoint-probe", "--resume", "--token-budget", 1_000_000,
                                      *self.knowledge_args(self.tmp / "packs", self.tmp / "trust" / "publisher-trust.json"), *self.double(script))
        self.assertEqual(code2, 2)
        self.assertIn("Resume refused", stderr2)

    def test_resume_needs_an_existing_campaign(self):
        code, _, stderr = self.main("run", "--model-profile", self.profile(), "--output-root", self.tmp / "none", "--scenarios", "B01", "--resume",
                                    "--skip-endpoint-probe", "--token-budget", 1_000_000, *self.double(self.script()))
        self.assertEqual(code, 2)
        self.assertIn("--resume", stderr)


class AnalysisTests(unittest.TestCase):
    def flags(self, answer):
        return an.diagnostic_flags(answer)

    def test_a_live_dump_is_not_equated_with_a_bsod(self):
        self.assertEqual(self.flags("Il codice 0x193 e un live dump, non un BSOD, e non riavvia il sistema.")["liveDumpTreatedAsCrash"], 0)
        self.assertEqual(self.flags("I LiveKernelEvent 193 sono schermate blu che hanno riavviato il PC.")["liveDumpTreatedAsCrash"], 1)
        self.assertEqual(self.flags("Sono stati registrati 12 live dump di dxgkrnl senza riavvio.")["liveDumpTreatedAsCrash"], 0)

    def test_a_hardware_cause_needs_a_hedge(self):
        self.assertEqual(self.flags("La GPU e guasta.")["hardwareCauseUnhedged"], 1)
        self.assertEqual(self.flags("Kernel-Power 41 con BugcheckCode 0 dimostra un guasto dell'alimentatore.")["hardwareCauseUnhedged"], 1)
        self.assertEqual(self.flags("La GPU potrebbe essere la causa, ma non e dimostrato.")["hardwareCauseUnhedged"], 0)
        self.assertEqual(self.flags("Un guasto dell'alimentatore e un'ipotesi da verificare con sensori indipendenti.")["hardwareCauseUnhedged"], 0)

    def test_different_whea_ids_are_not_the_same_thing(self):
        self.assertEqual(self.flags("Gli eventi WHEA ID 19 e ID 2 sono la stessa cosa.")["wheaIdsEquated"], 1)
        self.assertEqual(self.flags("Gli eventi WHEA ID 19 e ID 2 sono categorie diverse.")["wheaIdsEquated"], 0)

    def test_a_burst_is_not_a_list_of_independent_failures(self):
        self.assertEqual(self.flags("Ci sono 150 guasti hardware nel periodo.")["eventsAsIndependentFailures"], 1)
        self.assertEqual(self.flags("Ci sono 150 eventi raggruppati in 3 episodi; non sono guasti indipendenti.")["eventsAsIndependentFailures"], 0)

    def test_the_total_is_the_sum_of_the_flags(self):
        flags = self.flags("La GPU e guasta. Gli eventi WHEA ID 19 e ID 2 sono la stessa cosa.")
        self.assertEqual(flags["diagnosticFlags"], flags["hardwareCauseUnhedged"] + flags["wheaIdsEquated"] + flags["liveDumpTreatedAsCrash"] + flags["eventsAsIndependentFailures"])
        self.assertEqual(flags["diagnosticFlags"], 2)

    def test_outcome_classes_separate_timeout_context_protocol_and_provider_failures(self):
        def metrics(**kw):
            return {"modelCalls": 4, "modelCallFailures": 0, "timeoutFailures": 0, "contextOverflows": 0, "protocolErrors": 0,
                    "contextGateRejections": 0, "maxContextUsagePercent": 10.0, **kw}

        self.assertEqual(an.outcome_class("Completed", {}, metrics()), "completed")
        self.assertEqual(an.outcome_class("Failed", {"timedOut": True}, metrics()), "timeout")
        self.assertEqual(an.outcome_class("NoTaskState", {"timedOut": True}, metrics()), "timeout")
        self.assertEqual(an.outcome_class("NoTaskState", {}, metrics()), "no-state")
        self.assertEqual(an.outcome_class("Failed", {}, metrics(timeoutFailures=1)), "timeout")
        self.assertEqual(an.outcome_class("BudgetExceeded", {}, metrics()), "budget-exceeded")
        self.assertEqual(an.outcome_class("Failed", {}, metrics(contextOverflows=1)), "context-saturation")
        self.assertEqual(an.outcome_class("Failed", {}, metrics(maxContextUsagePercent=95.0)), "context-saturation")
        self.assertEqual(an.outcome_class("Failed", {}, metrics(protocolErrors=2)), "protocol-error")
        self.assertEqual(an.outcome_class("Failed", {}, metrics(modelCallFailures=4)), "provider-failure")
        self.assertEqual(an.outcome_class("Failed", {}, metrics()), "failed")

    def test_knowledge_metrics_count_availability_use_and_context_budget_refusals(self):
        audit = [{"eventType": "modelCall", "Outcome": 0, "Usage": {"PromptTokens": 7000}, "Context": {"ContextUsagePercent": 5.3, "CompactedToolGroups": 2, "DroppedTurns": 1}},
                 {"eventType": "modelCall", "Outcome": 1, "FailureKind": 8}, {"eventType": "modelCall", "Outcome": 1, "FailureKind": "Timeout"},
                 *knowledge_events(reads=3, refused=1)]
        metrics = an.compute_metrics(make_state(), audit, {"ok": True}, {"exitCode": 0, "wallSeconds": 5})
        self.assertEqual((metrics["knowledgeIndexPublished"], metrics["knowledgeReadsServed"], metrics["knowledgeReadsRefused"],
                          metrics["knowledgeReadsContextBudget"], metrics["knowledgeUsed"]), (1, 3, 1, 1, 1))
        self.assertEqual(metrics["knowledgeEstimatedTokensServed"], 900)
        self.assertEqual(metrics["knowledgeDistinctResources"], 3)
        self.assertEqual((metrics["maxPromptTokens"], metrics["maxContextUsagePercent"], metrics["compactedToolGroups"], metrics["droppedTurns"]), (7000, 5.3, 2, 1))
        self.assertEqual((metrics["contextOverflows"], metrics["timeoutFailures"]), (1, 1))

    def test_the_blind_sheet_carries_the_scenario_checklist_and_hides_the_variant(self):
        state = make_state(answer="Gli eventi WHEA ID 19 e ID 2 sono la stessa cosa.")
        sheet = an.build_evaluation_sheet("R07", "B02", state)
        self.assertEqual(set(sheet["scenarioChecklist"]), set(an.SCENARIO_CHECKLIST))
        self.assertEqual(sheet["scenarioChecklist"]["wheaIdsNotConfused"]["autoFlags"], 1)
        self.assertIsNone(sheet["scenarioChecklist"]["wheaIdsNotConfused"]["judgement"])
        self.assertNotIn("variant", json.dumps(sheet).lower().replace("verdictvocabulary", ""))


class Qwen38ProfileTests(Base):
    """The shipped example profile for Qwen3.8-Flash-Next-Smart at 128K context, F16 KV cache and 32K generation."""

    @staticmethod
    def example():
        return HERE.parent / "maf-cli-benchmark-profile.qwen38-smart-128k.example.json"

    @staticmethod
    def seconds(value):
        hours, minutes, seconds = (int(part) for part in value.split(":"))
        return hours * 3600 + minutes * 60 + seconds

    def test_the_profile_declares_the_exact_model_window_reserves_and_generation_limit(self):
        profile = mcb.load_profile(self.example())
        overlay = mcb.build_overlay(profile, "A", pathlib.Path("W"))

        self.assertEqual((profile["provider"], profile["model"]), ("LlamaCpp", "Qwen3.8-Flash-Next-Smart"))
        self.assertEqual(overlay["Agent__MaxOutputTokens"], "32768")
        self.assertEqual(overlay["Agent__Context__Profiles__0__Provider"], "LlamaCpp")  # the provider id the descriptor reports
        self.assertEqual(overlay["Agent__Context__Profiles__0__Model"], profile["model"])
        window = int(overlay["Agent__Context__Profiles__0__MaxContextTokens"])
        reserved = int(overlay["Agent__Context__Profiles__0__ReservedOutputTokens"])
        safety = int(overlay["Agent__Context__SafetyReserveTokens"])
        self.assertEqual((window, reserved, safety, window - reserved - safety), (131072, 32768, 2048, 96256))
        self.assertEqual(int(overlay["Agent__MaxOutputTokens"]), reserved)  # the request limit equals the reserve: no double deduction
        self.assertEqual(profile["expectedServer"], {"nCtx": 131072, "nPredict": 32768, "parallel": 1, "cacheTypeK": "f16", "cacheTypeV": "f16"})

    def test_the_timeouts_nest_and_leave_room_for_a_full_32k_generation(self):
        profile = mcb.load_profile(self.example())
        transport = self.seconds(profile["requestTimeout"])
        attempt = self.seconds(profile["agent"]["ModelCallAttemptTimeout"])
        budget = self.seconds(profile["agent"]["ModelCallBudget"])
        run = self.seconds(profile["agent"]["MaxAttemptDuration"])
        attempts = profile["agent"]["ModelCallMaxAttempts"]

        self.assertLess(attempt, transport)          # the runtime's attempt timeout fires before the HTTP one (validated by the CLI too)
        self.assertGreaterEqual(budget, attempts * attempt)  # the call budget covers every attempt
        self.assertLess(budget, run)                  # a single call cannot outlive the attempt
        self.assertGreaterEqual(attempt, 32768 / 20)  # 32768 tokens at 20 tok/s plus prefill still fit one attempt
        self.assertLess(run, 9000)                    # the runtime stops itself before the harness task timeout used by the commands

    def test_the_base_profile_has_no_secret_and_loads_in_both_knowledge_modes(self):
        profile = mcb.load_profile(self.example())
        self.assertNotIn("apiKeyEnv", {k for k, v in profile.items() if v})
        self.assertEqual(profile["knowledge"]["maxCharactersPerRead"], 12000)
        packs, trust = KnowledgeBase.make_pack(self)
        setup = mcb.load_knowledge(type("Args", (), {"knowledge_packs_dir": str(packs), "knowledge_trust_store": str(trust),
                                                       "knowledge_pack_id": PACK_ID, "knowledge_pack_version": PACK_VERSION})())
        overlay = mcb.build_overlay(profile, "D", pathlib.Path("W"), setup)
        self.assertEqual((overlay["Knowledge__Budget__ContextShare"], overlay["Knowledge__Budget__MaxReads"]), ("0.35", "8"))


class ShortKeyLeakScanTests(unittest.TestCase):
    """A local llama.cpp placeholder key such as 'local' is a word of ordinary text: it must not stop a campaign, but a real use of it
    as a credential must still be found. Long keys keep the full substring search."""

    def setUp(self):
        import tempfile
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.run_dir = pathlib.Path(self._tmp.name)

    def write(self, name, text):
        (self.run_dir / name).write_text(text, encoding="utf-8")

    def test_the_word_in_ordinary_text_is_not_a_leak(self):
        self.write("appsettings.json", '{"Docker": {"Volumes": {"Drivers": ["local"]}}}')
        self.write("stdout.txt", "The local machine reported localhost and a local disk; locally cached.")
        scan = mcb.leak_scan(self.run_dir, {"local"}, "local")
        self.assertEqual((scan["result"], scan["mode"], scan["files"]), ("clean", "contextual", []))

    def test_the_key_used_as_a_credential_is_found_and_only_the_file_name_is_reported(self):
        self.write("clean.txt", "nothing to see")
        self.write("stderr.txt", "request failed: Authorization: Bearer local")
        self.write("env.txt", "BOPS_MODEL_API_KEY=local")
        self.write("json.txt", '{"ApiKey": "local"}')
        scan = mcb.leak_scan(self.run_dir, {"local"}, "local")
        self.assertEqual(scan["result"], "leak")
        self.assertEqual(scan["files"], ["env.txt", "json.txt", "stderr.txt"])

    def test_a_longer_word_that_starts_with_the_key_is_not_a_credential(self):
        self.write("a.txt", "Bearer localhost and apiKey=locally")
        self.assertEqual(mcb.leak_scan(self.run_dir, {"local"}, "local")["result"], "clean")

    def test_a_long_key_keeps_the_full_substring_search(self):
        key = "0123456789abcdef0123456789abcdef"
        self.write("anywhere.txt", f"prefix {key} suffix")
        scan = mcb.leak_scan(self.run_dir, {key}, key)
        self.assertEqual((scan["result"], scan["mode"], scan["files"]), ("leak", "full", ["anywhere.txt"]))

    def test_another_short_environment_secret_is_not_downgraded(self):
        self.write("x.txt", "token abcdefgh in a sentence")
        scan = mcb.leak_scan(self.run_dir, {"local", "abcdefgh"}, "local")
        self.assertEqual((scan["result"], scan["mode"]), ("leak", "full+contextual"))

    def test_only_long_secrets_are_redacted_from_the_captured_output(self):
        strong, short = mcb.split_secrets({"local", "0123456789abcdef0123456789abcdef"}, "local")
        self.assertEqual((strong, short), ({"0123456789abcdef0123456789abcdef"}, {"local"}))
        self.assertEqual(mcb.split_secrets({"abcdefgh"}, None), ({"abcdefgh"}, set()))


class ShortKeyCampaignTests(Base):
    def test_a_campaign_with_a_short_key_runs_on_and_records_the_contextual_mode(self):
        profile = self.profile(apiKeyEnv="TEST_BENCH_KEY")
        with unittest.mock.patch.dict("os.environ", {"TEST_BENCH_KEY": "local"}):
            out, (code, *_) = self.run_campaign(self.script(state=with_audit(make_state())), "--allow-live", profile=profile)
        self.assertEqual(code, 0)
        first = json.loads((out / "campaign.json").read_text())["plannedRuns"][0]
        info = json.loads((out / "runs" / first["runDir"] / "run.json").read_text())
        self.assertEqual((info["secretLeakScan"], info["secretLeakFiles"]), ("clean", []))
        self.assertIn("contextual", info["secretLeakScanMode"])  # other, longer secrets of the environment keep the full search


if __name__ == "__main__":
    unittest.main()
