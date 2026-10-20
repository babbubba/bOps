import importlib.util
import csv
import io
import json
import os
import pathlib
import shutil
import sqlite3
import sys
import tempfile
import unittest
import zipfile
import subprocess
from unittest.mock import patch

MODULE = pathlib.Path(__file__).parents[1] / "live_benchmark.py"
spec = importlib.util.spec_from_file_location("live_benchmark", MODULE)
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


class RuntimeTargetTests(unittest.TestCase):
    """Offline repositories; no bOps processes, provider requests or real UAC."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="BENCH target with spaces ")
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.git("init", "--quiet")
        self.git("config", "user.email", "bench@example.test")
        self.git("config", "user.name", "BENCH offline fixture")
        self.git("config", "core.autocrlf", "false")
        self.source = self.root / "src/core/bOps.Runtime/source with spaces.cs"
        self.source.parent.mkdir(parents=True)
        self.source.write_bytes(b"AF-05G\n")
        config = self.root / "src/core/bOps.Api/appsettings.json"
        config.parent.mkdir(parents=True)
        config.write_bytes(b"{}")
        self.g = self.commit()
        self.source.write_bytes(b"AF-05H\n")
        self.h = self.commit()
        self.source.write_bytes(b"AF-05H2-R1\n")
        self.candidate = self.commit()
        self.anchors = patch.object(bench, "ANCHORS", [self.g, self.h])
        self.anchors.start()
        self.addCleanup(self.anchors.stop)

    def git(self, *args):
        return bench.git(self.root, *args)

    def commit(self):
        self.git("add", ".")
        self.git("-c", "core.hooksPath=NUL", "commit", "--quiet", "-m", "offline fixture")
        return self.git("rev-parse", "HEAD")

    def test_exact_candidate_accepts_committed_runtime_and_verifiable_provenance(self):
        anchor, head, status = bench.validate_runtime_target(self.root, self.candidate)
        self.assertEqual((self.h, self.candidate, ""), (anchor, head, status))
        evidence = bench.runtime_provenance(self.root, anchor, head, self.candidate)
        self.assertEqual("expected-target-commit", evidence["validationMode"])
        self.assertEqual(self.candidate, evidence["candidateCommit"])
        self.assertEqual(self.h, evidence["baselineCommit"])
        self.assertEqual([self.source.relative_to(self.root).as_posix()], evidence["changedFiles"])
        for key, paths in (("committedDiffSha256", []), ("runtimeDiffSha256", ["--", *evidence["runtimePaths"]])):
            raw = subprocess.check_output(["git", "-C", str(self.root), *evidence["diffGitArguments"], *paths])
            self.assertEqual(bench.sha(raw), evidence[key])
        self.assertEqual(evidence, bench.runtime_provenance(self.root, anchor, head, self.candidate))

    def test_wrong_sha_fails_before_provenance_or_api_startup(self):
        args = bench.make_argument_parser().parse_args(["--target-repo-root", str(self.root),
                                                       "--expected-target-commit", self.h, "--preflight-only"])
        with patch.object(bench, "start_api_process") as start, patch.object(bench, "runtime_provenance") as evidence:
            with self.assertRaisesRegex(RuntimeError, "HEAD does not match"):
                bench.run(args)
        start.assert_not_called()
        evidence.assert_not_called()

    def test_candidate_requires_af05h_ancestry_even_if_af05g_is_ancestor(self):
        self.git("checkout", "--quiet", "--detach", self.g)
        self.source.write_bytes(b"unrelated candidate\n")
        divergent = self.commit()
        with self.assertRaisesRegex(RuntimeError, "descend.*AF-05H"):
            bench.validate_runtime_target(self.root, divergent)

    def test_uncommitted_runtime_changes_rejected_staged_and_unstaged(self):
        self.source.write_bytes(b"dirty\n")
        for staged in (False, True):
            if staged:
                self.git("add", ".")
            with self.subTest(staged=staged), self.assertRaisesRegex(RuntimeError, "Uncommitted runtime"):
                bench.validate_runtime_target(self.root, self.candidate)

    def test_untracked_core_source_and_runtime_rename_to_config_are_rejected(self):
        extra = self.root / "src/core/bOps.Abstractions/new source.cs"
        extra.parent.mkdir(parents=True)
        extra.write_bytes(b"untracked\n")
        with self.assertRaisesRegex(RuntimeError, "Uncommitted runtime"):
            bench.validate_runtime_target(self.root, self.candidate)
        extra.unlink()
        self.git("mv", self.source.relative_to(self.root).as_posix(),
                 self.source.with_name("appsettings.json").relative_to(self.root).as_posix())
        with self.assertRaisesRegex(RuntimeError, "Uncommitted runtime"):
            bench.validate_runtime_target(self.root, self.candidate)

    def test_staged_change_is_rejected_even_when_working_content_matches_head(self):
        original = self.source.read_bytes()
        self.source.write_bytes(b"staged change\n")
        self.git("add", ".")
        self.source.write_bytes(original)
        with self.assertRaisesRegex(RuntimeError, "Uncommitted runtime"):
            bench.validate_runtime_target(self.root, self.candidate)

    def test_local_configuration_remains_allowed(self):
        config = self.root / "src/core/bOps.Api/appsettings.json"
        head = self.candidate
        config.write_bytes(b'{"local": true}')
        bench.validate_runtime_target(self.root, head)
        config.with_name("appsettings.Development.json").write_bytes(b"{}")
        bench.validate_runtime_target(self.root, head)

    def test_legacy_accepts_g_and_h_and_harness_only_descendants_but_rejects_candidate(self):
        with self.assertRaisesRegex(RuntimeError, "nearest supported"):
            bench.validate_runtime_target(self.root)
        for anchor in (self.g, self.h):
            self.git("checkout", "--quiet", "--detach", anchor)
            self.assertEqual(anchor, bench.validate_runtime_target(self.root)[0])
        (self.root / "harness.txt").write_bytes(b"collector only")
        descendant = self.commit()
        self.assertEqual((self.h, descendant, ""), bench.validate_runtime_target(self.root))
        self.source.write_bytes(b"dirty\n")
        with self.assertRaisesRegex(RuntimeError, "Uncommitted runtime"):
            bench.validate_runtime_target(self.root)

    def test_legacy_keeps_local_config_exception_and_reports_legacy_provenance(self):
        self.git("checkout", "--quiet", "--detach", self.h)
        (self.root / "src/core/bOps.Api/appsettings.json").write_bytes(b'{"local": true}')
        anchor, head, _ = bench.validate_runtime_target(self.root)
        evidence = bench.runtime_provenance(self.root, anchor, head, None)
        self.assertEqual("legacy-reference", evidence["validationMode"])
        self.assertEqual([], evidence["changedFiles"])
        self.assertEqual(bench.sha(b""), evidence["runtimeDiffSha256"])

    def test_parser_requires_full_sha_and_supports_both_spellings(self):
        parser = bench.make_argument_parser()
        self.assertIsNone(parser.parse_args(["--target-repo-root", str(self.root)]).expected_target_commit)
        for flag in ("--expected-target-commit", "-ExpectedTargetCommit"):
            self.assertEqual(self.candidate, parser.parse_args([
                "--target-repo-root", str(self.root), flag, self.candidate.upper(), "--preflight-only"
            ]).expected_target_commit)
            for value in ("HEAD", "abc123", "g" * 40, "a" * 41):
                with self.subTest(flag=flag, value=value), self.assertRaises(SystemExit), patch("sys.stderr", io.StringIO()):
                    parser.parse_args(["--target-repo-root", str(self.root), flag, value])


class CaptureTests(unittest.TestCase):
    def setUp(self):
        # Source resolution tests must not inherit an operator's configured paths.
        environment = {key: value for key, value in os.environ.items()
                       if key.casefold() not in {"settings__filepath", "vault__filepath"}}
        fixture_environment = patch.dict(os.environ, environment, clear=True)
        fixture_environment.start()
        self.addCleanup(fixture_environment.stop)

    def _write_ui_config(self, root, provider="fixture", model="fixture-model"):
        api = root / "source" / "src" / "core" / "bOps.Api"
        api.mkdir(parents=True, exist_ok=True)
        (api / "appsettings.json").write_text("{}", encoding="utf-8")
        (api / "appsettings.Development.json").unlink(missing_ok=True)
        (api / "settings.json").write_text(json.dumps({"activeProviderId": provider, "providers": {
            provider: {"model": model, "baseUrl": "https://example.invalid/v1"}}}), encoding="utf-8")
        (api / "vault.dat").write_bytes(b"ciphertext-only-fixture")
        return api.parents[2], api

    def test_real_api_settings_schema_from_git_root_in_both_target_modes(self):
        model = "nvidia/nemotron-3-super-120b-a12b:free"
        for expected in (None, "a" * 40):
            with self.subTest(expected=expected), tempfile.TemporaryDirectory() as temp:
                root = pathlib.Path(temp)
                source, api = self._write_ui_config(root)
                saved = {"SchemaVersion": 1, "Revision": 7, "ActiveProviderId": "OpenRouter",
                         "Providers": {"OpenRouter": {"ProviderId": "OpenRouter", "Model": model,
                                                       "BaseUrl": "https://openrouter.ai/api/v1"}}, "Fallbacks": []}
                (api / "settings.json").write_text(json.dumps(saved), encoding="utf-8-sig")
                # Runtime validation mode does not change source resolution.
                args = bench.make_argument_parser().parse_args([
                    "--target-repo-root", str(source), "--config-source-root", str(source)] +
                    (["--expected-target-commit", expected] if expected else []))
                snap, manifest, _ = bench.prepare_config_snapshot(pathlib.Path(args.config_source_root), root / "snap")
                selected = manifest["selectedConfiguration"]
                self.assertEqual(str(source.resolve()), manifest["sourceRoot"])
                self.assertEqual("OpenRouter", selected["provider"])
                self.assertEqual(model, selected["modelId"])
                self.assertEqual((api / "settings.json").read_bytes(), (snap / "settings.json").read_bytes())
                bench.verify_loaded_configuration(
                    {"active": {"provider": "OpenRouter", "model": model}},
                    {"effectiveModel": model, "effectiveBaseUrl": "https://openrouter.ai/api/v1",
                     "effectiveFallbacks": []}, selected)
                self.assertEqual(manifest["configurationFingerprint"],
                                 bench.config_snapshot_manifest(snap)["configurationFingerprint"])
                self.assertTrue(bench.prepare_config_snapshot(source, root / "unused", snap)[2])

    def test_configured_paths_use_api_working_directory_and_configuration_precedence(self):
        for mode in ("relative", "absolute", "development", "environment", "relative-environment"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temp:
                root = pathlib.Path(temp)
                source, api = self._write_ui_config(root)
                custom = api / "custom files"
                custom.mkdir()
                settings = custom / "selected.json"
                vault = custom / "encrypted.dat"
                settings.write_bytes((api / "settings.json").read_bytes())
                vault.write_bytes(b"custom-ciphertext")
                values = {"Settings": {"FilePath": str(settings) if mode == "absolute" else "custom files/selected.json"},
                          "Vault": {"FilePath": str(vault) if mode == "absolute" else "custom files/encrypted.dat"}}
                if mode in ("development", "environment", "relative-environment"):
                    bench.write_json(api / "appsettings.json", {"Settings": {"FilePath": "missing.json"},
                                                               "Vault": {"FilePath": "missing.dat"}})
                if mode == "development":
                    bench.write_json(api / "appsettings.Development.json", values)
                elif mode not in ("environment", "relative-environment"):
                    bench.write_json(api / "appsettings.json", values)
                overrides = {"Settings__FilePath": str(settings), "Vault__FilePath": str(vault)} if mode == "environment" else (
                    {"settings__filepath": "custom files/selected.json", "vault__filepath": "custom files/encrypted.dat"}
                    if mode == "relative-environment" else {})
                with patch.dict(os.environ, overrides):
                    snap, manifest, _ = bench.prepare_config_snapshot(source, root / "snap")
                    self.assertEqual(settings.read_bytes(), (snap / "settings.json").read_bytes())
                    self.assertEqual(vault.read_bytes(), (snap / "vault.dat").read_bytes())
                    self.assertEqual(bench.sha(vault.read_bytes()), manifest["files"]["vault.dat"]["sha256"])
                    self.assertTrue(bench.prepare_config_snapshot(source, root / "unused", snap)[2])
                    # Default files are decoys and cannot drive freshness checks.
                    (api / "settings.json").write_text("{}", encoding="utf-8")
                    (api / "vault.dat").write_bytes(b"decoy-changed")
                    self.assertTrue(bench.prepare_config_snapshot(source, root / "unused", snap)[2])
                    vault.write_bytes(b"changed-ciphertext")
                    self.assertTrue(bench.prepare_config_snapshot(source, root / "unused", snap)[2])

    def test_development_path_override_preserves_other_section_fields(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            source, api = self._write_ui_config(root)
            custom = api / "custom"
            custom.mkdir()
            (custom / "selected.json").write_bytes((api / "settings.json").read_bytes())
            (custom / "encrypted.dat").write_bytes(b"custom-ciphertext")
            bench.write_json(api / "appsettings.json", {
                "Settings": {"FilePath": "custom/selected.json"}, "Vault": {"FilePath": "missing.dat"}})
            bench.write_json(api / "appsettings.Development.json", {
                "settings": {"OtherOption": True}, "vault": {"filepath": "custom/encrypted.dat"}})
            snap, _, _ = bench.prepare_config_snapshot(source, root / "snap")
            self.assertEqual(b"custom-ciphertext", (snap / "vault.dat").read_bytes())
            self.assertEqual("fixture-model", bench.config_snapshot_manifest(snap)["selectedConfiguration"]["modelId"])

    def test_configured_path_missing_does_not_fall_back_to_default_files(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            source, api = self._write_ui_config(root)
            bench.write_json(api / "appsettings.json", {"Settings": {"FilePath": "missing.json"}})
            with self.assertRaisesRegex(RuntimeError, "missing required files: settings.json"):
                bench.prepare_config_snapshot(source, root / "snap")

    def test_snapshot_rejects_tampering_with_files_metadata_and_fingerprint(self):
        for change in ("file", "vault", "added-optional-file", "metadata", "fingerprint"):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as temp:
                root = pathlib.Path(temp)
                source, _ = self._write_ui_config(root)
                snap, manifest, _ = bench.prepare_config_snapshot(source, root / "snap")
                if change == "file":
                    (snap / "settings.json").write_text("{}", encoding="utf-8")
                elif change == "vault":
                    (snap / "vault.dat").write_bytes(b"changed-ciphertext")
                elif change == "added-optional-file":
                    (snap / "appsettings.Development.json").write_text("{}", encoding="utf-8")
                else:
                    if change == "metadata":
                        manifest["selectedConfiguration"]["modelId"] = "wrong-model"
                    else:
                        manifest["configurationFingerprint"] = "invalid"
                    bench.write_json(snap / "config-snapshot.json", manifest)
                with self.assertRaises(RuntimeError):
                    bench.prepare_config_snapshot(source, root / "unused", snap)

    def test_snapshot_reused_after_ui_change_without_reading_source(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            source, api = self._write_ui_config(root)
            snap, manifest, _ = bench.prepare_config_snapshot(source, root / "snapshot")
            (api / "settings.json").write_text(json.dumps({"activeProviderId": "openrouter", "providers": {
                "openrouter": {"model": "new-model", "baseUrl": "https://example.invalid/v1"}}}), encoding="utf-8")
            (api / "vault.dat").unlink()
            with patch.object(bench, "configuration_source_files", side_effect=AssertionError("explicit snapshot must not read UI")):
                reused, loaded, explicit = bench.prepare_config_snapshot(root / "missing-source", root / "unused", snap)
            self.assertEqual(snap, reused)
            self.assertTrue(explicit)
            self.assertEqual(manifest["configurationFingerprint"], loaded["configurationFingerprint"])
            self.assertEqual("fixture-model", loaded["selectedConfiguration"]["modelId"])

    def test_snapshot_requires_selected_ui_configuration(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            source, api = self._write_ui_config(root)
            (api / "settings.json").write_text('{"providers":{}}', encoding="utf-8")
            with self.assertRaisesRegex(RuntimeError, "missing an active provider"):
                bench.prepare_config_snapshot(source, root / "snapshot")

    def test_legacy_schema_snapshot_remains_reusable(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            source, _ = self._write_ui_config(root)
            snap, manifest, _ = bench.prepare_config_snapshot(source, root / "snapshot")
            manifest.pop("selectedConfiguration")
            manifest.pop("configurationFingerprint")
            manifest.pop("snapshotCompatibility", None)
            bench.write_json(snap / "config-snapshot.json", manifest)
            _, reused_manifest, reused = bench.prepare_config_snapshot(source, root / "unused", snap)
            self.assertTrue(reused)
            self.assertEqual("fixture-model", reused_manifest["selectedConfiguration"]["modelId"])

    def test_snapshot_never_copies_vault_into_capture_archive(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            source, _ = self._write_ui_config(root)
            _, _, _ = bench.prepare_config_snapshot(source, root / "snapshot")
            capture = root / "capture"
            capture.mkdir()
            (capture / "startup.json").write_text("{}", encoding="utf-8")
            archive = root / "capture.zip"
            bench.package(capture, archive)
            with zipfile.ZipFile(archive) as zf:
                content = b"".join(zf.read(name) for name in zf.namelist())
            self.assertNotIn(b"ciphertext-only-fixture", content)
            self.assertNotIn(b"vault.dat", content)

    def test_loaded_provider_model_and_fallbacks_are_fail_closed(self):
        selected = {"provider": "llamacpp", "modelId": "model-q4", "endpoint": "http://localhost:8080",
                    "endpointFingerprint": bench.sha(b"http://localhost:8080/v1")}
        good = {"active": {"provider": "llamacpp", "model": "model-q4", "baseUrl": "http://localhost:8080/v1"}}
        settings = {"effectiveModel": "model-q4", "effectiveBaseUrl": "http://localhost:8080/v1", "effectiveFallbacks": []}
        self.assertEqual("llamacpp", bench.verify_loaded_configuration(good, settings, selected)["provider"])
        cases = (("openrouter", "model-q4", []), ("llamacpp", "other", []),
                 ("llamacpp", "model-q4", [{"provider": "openrouter"}]))
        for active, model, fallbacks in cases:
            with self.subTest(active=active, model=model, fallbacks=fallbacks):
                bad_provider = {"active": {"provider": active, "model": model, "baseUrl": "http://localhost:8080/v1"}}
                bad_settings = {"effectiveModel": model, "effectiveBaseUrl": "http://localhost:8080/v1", "effectiveFallbacks": fallbacks}
                with self.assertRaises(RuntimeError):
                    bench.verify_loaded_configuration(bad_provider, bad_settings, selected)

    def test_requested_run_counts_produce_expected_round_robin(self):
        for runs, expected in ((1, ["B01", "B02", "B03"]), (3, ["B01", "B02", "B03"] * 3)):
            roster = [(scenario["id"], round_no) for round_no in range(1, runs + 1) for scenario in bench.SCENARIOS]
            self.assertEqual(expected, [scenario for scenario, _ in roster])
            self.assertEqual(3 * runs, len(roster))

    def test_runs_per_scenario_parser_default_valid_and_invalid_values(self):
        parser = bench.make_argument_parser()
        base = ["--target-repo-root", "."]
        self.assertEqual(3, parser.parse_args(base).runs_per_scenario)
        self.assertEqual(1, parser.parse_args(base + ["--runs-per-scenario", "1"]).runs_per_scenario)
        self.assertEqual(3, parser.parse_args(base + ["--runs-per-scenario", "3"]).runs_per_scenario)
        for value in ("0", "4", "invalid"):
            with self.subTest(value=value), self.assertRaises(SystemExit):
                parser.parse_args(base + ["--runs-per-scenario", value])

    def test_numeric_task_statuses_cover_the_complete_api_enum(self):
        expected = ["Running", "Completed", "MaxStepsReached", "BudgetExceeded",
                    "PolicyBlocked", "ReplanLimitReached", "Failed", "Cancelled"]
        for value, name in enumerate(expected):
            with self.subTest(value=value):
                self.assertEqual(name, bench.task_status_name(value))
                self.assertEqual(name, bench.task_status_name(str(value)))
        for value in (-1, 8, True, "unknown"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                bench.task_status_name(value)

    def test_scenario_runs_use_b01_b02_b03_order(self):
        self.assertEqual([(1, "B01"), (1, "B02"), (1, "B03")],
                         [(round_no, scenario["id"]) for round_no, scenario in bench.scenario_runs(1)])

    def test_every_terminal_numeric_status_ends_poll_but_running_continues(self):
        for value in range(1, 8):
            raw = json.dumps({"status": value, "executing": False}).encode()
            payload = json.loads(raw)
            with self.subTest(status=value), patch.object(bench, "http_json", return_value=(200, raw, payload)) as request:
                seen = []
                response, parsed = bench.poll_task("http://local", "key", "task", 30, seen.append)
                self.assertEqual(raw, response)
                self.assertEqual(raw, seen[0])
                self.assertEqual(1, request.call_count)
                self.assertEqual(value, parsed["status"])
        running = b'{"status":0,"executing":true}'
        responses = [(200, running, json.loads(running)),
                     (200, b'{"status":1,"executing":false}', {"status": 1, "executing": False})]
        with patch.object(bench, "http_json", side_effect=responses) as request, patch.object(bench.time, "sleep"):
            raw, _ = bench.poll_task("http://local", "key", "task", 30)
        self.assertEqual(2, request.call_count)
        self.assertEqual(b'{"status":1,"executing":false}', raw)

    def test_textual_enum_names_remain_supported(self):
        for name in bench.TASK_STATUS_BY_VALUE.values():
            with self.subTest(name=name):
                self.assertEqual(name, bench.task_status_name(name))

    def test_numeric_task_statuses_cover_the_complete_api_enum(self):
        expected = ["Running", "Completed", "MaxStepsReached", "BudgetExceeded",
                    "PolicyBlocked", "ReplanLimitReached", "Failed", "Cancelled"]
        for value, name in enumerate(expected):
            with self.subTest(value=value):
                self.assertEqual(name, bench.task_status_name(value))
                self.assertEqual(name, bench.task_status_name(str(value)))
        for value in (-1, 8, True, "unknown"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                bench.task_status_name(value)

    def test_scenario_runs_use_b01_b02_b03_order_after_each_terminal_poll(self):
        self.assertEqual([(1, "B01"), (1, "B02"), (1, "B03")],
                         [(round_no, scenario["id"]) for round_no, scenario in bench.scenario_runs(1)])

    def test_every_terminal_numeric_status_ends_poll_but_running_continues(self):
        for value in range(1, 8):
            raw = json.dumps({"status": value, "executing": False}).encode()
            payload = json.loads(raw)
            with self.subTest(status=value), patch.object(bench, "http_json", return_value=(200, raw, payload)) as request:
                seen = []
                response, parsed = bench.poll_task("http://local", "key", "task", 30, seen.append)
                self.assertEqual(raw, response)
                self.assertEqual(raw, seen[0])
                self.assertEqual(1, request.call_count)
                self.assertEqual(value, parsed["status"])
        running = b'{"status":0,"executing":true}'
        responses = [(200, running, json.loads(running)),
                     (200, b'{"status":1,"executing":false}', {"status": 1, "executing": False})]
        with patch.object(bench, "http_json", side_effect=responses) as request, patch.object(bench.time, "sleep"):
            raw, _ = bench.poll_task("http://local", "key", "task", 30)
        self.assertEqual(2, request.call_count)
        self.assertEqual(b'{"status":1,"executing":false}', raw)

    def test_textual_enum_names_remain_supported(self):
        for name in bench.TASK_STATUS_BY_VALUE.values():
            with self.subTest(name=name):
                self.assertEqual(name, bench.task_status_name(name))

    def test_powershell_launcher_passes_run_count_to_python_and_uac(self):
        launcher = (MODULE.parent / "run-live-benchmark.ps1").read_text(encoding="utf-8")
        self.assertIn("[ValidateRange(1, 3)] [int] $RunsPerScenario = 3", launcher)
        self.assertIn("'-RunsPerScenario', '$RunsPerScenario'", launcher)
        self.assertIn("'--runs-per-scenario', [string]$RunsPerScenario", launcher)
        self.assertIn("$startInfo.ArgumentList.Add([string]$argument)", launcher)
        self.assertIn("$startInfo.Arguments = $argumentLine", launcher)

    @unittest.skipUnless(os.name == "nt", "Windows PowerShell integration test")
    def test_launcher_invokes_python_with_absolute_script_and_distinct_target(self):
        powershell = shutil.which("pwsh") or shutil.which("powershell")
        if not powershell:
            self.skipTest("PowerShell is unavailable")
        with tempfile.TemporaryDirectory(prefix="BENCH launcher ") as temp:
            root = pathlib.Path(temp)
            script_dir = root / "BENCH script path" / "scripts" / "windows"
            script_dir.mkdir(parents=True)
            launcher = script_dir / "run-live-benchmark.ps1"
            source = MODULE.parent / "run-live-benchmark.ps1"
            launcher_text = source.read_text(encoding="utf-8")
            # Exercise the already-elevated branch without needing an actual UAC token.
            launcher_text = launcher_text.replace(
                "if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {",
                "if ($false) {",
            )
            launcher.write_text(launcher_text, encoding="utf-8")
            (script_dir / "live_benchmark.py").write_text(
                "import csv, io, json, os, subprocess, sys\n"
                "actual = next(csv.reader(io.StringIO(subprocess.check_output(['whoami','/user','/fo','csv','/nh'], text=True).strip())))[-1].strip()\n"
                "with open(os.environ['BENCH_ARGV_CAPTURE'], 'w', encoding='utf-8') as f: "
                "json.dump({'argv': sys.argv, 'masterKeyPresent': bool(os.environ.get('BOPS_VAULT_MASTER_KEY')), 'actualSid': actual}, f)\n",
                encoding="utf-8",
            )
            target = root / "target worktree"
            target.mkdir()
            capture = root / "argv.json"
            env = os.environ.copy()
            env["PATH"] = str(root) + os.pathsep + env.get("PATH", "")
            env["BENCH_ARGV_CAPTURE"] = str(capture)
            env["BOPS_VAULT_MASTER_KEY"] = "synthetic-master-secret"
            result = subprocess.run(
                [powershell, "-NoProfile", "-File", str(launcher), "-TargetRepoRoot", str(target),
                 "-RunsPerScenario", "1", "-Elevated", "-PreflightOnly",
                 "-ExpectedTargetCommit", bench.ANCHORS[-1]],
                cwd=root, env=env, text=True, capture_output=True, timeout=30,
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            captured = json.loads(capture.read_text(encoding="utf-8"))
            argv = captured["argv"]
            self.assertTrue(captured["masterKeyPresent"])
            self.assertNotIn("synthetic-master-secret", argv)
            self.assertEqual((script_dir / "live_benchmark.py").resolve(), pathlib.Path(argv[0]).resolve())
            self.assertEqual("--target-repo-root", argv[1])
            self.assertEqual(target.resolve(), pathlib.Path(argv[2]).resolve())
            self.assertEqual("--config-source-root", argv[3])
            self.assertEqual(str((script_dir.parent.parent).resolve()), argv[4])
            self.assertEqual(["--runs-per-scenario", "1"], argv[5:7])
            self.assertEqual("--expected-user-sid", argv[7])
            self.assertEqual(captured["actualSid"], argv[8])
            self.assertEqual(["--expected-target-commit", bench.ANCHORS[-1], "--preflight-only"], argv[9:])
            legacy = subprocess.run(
                [powershell, "-NoProfile", "-File", str(launcher), "-TargetRepoRoot", str(target),
                 "-RunsPerScenario", "1", "-Elevated"],
                cwd=root, env=env, text=True, capture_output=True, timeout=30,
            )
            self.assertEqual(0, legacy.returncode, legacy.stdout + legacy.stderr)
            legacy_argv = json.loads(capture.read_text(encoding="utf-8"))["argv"]
            self.assertEqual(argv[:9], legacy_argv)

    @unittest.skipUnless(os.name == "nt", "Windows PowerShell integration test")
    def test_uac_handoff_preserves_target_and_run_count(self):
        powershell = shutil.which("pwsh") or shutil.which("powershell")
        if not powershell:
            self.skipTest("PowerShell is unavailable")
        with tempfile.TemporaryDirectory(prefix="BENCH UAC ") as temp:
            root = pathlib.Path(temp)
            launcher = root / "run-live-benchmark.ps1"
            text = (MODULE.parent / "run-live-benchmark.ps1").read_text(encoding="utf-8")
            # Force the non-elevated branch; Start-Process is stubbed to inspect its encoded payload.
            text = text.replace(
                "if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {",
                "if ($true) {",
            )
            launcher.write_text(text, encoding="utf-8")
            target = root / "target worktree"
            target.mkdir()
            source = root / "configuration source"
            # Explicit snapshots must survive removal of the original UI source.
            snapshot = root / "private snapshot"
            snapshot.mkdir()
            payload_file = root / "uac-payload.txt"
            command = (
                "function Start-Process { param($FilePath,$Verb,$ArgumentList,[switch]$Wait,[switch]$PassThru); "
                "$payload=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($ArgumentList[-1])); "
                f"[IO.File]::WriteAllText('{payload_file}', $payload); "
                "[pscustomobject]@{ExitCode=0} }; "
                f"$env:BOPS_VAULT_MASTER_KEY='fixture-master-secret'; "
                f"& '{launcher}' -TargetRepoRoot '{target}' -ConfigSourceRoot '{source}' -ConfigSnapshotPath '{snapshot}' -RunsPerScenario 1 "
                f"-ExpectedTargetCommit {bench.ANCHORS[-1]} -PreflightOnly"
            )
            result = subprocess.run([powershell, "-NoProfile", "-Command", command], cwd=root,
                                    text=True, capture_output=True, timeout=30)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            payload = payload_file.read_text(encoding="utf-8")
            self.assertIn("'-TargetRepoRoot', $targetPath", payload)
            self.assertIn("'-ConfigSourceRoot', $configSourcePath", payload)
            self.assertIn("'-RunsPerScenario', '1'", payload)
            self.assertIn("'-ExpectedUserSid', $expectedSid", payload)
            self.assertIn("-PreflightOnly", payload)
            self.assertIn("@('-ExpectedTargetCommit', $expectedCommit)", payload)
            self.assertIn("@('-ConfigSnapshotPath', $snapshotPath)", payload)
            # Execute the handoff payload with the launcher replaced by an argv
            # recorder, proving the decoded value survives the second invocation.
            recorder = root / "record.ps1"
            recorder.write_text("[IO.File]::WriteAllText($env:BENCH_UAC_ARGV, ($args | ConvertTo-Json))", encoding="utf-8")
            replay = root / "replay.ps1"
            replay.write_text(payload.replace("& $scriptPath @benchArgs", f"& '{recorder}' @benchArgs"), encoding="utf-8")
            capture = root / "uac-argv.json"
            env = dict(os.environ, BENCH_UAC_ARGV=str(capture))
            replay_result = subprocess.run([powershell, "-NoProfile", "-File", str(replay)],
                                           env=env, text=True, capture_output=True, timeout=30)
            self.assertEqual(0, replay_result.returncode, replay_result.stdout + replay_result.stderr)
            argv = json.loads(capture.read_text(encoding="utf-8-sig"))
            self.assertEqual(bench.ANCHORS[-1], argv[argv.index("-ExpectedTargetCommit") + 1])
            self.assertTrue(snapshot.samefile(argv[argv.index("-ConfigSnapshotPath") + 1]))
            self.assertEqual(str(target), argv[argv.index("-TargetRepoRoot") + 1])
            self.assertIn("-PreflightOnly", argv)
            self.assertIn("$targetPath = [Text.Encoding]::UTF8.GetString", payload)
            self.assertNotIn("fixture-master-secret", payload)

    def test_benchmark_policy_only_allows_read_risk_automatically(self):
        self.assertEqual("defaults:\n  read: automatic\n", bench.READ_ONLY_POLICY)

    @unittest.skipUnless(os.name == "nt", "Windows identity validation")
    def test_windows_identity_matches_expected_sid_without_revealing_it(self):
        rows = csv.reader(io.StringIO(
            subprocess.check_output(["whoami", "/user", "/fo", "csv", "/nh"], text=True).strip()))
        sid = next(rows)[-1].strip()
        result = bench.verify_windows_identity(sid)
        self.assertTrue(result["verified"])
        self.assertTrue(result["expectedSidMatched"])
        with self.assertRaisesRegex(RuntimeError, "identity does not match"):
            bench.verify_windows_identity("S-1-5-21-not-this-user")

    def test_failed_repackage_keeps_last_complete_archive(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            capture = root / "capture"
            capture.mkdir()
            (capture / "evidence.txt").write_text("partial capture")
            archive = root / "capture.zip"
            archive.write_bytes(b"previous complete archive")
            with patch.object(bench.zipfile, "ZipFile", side_effect=OSError("fixture failure")):
                with self.assertRaises(OSError):
                    bench.package(capture, archive)
            self.assertEqual(b"previous complete archive", archive.read_bytes())

    def test_api_exit_one_preserves_complete_startup_output_in_local_log_and_archive(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            log_path = root / "api-startup.log"
            stdout = b"dotnet stdout before failure\n"
            stderr_prefix = "Unhandled exception: "
            stderr_suffix = "full diagnostic detail " * 12000 + "\n"
            stderr = (stderr_prefix + stderr_suffix).encode("utf-8")
            original = stdout + stderr
            code = ("import sys; sys.stdout.buffer.write(" + repr(stdout) + "); sys.stdout.flush(); "
                    + "sys.stderr.buffer.write((" + repr(stderr_prefix) + " + " + repr("full diagnostic detail ")
                    + " * 12000 + " + repr("\n") + ").encode()); sys.stderr.flush(); sys.exit(1)")
            process, stream, output_thread = bench.start_api_process([sys.executable, "-c", code], root, os.environ.copy(), log_path)
            self.assertEqual(1, process.wait(timeout=15))
            output_thread.join(timeout=15)
            process.stdout.close()
            stream.close()

            with self.assertRaises(bench.ApiStartupFailure) as caught:
                bench.check_api_startup(process, log_path)
            self.assertEqual(1, caught.exception.exit_code)
            self.assertEqual(log_path, caught.exception.log_path)
            self.assertEqual(original, log_path.read_bytes())

            capture_root = root / "capture"
            archived_log = bench.include_startup_diagnostic(log_path, capture_root)
            archive = root / "forensic.zip"
            bench.package(capture_root, archive)
            with zipfile.ZipFile(archive) as zf:
                self.assertEqual(original, zf.read("diagnostics/api-startup.log"))
                self.assertEqual(original, archived_log.read_bytes())

    def test_preflight_cleanup_stops_child_holding_inherited_stdout(self):
        """dotnet run's API child can keep the pipe open after the launcher exits."""
        with tempfile.TemporaryDirectory(prefix="BENCH child cleanup ") as temp:
            root = pathlib.Path(temp)
            log_path = root / "api-startup.log"
            ready_path = root / "child-ready"
            child = ("import pathlib, time; print('API child ready', flush=True); "
                     "pathlib.Path(" + repr(str(ready_path)) + ").write_text('ready'); time.sleep(300)")
            parent = ("import pathlib, subprocess, sys, time\n"
                      "subprocess.Popen([sys.executable, '-c', " + repr(child) + "])\n"
                      "ready=pathlib.Path(" + repr(str(ready_path)) + ")\n"
                      "deadline=time.monotonic()+10\n"
                      "while not ready.exists() and time.monotonic()<deadline: time.sleep(.01)\n"
                      "time.sleep(300)\n")
            process, stream, output_thread = bench.start_api_process(
                [sys.executable, "-c", parent], root, os.environ.copy(), log_path)
            deadline = bench.time.monotonic() + 10
            while not ready_path.exists() and bench.time.monotonic() < deadline:
                bench.time.sleep(0.01)
            self.assertTrue(ready_path.exists(), "fixture API child did not start")
            bench.stop_api_process(process, output_thread, stream)
            self.assertIsNotNone(process.poll())
            self.assertFalse(output_thread.is_alive())
            self.assertIn(b"API child ready", log_path.read_bytes())

    def test_startup_log_redacts_environment_credentials_without_truncating(self):
        secret = "synthetic-master-secret"
        output = b"prefix " + secret.encode() + b" suffix\n"
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            log_path = root / "api-startup.log"
            code = "import os,sys; sys.stdout.write('prefix '); sys.stdout.flush(); sys.stdout.write(os.environ['BOPS_VAULT_MASTER_KEY'] + ' suffix\\n')"
            env = os.environ.copy()
            env["BOPS_VAULT_MASTER_KEY"] = secret
            process, stream, output_thread = bench.start_api_process([sys.executable, "-c", code], root, env, log_path)
            self.assertEqual(0, process.wait(timeout=15))
            output_thread.join(timeout=15)
            process.stdout.close()
            stream.close()
            saved = log_path.read_bytes()
            # The child writes a text-mode "\n": the platform terminator is CRLF on Windows and LF elsewhere. The collector keeps
            # the original bytes, so the expectation is exact for each platform instead of normalized.
            terminator = b"\r\n" if os.name == "nt" else b"\n"
            self.assertEqual(b"prefix [REDACTED: credential] suffix" + terminator, saved)
            self.assertNotIn(secret.encode(), saved)

    def test_startup_log_redacts_credentials_found_in_snapshot_configuration(self):
        secret = "fixture-provider-secret"
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            snapshot = root / "snapshot"
            snapshot.mkdir()
            (snapshot / "appsettings.json").write_text(json.dumps({"Provider": {"ApiKey": secret}}), encoding="utf-8")
            log_path = root / "api-startup.log"
            env = os.environ.copy()
            env.update({"ASPNETCORE_CONTENTROOT": str(snapshot), "FIXTURE_OUTPUT": secret})
            code = "import os,sys; sys.stdout.write(os.environ['FIXTURE_OUTPUT'])"
            process, stream, output_thread = bench.start_api_process([sys.executable, "-c", code], root, env, log_path)
            self.assertEqual(0, process.wait(timeout=15))
            output_thread.join(timeout=15)
            process.stdout.close()
            stream.close()
            self.assertEqual(b"[REDACTED: credential]", log_path.read_bytes())
            self.assertNotIn(secret.encode(), log_path.read_bytes())

    def test_config_snapshot_requires_sources_and_copies_vault_only_to_private_snapshot(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            source_api = root / "source" / "src" / "core" / "bOps.Api"
            source_api.mkdir(parents=True)
            destination = root / "snapshot"
            with self.assertRaisesRegex(RuntimeError, "missing required files"):
                bench.prepare_config_snapshot(source_api.parents[2], destination)
            for name, data in (("appsettings.json", b'{"Provider":"fixture"}'),
                               ("settings.json", b'{"activeProviderId":"fixture","providers":{"fixture":{"model":"fixture-model","baseUrl":"http://localhost:1234/v1"}}}'), ("vault.dat", b"synthetic-vault"),
                               ("appsettings.Development.json", b'{"Fixture":"development"}')):
                (source_api / name).write_bytes(data)
            snapshot, manifest, reused = bench.prepare_config_snapshot(source_api.parents[2], destination)
            self.assertFalse(reused)
            self.assertEqual(b"synthetic-vault", (snapshot / "vault.dat").read_bytes())
            self.assertEqual(bench.sha(b"synthetic-vault"), manifest["files"]["vault.dat"]["sha256"])
            self.assertEqual(b'{"Provider":"fixture"}', (source_api / "appsettings.json").read_bytes())
            archive_root = root / "capture"
            archive_root.mkdir()
            (archive_root / "startup.json").write_text(json.dumps({"configurationProvenance": {"files": manifest["files"]}}))
            archive = root / "capture.zip"
            bench.package(archive_root, archive)
            with zipfile.ZipFile(archive) as zf:
                self.assertFalse(any(pathlib.PurePosixPath(name).name == "vault.dat" for name in zf.namelist()))
            reused_snapshot, _, reused = bench.prepare_config_snapshot(source_api.parents[2], root / "unused", snapshot)
            self.assertTrue(reused)
            self.assertEqual(snapshot, reused_snapshot)
            self.assertFalse((root / "unused").exists())

    def test_api_environment_isolates_state_and_uses_snapshot_content_root(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            snapshot = root / "snapshot"
            state = root / "state"
            snapshot.mkdir()
            (snapshot / "settings.json").write_text("{}")
            (snapshot / "vault.dat").write_bytes(b"fixture")
            state.mkdir()
            names = ("tasks.db", "audit.jsonl", "sessions.db", "system-messages.db", "delegations.db", "filesystem-inventory.db", "read-only-policy.yaml")
            paths = {name: state / name for name in names}
            env = bench.build_api_environment(snapshot, paths, 48123, "S-1-5-21-fixture", {})
            self.assertTrue(pathlib.Path(env["ASPNETCORE_CONTENTROOT"]).samefile(snapshot))
            self.assertTrue(pathlib.Path(env["Settings__FilePath"]).samefile(snapshot / "settings.json"))
            self.assertTrue(pathlib.Path(env["Vault__FilePath"]).samefile(snapshot / "vault.dat"))
            for name in names[:-1]:
                self.assertEqual(str(paths[name]), env[
                    {"tasks.db": "Memory__FilePath", "audit.jsonl": "Audit__FilePath", "sessions.db": "BrowserSession__FilePath",
                     "system-messages.db": "SystemMessages__FilePath", "delegations.db": "Delegation__FilePath",
                     "filesystem-inventory.db": "Filesystem__Inventory__ManifestStorePath"}[name]])
            self.assertEqual("S-1-5-21-fixture", env["BENCH_EXPECTED_WINDOWS_USER_SID"])

    def test_web_application_create_builder_loads_appsettings_from_snapshot_content_root(self):
        dotnet = shutil.which("dotnet")
        if not dotnet:
            self.skipTest("dotnet SDK is unavailable")
        with tempfile.TemporaryDirectory(prefix="BENCH content root ") as temp:
            root = pathlib.Path(temp)
            project = root / "target" / "ApiFixture"
            project.mkdir(parents=True)
            snapshot = root / "snapshot"
            snapshot.mkdir()
            working = root / "working"
            working.mkdir()
            (project / "ApiFixture.csproj").write_text(
                "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><OutputType>Exe</OutputType>"
                "<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>"
                "<Nullable>enable</Nullable></PropertyGroup></Project>", encoding="utf-8")
            (project / "Program.cs").write_text(
                "var builder = WebApplication.CreateBuilder(args); "
                "Console.WriteLine(\"BENCH_FIXTURE=\" + builder.Configuration[\"BenchFixture:Value\"]); "
                "Console.WriteLine(\"BENCH_CWD=\" + Directory.GetCurrentDirectory()); "
                "Console.WriteLine(\"BENCH_ROOT=\" + builder.Environment.ContentRootPath);", encoding="utf-8")
            (snapshot / "appsettings.json").write_text(
                json.dumps({"BenchFixture": {"Value": "loaded-from-snapshot"}}), encoding="utf-8")
            env = os.environ.copy()
            env.update({"ASPNETCORE_CONTENTROOT": str(snapshot), "ASPNETCORE_ENVIRONMENT": "Development",
                        "DOTNET_ENVIRONMENT": "Development", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"})
            result = subprocess.run([dotnet, "run", "--project", str(project / "ApiFixture.csproj"), "--no-launch-profile"],
                                    cwd=working, env=env, text=True, capture_output=True, timeout=120)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("BENCH_FIXTURE=loaded-from-snapshot", result.stdout)
            roots = [pathlib.Path(line.partition("=")[2]) for line in result.stdout.splitlines()
                     if line.startswith("BENCH_ROOT=")]
            self.assertTrue(any(path.samefile(snapshot) for path in roots))
            working_directories = [pathlib.Path(line.partition("=")[2]) for line in result.stdout.splitlines()
                                   if line.startswith("BENCH_CWD=")]
            self.assertTrue(any(path.samefile(project) for path in working_directories))

    def test_preflight_checks_provider_without_submitting_tasks(self):
        for expected in (None, bench.ANCHORS[-1]):
            for reuse in (False, True):
                with self.subTest(expected=expected, reuse=reuse):
                    self._check_offline_preflight(expected, reuse)

    def test_reused_snapshot_preflight_rejects_wrong_model_endpoint_and_missing_credentials(self):
        for failure in ("model", "endpoint", "credentials"):
            with self.subTest(failure=failure):
                self._check_offline_preflight("a" * 40, True, failure)

    def test_helper_bootstrap_restores_both_projects_and_fails_closed(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            names = ("bOps.Packages.Firewall.Windows.Helper", "bOps.Packages.System.Windows.Updates.Helper")
            for name in names:
                project = root / "src" / "packages" / name / f"{name}.csproj"
                project.parent.mkdir(parents=True)
                project.write_text("fixture")
            env = {"fixture": "environment"}
            with patch.object(bench.subprocess, "run", return_value=unittest.mock.Mock(returncode=0)) as restore:
                results = bench.restore_api_helpers(root, env)
            self.assertEqual(2, len(results))
            for call, name in zip(restore.call_args_list, names):
                self.assertEqual(["dotnet", "restore", str(root / "src" / "packages" / name / f"{name}.csproj"), "--locked-mode"], call.args[0])
                self.assertIs(env, call.kwargs["env"])
                self.assertEqual(subprocess.DEVNULL, call.kwargs["stdout"])
            with patch.object(bench.subprocess, "run", return_value=unittest.mock.Mock(returncode=1)) as restore:
                with self.assertRaisesRegex(RuntimeError, "restore failed.*no API was started"):
                    bench.restore_api_helpers(root, env)
                self.assertEqual(1, restore.call_count)

    def _check_offline_preflight(self, expected, reuse, failure=None):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            source_api = root / "source" / "src" / "core" / "bOps.Api"
            source_api.mkdir(parents=True)
            for name in bench.REQUIRED_SNAPSHOT_FILES:
                (source_api / name).write_text("{}")
            (source_api / "settings.json").write_text(json.dumps({"ActiveProviderId": "fixture", "Providers": {
                "fixture": {"Model": "fixture-model", "BaseUrl": "https://example.test"}}, "Fallbacks": []}), encoding="utf-8")
            target = root / "target"
            target.mkdir()
            fake_project = target / "src" / "core" / "bOps.Api" / "bOps.Api.csproj"
            fake_project.parent.mkdir(parents=True)
            fake_project.write_text("fixture", encoding="utf-8")
            output = root / "output"
            args = bench.make_argument_parser().parse_args([
                "--target-repo-root", str(target), "--config-source-root", str(source_api.parents[2]),
                "--output-root", str(output), "--preflight-only",
            ] + (["--expected-target-commit", expected] if expected else []))
            if reuse:
                args.config_snapshot_path = str(bench.prepare_config_snapshot(source_api.parents[2], root / "snapshot")[0])
                (source_api / "settings.json").write_text("{}", encoding="utf-8")
                (source_api / "vault.dat").unlink()
            process = unittest.mock.Mock()
            process.poll.side_effect = [None, 0]
            process.wait.return_value = 0
            providers = {"active": {"provider": "fixture", "model": "fixture-model", "baseUrl": "https://example.test", "hasApiKey": True}}
            if failure == "credentials":
                providers["active"]["hasApiKey"] = False
            def fake_http(url, key, **kwargs):
                if url.endswith("/api/providers"):
                    return 200, b"{}", providers
                if url.endswith("/api/settings/"):
                    return 200, b"{}", {"activeProviderId": "fixture", "effectiveModel": "wrong-model" if failure == "model" else "fixture-model",
                                         "effectiveBaseUrl": "https://wrong.test" if failure == "endpoint" else "https://example.test", "effectiveFallbacks": [],
                                         "effectiveCredentialAvailable": True}
                raise AssertionError(url)
            runtime_commit = expected or "b" * 40
            provenance = {"candidateCommit": runtime_commit, "baselineCommit": bench.ANCHORS[-1],
                          "validationMode": "expected-target-commit", "changedFiles": [],
                          "runtimeDiffSha256": bench.sha(b"")}
            with patch.object(bench, "validate_runtime_target", return_value=(bench.ANCHORS[-1], runtime_commit, "")) as validate, \
                 patch.object(bench, "runtime_provenance", return_value=provenance), \
                 patch.object(bench, "verify_windows_identity", return_value={"verified": True}), \
                 patch.object(bench, "effective_policy_path", return_value=root / "policy.yaml"), \
                 patch.object(bench, "git", side_effect=["fixture", ""]), \
                 patch.object(bench, "restore_api_helpers", return_value=[]) as restore, \
                 patch.object(bench, "start_api_process", return_value=(process, None, None)), \
                 patch.object(bench, "http_json", side_effect=fake_http), \
                 patch.object(bench, "submit_task", side_effect=AssertionError("preflight must not create tasks")), \
                 patch.dict(os.environ, {"BOPS_API_KEY": "synthetic-api-key", "BOPS_VAULT_MASTER_KEY": "synthetic-master-key"}):
                if failure:
                    with self.assertRaisesRegex(RuntimeError, "no tasks were started"):
                        bench.run(args)
                else:
                    bench.run(args)
            validate.assert_called_once_with(target.resolve(), expected)
            restore.assert_called_once()
            archive = next(output.glob("*.zip"))
            self.assertTrue(archive.name.endswith(f"-{runtime_commit[:12]}.zip"))
            with zipfile.ZipFile(archive) as zf:
                manifest = json.loads(zf.read("manifest.json"))
                startup = json.loads(zf.read("startup.json"))
                self.assertEqual("fixture", manifest["effectiveConfiguration"]["active"]["provider"])
                self.assertEqual(bench.sha(b"{}"), manifest["configurationProvenance"]["files"]["appsettings.json"]["sha256"])
                self.assertNotIn("vault.dat", zf.namelist())
                content = b"".join(zf.read(name) for name in zf.namelist())
                self.assertNotIn(b"synthetic-master-key", content)
                self.assertNotIn(b"synthetic-api-key", content)
                self.assertEqual(reuse, startup["configurationProvenance"]["snapshotReusedExplicitly"])
                self.assertEqual(provenance, manifest["runtimeProvenance"])
                self.assertEqual(provenance, startup["runtimeProvenance"])
                self.assertEqual(runtime_commit, startup["gitCommit"])
                self.assertEqual(startup["effectivePolicySha256"], bench.sha(zf.read("read-only-policy.yaml")))
                policy_entry = next(entry for entry in manifest["files"] if entry["path"] == "read-only-policy.yaml")
                self.assertEqual(startup["effectivePolicySha256"], policy_entry["sha256"])
                self.assertEqual("fixture-model", manifest["configurationProvenance"]["selectedConfiguration"]["modelId"])
                self.assertEqual(bench.config_snapshot_manifest(pathlib.Path(startup["configurationProvenance"]["snapshotRoot"]))["configurationFingerprint"],
                                 manifest["configurationProvenance"]["configurationFingerprint"])
            self.assertTrue(pathlib.Path(startup["configurationProvenance"]["snapshotRoot"]).is_dir())

    def test_retry_keeps_idempotency_key_and_payload(self):
        calls = []
        responses = [(503, b"busy", None), (202, b'{"taskId":"abc"}', {"taskId": "abc"})]

        def fake(url, key, method, body, idem, timeout):
            calls.append((method, body, idem))
            return responses.pop(0)

        idem, goal = "stable-key", "prompt"
        with patch.object(bench, "http_json", side_effect=fake), patch.object(bench.time, "sleep"):
            _, accepted = bench.submit_task("http://local", "secret", goal, idem)
        self.assertEqual({"taskId": "abc"}, accepted)
        self.assertEqual([("POST", {"goal": goal}, idem)] * 2, calls)

    def test_target_selection_and_api_command_preserve_spaces(self):
        with tempfile.TemporaryDirectory(prefix="target repo ") as temp:
            root = pathlib.Path(temp)
            head = bench.ANCHORS[-1]
            with patch.object(bench, "git", side_effect=[head, ""]), patch.object(
                bench.subprocess, "run", side_effect=[subprocess.CompletedProcess([], 0), subprocess.CompletedProcess([], 0)]
            ):
                anchor, selected_head, status = bench.validate_runtime_target(root)
            project = bench.api_project_path(root)
            self.assertEqual(head, anchor)
            self.assertEqual(head, selected_head)
            self.assertEqual("", status)
            self.assertEqual(str(project), bench.api_command(project)[3])
            self.assertEqual(["dotnet", "run", "--project", str(project), "--no-launch-profile"], bench.api_command(project))

    def test_submission_timeout_retries_same_intention(self):
        calls = []

        def fake(*args, **kwargs):
            calls.append((args[4], args[3]))
            if len(calls) == 1:
                raise TimeoutError("offline fixture")
            return 202, b'{"taskId":"idempotent"}', {"taskId": "idempotent"}

        with patch.object(bench, "http_json", side_effect=fake), patch.object(bench.time, "sleep"):
            _, response = bench.submit_task("http://local", "secret", "goal", "same-key")
        self.assertEqual("idempotent", response["taskId"])
        self.assertEqual([("same-key", {"goal": "goal"})] * 2, calls)

    def test_poll_retries_temporary_404_503_then_finishes_serially(self):
        responses = [
            (404, b"", None), (503, b"busy", None),
            (200, b'{"status":"Running","executing":true}', {"status": "Running", "executing": True}),
            (200, b'{"status":"Completed","executing":false}', {"status": "Completed", "executing": False}),
        ]
        with patch.object(bench, "http_json", side_effect=responses) as request, patch.object(bench.time, "sleep"):
            seen = []
            raw, payload = bench.poll_task("http://local", "key", "task", 60, seen.append)
        self.assertEqual(4, request.call_count)
        self.assertEqual(2, len(seen))
        self.assertEqual(b'{"status":"Completed","executing":false}', raw)
        self.assertEqual("Completed", payload["status"])

    def test_poll_does_not_recover_or_reconcile_orphaned_running_task(self):
        with patch.object(bench, "http_json", return_value=(200, b'{"status":"Running","executing":false}',
                                                             {"status": "Running", "executing": False})):
            with self.assertRaises(bench.RunningTaskNotExecuting):
                bench.poll_task("http://local", "key", "task", 30)
        with patch.object(bench.time, "monotonic", return_value=10):
            with self.assertRaises(TimeoutError):
                bench.poll_task("http://local", "key", "task", 0)

    def test_wal_snapshot_exports_full_state_and_only_complete_matching_audit(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            db = root / "tasks.db"
            connection = sqlite3.connect(db)
            connection.execute("PRAGMA journal_mode=WAL")
            connection.execute("CREATE TABLE tasks(id TEXT PRIMARY KEY, status TEXT, execution_attempt INTEGER, updated_at_utc TEXT, state_json TEXT NOT NULL)")
            connection.execute("CREATE TABLE task_mutation_journal(task_id TEXT, execution_attempt INTEGER, sequence INTEGER, payload TEXT)")
            state = json.dumps({"goal": "è", "modelCalls": [{"request": "x" * 1800000, "response": {"nested": True}}]}, ensure_ascii=False)
            connection.execute("INSERT INTO tasks VALUES(?,?,?,?,?)", ("task-a", "Completed", 1, "now", state))
            connection.execute("INSERT INTO task_mutation_journal VALUES(?,?,?,?)", ("task-a", 1, 1, "original"))
            connection.commit()
            event = {"TaskId": "task-a", "Kind": "Tool"}
            line = json.dumps({"Seq": 2, "PrevHash": "p", "Hash": "h", "EventJson": json.dumps(event)}, separators=(",", ":")).encode() + b"\n"
            other = json.dumps({"Seq": 1, "PrevHash": "", "Hash": "", "EventJson": json.dumps({"TaskId": "other"})}).encode() + b"\n"
            (root / "audit.jsonl").write_bytes(other + line + b'{"partial":')
            output = root / "out"
            bench.extract_records(db, ["task-a"], output, root / "audit.jsonl")
            self.assertEqual(state.encode(), (output / "tasks/task-a/state_json.utf8").read_bytes())
            self.assertEqual(line, (output / "audit/selected-original-lines.jsonl").read_bytes())
            facts = json.loads((output / "audit/capture-status.json").read_text())
            self.assertEqual([1], facts["otherGlobalSequenceNumbersOmitted"])
            self.assertEqual(1, facts["incompleteTrailingLineCount"])
            self.assertEqual("original", json.loads((output / "tasks/task-a/mutation-journal.json").read_text())[0]["payload"])
            archive = root / "capture.zip"
            bench.package(output, archive, runs_per_scenario=1)
            with zipfile.ZipFile(archive) as zf:
                self.assertTrue(zf.testzip() is None)
                self.assertEqual(state.encode(), zf.read("tasks/task-a/state_json.utf8"))
                manifest = json.loads(zf.read("manifest.json"))
                self.assertEqual(1, manifest["runsPerScenario"])
                self.assertEqual(3, manifest["requestedTaskCount"])
                for entry in manifest["files"]:
                    self.assertEqual(entry["sha256"], bench.sha(zf.read(entry["path"])))
                manifest_hash = zf.read("manifest.sha256").decode().split()[0]
                self.assertEqual(manifest_hash, bench.sha(zf.read("manifest.json")))
            connection.close()


if __name__ == "__main__":
    unittest.main()
