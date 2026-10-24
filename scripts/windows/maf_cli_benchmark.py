#!/usr/bin/env python3
"""BOPS-0125 MAF-CLI-AB-01: serial A/B campaign of the real bOps CLI, MAF governed turn OFF (A) versus ON (B).

* Both variants run the same build, model profile, policy, limits and prompts; only ``Cli__Experimental__MafGovernedTurn`` differs.
* The effective mode is verified from the process itself (the EXPERIMENTAL line on standard error), never assumed from the variable.
* Every run has isolated state (tasks, audit, inventory, plugins) and is captured in full into a private directory outside the repository.
* Nothing is uploaded, no runtime semantics are changed, no live run starts without ``--allow-live``.

Sub-commands: preflight | run | analyze | verify | estimate.  Reuses the BENCH-00 collector helpers (live_benchmark.py).
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import http.server
import json
import os
import pathlib
import random
import re
import shutil
import signal
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from urllib.parse import urlsplit

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import live_benchmark as lb  # noqa: E402  (BENCH-00 helpers: capture, redaction, provenance)
import maf_cli_analysis as an  # noqa: E402

SCHEMA = 1
MAF_KEY = "Cli__Experimental__MafGovernedTurn"
MARKER = "EXPERIMENTAL: each model turn is hosted by a Microsoft Agent Framework agent"
API_KEY_ENV = "BOPS_MODEL_API_KEY"
SCENARIO_IDS = [s["id"] for s in lb.SCENARIOS]
GOALS = {s["id"]: s["goal"] for s in lb.SCENARIOS}
VARIANTS = {"A": {"label": "MAF OFF", "maf": False}, "B": {"label": "MAF ON", "maf": True}}
# Same effective policy as BENCH-00 but with every level explicit: Read automatic, everything else forbidden, no approval path.
READ_ONLY_POLICY = "defaults:\n  read: automatic\n  low: forbidden\n  medium: forbidden\n  high: forbidden\n  critical: forbidden\n"
SCRUB_PREFIXES = ("CLI__", "KNOWLEDGE__", "AGENT__", "MODELPROVIDER__", "POLICY__", "AUDIT__", "MEMORY__", "PLUGINS__", "FILESYSTEM__",
                  "DOCKER__", "WEB__", "DELEGATION__", "SYSTEMMESSAGES__", "PREREQUISITES__", "LOGGING__", "OTEL_", "ASPNETCORE_",
                  "BROWSERSESSION__", "SETTINGS__", "VAULT__")
SCRUB_EXACT = {API_KEY_ENV, "DOTNET_ENVIRONMENT"}
AGENT_KEYS = {"MaxSteps", "MaxLifetimeSteps", "MaxReplans", "MaxLifetimeReplans", "MaxTotalTokens", "MaxAttemptDuration",
              "DefaultToolTimeout", "MaxToolTimeout", "MaxObservationCharacters", "MaxOutputTokens", "ModelCallMaxAttempts",
              "ModelCallAttemptTimeout", "ModelCallBudget", "EmptyFinalResponseRetries", "EvidenceDisclosureEnabled",
              "MaxModelPayloadCharacters", "MaxConsecutivePolicyDenials"}
DEFAULT_PER_RUN_TOKEN_CAP = 350_000  # shipped Agent:MaxTotalTokens
EVIDENCE_EXCLUDED = {"report", "evaluation", "integrity.json", "campaign-state.json"}
LOOPBACK = {"127.0.0.1", "localhost", "::1"}
README = ("PRIVATE - POTENTIALLY SENSITIVE MATERIAL\n\nThis directory holds complete, unredacted task state, model request/response payloads, "
          "tool results and audit records of this PC. Known credential values are redacted from captured process output and the "
          "campaign is scanned for them, but nothing else is anonymised. Never commit, upload or share it. This tool does not upload anything.\n")

secure_dir = lb.secure_private_directory  # patched in offline tests


class BenchmarkError(RuntimeError):
    pass


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def sha_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_json(path: pathlib.Path, value) -> None:
    lb.write_json(path, value)


# --------------------------------------------------------------------------------------------------------------------
# Model profile (non-secret) and effective configuration
# --------------------------------------------------------------------------------------------------------------------

def load_profile(path: pathlib.Path) -> dict:
    try:
        profile = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as exc:
        raise BenchmarkError(f"Model profile is unreadable: {exc}") from exc
    if not isinstance(profile, dict):
        raise BenchmarkError("Model profile must be a JSON object.")
    for key, value in profile.items():
        low = key.casefold()
        if key != "apiKeyEnv" and any(m in low for m in ("apikey", "secret", "password", "bearer", "credential")) and value:
            raise BenchmarkError(f"Model profile key '{key}' looks like a secret; profiles hold no secrets (use apiKeyEnv to name an environment variable).")
    for required in ("provider", "baseUrl", "model"):
        if not isinstance(profile.get(required), str) or not profile[required].strip():
            raise BenchmarkError(f"Model profile requires a non-empty '{required}'.")
    parsed = urlsplit(profile["baseUrl"])
    if parsed.scheme not in ("http", "https") or not parsed.hostname or parsed.username or parsed.password or parsed.query:
        raise BenchmarkError("Model profile baseUrl must be an http(s) URL without credentials or query.")
    key_env = profile.get("apiKeyEnv")
    if key_env is not None and (not isinstance(key_env, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key_env)):
        raise BenchmarkError("apiKeyEnv must be the NAME of an environment variable.")
    agent = profile.get("agent") or {}
    if not isinstance(agent, dict) or set(agent) - AGENT_KEYS:
        raise BenchmarkError("Model profile 'agent' allows only: " + ", ".join(sorted(AGENT_KEYS)))
    extra = profile.get("extraConfiguration") or {}
    if not isinstance(extra, dict) or any(not k.startswith("Agent:Context:") for k in extra):
        raise BenchmarkError("extraConfiguration allows only keys under 'Agent:Context:'.")
    for value in list(agent.values()) + list(extra.values()):
        if isinstance(value, (dict, list)):
            raise BenchmarkError("Profile configuration values must be scalars.")
    return {"profileId": profile.get("profileId") or "unnamed", "provider": profile["provider"].strip(), "baseUrl": profile["baseUrl"].strip(),
            "model": profile["model"].strip(), "supportsNativeToolCalling": bool(profile.get("supportsNativeToolCalling", True)),
            "requestTimeout": profile.get("requestTimeout"), "apiKeyEnv": key_env, "agent": agent, "extraConfiguration": extra,
            "pricing": profile.get("pricing") or {}, "remote": parsed.hostname.casefold() not in LOOPBACK}


def apply_token_cap(profile: dict, cap) -> dict:
    """A per-run token cap given on the command line overrides the profile's Agent:MaxTotalTokens for BOTH variants."""
    if cap is not None:
        if cap < 1000:
            raise BenchmarkError("--per-run-token-cap must be at least 1000 tokens.")
        profile["agent"] = {**profile["agent"], "MaxTotalTokens": int(cap)}
    return profile


