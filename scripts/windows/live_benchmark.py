#!/usr/bin/env python3
"""Capture the original persisted evidence from nine serial bOps live tasks."""
from __future__ import annotations

import argparse
import csv
import hashlib
import io
import json
import os
import pathlib
import re
import shutil
import signal
import socket
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid
import zipfile
from datetime import datetime, timezone
from urllib.parse import urlsplit, urlunsplit

SCENARIOS = [
    {"id": "B01", "goal": "Controlla lo stato attuale di CPU e memoria di questo PC e dimmi se vedi anomalie. Fai solo i controlli necessari e dammi una sintesi."},
    {"id": "B02", "goal": "Diagnostica questo PC per capire se ci sono evidenze di instabilità, freeze o riavvii anomali. Controlla le informazioni di sistema, la stabilità negli ultimi 180 giorni, i crash negli ultimi 180 giorni, gli eventi di sistema rilevanti e poi completa comunque l'analisi controllando CPU, memoria e salute dello storage. Se qualche fonte è parziale, troncata o non disponibile dichiaralo esplicitamente. Se serve approfondire evidenza già raccolta usa l'evidenza persistita disponibile invece di ripetere inutilmente gli stessi controlli. Alla fine distingui chiaramente fatti osservati, ipotesi e limitazioni dell'analisi."},
    {"id": "B03", "goal": "trova la causa dei continui crash che ha questo computer"},
]
ANCHORS = ["97507aa7a914cdabdfafbd42e382c0f1dfd399da", "07e422a4c9c65bd617cafaf209d2352657f1c130"]
RUNTIME_PATHS = ["src/core/bOps.Api", "src/core/bOps.AgentFramework", "src/core/bOps.Runtime", "src/core/bOps.Memory", "src/core/bOps.Audit", "src/packages"]
CANDIDATE_RUNTIME_PATHS = ["src/core", "src/packages"]
README = """PRIVATE — POTENTIALLY SENSITIVE MATERIAL\n\nThis archive contains complete, unredacted task state, model request/response payloads, tool results, audit records and the API startup diagnostic log from this PC. The diagnostic log preserves the API process stdout/stderr and may disclose paths, configuration details, usernames, host names, IP addresses, system events, and logs. Known credential values from process environment and configuration files are redacted from that diagnostic log; vault.dat and the master key are not included. Review before sharing. Keep local ACLs restrictive. This collector does not anonymize task evidence and does not upload it.\n"""
READ_ONLY_POLICY = "defaults:\n  read: automatic\n"
TERMINAL = {"Completed", "MaxStepsReached", "BudgetExceeded", "PolicyBlocked", "ReplanLimitReached", "Failed", "Cancelled"}
TASK_STATUS_BY_VALUE = {
    0: "Running",
    1: "Completed",
    2: "MaxStepsReached",
    3: "BudgetExceeded",
    4: "PolicyBlocked",
    5: "ReplanLimitReached",
    6: "Failed",
    7: "Cancelled",
}


def task_status_name(value):
    """Decode the ASP.NET Core enum JSON value without changing the source payload."""
    if isinstance(value, bool):
        raise ValueError(f"Unsupported task status: {value!r}")
    if isinstance(value, int):
        try:
            return TASK_STATUS_BY_VALUE[value]
        except KeyError as exc:
            raise ValueError(f"Unsupported task status: {value!r}") from exc
    if isinstance(value, str):
        name = value.strip()
        if name in TASK_STATUS_BY_VALUE.values():
            return name
        # Numeric strings are accepted for compatibility with serializers/proxies
        # that stringify enum values; the original response bytes remain untouched.
        if name.isdecimal():
            return task_status_name(int(name))
    raise ValueError(f"Unsupported task status: {value!r}")


def scenario_runs(runs_per_scenario):
    for round_number in range(1, runs_per_scenario + 1):
        for scenario in SCENARIOS:
            yield round_number, scenario


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def git(root: pathlib.Path, *args: str) -> str:
    return subprocess.check_output(["git", "-C", str(root), *args], text=True, stderr=subprocess.STDOUT).strip()


def full_commit_sha(value: str) -> str:
    if not re.fullmatch(r"[0-9a-fA-F]{40}", value):
        raise argparse.ArgumentTypeError("ExpectedTargetCommit must be a full 40-character Git SHA.")
    return value.lower()


