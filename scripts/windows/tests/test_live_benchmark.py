import importlib.util
import json
import pathlib
import sqlite3
import tempfile
import unittest
import zipfile
import subprocess
from unittest.mock import patch

MODULE = pathlib.Path(__file__).parents[1] / "live_benchmark.py"
spec = importlib.util.spec_from_file_location("live_benchmark", MODULE)
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


class CaptureTests(unittest.TestCase):
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

    def test_powershell_launcher_passes_run_count_to_python_and_uac(self):
        launcher = (MODULE.parent / "run-live-benchmark.ps1").read_text(encoding="utf-8")
        self.assertIn("[ValidateRange(1, 3)] [int] $RunsPerScenario = 3", launcher)
        self.assertIn("-RunsPerScenario $RunsPerScenario -Elevated", launcher)
        self.assertIn("--runs-per-scenario $RunsPerScenario", launcher)

    def test_benchmark_policy_only_allows_read_risk_automatically(self):
        self.assertEqual("defaults:\n  read: automatic\n", bench.READ_ONLY_POLICY)

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