def _env_scalar(value) -> str:
    return str(value).lower() if isinstance(value, bool) else str(value)


def build_overlay(profile: dict, variant: str, work: pathlib.Path) -> dict:
    """Non-secret environment overlay of one run. Paths are the only per-run difference besides the MAF key."""
    overlay = {
        "DOTNET_ENVIRONMENT": "Production", "DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "OTEL_SDK_DISABLED": "true",
        "ModelProvider__Provider": profile["provider"], "ModelProvider__BaseUrl": profile["baseUrl"], "ModelProvider__Model": profile["model"],
        "ModelProvider__SupportsNativeToolCalling": _env_scalar(profile["supportsNativeToolCalling"]),
        "Knowledge__Enabled": "false", MAF_KEY: _env_scalar(VARIANTS[variant]["maf"]),
        "Memory__FilePath": str(work / "tasks.db"), "Audit__FilePath": str(work / "audit.jsonl"), "Policy__FilePath": str(work / "policy.yaml"),
        "SystemMessages__FilePath": str(work / "system-messages.db"), "Delegation__FilePath": str(work / "delegations.db"),
        "Filesystem__Inventory__ManifestStorePath": str(work / "filesystem-inventory.db"),
        "Plugins__RootPath": str(work / "plugins"), "Plugins__StorePath": str(work / "plugins.json"),
        "Plugins__TrustStorePath": str(work / "publisher-trust.json"),
    }
    if profile.get("requestTimeout"):
        overlay["ModelProvider__RequestTimeout"] = str(profile["requestTimeout"])
    if profile["apiKeyEnv"]:
        overlay["ModelProvider__ApiKeySecret__Provider"] = "environment"
        overlay["ModelProvider__ApiKeySecret__Name"] = API_KEY_ENV
    for key, value in profile["agent"].items():
        overlay[f"Agent__{key}"] = _env_scalar(value)
    for key, value in profile["extraConfiguration"].items():
        overlay[key.replace(":", "__")] = _env_scalar(value)
    return overlay


PATH_KEYS = {"Memory__FilePath", "Audit__FilePath", "Policy__FilePath", "SystemMessages__FilePath", "Delegation__FilePath",
             "Filesystem__Inventory__ManifestStorePath", "Plugins__RootPath", "Plugins__StorePath", "Plugins__TrustStorePath"}


def config_fingerprint(overlay: dict, policy_sha: str, appsettings_sha: str | None, cli_sha: str | None) -> str:
    """Everything that must be identical between A and B: the overlay without per-run paths and without the MAF key."""
    stable = {k: v for k, v in sorted(overlay.items()) if k not in PATH_KEYS and k != MAF_KEY}
    return sha_bytes(json.dumps({"overlay": stable, "policy": policy_sha, "appsettings": appsettings_sha, "cli": cli_sha},
                                sort_keys=True, separators=(",", ":")).encode("utf-8"))


def child_environment(parent: dict, overlay: dict, api_key: str | None):
    """Return (environment, scrubbed variable NAMES). Ambient bOps/ASP.NET configuration never reaches the child."""
    env, scrubbed = {}, []
    for key, value in parent.items():
        upper = key.upper()
        if upper in SCRUB_EXACT or upper.startswith(SCRUB_PREFIXES):
            scrubbed.append(key)
            continue
        env[key] = value
    env.update(overlay)
    if api_key:
        env[API_KEY_ENV] = api_key
    return env, sorted(scrubbed)


def secret_values(env: dict, api_key: str | None) -> set[str]:
    values = {api_key} if api_key else set()
    for name, value in env.items():
        if any(m in name.upper() for m in ("KEY", "TOKEN", "SECRET", "PASSWORD", "CREDENTIAL")) and isinstance(value, str) and len(value) >= 8:
            values.add(value)
    return {v for v in values if v}


# --------------------------------------------------------------------------------------------------------------------
# Environment facts
# --------------------------------------------------------------------------------------------------------------------