def validate_runtime_target(root: pathlib.Path, expected_target_commit: str | None = None):
    head = git(root, "rev-parse", "HEAD")
    if expected_target_commit is not None:
        expected = full_commit_sha(expected_target_commit)
        if head != expected:
            raise RuntimeError("Target HEAD does not match ExpectedTargetCommit.")
        anchor = ANCHORS[-1]  # The candidate contract requires AF-05H, never AF-05G fallback.
        ancestry = subprocess.run(["git", "-C", str(root), "merge-base", "--is-ancestor", anchor, head],
                                  stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if ancestry.returncode != 0:
            raise RuntimeError("ExpectedTargetCommit must descend from the expected AF-05H baseline.")
        # Include every core project (also Abstractions), staged changes, both sides
        # of renames, and untracked source. Generated ignored build outputs are excluded.
        paths = CANDIDATE_RUNTIME_PATHS
        changed = subprocess.check_output([
            "git", "-C", str(root), "diff", "--no-ext-diff", "--no-renames",
            "--name-only", "-z", "--", *paths])
        staged = subprocess.check_output([
            "git", "-C", str(root), "diff", "--no-ext-diff", "--no-renames",
            "--cached", "--name-only", "-z", "HEAD", "--", *paths])
        untracked = subprocess.check_output([
            "git", "-C", str(root), "ls-files", "--others", "--exclude-standard", "-z", "--", *paths])
        for name in (changed + staged + untracked).decode("utf-8").split("\0"):
            if name and pathlib.PurePosixPath(name).name not in {"appsettings.json", "appsettings.Development.json"}:
                raise RuntimeError(f"Uncommitted runtime source change prevents exact benchmark target: {name}")
        return anchor, head, git(root, "status", "--porcelain", "--untracked-files=no")
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


def runtime_provenance(root: pathlib.Path, anchor: str, head: str, expected: str | None):
    def output(*args):
        return subprocess.check_output(["git", "-C", str(root), *args])

    # Pin formatting so local diff display preferences do not change fingerprints.
    diff_args = ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--binary",
                 "--full-index", "--no-renames", "--diff-algorithm=myers", "--no-indent-heuristic",
                 "--src-prefix=a/", "--dst-prefix=b/", "--no-relative", anchor, head]
    runtime_paths = CANDIDATE_RUNTIME_PATHS if expected is not None else RUNTIME_PATHS
    return {
        "validationMode": "expected-target-commit" if expected is not None else "legacy-reference",
        "candidateCommit": head, "baselineCommit": anchor,
        "expectedTargetCommit": expected,
        "changedFiles": [name for name in output("diff", "--no-ext-diff", "--no-renames", "--name-only", "-z",
                                                anchor, head).decode("utf-8").split("\0") if name],
        "committedDiffSha256": sha(output(*diff_args)),
        "runtimeDiffSha256": sha(output(*diff_args, "--", *runtime_paths)),
        "diffGitArguments": diff_args,
        "runtimePaths": runtime_paths,
    }


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


def restore_api_helpers(root: pathlib.Path, env: dict):
    # These projects are built by API AfterTargets, not ProjectReferences, so
    # dotnet run's implicit restore does not visit them on a fresh checkout.
    restored = []
    for name in ("bOps.Packages.Firewall.Windows.Helper", "bOps.Packages.System.Windows.Updates.Helper"):
        project = root / "src" / "packages" / name / f"{name}.csproj"
        if not project.is_file():
            raise RuntimeError(f"API helper project not found: {project}")
        command = ["dotnet", "restore", str(project), "--locked-mode"]
        # Restore output can contain feed credentials; never archive or print it.
        result = subprocess.run(command, cwd=str(root), env=env, stdout=subprocess.DEVNULL,
                                stderr=subprocess.STDOUT, timeout=180)
        if result.returncode != 0:
            raise RuntimeError(f"API helper restore failed ({result.returncode}): {name}; no API was started.")
        restored.append({"project": str(project), "command": command, "exitCode": result.returncode})
    return restored


def verify_windows_identity(expected_sid: str | None):
    if os.name != "nt":
        return {"verified": False, "reason": "Windows identity check is only available on Windows."}
    rows = list(csv.reader(io.StringIO(subprocess.check_output(
        ["whoami", "/user", "/fo", "csv", "/nh"], text=True, encoding="utf-8").strip())))
    actual_sid = rows[0][-1].strip() if rows and rows[0] else ""
    matches = bool(actual_sid) and (not expected_sid or actual_sid.casefold() == expected_sid.casefold())
    if not matches:
        raise RuntimeError("The benchmark process Windows identity does not match the requesting user.")
    return {"verified": True, "expectedSidMatched": bool(expected_sid)}


