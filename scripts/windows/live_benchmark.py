#!/usr/bin/env python3
"""Capture the original persisted evidence from nine serial bOps live tasks."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import shutil
import socket
import sqlite3
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
import zipfile
from datetime import datetime, timezone

SCENARIOS = [
    {"id": "B01", "goal": "Controlla lo stato attuale di CPU e memoria di questo PC e dimmi se vedi anomalie. Fai solo i controlli necessari e dammi una sintesi."},
    {"id": "B02", "goal": "Diagnostica questo PC per capire se ci sono evidenze di instabilità, freeze o riavvii anomali. Controlla le informazioni di sistema, la stabilità negli ultimi 180 giorni, i crash negli ultimi 180 giorni, gli eventi di sistema rilevanti e poi completa comunque l'analisi controllando CPU, memoria e salute dello storage. Se qualche fonte è parziale, troncata o non disponibile dichiaralo esplicitamente. Se serve approfondire evidenza già raccolta usa l'evidenza persistita disponibile invece di ripetere inutilmente gli stessi controlli. Alla fine distingui chiaramente fatti osservati, ipotesi e limitazioni dell'analisi."},
    {"id": "B03", "goal": "trova la causa dei continui crash che ha questo computer"},
]
ANCHORS = ["97507aa7a914cdabdfafbd42e382c0f1dfd399da", "07e422a4c9c65bd617cafaf209d2352657f1c130"]
RUNTIME_PATHS = ["src/core/bOps.Api", "src/core/bOps.AgentFramework", "src/core/bOps.Runtime", "src/core/bOps.Memory", "src/core/bOps.Audit", "src/packages"]
README = """PRIVATE — POTENTIALLY SENSITIVE MATERIAL\n\nThis archive contains complete, unredacted task state, model request/response payloads, tool results and audit records from this PC. It may disclose usernames, host names, IP addresses, system events, logs, or secrets recorded by the runtime. Review before sharing. Keep local ACLs restrictive. This collector does not anonymize content and does not upload it.\n"""
READ_ONLY_POLICY = "defaults:\n  read: automatic\n"
TERMINAL = {"Completed", "MaxStepsReached", "BudgetExceeded", "PolicyBlocked", "ReplanLimitReached", "Failed", "Cancelled"}


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def git(root: pathlib.Path, *args: str) -> str:
    return subprocess.check_output(["git", "-C", str(root), *args], text=True, stderr=subprocess.STDOUT).strip()


def validate_runtime_target(root: pathlib.Path):
    head = git(root, "rev-parse", "HEAD")
    anchor = next((candidate for candidate in reversed(ANCHORS)
                   if subprocess.run(["git", "-C", str(root), "merge-base", "--is-ancestor", candidate, "HEAD"],
                                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0), None)
    if anchor is None:
        raise RuntimeError("Target must contain the AF-05G or AF-05H reference commit in its history.")
    diff = subprocess.run(["git", "-C", str(root), "diff", "--quiet", anchor, "HEAD", "--", *RUNTIME_PATHS]).returncode
    if diff != 0:
        raise RuntimeError("Target runtime source differs from its nearest supported AF-05G/AF-05H reference commit.")
    status = git(root, "status", "--porcelain", "--untracked-files=no")
    changed_paths = [line[3:].split(" -> ")[-1].replace("\\", "/") for line in status.splitlines() if len(line) >= 4]
    for changed in changed_paths:
        if any(changed.startswith(prefix + "/") for prefix in RUNTIME_PATHS):
            if pathlib.PurePosixPath(changed).name not in {"appsettings.json", "appsettings.Development.json"}:
                raise RuntimeError(f"Uncommitted runtime source change prevents exact benchmark target: {changed}")
    return anchor, head, status


def effective_policy_path(root: pathlib.Path):
    api_dir = root / "src" / "core" / "bOps.Api"
    setting = None
    for name in ("appsettings.json", "appsettings.Development.json"):
        file = api_dir / name
        if file.exists():
            document = json.loads(file.read_text(encoding="utf-8"))
            setting = (document.get("Policy") or {}).get("FilePath", setting)
    setting = os.environ.get("Policy__FilePath", setting or "policy.yaml")
    path = pathlib.Path(setting)
    return path.resolve() if path.is_absolute() else (root / path).resolve()


def api_project_path(root: pathlib.Path):
    return root / "src" / "core" / "bOps.Api" / "bOps.Api.csproj"


def api_command(project: pathlib.Path):
    return ["dotnet", "run", "--project", str(project), "--no-launch-profile"]


def reserve_port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def http_json(url, key, method="GET", body=None, idem=None, timeout=30):
    headers = {"Authorization": f"Bearer {key}", "Accept": "application/json"}
    payload = None
    if body is not None:
        headers["Content-Type"] = "application/json; charset=utf-8"
        payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
    if idem:
        headers["Idempotency-Key"] = idem
    request = urllib.request.Request(url, payload, headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            raw = response.read()
            return response.status, raw, json.loads(raw) if raw else None
    except urllib.error.HTTPError as exc:
        raw = exc.read()
        return exc.code, raw, None


def submit_task(api: str, key: str, goal: str, idem: str, attempts=12):
    for attempt in range(attempts):
        try:
            code, raw, response = http_json(api + "/api/agents/tasks/", key, "POST", {"goal": goal}, idem, timeout=30)
        except (OSError, TimeoutError):
            time.sleep(min(attempt + 1, 10))
            continue
        if code == 202 and response:
            return raw, response
        if code in (404, 503):
            time.sleep(min(attempt + 1, 10))
            continue
        raise RuntimeError(f"Task submission failed HTTP {code}.")
    raise TimeoutError("Task submission did not succeed after bounded retries.")


def write_json(path: pathlib.Path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def audit_rows(path: pathlib.Path, task_ids: set[str]):
    selected, global_sequences = [], []
    malformed_complete_lines = incomplete_trailing_lines = 0
    if not path.exists():
        return selected, {"completeButUnparseableLineCount": 0, "incompleteTrailingLineCount": 0, "otherGlobalSequenceNumbersOmitted": []}
    with path.open("rb") as stream:
        for line in stream:
            if not line.endswith(b"\n"):
                incomplete_trailing_lines += 1
                continue
            try:
                envelope = json.loads(line)
                event = json.loads(envelope["EventJson"])
            except (ValueError, KeyError, TypeError):
                malformed_complete_lines += 1
                continue
            if isinstance(envelope.get("Seq"), int):
                global_sequences.append(envelope["Seq"])
            if str(event.get("TaskId", event.get("taskId", ""))) in task_ids:
                selected.append(line)
    selected_sequences = {json.loads(line)["Seq"] for line in selected}
    return selected, {
        "completeButUnparseableLineCount": malformed_complete_lines,
        "incompleteTrailingLineCount": incomplete_trailing_lines,
        "otherGlobalSequenceNumbersOmitted": sorted(set(global_sequences) - selected_sequences),
    }


def extract_records(db_path: pathlib.Path, ids: list[str], out: pathlib.Path, audit_path: pathlib.Path):
    # SQLite backup uses the online backup API; reads a consistent snapshot including committed WAL frames.
    uri = db_path.resolve().as_uri() + "?mode=ro"
    source = sqlite3.connect(uri, uri=True, timeout=30)
    try:
        source.execute("PRAGMA busy_timeout=30000")
        snapshot = sqlite3.connect(":memory:")
        source.backup(snapshot)
        snapshot.row_factory = sqlite3.Row
        task_ids = set(ids)
        for task_id in ids:
            row = snapshot.execute("SELECT * FROM tasks WHERE id=?", (task_id,)).fetchone()
            folder = out / "tasks" / task_id
            folder.mkdir(parents=True, exist_ok=True)
            if row is None:
                write_json(folder / "capture-status.json", {"taskId": task_id, "record": "missing"})
                continue
            columns = {key: row[key] for key in row.keys()}
            state = columns.get("state_json")
            state_bytes = state.encode("utf-8") if isinstance(state, str) else bytes(state or b"")
            (folder / "state_json.utf8").write_bytes(state_bytes)
            journal = snapshot.execute("SELECT * FROM task_mutation_journal WHERE task_id=? ORDER BY execution_attempt, sequence", (task_id,)).fetchall()
            write_json(folder / "mutation-journal.json", [{key: item[key] for key in item.keys()} for item in journal])
            schema = {entry[1]: entry[2] for entry in snapshot.execute("PRAGMA table_info(tasks)")}
            write_json(folder / "task-row.json", {
                "columns": [{"name": key, "declaredType": schema.get(key), "sqliteType": snapshot.execute("SELECT typeof(?)", (columns[key],)).fetchone()[0]} for key in columns],
                "values": {key: (None if key == "state_json" else value) for key, value in columns.items()},
                "stateJsonFile": "state_json.utf8", "stateJsonSha256": sha(state_bytes),
            })
        rows, audit_facts = audit_rows(audit_path, task_ids)
        (out / "audit" / "selected-original-lines.jsonl").parent.mkdir(parents=True, exist_ok=True)
        (out / "audit" / "selected-original-lines.jsonl").write_bytes(b"".join(rows))
        write_json(out / "audit" / "capture-status.json", {
            "selectedCompleteLineCount": len(rows), "globalSequenceMayContainOtherTasks": True, **audit_facts,
        })
        snapshot.close()
    finally:
        source.close()


def package(out: pathlib.Path, zip_path: pathlib.Path):
    manifest = out / "manifest.json"
    manifest_hash = out / "manifest.sha256"
    files = []
    for path in sorted(p for p in out.rglob("*") if p.is_file() and p not in (zip_path, manifest, manifest_hash)):
        data = path.read_bytes()
        files.append({"path": path.relative_to(out).as_posix(), "sizeBytes": len(data), "sha256": sha(data)})
    write_json(manifest, {"files": files})
    manifest_hash.write_text(sha(manifest.read_bytes()) + "  manifest.json\n", encoding="ascii")
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, allowZip64=True) as archive:
        for path in sorted(p for p in out.rglob("*") if p.is_file() and p != zip_path):
            archive.write(path, path.relative_to(out).as_posix())


def run(args):
    root = pathlib.Path(args.target_repo_root).resolve()
    anchor, commit, dirty_status = validate_runtime_target(root)
    original_policy_path = effective_policy_path(root)
    if not os.environ.get("BOPS_API_KEY"):
        raise RuntimeError("BOPS_API_KEY is unavailable to the elevated process; no tasks were started.")
    # Resolve repository status without including diff contents or secrets.
    branch = git(root, "branch", "--show-current")
    dirty = bool(git(root, "status", "--porcelain"))
    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    output = pathlib.Path(args.output_root).resolve() if args.output_root else pathlib.Path.home() / "Documents" / "bOps-Benchmarks" / run_id
    output.mkdir(parents=True, exist_ok=False)
    if os.name == "nt":
        account = subprocess.check_output(["whoami"], text=True).strip()
        subprocess.run(["icacls", str(output), "/inheritance:r", "/grant:r", f"{account}:(OI)(CI)F", "SYSTEM:(OI)(CI)F"],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    run_root = output / ".capture"
    run_root.mkdir()
    paths = {name: (run_root / name).resolve() for name in (
        "tasks.db", "audit.jsonl", "sessions.db", "system-messages.db", "delegations.db", "filesystem-inventory.db")}
    readonly_policy_path = run_root / "read-only-policy.yaml"
    readonly_policy_path.write_text(READ_ONLY_POLICY, encoding="utf-8")
    port = reserve_port()
    env = os.environ.copy()
    env.update({
        "ASPNETCORE_ENVIRONMENT": "Development", "DOTNET_ENVIRONMENT": "Development",
        "ASPNETCORE_URLS": f"http://127.0.0.1:{port}", "Memory__FilePath": str(paths["tasks.db"]),
        "Audit__FilePath": str(paths["audit.jsonl"]), "BrowserSession__FilePath": str(paths["sessions.db"]),
        "SystemMessages__FilePath": str(paths["system-messages.db"]), "Delegation__FilePath": str(paths["delegations.db"]),
        "Filesystem__Inventory__ManifestStorePath": str(paths["filesystem-inventory.db"]),
        "Policy__FilePath": str(readonly_policy_path),
    })
    project = api_project_path(root)
    if not project.is_file():
        raise RuntimeError(f"bOps.Api project not found under target root: {root}")
    process = subprocess.Popen(api_command(project), cwd=root, env=env,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    api = f"http://127.0.0.1:{port}"
    startup = {"startedAtUtc": utc_now(), "targetRepoRoot": str(root), "gitCommit": commit, "runtimeReferenceCommit": anchor,
               "gitBranch": branch, "gitDirty": dirty, "gitDirtyPaths": [line[3:] for line in dirty_status.splitlines()],
               "originalPolicyPath": str(original_policy_path),
               "originalPolicySha256": sha(original_policy_path.read_bytes()) if original_policy_path.is_file() else None,
               "effectivePolicyPath": str(readonly_policy_path), "effectivePolicySha256": sha(READ_ONLY_POLICY.encode("utf-8")),
               "apiBaseUrl": api, "apiProject": str(project), "hostMode": "API-only",
               "environment": "Development", "isolatedOverrides": {"ASPNETCORE_URLS": api, **{k: str(v) for k,v in paths.items()}},
        "originalConfigurationFilesUnchanged": True,
        "effectivePolicy": "BENCH-00 isolated policy: Read=Automatic; Low/Medium/High/Critical=Forbidden; no approval is automated",
        "harness": "BENCH-00 stdlib Python", "python": sys.version,
               "dotnetCommand": "dotnet run --project <target>/src/core/bOps.Api/bOps.Api.csproj --no-launch-profile",
               "timeoutsSeconds": {"startup": args.startup_timeout, "task": args.task_timeout, "http": 30}}
    write_json(run_root / "startup.json", startup)
    write_json(run_root / "scenarios.json", SCENARIOS)
    archive_path = output / f"bops-live-benchmark-{datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')}-{anchor[:12]}.zip"
    (run_root / "README-SENSITIVE").write_text(README, encoding="utf-8")
    roster, terminal = [], {}
    write_json(run_root / "run-roster.json", roster)
    status_doc = {"runId": run_id, "state": "starting", "tasks": []}
    try:
        deadline = time.monotonic() + args.startup_timeout
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError(f"bOps.Api exited during startup (exit {process.returncode}); see output is intentionally not captured because it may contain secrets.")
            try:
                code, provider_raw, provider_info = http_json(api + "/api/providers", os.environ["BOPS_API_KEY"], timeout=3)
                if code == 200:
                    active = (provider_info or {}).get("active") or {}
                    startup["effectiveProvider"] = {name: active.get(name) for name in ("providerId", "model", "credentialAvailable")}
                    write_json(run_root / "startup.json", startup)
                    if active.get("credentialAvailable") is not True:
                        raise RuntimeError("The selected provider credential is unavailable in the benchmark process; no tasks were started.")
                    break
                if code in (401, 403):
                    raise RuntimeError(f"API authentication/authorization failed ({code}); no tasks were started.")
            except (OSError, TimeoutError):
                pass
            time.sleep(1)
        else:
            raise TimeoutError("Dedicated bOps.Api did not become ready in time.")
        ordinal = 0
        for round_number in range(1, 4):
            for scenario in SCENARIOS:
                ordinal += 1
                intention = f"{scenario['id']}-R{round_number}"
                idem = str(uuid.uuid4())
                raw, started = submit_task(api, os.environ["BOPS_API_KEY"], scenario["goal"], idem)
                task_id = str(started.get("taskId", started.get("TaskId", "")))
                if not task_id:
                    raise RuntimeError(f"Task submission {intention} returned no task id.")
                entry = {"ordinal": ordinal, "intention": intention, "scenarioId": scenario["id"], "round": round_number,
                         "idempotencyKey": idem, "taskId": task_id, "acceptedAtUtc": utc_now()}
                roster.append(entry); write_json(run_root / "run-roster.json", roster)
                task_folder = run_root / "tasks" / task_id
                task_folder.mkdir(parents=True, exist_ok=True)
                (task_folder / "api-accepted-original.json").write_bytes(raw)
                task_deadline = time.monotonic() + args.task_timeout
                last_status = None
                while time.monotonic() < task_deadline:
                    try:
                        code, raw_status, payload = http_json(api + f"/api/agents/tasks/{task_id}", os.environ["BOPS_API_KEY"], timeout=30)
                    except (OSError, TimeoutError):
                        time.sleep(2); continue
                    if code == 404:
                        time.sleep(1); continue
                    if code in (429, 503):
                        time.sleep(3); continue
                    if code != 200:
                        raise RuntimeError(f"Task poll {task_id} failed HTTP {code}.")
                    last_status = payload
                    state = str(payload.get("status", payload.get("Status", "")))
                    if state.lower() in {x.lower() for x in TERMINAL}:
                        terminal[task_id] = raw_status
                        (task_folder / "api-terminal-original.json").write_bytes(raw_status)
                        break
                    if state.lower() == "running" and payload.get("executing") is False:
                        (task_folder / "api-last-original.json").write_bytes(raw_status)
                        write_json(task_folder / "capture-status.json", {"taskId": task_id, "status": "running-not-executing", "automaticRecovery": False})
                        raise RuntimeError(f"Task {task_id} is persisted Running but the API reports no executor; it was not resumed or reconciled.")
                    time.sleep(2)
                else:
                    write_json(task_folder / "capture-status.json", {"taskId": task_id, "status": "task-timeout", "lastApiResponse": last_status})
                    raise TimeoutError(f"Task {task_id} remained non-terminal; partial capture retained.")
                # Capture the persisted source after every completed task; an interruption preserves all earlier rows.
                extract_records(paths["tasks.db"], [item["taskId"] for item in roster], run_root, paths["audit.jsonl"])
                status_doc = {"runId": run_id, "state": "running", "tasks": [{"taskId": x["taskId"], "terminalApiCaptured": x["taskId"] in terminal} for x in roster]}
                write_json(run_root / "capture-status.json", status_doc)
                package(run_root, archive_path)
                print(f"Captured {ordinal}/9: {intention} ({task_id})", flush=True)
        status_doc["state"] = "complete"
    except KeyboardInterrupt:
        status_doc["state"] = "interrupted"
        raise
    except Exception as exc:
        status_doc = {"runId": run_id, "state": "partial-error", "error": str(exc), "tasks": roster}
        raise
    finally:
        write_json(run_root / "capture-status.json", status_doc)
        if paths["tasks.db"].exists():
            try: extract_records(paths["tasks.db"], [item["taskId"] for item in roster], run_root, paths["audit.jsonl"])
            except (sqlite3.Error, OSError): pass
        package(run_root, archive_path)
        shutil.rmtree(run_root, ignore_errors=True)
        if process.poll() is None:
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                try: process.wait(timeout=15)
                except subprocess.TimeoutExpired: process.kill(); process.wait()
            else:
                process.terminate()
                try: process.wait(timeout=15)
                except subprocess.TimeoutExpired: process.kill(); process.wait()
        print(f"Archive: {archive_path}", flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--target-repo-root", required=True)
    parser.add_argument("--output-root")
    parser.add_argument("--startup-timeout", type=int, default=180)
    parser.add_argument("--task-timeout", type=int, default=5400)
    args = parser.parse_args()
    run(args)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"BENCH-00 failed: {error}", file=sys.stderr)
        sys.exit(1)