def host_snapshot(sample_seconds: float = 1.0) -> dict:
    snap = {"capturedAtUtc": utc_now(), "cpuCount": os.cpu_count()}
    try:
        if os.name == "nt":
            class MemoryStatus(ctypes.Structure):
                _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong), ("ullTotalPhys", ctypes.c_ulonglong),
                            ("ullAvailPhys", ctypes.c_ulonglong), ("ullTotalPageFile", ctypes.c_ulonglong),
                            ("ullAvailPageFile", ctypes.c_ulonglong), ("ullTotalVirtual", ctypes.c_ulonglong),
                            ("ullAvailVirtual", ctypes.c_ulonglong), ("sullAvailExtendedVirtual", ctypes.c_ulonglong)]
            status = MemoryStatus()
            status.dwLength = ctypes.sizeof(MemoryStatus)
            ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status))
            snap.update(memoryTotalMb=status.ullTotalPhys // 2**20, memoryAvailableMb=status.ullAvailPhys // 2**20)

            def times():
                idle, kernel, user = (ctypes.c_ulonglong(), ctypes.c_ulonglong(), ctypes.c_ulonglong())
                ctypes.windll.kernel32.GetSystemTimes(ctypes.byref(idle), ctypes.byref(kernel), ctypes.byref(user))
                return idle.value, kernel.value + user.value
            i0, t0 = times()
            time.sleep(sample_seconds)
            i1, t1 = times()
            snap["cpuBusyPercent"] = round(100.0 * (1 - (i1 - i0) / max(1, t1 - t0)), 1)
        else:
            meminfo = dict(line.split(":", 1) for line in pathlib.Path("/proc/meminfo").read_text().splitlines() if ":" in line)
            snap.update(memoryTotalMb=int(meminfo["MemTotal"].split()[0]) // 1024, memoryAvailableMb=int(meminfo["MemAvailable"].split()[0]) // 1024)
            snap["loadAverage1m"] = os.getloadavg()[0]
    except Exception as exc:  # noqa: BLE001 - environment facts are best effort and must never stop a run
        snap["unavailable"] = type(exc).__name__
    return snap


def is_elevated() -> bool | None:
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin()) if os.name == "nt" else os.geteuid() == 0
    except Exception:  # noqa: BLE001
        return None


def tool_version(*command) -> str | None:
    try:
        return subprocess.check_output(list(command), text=True, stderr=subprocess.STDOUT, timeout=30).strip().splitlines()[0]
    except Exception:  # noqa: BLE001
        return None


def under_git_work_tree(path: pathlib.Path) -> bool:
    return any((p / ".git").exists() for p in [path, *path.parents])


# --------------------------------------------------------------------------------------------------------------------
# Build, CLI command and process execution
# --------------------------------------------------------------------------------------------------------------------

def find_cli_dll(root: pathlib.Path) -> pathlib.Path | None:
    candidates = sorted((root / "src" / "core" / "bOps.Cli" / "bin" / "Release").glob("net*/bops.dll"), key=lambda p: p.stat().st_mtime)
    return candidates[-1] if candidates else None


def build_cli(root: pathlib.Path, env: dict) -> pathlib.Path:
    lb.restore_api_helpers(root, env)  # the CLI csproj copies the same two Windows helpers after Build
    project = root / "src" / "core" / "bOps.Cli" / "bOps.Cli.csproj"
    result = subprocess.run(["dotnet", "build", str(project), "-c", "Release", "-nologo", "-v", "q"], cwd=str(root), env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT, timeout=1800)  # NuGet output may carry feed credentials
    if result.returncode != 0:
        raise BenchmarkError(f"dotnet build of bOps.Cli failed (exit {result.returncode}); build it manually to read the diagnostics.")
    dll = find_cli_dll(root)
    if dll is None:
        raise BenchmarkError("bops.dll was not found after the build.")
    return dll


def kill_tree(process: subprocess.Popen) -> None:
    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    try:
        process.wait(timeout=15)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()


def execute_cli(command: list[str], goal: str, cwd: pathlib.Path, env: dict, secrets: set[str], stdout_path: pathlib.Path,
                stderr_path: pathlib.Path, timeout: float) -> dict:
    """Run the CLI with stdin closed (an approval prompt reads EOF and denies), redacted output files and a hard timeout."""
    started = time.monotonic()
    process = subprocess.Popen([*command, goal], cwd=str(cwd), env=env, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, start_new_session=(os.name != "nt"))
    streams, threads = [], []
    for pipe, path in ((process.stdout, stdout_path), (process.stderr, stderr_path)):
        stream = path.open("wb")
        streams.append(stream)
        thread = threading.Thread(target=lb._copy_redacted_output, args=(pipe, stream, secrets), daemon=True)
        thread.start()
        threads.append(thread)
    timed_out = interrupted = False
    try:
        process.wait(timeout=max(1.0, timeout))
    except subprocess.TimeoutExpired:
        timed_out = True
        kill_tree(process)
    except KeyboardInterrupt:
        interrupted = True
        kill_tree(process)
    for thread in threads:
        thread.join(timeout=15)
    for stream in streams:
        stream.flush()
        stream.close()
    return {"exitCode": process.returncode, "timedOut": timed_out, "interrupted": interrupted,
            "wallSeconds": round(time.monotonic() - started, 3)}


# --------------------------------------------------------------------------------------------------------------------
# Preflight
# --------------------------------------------------------------------------------------------------------------------

class StubModelServer:
    """Loopback server that answers every request with HTTP 400 and counts it. Used only by the mode probe: no model is involved."""

    def __init__(self):
        self.requests = 0
        outer = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def _answer(self):
                outer.requests += 1
                length = int(self.headers.get("Content-Length") or 0)
                if length:
                    self.rfile.read(length)
                body = b'{"error":{"message":"preflight stub: no model is attached","type":"invalid_request_error"}}'
                self.send_response(400)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            do_GET = do_POST = _answer

            def log_message(self, *args):
                pass

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)

    @property
    def base_url(self) -> str:
        return f"http://127.0.0.1:{self.server.server_address[1]}/v1"

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *exc):
        self.server.shutdown()
        self.server.server_close()


def probe_endpoint(profile: dict, api_key: str | None) -> dict:
    """GET {baseUrl}/models: a listing, never an inference request."""
    headers = {"Accept": "application/json"}
    if api_key:
        headers["Authorization"] = f"Bearer {api_key}"
    request = urllib.request.Request(profile["baseUrl"].rstrip("/") + "/models", headers=headers, method="GET")
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            payload = json.loads(response.read() or b"{}")
            ids = [item.get("id") for item in (payload.get("data") or []) if isinstance(item, dict)]
            return {"status": "pass", "http": response.status, "modelListed": profile["model"] in ids, "listedModels": len(ids)}
    except urllib.error.HTTPError as exc:
        return {"status": "fail", "http": exc.code}
    except (OSError, ValueError) as exc:
        return {"status": "fail", "error": type(exc).__name__}