def secure_private_directory(path: pathlib.Path):
    if os.name == "nt":
        account = subprocess.check_output(["whoami"], text=True, encoding="utf-8").strip()
        subprocess.run(["icacls", str(path), "/inheritance:r", "/grant:r",
                        f"{account}:(OI)(CI)F", "SYSTEM:(OI)(CI)F", "/C"], check=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(["icacls", str(path), "/T", "/grant:r", f"{account}:F", "SYSTEM:F", "/C"], check=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        path.chmod(0o700)


CONFIG_SNAPSHOT_FILES = ("appsettings.json", "appsettings.Development.json", "settings.json", "vault.dat")
REQUIRED_SNAPSHOT_FILES = ("appsettings.json", "settings.json", "vault.dat")


def configuration_source_files(source_root: pathlib.Path):
    # dotnet run uses the API project directory as its working directory. Keep
    # ConfigSourceRoot at the Git root, just as the collector has always done.
    source_dir = api_project_path(source_root.resolve()).parent
    if not source_dir.is_dir():
        raise RuntimeError(f"Configuration source API directory not found: {source_dir}")
    sources = {name: source_dir / name for name in CONFIG_SNAPSHOT_FILES}
    paths = {"settings": "settings.json", "vault": "vault.dat"}
    for name in ("appsettings.json", "appsettings.Development.json"):
        if sources[name].is_file():
            document = json.loads(sources[name].read_text(encoding="utf-8-sig"))
            for section, values in document.items():
                if section.casefold() in paths and isinstance(values, dict):
                    for key, value in values.items():
                        if key.casefold() == "filepath":
                            paths[section.casefold()] = value
    # IConfiguration environment keys are case insensitive too.
    environment = {key.casefold(): value for key, value in os.environ.items()}
    for section, filename in (("settings", "settings.json"), ("vault", "vault.dat")):
        value = environment.get(f"{section}__filepath", paths[section])
        if not isinstance(value, str) or not value.strip():
            raise RuntimeError(f"Configuration source has an invalid {section} file path.")
        path = pathlib.Path(value)
        sources[filename] = (path if path.is_absolute() else source_dir / path).resolve()
    return sources


def config_snapshot_manifest(snapshot: pathlib.Path):
    manifest_path = snapshot / "config-snapshot.json"
    if not manifest_path.is_file():
        raise RuntimeError(f"Configuration snapshot manifest is missing: {manifest_path}")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    files = manifest.get("files")
    if not isinstance(files, dict):
        raise RuntimeError("Configuration snapshot manifest has an invalid files section.")
    for name in REQUIRED_SNAPSHOT_FILES:
        entry = files.get(name) or {}
        path = snapshot / name
        if not path.is_file() or entry.get("sha256") != sha(path.read_bytes()):
            raise RuntimeError(f"Configuration snapshot is incomplete or changed: {name}")
    if set(files) != set(CONFIG_SNAPSHOT_FILES):
        raise RuntimeError("Configuration snapshot manifest has an unexpected file roster.")
    for name, entry in files.items():
        path = snapshot / name
        if bool(entry.get("present")) != path.is_file() or (path.is_file() and entry.get("sha256") != sha(path.read_bytes())):
            raise RuntimeError(f"Configuration snapshot is incomplete or changed: {name}")
    settings = json.loads((snapshot / "settings.json").read_text(encoding="utf-8-sig"))
    selected = selected_ui_configuration(settings)
    if manifest.get("selectedConfiguration") not in (None, selected):
        raise RuntimeError("Configuration snapshot is stale or its selected provider/model metadata changed.")
    fingerprint = sha(json.dumps(files, sort_keys=True, separators=(",", ":")).encode("utf-8"))
    if manifest.get("configurationFingerprint") not in (None, fingerprint):
        raise RuntimeError("Configuration snapshot fingerprint does not match its files.")
    manifest.setdefault("selectedConfiguration", selected)
    manifest.setdefault("configurationFingerprint", fingerprint)
    manifest.setdefault("snapshotCompatibility", "legacy-schema-1")
    return manifest


def selected_ui_configuration(settings: dict):
    # SettingsJsonContext persists PascalCase; older collector snapshots used
    # camelCase. Normalize property names only, never provider dictionary keys.
    if not isinstance(settings, dict):
        raise RuntimeError("Saved UI configuration is missing an active provider, model, or endpoint.")
    settings = {key.casefold(): value for key, value in settings.items()}
    provider = settings.get("activeproviderid")
    providers = settings.get("providers")
    profile = providers.get(provider) if isinstance(providers, dict) and isinstance(provider, str) else None
    profile = {key.casefold(): value for key, value in profile.items()} if isinstance(profile, dict) else {}
    if not isinstance(provider, str) or not provider.strip() or any(
            not isinstance(profile.get(key), str) or not profile[key].strip() for key in ("model", "baseurl")):
        raise RuntimeError("Saved UI configuration is missing an active provider, model, or endpoint.")
    return {"provider": provider, "modelId": profile["model"], "endpoint": safe_endpoint(profile["baseurl"]),
            "endpointFingerprint": sha(profile["baseurl"].encode("utf-8"))}


def verify_loaded_configuration(provider_info: dict, settings_view: dict, selected: dict):
    active = provider_info.get("active") or {}
    provider = active.get("provider", active.get("providerId"))
    model = settings_view.get("effectiveModel")
    endpoint = safe_endpoint(settings_view.get("effectiveBaseUrl", active.get("baseUrl", active.get("baseURL"))))
    if provider != selected["provider"] or active.get("model") != selected["modelId"] or model != selected["modelId"]:
        raise RuntimeError("Loaded API provider/model does not match the saved UI selection; no tasks were started.")
    raw_endpoint = settings_view.get("effectiveBaseUrl", active.get("baseUrl", active.get("baseURL")))
    if endpoint != selected["endpoint"] or sha(str(raw_endpoint).encode("utf-8")) != selected["endpointFingerprint"]:
        raise RuntimeError("Loaded API endpoint does not match the saved UI selection; no tasks were started.")
    if settings_view.get("effectiveFallbacks"):
        raise RuntimeError("Automatic fallback models are configured; comparative benchmark requires an empty fallback chain.")
    return {"provider": provider, "modelId": model, "endpoint": endpoint}


def prepare_config_snapshot(source_root: pathlib.Path, destination: pathlib.Path, reuse_path: pathlib.Path | None = None):
    if reuse_path is not None:
        snapshot = reuse_path.resolve()
        if not snapshot.is_dir():
            raise RuntimeError(f"Requested configuration snapshot does not exist: {snapshot}")
        secure_private_directory(snapshot)
        manifest = config_snapshot_manifest(snapshot)
        # Explicit snapshots are independent of the current UI and source files.
        # Integrity is checked above; loaded selection and credentials below run
        # through the same fail-closed API preflight as newly captured snapshots.
        return snapshot, manifest, True

    sources = configuration_source_files(source_root)
    missing = [name for name in REQUIRED_SNAPSHOT_FILES if not sources[name].is_file()]
    if missing:
        raise RuntimeError("Configuration source is missing required files: " + ", ".join(missing))
    snapshot = destination.resolve()
    snapshot.mkdir(parents=True, exist_ok=False)
    secure_private_directory(snapshot)
    files = {}
    for name in CONFIG_SNAPSHOT_FILES:
        source = sources[name]
        if source.is_file():
            target = snapshot / name
            shutil.copyfile(source, target)
            if os.name != "nt":
                target.chmod(0o600)
            files[name] = {"present": True, "sha256": sha(target.read_bytes())}
        else:
            files[name] = {"present": False, "sha256": None}
    try:
        selected = selected_ui_configuration(json.loads((snapshot / "settings.json").read_text(encoding="utf-8-sig")))
    except (json.JSONDecodeError, TypeError) as exc:
        raise RuntimeError("Saved UI settings are invalid or incomplete.") from exc
    manifest = {"schemaVersion": 1, "sourceRoot": str(source_root.resolve()),
                "createdAtUtc": utc_now(), "files": files, "selectedConfiguration": selected,
                "configurationFingerprint": sha(json.dumps(files, sort_keys=True, separators=(",", ":")).encode("utf-8"))}
    write_json(snapshot / "config-snapshot.json", manifest)
    if os.name != "nt":
        (snapshot / "config-snapshot.json").chmod(0o600)
    return snapshot, manifest, False


class ApiStartupFailure(RuntimeError):
    def __init__(self, exit_code, phase: str, log_path: pathlib.Path):
        self.exit_code = exit_code
        self.phase = phase
        self.log_path = log_path
        code_text = f"exit code {exit_code}" if exit_code is not None else "exit code unavailable"
        super().__init__(f"API startup failed ({code_text}; phase {phase}); log: {log_path}")


def _copy_redacted_output(pipe, destination, secret_values):
    secrets = sorted({value.encode("utf-8") for value in secret_values if isinstance(value, str) and value},
                     key=len, reverse=True)
    overlap = max((len(value) for value in secrets), default=1) - 1
    pending = b""
    while True:
        chunk = pipe.read(65536)
        if not chunk:
            break
        pending += chunk
        safe_limit = max(0, len(pending) - overlap)
        while safe_limit:
            crossing = []
            for secret in secrets:
                position = pending.find(secret)
                while position >= 0:
                    if position < safe_limit < position + len(secret):
                        crossing.append(position)
                    position = pending.find(secret, position + 1)
            if not crossing:
                break
            safe_limit = min(crossing)
        safe, pending = pending[:safe_limit], pending[safe_limit:]
        for secret in secrets:
            safe = safe.replace(secret, b"[REDACTED: credential]")
        destination.write(safe)
    for secret in secrets:
        pending = pending.replace(secret, b"[REDACTED: credential]")
    destination.write(pending)
    destination.flush()


def configuration_secret_values(snapshot: pathlib.Path, env: dict):
    values = {value for name, value in env.items()
              if any(marker in name.upper() for marker in ("KEY", "TOKEN", "SECRET", "PASSWORD", "CREDENTIAL"))
              and isinstance(value, str) and value}
    for name in ("appsettings.json", "appsettings.Development.json", "settings.json"):
        path = snapshot / name
        if not path.is_file():
            continue
        try:
            document = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        def visit(value, key="", sensitive_parent=False):
            sensitive = sensitive_parent or any(
                marker in key.casefold() for marker in ("key", "token", "secret", "password", "credential"))
            if isinstance(value, dict):
                for child_key, child in value.items():
                    visit(child, str(child_key), sensitive)
            elif isinstance(value, list):
                for child in value:
                    visit(child, key, sensitive)
            elif isinstance(value, (str, int, float)) and sensitive:
                if str(value):
                    values.add(str(value))
        visit(document)
    return values


def start_api_process(command, cwd: pathlib.Path, env: dict, log_path: pathlib.Path):
    log_path.parent.mkdir(parents=True, exist_ok=True)
    stream = log_path.open("wb")
    try:
        process = subprocess.Popen(command, cwd=str(cwd), env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   start_new_session=(os.name != "nt"))
    except BaseException:
        stream.close()
        raise
    snapshot = pathlib.Path(env["ASPNETCORE_CONTENTROOT"]) if env.get("ASPNETCORE_CONTENTROOT") else log_path.parent
    credential_values = configuration_secret_values(snapshot, env)
    output_thread = threading.Thread(target=_copy_redacted_output, args=(process.stdout, stream, credential_values), daemon=True)
    output_thread.start()
    return process, stream, output_thread


def stop_api_process(process, output_thread, startup_log):
    """Stop dotnet and its API child, then drain inherited stdout before closing the log."""
    if os.name == "nt":
        # dotnet run is a launcher; bOps.Api can outlive it and keep the pipe open.
        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        # start_api_process gives dotnet its own session/process group.
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
    try:
        process.wait(timeout=15)
    except subprocess.TimeoutExpired:
        if os.name == "nt":
            process.kill()
        else:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        process.wait()
    if output_thread is not None:
        output_thread.join(timeout=15)
        if output_thread.is_alive() and os.name != "nt":
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            output_thread.join(timeout=15)
    # Never close a pipe while its reader is blocked in read(): on Windows close()
    # waits for that read lock, which used to strand -PreflightOnly until Ctrl+C.
    if output_thread is None or not output_thread.is_alive():
        if process.stdout is not None:
            process.stdout.close()
        if startup_log is not None:
            startup_log.flush()
            startup_log.close()


def check_api_startup(process, log_path: pathlib.Path):
    exit_code = process.poll()
    if exit_code is not None:
        raise ApiStartupFailure(exit_code, "API process startup", log_path)


def include_startup_diagnostic(log_path: pathlib.Path, capture_root: pathlib.Path):
    destination = capture_root / "diagnostics" / "api-startup.log"
    destination.parent.mkdir(parents=True, exist_ok=True)
    if log_path.is_file():
        shutil.copyfile(log_path, destination)
    return destination


def safe_endpoint(value):
    if not isinstance(value, str) or not value:
        return value
    try:
        parsed = urlsplit(value)
        host = parsed.hostname or ""
        if parsed.port is not None:
            host += f":{parsed.port}"
        return urlunsplit((parsed.scheme, host, "", "", ""))
    except ValueError:
        return "<configured endpoint omitted: invalid URL>"


def describe_effective_configuration(providers: dict, settings_view: dict | None):
    active = providers.get("active") or {}
    description = {
        "registeredProviders": providers.get("registeredProviderIds", []),
        "active": {
            "provider": active.get("provider", active.get("providerId")),
            "model": active.get("model"),
            "endpoint": safe_endpoint(active.get("baseUrl", active.get("baseURL"))),
            "credentialAvailable": active.get("hasApiKey", active.get("credentialAvailable")) is True,
        },
        "settingsViewAvailable": isinstance(settings_view, dict),
    }
    if isinstance(settings_view, dict):
        for key in (
            "activeProviderId", "activeProviderSource", "effectiveModel", "effectiveSupportsNativeToolCalling",
            "effectiveRequestTimeout", "effectiveBaseUrlSource", "effectiveModelSource",
            "effectiveToolCallingSource", "effectiveRequestTimeoutSource", "effectiveCredentialAvailable",
            "effectiveCredentialSource", "effectiveFallbackSource", "configurationGeneration",
        ):
            if key in settings_view:
                description[key] = settings_view[key]
        if "effectiveBaseUrl" in settings_view:
            description["effectiveEndpoint"] = safe_endpoint(settings_view["effectiveBaseUrl"])
        fallbacks = settings_view.get("effectiveFallbacks")
        if isinstance(fallbacks, list):
            description["effectiveFallbacks"] = [
                {key: item.get(key) for key in ("ordinal", "provider", "model", "supportsNativeToolCalling", "credentialAvailable")}
                | ({"endpoint": safe_endpoint(item.get("baseUrl"))} if "baseUrl" in item else {})
                for item in fallbacks if isinstance(item, dict)
            ]
    return description


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


class RunningTaskNotExecuting(RuntimeError):
    pass


def poll_task(api: str, key: str, task_id: str, timeout_seconds: int, on_response=None):
    deadline = time.monotonic() + timeout_seconds
    terminal = {status.lower() for status in TERMINAL}
    while time.monotonic() < deadline:
        try:
            code, raw, payload = http_json(api + f"/api/agents/tasks/{task_id}", key, timeout=30)
        except (OSError, TimeoutError):
            time.sleep(2)
            continue
        if code == 404:
            time.sleep(1)
            continue
        if code in (429, 503):
            time.sleep(3)
            continue
        if code != 200:
            raise RuntimeError(f"Task poll {task_id} failed HTTP {code}.")
        if on_response is not None:
            on_response(raw)
        state = task_status_name((payload or {}).get("status", (payload or {}).get("Status", "")))
        if state.lower() in terminal:
            return raw, payload
        if state == "Running" and payload.get("executing") is False:
            raise RunningTaskNotExecuting(f"Task {task_id} is persisted Running but the API reports no executor.")
        time.sleep(2)
    raise TimeoutError(f"Task {task_id} did not reach a terminal state before timeout.")


def write_json(path: pathlib.Path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def build_api_environment(snapshot: pathlib.Path, state_paths: dict[str, pathlib.Path], port: int,
                          expected_user_sid: str | None, parent_environment=None):
    env = dict(os.environ if parent_environment is None else parent_environment)
    api_url = f"http://127.0.0.1:{port}"
    env.update({
        "ASPNETCORE_ENVIRONMENT": "Development", "DOTNET_ENVIRONMENT": "Development",
        "ASPNETCORE_CONTENTROOT": str(snapshot), "ASPNETCORE_URLS": api_url,
        "Settings__FilePath": str(snapshot / "settings.json"),
        "Vault__FilePath": str(snapshot / "vault.dat"),
        "Memory__FilePath": str(state_paths["tasks.db"]),
        "Audit__FilePath": str(state_paths["audit.jsonl"]),
        "BrowserSession__FilePath": str(state_paths["sessions.db"]),
        "SystemMessages__FilePath": str(state_paths["system-messages.db"]),
        "Delegation__FilePath": str(state_paths["delegations.db"]),
        "Filesystem__Inventory__ManifestStorePath": str(state_paths["filesystem-inventory.db"]),
        "Policy__FilePath": str(state_paths["read-only-policy.yaml"]),
        "BENCH_EXPECTED_WINDOWS_USER_SID": expected_user_sid or "",
    })
    return env


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
        stored_ids = [row[0] for row in snapshot.execute("SELECT id FROM tasks ORDER BY rowid")]
        all_ids = list(dict.fromkeys(ids + stored_ids))
        for task_id in all_ids:
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
        return all_ids
    finally:
        source.close()


def package(out: pathlib.Path, zip_path: pathlib.Path, runs_per_scenario: int = 3):
    temporary_zip = zip_path.with_suffix(zip_path.suffix + ".partial")
    manifest = out / "manifest.json"
    manifest_hash = out / "manifest.sha256"
    files = []
    for path in sorted(p for p in out.rglob("*") if p.is_file() and p not in (zip_path, manifest, manifest_hash)):
        data = path.read_bytes()
        files.append({"path": path.relative_to(out).as_posix(), "sizeBytes": len(data), "sha256": sha(data)})
    startup_path = out / "startup.json"
    startup = json.loads(startup_path.read_text(encoding="utf-8")) if startup_path.is_file() else {}
    write_json(manifest, {
        "runsPerScenario": runs_per_scenario,
        "requestedTaskCount": len(SCENARIOS) * runs_per_scenario,
        "configurationProvenance": startup.get("configurationProvenance"),
        "effectiveConfiguration": startup.get("effectiveConfiguration"),
        "runtimeProvenance": startup.get("runtimeProvenance"),
        "files": files,
    })
    manifest_hash.write_text(sha(manifest.read_bytes()) + "  manifest.json\n", encoding="ascii")
    with zipfile.ZipFile(temporary_zip, "w", compression=zipfile.ZIP_DEFLATED, allowZip64=True) as archive:
        for path in sorted(p for p in out.rglob("*") if p.is_file() and p != zip_path):
            archive.write(path, path.relative_to(out).as_posix())
    os.replace(temporary_zip, zip_path)


def run(args):
    root = pathlib.Path(args.target_repo_root).resolve()
    anchor, commit, dirty_status = validate_runtime_target(root, args.expected_target_commit)
    target_provenance = runtime_provenance(root, anchor, commit, args.expected_target_commit)
    windows_identity = verify_windows_identity(args.expected_user_sid)
    original_policy_path = effective_policy_path(root)
    if not os.environ.get("BOPS_API_KEY"):
        raise RuntimeError("BOPS_API_KEY is unavailable to the elevated process; no tasks were started.")
    if os.name == "nt" and not os.environ.get("BOPS_VAULT_MASTER_KEY"):
        raise RuntimeError("BOPS_VAULT_MASTER_KEY is unavailable to the API child process; no API was started.")
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
    snapshot_destination = pathlib.Path(tempfile.gettempdir()) / f"bOps-BENCH00-config-{run_id}"
    config_snapshot_path, snapshot_manifest, snapshot_reused = prepare_config_snapshot(
        pathlib.Path(args.config_source_root), snapshot_destination,
        pathlib.Path(args.config_snapshot_path) if args.config_snapshot_path else None,
    )
    paths = {name: (run_root / name).resolve() for name in (
        "tasks.db", "audit.jsonl", "sessions.db", "system-messages.db", "delegations.db", "filesystem-inventory.db")}
    readonly_policy_path = run_root / "read-only-policy.yaml"
    readonly_policy_path.write_text(READ_ONLY_POLICY, encoding="utf-8")
    startup_log_path = output / "api-startup.log"
    port = reserve_port()
    env = build_api_environment(config_snapshot_path, {**paths, "read-only-policy.yaml": readonly_policy_path},
                                port, args.expected_user_sid)
    api_child_master_key_present = bool(env.get("BOPS_VAULT_MASTER_KEY"))
    project = api_project_path(root)
    if not project.is_file():
        raise RuntimeError(f"bOps.Api project not found under target root: {root}")
    target_config_files = {}
    for config_path in (project.parent / "appsettings.json", project.parent / "appsettings.Development.json", project.parent / "Properties" / "launchSettings.json"):
        target_config_files[config_path.name if config_path.parent == project.parent else "Properties/launchSettings.json"] = {
            "present": config_path.is_file(), "sha256": sha(config_path.read_bytes()) if config_path.is_file() else None,
        }
    process = None
    startup_log = None
    api = f"http://127.0.0.1:{port}"
    startup = {"startedAtUtc": utc_now(), "targetRepoRoot": str(root), "gitCommit": commit, "runtimeReferenceCommit": anchor,
               "runtimeProvenance": target_provenance,
               "gitBranch": branch, "gitDirty": dirty, "gitDirtyPaths": [line[3:] for line in dirty_status.splitlines()],
               "originalPolicyPath": str(original_policy_path),
               "originalPolicySha256": sha(original_policy_path.read_bytes()) if original_policy_path.is_file() else None,
               "effectivePolicyPath": str(readonly_policy_path), "effectivePolicySha256": sha(readonly_policy_path.read_bytes()),
               "apiBaseUrl": api, "apiProject": str(project), "targetProjectConfigurationFingerprintsNotLoaded": target_config_files,
               "hostMode": "API-only",
               "apiWorkingDirectory": str(root), "launchProfileUsed": False,
               "startupDiagnosticLog": str(startup_log_path),
               "environment": "Development", "windowsIdentityVerification": windows_identity,
               "apiChildMasterKeyPresent": api_child_master_key_present,
               "configurationProvenance": {
                   "sourceRoot": snapshot_manifest["sourceRoot"],
                   "snapshotRoot": str(config_snapshot_path),
                   "snapshotReusedExplicitly": snapshot_reused,
                   "contentRootOverride": str(config_snapshot_path),
                   "files": snapshot_manifest["files"],
                   "selectedConfiguration": snapshot_manifest["selectedConfiguration"],
                   "configurationFingerprint": snapshot_manifest["configurationFingerprint"],
                   "vaultFileIncludedInArchive": False,
                   "masterKeyValueIncludedInArchive": False,
               },
               "isolatedOverrides": {
                   "ASPNETCORE_CONTENTROOT": str(config_snapshot_path), "ASPNETCORE_URLS": api,
                   "Policy__FilePath": str(readonly_policy_path),
                   "Settings__FilePath": str(config_snapshot_path / "settings.json"),
                   "Vault__FilePath": str(config_snapshot_path / "vault.dat"),
                   "apiChildMasterKeyPresent": api_child_master_key_present,
                   "BENCH_EXPECTED_WINDOWS_USER_SID": args.expected_user_sid or "<verified by launcher>",
                   **{k: str(v) for k,v in paths.items()},
               },
        "originalConfigurationFilesUnchanged": True,
        "effectivePolicy": "BENCH-00 isolated policy: Read=Automatic; Low/Medium/High/Critical=Forbidden; no approval is automated",
        "harness": "BENCH-00 stdlib Python", "python": sys.version,
               "dotnetCommand": "dotnet run --project <target>/src/core/bOps.Api/bOps.Api.csproj --no-launch-profile",
               "timeoutsSeconds": {"startup": args.startup_timeout, "task": args.task_timeout, "http": 30}}
    write_json(run_root / "startup.json", startup)
    write_json(run_root / "scenarios.json", SCENARIOS)
    archive_path = output / f"bops-live-benchmark-{run_id}-{commit[:12]}.zip"
    (run_root / "README-SENSITIVE").write_text(README, encoding="utf-8")
    roster, terminal = [], {}
    def write_roster():
        write_json(run_root / "run-roster.json", {
            "runsPerScenario": args.runs_per_scenario,
            "requestedTaskCount": len(SCENARIOS) * args.runs_per_scenario,
            "tasks": roster,
        })
    write_roster()
    status_doc = {"runId": run_id, "state": "starting", "tasks": []}
    startup_failure = False
    try:
        startup["helperRestore"] = restore_api_helpers(root, env)
        write_json(run_root / "startup.json", startup)
        process, startup_log, startup_output_thread = start_api_process(api_command(project), root, env, startup_log_path)
        deadline = time.monotonic() + args.startup_timeout
        while time.monotonic() < deadline:
            check_api_startup(process, startup_log_path)
            try:
                code, provider_raw, provider_info = http_json(api + "/api/providers", os.environ["BOPS_API_KEY"], timeout=3)
                if code == 200:
                    settings_code, _, settings_view = http_json(api + "/api/settings/", os.environ["BOPS_API_KEY"], timeout=10)
                    if settings_code != 200:
                        raise RuntimeError(f"API effective configuration is unavailable (HTTP {settings_code}); no tasks were started.")
                    active = (provider_info or {}).get("active") or {}
                    credential_available = active.get("hasApiKey", active.get("credentialAvailable")) is True
                    startup["effectiveConfiguration"] = describe_effective_configuration(provider_info or {}, settings_view)
                    startup["effectiveProvider"] = startup["effectiveConfiguration"]["active"]
                    write_json(run_root / "startup.json", startup)
                    verify_loaded_configuration(provider_info or {}, settings_view or {}, snapshot_manifest["selectedConfiguration"])
                    if not credential_available:
                        raise RuntimeError("The selected provider credential is unavailable in the benchmark process; no tasks were started.")
                    break
                if code in (401, 403):
                    raise RuntimeError(f"API authentication/authorization failed ({code}); no tasks were started.")
            except (OSError, TimeoutError):
                pass
            time.sleep(1)
        else:
            raise ApiStartupFailure(None, "API readiness timeout", startup_log_path)
        if args.preflight_only:
            status_doc = {"runId": run_id, "state": "preflight-complete", "tasks": []}
            print("BENCH-00 preflight passed: API authentication and provider credential are available; no tasks or model calls were made.", flush=True)
            print(f"Config snapshot: {config_snapshot_path}", flush=True)
            return
        ordinal = 0
        for round_number, scenario in scenario_runs(args.runs_per_scenario):
            config_snapshot_manifest(config_snapshot_path)
            ordinal += 1
            intention = f"{scenario['id']}-R{round_number}"
            idem = str(uuid.uuid4())
            raw, started = submit_task(api, os.environ["BOPS_API_KEY"], scenario["goal"], idem)
            task_id = str(started.get("taskId", started.get("TaskId", "")))
            if not task_id:
                raise RuntimeError(f"Task submission {intention} returned no task id.")
            entry = {"ordinal": ordinal, "intention": intention, "scenarioId": scenario["id"], "round": round_number,
                     "idempotencyKey": idem, "taskId": task_id, "acceptedAtUtc": utc_now()}
            roster.append(entry); write_roster()
            task_folder = run_root / "tasks" / task_id
            task_folder.mkdir(parents=True, exist_ok=True)
            (task_folder / "api-accepted-original.json").write_bytes(raw)
            try:
                raw_status, _ = poll_task(api, os.environ["BOPS_API_KEY"], task_id, args.task_timeout,
                                          lambda raw: (task_folder / "api-latest-original.json").write_bytes(raw))
            except RunningTaskNotExecuting:
                write_json(task_folder / "capture-status.json", {"taskId": task_id, "status": "running-not-executing", "automaticRecovery": False})
                raise
            except TimeoutError:
                write_json(task_folder / "capture-status.json", {"taskId": task_id, "status": "task-timeout"})
                raise
            terminal[task_id] = raw_status
            (task_folder / "api-terminal-original.json").write_bytes(raw_status)
            # Keep the roster useful in a partial capture before moving to the next scenario.
            terminal_payload = json.loads(raw_status)
            entry["terminalStatus"] = task_status_name((terminal_payload or {}).get("status", (terminal_payload or {}).get("Status", "")))
            entry["terminalCapturedAtUtc"] = utc_now()
            write_roster()
            # Capture the persisted source after every completed task; an interruption preserves all earlier rows.
            captured_ids = extract_records(paths["tasks.db"], [item["taskId"] for item in roster], run_root, paths["audit.jsonl"])
            roster_ids = {item["taskId"] for item in roster}
            status_doc = {"runId": run_id, "state": "running", "tasks": [{"taskId": x["taskId"], "terminalApiCaptured": x["taskId"] in terminal} for x in roster],
                          "unrosteredPersistedTaskIds": sorted(set(captured_ids) - roster_ids)}
            write_json(run_root / "capture-status.json", status_doc)
            package(run_root, archive_path, args.runs_per_scenario)
            print(f"Captured {ordinal}/{len(SCENARIOS) * args.runs_per_scenario}: {intention} ({task_id})", flush=True)
        status_doc["state"] = "complete"
    except KeyboardInterrupt:
        status_doc["state"] = "interrupted"
        raise
    except ApiStartupFailure as exc:
        startup_failure = True
        status_doc = {"runId": run_id, "state": "partial-error", "error": str(exc), "tasks": roster}
        raise
    except Exception as exc:
        status_doc = {"runId": run_id, "state": "partial-error", "error": str(exc), "tasks": roster}
        raise
    finally:
        write_json(run_root / "capture-status.json", status_doc)
        if paths["tasks.db"].exists():
            try:
                captured_ids = extract_records(paths["tasks.db"], [item["taskId"] for item in roster], run_root, paths["audit.jsonl"])
                roster_ids = {item["taskId"] for item in roster}
                status_doc["unrosteredPersistedTaskIds"] = sorted(set(captured_ids) - roster_ids)
                write_json(run_root / "capture-status.json", status_doc)
            except (sqlite3.Error, OSError): pass
        if startup_log is not None:
            if process is not None:
                stop_api_process(process, locals().get("startup_output_thread"), startup_log)
            else:
                startup_log.close()
        include_startup_diagnostic(startup_log_path, run_root)
        package(run_root, archive_path, args.runs_per_scenario)
        shutil.rmtree(run_root, ignore_errors=True)
        if not startup_failure:
            print(f"Archive: {archive_path}", flush=True)
            print(f"Config snapshot: {config_snapshot_path}", flush=True)


def make_argument_parser():
    parser = argparse.ArgumentParser()
    parser.add_argument("--target-repo-root", required=True)
    parser.add_argument("--expected-target-commit", "-ExpectedTargetCommit", type=full_commit_sha,
                        help="Require this full target HEAD SHA and AF-05H ancestry; allow committed runtime changes.")
    parser.add_argument("--config-source-root", default=str(pathlib.Path(__file__).resolve().parents[2]))
    parser.add_argument("--config-snapshot-path")
    parser.add_argument("--runs-per-scenario", type=int, choices=range(1, 4), default=3)
    parser.add_argument("--preflight-only", action="store_true")
    parser.add_argument("--expected-user-sid")
    parser.add_argument("--output-root")
    parser.add_argument("--startup-timeout", type=int, default=180)
    parser.add_argument("--task-timeout", type=int, default=5400)
    return parser


def main(argv=None):
    args = make_argument_parser().parse_args(argv)
    run(args)


if __name__ == "__main__":
    try:
        main()
    except ApiStartupFailure as error:
        code_text = f"exit code {error.exit_code}" if error.exit_code is not None else "exit code unavailable"
        print(f"BENCH-00 failed: {code_text}; phase: {error.phase}; log: {error.log_path}", file=sys.stderr)
        sys.exit(1)
    except Exception as error:
        print(f"BENCH-00 failed: {error}", file=sys.stderr)
        sys.exit(1)