def read_llama_props(profile: dict) -> dict | None:
    """Best effort, loopback only: a whitelist of llama.cpp server properties that explain throughput differences."""
    if profile["remote"]:
        return None
    root = profile["baseUrl"].rstrip("/")
    root = root[:-3] if root.endswith("/v1") else root
    try:
        with urllib.request.urlopen(root + "/props", timeout=10) as response:
            props = json.loads(response.read())
    except (OSError, ValueError):
        return None
    settings = props.get("default_generation_settings") or {}
    keep = {"total_slots": props.get("total_slots"), "build_info": props.get("build_info"), "n_ctx": settings.get("n_ctx"),
            "model_alias": props.get("model_alias")}
    return {k: v for k, v in keep.items() if v is not None}


def environment_facts(root: pathlib.Path) -> dict:
    return {"platform": sys.platform, "osRelease": tool_version("cmd", "/c", "ver") if os.name == "nt" else None,
            "python": sys.version.split()[0], "dotnet": tool_version("dotnet", "--version"), "cpuCount": os.cpu_count(),
            "elevated": is_elevated(), "host": host_snapshot(0.2), "machine": hashlib.sha256(os.environ.get("COMPUTERNAME", "").encode()).hexdigest()[:12]}


def prepare_work(work: pathlib.Path, appsettings_source: pathlib.Path | None) -> dict:
    work.mkdir(parents=True, exist_ok=False)
    (work / "plugins").mkdir()
    (work / "policy.yaml").write_text(READ_ONLY_POLICY, encoding="utf-8", newline="\n")
    sha = None
    if appsettings_source and appsettings_source.is_file():
        shutil.copyfile(appsettings_source, work / "appsettings.json")
        sha = sha_file(work / "appsettings.json")
    return {"policySha256": sha_file(work / "policy.yaml"), "appsettingsSha256": sha}


def mode_probe(ctx: "Context", variant: str, stub_url: str, directory: pathlib.Path) -> dict:
    """Run the real CLI once against a loopback stub (HTTP 400, no model) and read the mode back from standard error."""
    profile = {**ctx.profile, "baseUrl": stub_url, "apiKeyEnv": "STUB", "remote": False}
    work = directory / f"mode-probe-{variant}"
    facts = prepare_work(work, ctx.appsettings_source)
    overlay = build_overlay(profile, variant, work)
    env, _ = child_environment(os.environ, overlay, "stub-key-not-a-real-credential")
    result = execute_cli(ctx.cli_command, "preflight mode probe: no model is attached", work, env, {"stub-key-not-a-real-credential"},
                         work / "stdout.txt", work / "stderr.txt", 120)
    stderr = (work / "stderr.txt").read_text(encoding="utf-8", errors="replace")
    seen = MARKER in stderr
    expected = VARIANTS[variant]["maf"]
    return {"variant": variant, "expectedMaf": expected, "markerSeen": seen, "exitCode": result["exitCode"], "timedOut": result["timedOut"],
            "status": "pass" if seen == expected and not result["timedOut"] and result["exitCode"] != 2 else "fail", **facts}


class Context:
    """Everything a campaign shares: target, commit, CLI command, profile, secret and limits."""

    def __init__(self, args):
        self.args = args
        self.root = pathlib.Path(args.target_repo_root).resolve()
        self.test_double = bool(getattr(args, "test_double", False))
        self.profile = apply_token_cap(load_profile(pathlib.Path(args.model_profile)), getattr(args, "per_run_token_cap", None))
        self.api_key = os.environ.get(self.profile["apiKeyEnv"]) if self.profile["apiKeyEnv"] else None
        self.commit = None
        self.cli_command: list[str] = []
        self.cli_sha = None
        self.appsettings_source = None
        self.checks: list[dict] = []
        self.output = pathlib.Path(args.output_root).resolve() if args.output_root else (
            pathlib.Path.home() / "Documents" / "bOps-MafBenchmarks" / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + os.urandom(4).hex()))

    def check(self, name: str, status: str, detail=None, **extra) -> bool:
        self.checks.append({"name": name, "status": status, "detail": detail, **extra})
        return status != "fail"


def run_preflight(ctx: Context, directory: pathlib.Path, allow_remote: bool, allow_live: bool) -> bool:
    args = ctx.args
    ok = True
    # 1 target
    try:
        if ctx.test_double:
            ctx.commit = args.expected_target_commit or "0" * 40
            ok &= ctx.check("target-commit", "skipped", "test double: no repository validation")
        else:
            anchor, head, status = lb.validate_runtime_target(ctx.root, args.expected_target_commit) if args.expected_target_commit else (None, lb.git(ctx.root, "rev-parse", "HEAD"), "")
            ctx.commit = head
            ok &= ctx.check("target-commit", "pass" if args.expected_target_commit else "warn",
                            "HEAD equals ExpectedTargetCommit and no runtime source is modified" if args.expected_target_commit
                            else "no ExpectedTargetCommit given: the campaign cannot prove it ran the intended commit", head=head)
    except Exception as exc:  # noqa: BLE001
        ok &= ctx.check("target-commit", "fail", str(exc))
        ctx.commit = ctx.commit or "unknown"
    # 2 output location
    inside = under_git_work_tree(ctx.output) or ctx.root in [ctx.output, *ctx.output.parents]
    ok &= ctx.check("output-outside-repository", "fail" if inside else "pass", str(ctx.output))
    # 3 profile and authorizations
    ok &= ctx.check("profile", "pass", {"provider": ctx.profile["provider"], "model": ctx.profile["model"],
                                         "endpoint": lb.safe_endpoint(ctx.profile["baseUrl"]), "remote": ctx.profile["remote"]})
    if ctx.profile["remote"] and not allow_remote:
        ok &= ctx.check("remote-endpoint-authorization", "fail", "The endpoint is not loopback; a remote provider needs --allow-remote-endpoint "
                        "(separate cost and model authorization).")
    if ctx.profile["apiKeyEnv"] and not ctx.api_key:
        ok &= ctx.check("api-key-available", "fail", f"Environment variable {ctx.profile['apiKeyEnv']} is not set (value is never printed).")
    elif ctx.profile["apiKeyEnv"]:
        ok &= ctx.check("api-key-available", "pass", "present (value not recorded)")
    ok &= ctx.check("knowledge-disabled", "pass", "Knowledge__Enabled=false is forced in both variants (C01-R22 refuses the combination).")
    # 4 CLI build
    if ctx.test_double:
        ctx.cli_command = json.loads(args.cli_command_json)
        ok &= ctx.check("cli-binary", "skipped", "test double command")
    else:
        try:
            dll = pathlib.Path(args.cli_dll).resolve() if args.cli_dll else (find_cli_dll(ctx.root) if args.no_build else build_cli(ctx.root, os.environ.copy()))
            if dll is None or not dll.is_file():
                raise BenchmarkError("bops.dll not found; run without --no-build.")
            ctx.cli_command = ["dotnet", str(dll)]
            ctx.cli_sha = sha_file(dll)
            ctx.appsettings_source = dll.parent / "appsettings.json"
            ok &= ctx.check("cli-binary", "pass" if not args.no_build else "warn", {"dll": str(dll), "sha256": ctx.cli_sha},
                            note=None if not args.no_build else "built earlier: the commit of the binary is not proven by this run")
        except Exception as exc:  # noqa: BLE001
            ok &= ctx.check("cli-binary", "fail", str(exc))
    # 5 policy
    directory.mkdir(parents=True, exist_ok=True)
    policy_probe = directory / "policy.yaml"
    policy_probe.write_text(READ_ONLY_POLICY, encoding="utf-8", newline="\n")
    ctx.policy_sha = sha_file(policy_probe)
    ok &= ctx.check("read-only-policy", "pass", {"sha256": ctx.policy_sha, "modes": "read=automatic; low/medium/high/critical=forbidden; no approval path"})
    # 6 endpoint (never inference)
    if args.skip_endpoint_probe:
        ctx.check("endpoint-reachable", "skipped", "skipped by request")
    else:
        result = probe_endpoint(ctx.profile, ctx.api_key)
        ok &= ctx.check("endpoint-reachable", result["status"], result)
        props = read_llama_props(ctx.profile)
        if props:
            ctx.endpoint_props = props
    # 7 mode probes (real CLI, loopback stub, no model)
    if args.skip_mode_probe or not ctx.cli_command:
        ctx.check("mode-probe", "skipped", "skipped by request or no CLI command")
    else:
        with StubModelServer() as stub:
            probes = {v: mode_probe(ctx, v, stub.base_url, directory) for v in VARIANTS}
            requests = stub.requests
        good = all(p["status"] == "pass" for p in probes.values())
        ok &= ctx.check("mode-probe", "pass" if good else "fail",
                        "MAF OFF prints no EXPERIMENTAL line and MAF ON prints it (read back from the process, against a loopback stub; "
                        "no model was contacted)", probes=probes, stubRequests=requests)
        if good:
            fa = {v: (probes[v]["policySha256"], probes[v]["appsettingsSha256"]) for v in probes}
            ok &= ctx.check("variant-inputs-identical", "pass" if fa["A"] == fa["B"] else "fail", "policy and appsettings hashes match across variants")
    # 8 live authorization (informational for preflight)
    ctx.check("live-authorization", "pass" if allow_live else "info", "live runs allowed" if allow_live else "no live run authorized in this invocation")
    # 9 environment
    ctx.environment = environment_facts(ctx.root)
    ctx.check("environment", "pass", ctx.environment)
    return ok


# --------------------------------------------------------------------------------------------------------------------
# Campaign
# --------------------------------------------------------------------------------------------------------------------

def plan_runs(scenarios, variants, rounds, order: str, seed: str):
    entries = []
    for rnd in range(1, rounds + 1):
        for scenario in scenarios:
            sequence = list(variants) if (order == "fixed" or rnd % 2 == 1) else list(reversed(variants))
            for variant in sequence:
                entries.append({"round": rnd, "scenario": scenario, "variant": variant})
    codes = [f"R{i:02d}" for i in range(1, len(entries) + 1)]
    random.Random(seed).shuffle(codes)
    for ordinal, (entry, code) in enumerate(zip(entries, codes), start=1):
        entry.update({"ordinal": ordinal, "blindCode": code, "runDir": f"{ordinal:02d}-{entry['scenario']}-{entry['variant']}-r{entry['round']}",
                      "goalSha256": sha_bytes(GOALS[entry["scenario"]].encode("utf-8"))})
    return entries


def capture_run(run_dir: pathlib.Path, work: pathlib.Path) -> dict:
    capture = run_dir / "capture"
    capture.mkdir(exist_ok=True)
    result = {"taskDatabase": (work / "tasks.db").is_file(), "auditFile": (work / "audit.jsonl").is_file(), "tasks": 0}
    if (work / "audit.jsonl").is_file():
        shutil.copyfile(work / "audit.jsonl", run_dir / "audit-full.jsonl")
    if (work / "tasks.db").is_file():
        try:
            ids = lb.extract_records(work / "tasks.db", [], capture, work / "audit.jsonl")
            result["tasks"] = len(ids)
        except Exception as exc:  # noqa: BLE001 - a half-written database must not lose the rest of the evidence
            result["captureError"] = f"{type(exc).__name__}: {exc}"
    return result


def leak_scan(run_dir: pathlib.Path, secrets: set[str]) -> str:
    if not secrets:
        return "n/a"
    needles = [s.encode("utf-8") for s in secrets]
    for path in run_dir.rglob("*"):
        if path.is_file() and path.stat().st_size < 512 * 1024 * 1024:
            data = path.read_bytes()
            if any(n in data for n in needles):
                return "leak"
    return "clean"


def run_one(ctx: Context, entry: dict, timeout: float, campaign: pathlib.Path) -> dict:
    run_dir = campaign / "runs" / entry["runDir"]
    run_dir.mkdir(parents=True)
    work = run_dir / "work"
    facts = prepare_work(work, ctx.appsettings_source)
    overlay = build_overlay(ctx.profile, entry["variant"], work)
    env, scrubbed = child_environment(os.environ, overlay, ctx.api_key)
    secrets = secret_values(env, ctx.api_key)
    host_before = host_snapshot()
    started = utc_now()
    outcome = execute_cli(ctx.cli_command, GOALS[entry["scenario"]], work, env, secrets, run_dir / "stdout.txt", run_dir / "stderr.txt", timeout)
    host_after = host_snapshot(0.2)
    captured = capture_run(run_dir, work)
    stderr = (run_dir / "stderr.txt").read_text(encoding="utf-8", errors="replace")
    mode_seen = MARKER in stderr
    expected = VARIANTS[entry["variant"]]["maf"]
    scan = leak_scan(run_dir, secrets)
    info = {"schema": SCHEMA, **{k: entry[k] for k in ("ordinal", "round", "scenario", "variant", "runDir")},
            "startedAtUtc": started, "finishedAtUtc": utc_now(), **outcome, "hostBefore": host_before, "hostAfter": host_after,
            "mafExpected": expected, "mafMarkerSeen": mode_seen, "modeVerified": mode_seen == expected and outcome["exitCode"] != 2,
            "secretLeakScan": scan, "capture": captured,
            "configFingerprint": config_fingerprint(overlay, facts["policySha256"], facts["appsettingsSha256"], ctx.cli_sha),
            "environmentOverlay": {k: v for k, v in sorted(overlay.items())}, "scrubbedParentVariables": scrubbed,
            "command": [*ctx.cli_command[:-1], "<cli>"] if len(ctx.cli_command) > 1 else ["<cli>"], "goalSha256": entry["goalSha256"], **facts}
    write_json(run_dir / "run.json", info)
    state, _ = an.load_task_state(run_dir / "capture")
    write_json(campaign / "evaluation" / f"{entry['blindCode']}.json", an.build_evaluation_sheet(entry["blindCode"], entry["scenario"], state))
    return info


def write_integrity(campaign: pathlib.Path) -> dict:
    files = {}
    for path in sorted(p for p in campaign.rglob("*") if p.is_file()):
        rel = path.relative_to(campaign)
        if rel.parts[0] in EVIDENCE_EXCLUDED:
            continue
        files[rel.as_posix()] = {"sha256": sha_file(path), "size": path.stat().st_size}
    document = {"schema": SCHEMA, "generatedAtUtc": utc_now(), "excluded": sorted(EVIDENCE_EXCLUDED), "files": files}
    write_json(campaign / "integrity.json", document)
    return document


def verify_integrity(campaign: pathlib.Path) -> list[str]:
    path = campaign / "integrity.json"
    if not path.is_file():
        return ["integrity.json is missing"]
    recorded = json.loads(path.read_text(encoding="utf-8"))["files"]
    problems, present = [], set()
    for file in sorted(p for p in campaign.rglob("*") if p.is_file()):
        rel = file.relative_to(campaign)
        if rel.parts[0] in EVIDENCE_EXCLUDED:
            continue
        key = rel.as_posix()
        present.add(key)
        if key not in recorded:
            problems.append(f"unexpected file: {key}")
        elif sha_file(file) != recorded[key]["sha256"]:
            problems.append(f"changed file: {key}")
    problems += [f"missing file: {k}" for k in sorted(set(recorded) - present)]
    return problems


def update_state(campaign: pathlib.Path, **fields) -> None:
    path = campaign / "campaign-state.json"
    state = json.loads(path.read_text(encoding="utf-8")) if path.is_file() else {}
    state.update(fields, updatedAtUtc=utc_now())
    write_json(path, state)


def cmd_preflight(args) -> int:
    try:
        ctx = Context(args)
    except BenchmarkError as exc:
        print(f"Preflight failed: {exc}", file=sys.stderr)
        return 2
    directory = ctx.output / "preflight"
    if ctx.output.exists() and any(ctx.output.iterdir()):
        print(f"Output directory is not empty: {ctx.output}", file=sys.stderr)
        return 2
    if under_git_work_tree(ctx.output):
        print("Refusing to write a private preflight inside a Git work tree.", file=sys.stderr)
        return 2
    ctx.output.mkdir(parents=True, exist_ok=True)
    secure_dir(ctx.output)
    ok = run_preflight(ctx, directory, args.allow_remote_endpoint, args.allow_live)
    document = {"schema": SCHEMA, "generatedAtUtc": utc_now(), "testDouble": ctx.test_double, "commit": ctx.commit, "ok": ok,
                "noTaskSentToAModel": True, "noRealModelCall": True,
                "throwawayProbeTasksAgainstLoopbackStub": not args.skip_mode_probe, "checks": ctx.checks}
    write_json(ctx.output / "preflight.json", document)
    for check in ctx.checks:
        print(f"[{check['status'].upper():7}] {check['name']}")
    print(("Preflight PASSED" if ok else "Preflight FAILED") + f" - details: {ctx.output / 'preflight.json'}")
    return 0 if ok else 2


def cmd_run(args) -> int:
    if not args.allow_live and not args.test_double:
        print("A live run needs --allow-live (the operator's explicit go-ahead). Use preflight to prepare.", file=sys.stderr)
        return 3
    scenarios = [s.strip().upper() for s in args.scenarios.split(",") if s.strip()]
    variants = [v.strip().upper() for v in args.variants.split(",") if v.strip()]
    if not scenarios or any(s not in GOALS for s in scenarios) or not variants or any(v not in VARIANTS for v in variants):
        print(f"Scenarios must be among {SCENARIO_IDS} and variants among {list(VARIANTS)}.", file=sys.stderr)
        return 2
    if not args.expected_target_commit and not args.test_double:
        print("run requires --expected-target-commit (the full SHA being measured).", file=sys.stderr)
        return 2
    try:
        ctx = Context(args)
    except BenchmarkError as exc:
        print(f"Run refused: {exc}", file=sys.stderr)
        return 2
    cap = int(ctx.profile["agent"].get("MaxTotalTokens", DEFAULT_PER_RUN_TOKEN_CAP))
    if not args.token_budget or args.token_budget < cap:
        print(f"Run refused: --token-budget is mandatory and must cover at least one run (per-run cap {cap} tokens). "
              "Use --per-run-token-cap to lower the cap.", file=sys.stderr)
        return 2
    if args.task_timeout < 1 or args.campaign_timeout < args.task_timeout:
        print("Run refused: --task-timeout must be positive and not exceed --campaign-timeout.", file=sys.stderr)
        return 2
    campaign = ctx.output
    if campaign.exists() and any(campaign.iterdir()):
        print(f"Output directory is not empty: {campaign}", file=sys.stderr)
        return 2
    if under_git_work_tree(campaign) or ctx.root in campaign.parents:
        print("Refusing to write private evidence inside a Git work tree.", file=sys.stderr)
        return 2
    campaign.mkdir(parents=True, exist_ok=True)
    secure_dir(campaign)
    (campaign / "README-SENSITIVE").write_text(README, encoding="utf-8")
    campaign_id = campaign.name
    ok = run_preflight(ctx, campaign / "preflight", args.allow_remote_endpoint, True)
    write_json(campaign / "preflight.json", {"ok": ok, "checks": ctx.checks})
    if not ok:
        update_state(campaign, state="preflight-failed", stopReason="preflight")
        print("Preflight failed; no task was started. See preflight.json.", file=sys.stderr)
        return 2
    runs = plan_runs(scenarios, variants, args.rounds, args.order, campaign_id)
    fingerprints = {}
    for variant in variants:
        probe_overlay = build_overlay(ctx.profile, variant, pathlib.Path("<work>"))
        fingerprints[variant] = config_fingerprint(probe_overlay, ctx.policy_sha, sha_file(ctx.appsettings_source) if ctx.appsettings_source and ctx.appsettings_source.is_file() else None, ctx.cli_sha)
    manifest = {"schema": SCHEMA, "campaignId": campaign_id, "createdAtUtc": utc_now(), "testDouble": ctx.test_double, "commit": ctx.commit,
                "branch": None if ctx.test_double else lb.git(ctx.root, "branch", "--show-current"),
                "model": {k: ctx.profile[k] for k in ("profileId", "provider", "model", "supportsNativeToolCalling", "requestTimeout")}
                         | {"endpoint": lb.safe_endpoint(ctx.profile["baseUrl"]), "remote": ctx.profile["remote"]},
                "agentOverrides": ctx.profile["agent"], "extraConfiguration": ctx.profile["extraConfiguration"], "pricing": ctx.profile["pricing"],
                "mafKey": "Cli:Experimental:MafGovernedTurn", "knowledge": "disabled", "policySha256": ctx.policy_sha,
                "cliSha256": ctx.cli_sha, "configFingerprintByVariant": fingerprints,
                "limits": {"taskTimeoutSeconds": args.task_timeout, "campaignTimeoutSeconds": args.campaign_timeout,
                           "perRunTokenCap": cap, "campaignTokenBudget": args.token_budget, "costBudgetUsd": args.cost_budget_usd},
                "scenarios": scenarios, "variants": variants, "rounds": args.rounds, "order": args.order,
                "environment": getattr(ctx, "environment", None), "endpointProps": getattr(ctx, "endpoint_props", None),
                "plannedRuns": runs, "harness": "maf_cli_benchmark.py",
                "harnessSha256": {n: sha_file(pathlib.Path(__file__).with_name(n)) for n in ("maf_cli_benchmark.py", "maf_cli_analysis.py", "live_benchmark.py")},
                "profileSha256": sha_file(pathlib.Path(args.model_profile)), "python": sys.version.split()[0]}
    write_json(campaign / "campaign.json", manifest)
    update_state(campaign, state="running", runsDone=0, runsPlanned=len(runs), tokensUsed=0, costUsd=0.0)
    (campaign / "evaluation").mkdir(exist_ok=True)

    deadline = time.monotonic() + args.campaign_timeout
    tokens_used, cost_used, stop = 0, 0.0, None
    try:
        for done, entry in enumerate(runs):
            if args.token_budget and tokens_used + cap > args.token_budget:
                stop = f"token-budget: {tokens_used} used, next run may use up to {cap}, budget {args.token_budget}"
                break
            remaining = deadline - time.monotonic()
            if remaining < 30:
                stop = "campaign-timeout"
                break
            info = run_one(ctx, entry, min(args.task_timeout, remaining), campaign)
            state, _ = an.load_task_state(campaign / "runs" / entry["runDir"] / "capture")
            events, chain = an.load_audit(campaign / "runs" / entry["runDir"] / "audit-full.jsonl")
            metrics = an.compute_metrics(state, events, chain, info, ctx.profile["pricing"])
            tokens_used += int(metrics["accountedTokens"] or metrics["estimatedTokens"] or 0)
            cost_used += float(metrics["estimatedCostUsd"] or 0.0)
            update_state(campaign, runsDone=done + 1, tokensUsed=tokens_used, costUsd=round(cost_used, 6), lastRun=entry["runDir"])
            print(f"[{done + 1}/{len(runs)}] {entry['runDir']}: {metrics['terminalStatus']} exit={info['exitCode']} "
                  f"wall={info['wallSeconds']}s tokens={metrics['accountedTokens']} mode-verified={info['modeVerified']}", flush=True)
            if not info["modeVerified"]:
                stop = f"mode-mismatch in {entry['runDir']}: expected MAF {'ON' if info['mafExpected'] else 'OFF'}, marker seen={info['mafMarkerSeen']}"
            elif info["secretLeakScan"] == "leak":
                stop = f"credential found in the captured evidence of {entry['runDir']}"
            elif info["timedOut"] and not args.continue_on_timeout:
                stop = f"timeout in {entry['runDir']} (evidence preserved)"
            elif state is None and info["exitCode"] not in (0, 1):
                stop = f"CLI crashed without task state in {entry['runDir']} (exit {info['exitCode']})"
            elif (metrics["modelCalls"] or 0) > 0 and metrics["modelCallFailures"] == metrics["modelCalls"] and not args.continue_on_provider_failure:
                stop = f"every model call failed in {entry['runDir']}: provider unavailable"
            elif args.cost_budget_usd is not None and cost_used > args.cost_budget_usd:
                stop = f"cost-budget: {cost_used:.4f} USD used, budget {args.cost_budget_usd}"
            if stop:
                break
        final = "complete" if stop is None else f"stopped: {stop}"
    except KeyboardInterrupt:
        final = "interrupted"
        stop = "operator interrupt"
    update_state(campaign, state=final, stopReason=stop)
    write_integrity(campaign)
    try:
        an.write_report(campaign)
    except Exception as exc:  # noqa: BLE001 - a report problem must not lose the evidence
        print(f"Report generation failed: {exc}", file=sys.stderr)
    print(f"Campaign {final}. Evidence: {campaign}")
    return 0 if stop is None else 1


def cmd_analyze(args) -> int:
    campaign = pathlib.Path(args.campaign_dir).resolve()
    if not (campaign / "campaign.json").is_file():
        print("Not a campaign directory (campaign.json is missing).", file=sys.stderr)
        return 2
    report = an.write_report(campaign)
    print(f"Report: {campaign / 'report'} (rounds per cell {report['roundsPerCell']}, exploratory={report['exploratory']})")
    return 0


def cmd_verify(args) -> int:
    problems = verify_integrity(pathlib.Path(args.campaign_dir).resolve())
    for problem in problems:
        print(problem)
    print("Integrity OK" if not problems else f"Integrity FAILED ({len(problems)} problem(s))")
    return 0 if not problems else 1


def cmd_estimate(args) -> int:
    try:
        profile = apply_token_cap(load_profile(pathlib.Path(args.model_profile)), args.per_run_token_cap)
    except BenchmarkError as exc:
        print(f"Estimate refused: {exc}", file=sys.stderr)
        return 2
    scenarios = [s.strip().upper() for s in args.scenarios.split(",") if s.strip()]
    variants = [v.strip().upper() for v in args.variants.split(",") if v.strip()]
    history = an.history_from_bench00(pathlib.Path(args.history_dir)) if args.history_dir else None
    cap = int(profile["agent"].get("MaxTotalTokens", DEFAULT_PER_RUN_TOKEN_CAP))
    estimate = an.estimate_campaign(scenarios, variants, args.rounds, cap, args.task_timeout, history, profile["pricing"])
    print(json.dumps(estimate, indent=2))
    return 0


def make_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    def common(p, needs_profile=True):
        p.add_argument("--target-repo-root", default=str(pathlib.Path(__file__).resolve().parents[2]))
        p.add_argument("--expected-target-commit", type=lb.full_commit_sha)
        p.add_argument("--model-profile", required=needs_profile)
        p.add_argument("--output-root")
        p.add_argument("--cli-dll")
        p.add_argument("--no-build", action="store_true")
        p.add_argument("--skip-endpoint-probe", action="store_true")
        p.add_argument("--skip-mode-probe", action="store_true")
        p.add_argument("--allow-remote-endpoint", action="store_true")
        p.add_argument("--allow-live", action="store_true")
        p.add_argument("--test-double", action="store_true", help=argparse.SUPPRESS)
        p.add_argument("--cli-command-json", help=argparse.SUPPRESS)

    pre = sub.add_parser("preflight", help="Check everything without submitting a task or calling a model")
    common(pre)
    run = sub.add_parser("run", help="Run the A/B campaign (needs --allow-live)")
    common(run)
    run.add_argument("--scenarios", default="B01")
    run.add_argument("--variants", default="A,B")
    run.add_argument("--rounds", type=int, choices=range(1, 4), default=1)
    run.add_argument("--order", choices=("alternate", "fixed"), default="alternate")
    run.add_argument("--task-timeout", type=int, default=1800)
    run.add_argument("--campaign-timeout", type=int, default=6 * 3600)
    run.add_argument("--per-run-token-cap", type=int, help="override Agent:MaxTotalTokens for every run (both variants)")
    run.add_argument("--token-budget", type=int, help="mandatory: campaign-wide token ceiling, checked before every run")
    run.add_argument("--cost-budget-usd", type=float)
    run.add_argument("--continue-on-timeout", action="store_true")
    run.add_argument("--continue-on-provider-failure", action="store_true")
    ana = sub.add_parser("analyze", help="Regenerate the JSON/CSV/Markdown comparison of a campaign")
    ana.add_argument("--campaign-dir", required=True)
    ver = sub.add_parser("verify", help="Re-hash the evidence of a campaign")
    ver.add_argument("--campaign-dir", required=True)
    est = sub.add_parser("estimate", help="Token, duration and cost estimate before a live run")
    est.add_argument("--model-profile", required=True)
    est.add_argument("--scenarios", default="B01")
    est.add_argument("--variants", default="A,B")
    est.add_argument("--rounds", type=int, choices=range(1, 4), default=1)
    est.add_argument("--task-timeout", type=int, default=1800)
    est.add_argument("--per-run-token-cap", type=int)
    est.add_argument("--history-dir")
    return parser


def main(argv=None) -> int:
    args = make_parser().parse_args(argv)
    if getattr(args, "test_double", False) and not getattr(args, "cli_command_json", None):
        print("--test-double needs --cli-command-json.", file=sys.stderr)
        return 2
    handler = {"preflight": cmd_preflight, "run": cmd_run, "analyze": cmd_analyze, "verify": cmd_verify, "estimate": cmd_estimate}[args.command]
    try:
        return handler(args)
    except BenchmarkError as exc:
        print(f"Benchmark failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
