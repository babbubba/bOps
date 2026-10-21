#!/usr/bin/env python3
"""Idempotent, conflict-detecting synchronizer: register.json <-> GitHub Issues <-> Project #6 (BOPS-0119).

Dry-run by default. The register is authoritative, but a change made on GitHub by a person is never overwritten silently:
it is reported as a conflict. Design and rules: agentic/_tasks/2026-10-21-bops-0119-project-governance-02-sync.md.

    python scripts/governance/sync_remote.py                      # read-only plan
    python scripts/governance/sync_remote.py --apply              # write Status/field updates (re-read and verified)
    python scripts/governance/sync_remote.py --apply --close-done # also close Issues of DONE tasks
    python scripts/governance/sync_remote.py --report r.json --apply-register   # patch the local register baseline

Exit codes: 0 in sync, 1 differences or pending register updates, 2 conflicts, 3 environment (account, rate limit, validator, transport).
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import validate_register as vr  # noqa: E402

ACCOUNT = "babbubba"
PREFIX = re.compile(r"^(BOPS-\d{4})\b")
STATUS_LINE = re.compile(r"\*\*Status in the register:\*\* (\w[\w ]*?) \(`(\w+)`\)")
RATE_RE = re.compile(r"secondary rate limit|already exceeded|abuse detection|rate limit", re.I)
# remote snapshot key -> Project field name; every field except Status is fill-only
FIELDS = {"priority": "Priority", "area": "Area", "milestone": "bOps Milestone", "evidence": "Evidence Level", "effort": "Effort"}
ITEM_KEYS = {"status": "status", "priority": "priority", "area": "area", "bOps Milestone": "milestone", "evidence Level": "evidence", "effort": "effort"}


class SyncAbort(Exception):
    """Environment problem: exit code 3, nothing is claimed as synchronized."""


class RateLimited(SyncAbort):
    pass


# ---------------------------------------------------------------------------------------------------------------- planning

def evidence_level(task: dict) -> str:
    ach = task["validation"]["achieved"]
    if "LIVE_REAL_TOOLS" in ach:
        return "Live (real tools)"
    if "REPLAY_REAL_MODEL" in ach:
        return "Replay (real model)"
    return "Offline tests" if "OFFLINE_TESTS" in ach else "None"


def desired(task: dict, reg: dict) -> dict:
    d = {"status": reg["board_columns"].get(task["status"]), "priority": task.get("priority"), "area": task.get("area"),
         "milestone": task.get("milestone"), "evidence": evidence_level(task), "effort": task.get("effort")}
    return {k: v for k, v in d.items() if v}


def plan(reg: dict, remote: dict, *, close_done: bool = False, now: str = "") -> dict:
    """Pure: compare the register with a remote snapshot. Never raises on divergence; classifies it."""
    out = {"updates": [], "conflicts": [], "reports": [], "pending_register": []}
    items = {i["id"]: i for i in remote["items"]}
    issues = {i["number"]: i for i in remote["issues"]}
    by_prefix: dict[str, list] = {}
    for i in remote["items"]:
        m = PREFIX.match(i.get("title", ""))
        if m:
            by_prefix.setdefault(m.group(1), []).append(i["id"])
    tasks = {t["id"]: t for t in reg["tasks"]}
    for tid, ids in sorted(by_prefix.items()):
        if len(ids) > 1:
            out["reports"].append({"kind": "duplicate", "task": tid, "detail": f"{len(ids)} project items share the prefix {tid}"})
        elif tid not in tasks:
            out["reports"].append({"kind": "unknown-remote", "task": tid, "detail": "prefix not in the register"})
        elif tasks[tid].get("project_item") not in ids:
            out["reports"].append({"kind": "unlinked", "task": tid, "detail": f"remote item {ids[0]} exists but is not the register's project_item"})
    seen_issue: dict[str, int] = {}
    for n, i in issues.items():
        m = PREFIX.match(i.get("title", ""))
        if m:
            seen_issue[m.group(1)] = seen_issue.get(m.group(1), 0) + 1
    for tid, c in seen_issue.items():
        if c > 1:
            out["reports"].append({"kind": "duplicate", "task": tid, "detail": f"{c} Issues share the prefix {tid}"})

    for tid, t in sorted(tasks.items()):
        if not t.get("project_item"):
            continue
        item = items.get(t["project_item"])
        if item is None:
            out["reports"].append({"kind": "vanished", "task": tid, "detail": f"project item {t['project_item']} not found"})
            continue
        want = desired(t, reg)
        if t["status"] == "SUPERSEDED":
            out["reports"].append({"kind": "superseded-on-board", "task": tid, "detail": "SUPERSEDED tasks have no board column; remove manually"})
            continue
        # kind
        is_issue = item.get("kind") == "Issue"
        if is_issue != (t.get("remote_kind") == "issue"):
            out["conflicts"].append({"task": tid, "what": "kind", "detail": f"register says {t.get('remote_kind')}, remote item is {item.get('kind')}"})
            continue
        # title
        wt = vr.task_title(t)
        if item.get("title") != wt:
            out["conflicts"].append({"task": tid, "what": "title", "detail": f"remote {item.get('title')!r} != register {wt!r}"})
        # status: three-way
        base = (t.get("sync") or {}).get("status")
        cur = item.get("status")
        if cur == want.get("status"):
            if base != cur:
                out["pending_register"].append({"task": tid, "sync": {"status": cur, "verified_at": now}})
        elif cur == base and base is not None:
            out["updates"].append({"task": tid, "item": item["id"], "field": "Status", "old": cur, "new": want["status"]})
        else:
            why = "no baseline" if base is None else f"remote changed since the last sync (baseline {base!r})"
            out["conflicts"].append({"task": tid, "what": "Status", "detail": f"remote {cur!r}, register wants {want['status']!r}: {why}"})
        # other fields: fill-only
        for key, name in FIELDS.items():
            have, w = item.get(key), want.get(key)
            if w is None or have == w:
                continue
            if have in (None, ""):
                out["updates"].append({"task": tid, "item": item["id"], "field": name, "old": have, "new": w})
            else:
                out["conflicts"].append({"task": tid, "what": name, "detail": f"remote {have!r} differs from register {w!r}; not overwritten"})
        # issue state and body
        if is_issue:
            iss = issues.get(t.get("issue"))
            if iss is None:
                out["reports"].append({"kind": "vanished", "task": tid, "detail": f"Issue #{t.get('issue')} not found"})
                continue
            if t["status"] == "DONE" and iss["state"] == "open":
                if close_done:
                    out["updates"].append({"task": tid, "issue": iss["number"], "field": "IssueState", "old": "open", "new": "closed"})
                else:
                    out["reports"].append({"kind": "pending-close", "task": tid, "detail": f"Issue #{iss['number']} open while the task is DONE (use --close-done)"})
            elif t["status"] != "DONE" and iss["state"] != "open":
                out["conflicts"].append({"task": tid, "what": "IssueState", "detail": f"Issue #{iss['number']} is {iss['state']} but the task is {t['status']}; not reopened"})
            m = STATUS_LINE.search(iss.get("body") or "")
            if m and m.group(2) != t["status"]:
                out["reports"].append({"kind": "body-stale", "task": tid, "detail": f"Issue #{iss['number']} body says {m.group(2)}, register {t['status']}"})
    return out


def is_clean(p: dict) -> bool:
    return not (p["updates"] or p["conflicts"] or p["pending_register"] or [r for r in p["reports"] if r["kind"] != "body-stale"])


# ------------------------------------------------------------------------------------------------------------------ transport

def default_runner(args: list[str]) -> tuple[int, str, str]:
    env = {k: v for k, v in os.environ.items() if k not in ("GH_TOKEN", "GITHUB_TOKEN")}
    r = subprocess.run(["gh", *args], capture_output=True, text=True, encoding="utf-8", errors="replace", env=env)
    return r.returncode, r.stdout, r.stderr


class GhClient:
    def __init__(self, repo="babbubba/bOps", owner=ACCOUNT, project=6, runner=default_runner, sleep=time.sleep,
                 retries=3, backoff=60.0, delay=2.0, min_graphql=200, wait=False, clock=time.time):
        self.repo, self.owner, self.project, self.run_ = repo, owner, project, runner
        self.sleep, self.retries, self.backoff, self.delay, self.min_graphql, self.wait, self.clock = sleep, retries, backoff, delay, min_graphql, wait, clock
        self._fields = None

    def call(self, args: list[str]) -> str:
        for attempt in range(self.retries + 1):
            rc, out, err = self.run_(args)
            if rc == 0:
                return out
            if RATE_RE.search(err + out):
                if attempt == self.retries:
                    raise RateLimited(f"GitHub rate limit persists after {self.retries} retries: {err.strip()[:160]}")
                self.sleep(self.backoff * (2 ** attempt))
                continue
            if "unknown owner type" in err:
                # gh reports a rate-limited owner lookup this way; confirm with a cheap probe instead of guessing
                self.probe_graphql()
            raise SyncAbort(f"gh {' '.join(args[:3])} failed: {err.strip()[:200]}")
        raise SyncAbort("unreachable")

    def probe_graphql(self) -> None:
        rc, out, err = self.run_(["api", "graphql", "-f", "query={viewer{login}}"])
        if rc != 0 and RATE_RE.search(err + out):
            raise RateLimited("GraphQL refuses requests while the primary budget is healthy: a secondary rate limit is active; retry later (no token switching)")

    def guard_account(self) -> None:
        login = self.call(["api", "user", "--jq", ".login"]).strip()
        if login != ACCOUNT:
            raise SyncAbort(f"authenticated as {login!r}; this tool runs only as {ACCOUNT!r}")

    def preflight(self) -> None:
        rl = json.loads(self.call(["api", "rate_limit"]))["resources"]["graphql"]
        self.probe_graphql()
        if rl["remaining"] < self.min_graphql:
            if not self.wait:
                raise RateLimited(f"GraphQL budget low ({rl['remaining']} left, resets at {rl['reset']}); use --wait or retry later")
            self.sleep(max(0, rl["reset"] - self.clock()) + 5)

    def fetch(self) -> dict:
        raw = json.loads(self.call(["project", "item-list", str(self.project), "--owner", self.owner, "--limit", "200", "--format", "json"]))
        items = []
        for i in raw["items"]:
            c = i.get("content") or {}
            row = {"id": i["id"], "title": i.get("title", ""), "kind": "Issue" if c.get("type") == "Issue" else "DraftIssue", "issue": c.get("number")}
            for k, v in ITEM_KEYS.items():
                row[v] = i.get(k)
            items.append(row)
        pages = self.call(["api", f"repos/{self.repo}/issues?state=all&per_page=100", "--paginate"])
        issues = [{"number": x["number"], "title": x["title"], "state": x["state"], "body": x.get("body")}
                  for x in json.loads("[" + pages.replace("][", ",").strip()[1:-1] + "]") if "pull_request" not in x]
        return {"items": items, "issues": issues}

    def fields(self) -> dict:
        if self._fields is None:
            raw = json.loads(self.call(["project", "field-list", str(self.project), "--owner", self.owner, "--format", "json"]))["fields"]
            self._fields = {f["name"]: f for f in raw}
            self.project_id = json.loads(self.call(["project", "view", str(self.project), "--owner", self.owner, "--format", "json"]))["id"]
        return self._fields

    def edit_field(self, item: str, name: str, value: str) -> None:
        f = self.fields()[name]
        a = ["project", "item-edit", "--id", item, "--project-id", self.project_id, "--field-id", f["id"]]
        if f["type"] == "ProjectV2SingleSelectField":
            opt = next((o["id"] for o in f["options"] if o["name"] == value), None)
            if opt is None:
                raise SyncAbort(f"no option {value!r} in field {name}")
            a += ["--single-select-option-id", opt]
        else:
            a += ["--text", value]
        self.call(a)

    def close_issue(self, number: int) -> None:
        self.call(["api", "-X", "PATCH", f"repos/{self.repo}/issues/{number}", "-f", "state=closed"])


def apply_updates(client, p: dict) -> None:
    for u in p["updates"]:
        if u["field"] == "IssueState":
            client.close_issue(u["issue"])
        else:
            client.edit_field(u["item"], u["field"], u["new"])
        client.sleep(client.delay)


# ----------------------------------------------------------------------------------------------------------------------- CLI

def patch_register(path: Path, pending: list[dict]) -> int:
    reg = json.loads(path.read_text(encoding="utf-8"))
    by = {t["id"]: t for t in reg["tasks"]}
    for p in pending:
        by[p["task"]]["sync"] = p["sync"]
    path.write_text(json.dumps(reg, indent=1, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    return len(pending)


def render(p: dict) -> str:
    lines = []
    for u in p["updates"]:
        lines.append(f'UPDATE   {u["task"]} {u["field"]}: {u["old"]!r} -> {u["new"]!r}')
    for c in p["conflicts"]:
        lines.append(f'CONFLICT {c["task"]} {c["what"]}: {c["detail"]}')
    for r in p["reports"]:
        lines.append(f'REPORT   {r["task"]} {r["kind"]}: {r["detail"]}')
    for q in p["pending_register"]:
        lines.append(f'PENDING  {q["task"]} register sync.status -> {q["sync"]["status"]!r} (not yet recorded on main)')
    return "\n".join(lines) or "in sync"


def main(argv=None, client=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--close-done", action="store_true")
    ap.add_argument("--apply-register", action="store_true")
    ap.add_argument("--report", type=Path)
    ap.add_argument("--min-graphql", type=int, default=200)
    ap.add_argument("--retries", type=int, default=3)
    ap.add_argument("--backoff", type=float, default=60.0)
    ap.add_argument("--delay", type=float, default=2.0)
    ap.add_argument("--wait", action="store_true")
    a = ap.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    regpath = a.root / "agentic" / "_tasks" / "register.json"
    reg = json.loads(regpath.read_text(encoding="utf-8"))
    hp = a.root / "HANDOFF.md"
    errs = vr.validate(reg, a.root, hp.read_text(encoding="utf-8") if hp.is_file() else None)
    if errs:
        print("register invalid, refusing to synchronize:\n  " + "\n  ".join(errs))
        return 3
    client = client or GhClient(retries=a.retries, backoff=a.backoff, delay=a.delay, min_graphql=a.min_graphql, wait=a.wait)
    now = time.strftime("%Y-%m-%dT%H:%M:%S%z")
    try:
        client.guard_account()
        client.preflight()
        p = plan(reg, client.fetch(), close_done=a.close_done, now=now)
        print(render(p))
        failed = False
        if a.apply and p["updates"]:
            if p["conflicts"]:
                print("conflicts present: applying only the non-conflicting updates")
            apply_updates(client, p)
            after = plan(reg, client.fetch(), close_done=a.close_done, now=now)
            left = [u for u in after["updates"]]
            if left:
                failed = True
                print("NOT synchronized after re-read:\n" + "\n".join(f'  {u["task"]} {u["field"]}' for u in left))
            else:
                print("re-read after apply: all updates verified")
            p = after
        if a.report:
            a.report.write_text(json.dumps(p, indent=1), encoding="utf-8")
        if a.apply_register and p["pending_register"]:
            print(f"patched {patch_register(regpath, p['pending_register'])} register entries locally (commit them with the next substantive PR)")
    except SyncAbort as e:
        print("ABORT:", e)
        return 3
    if p["conflicts"]:
        return 2
    return 1 if (failed or not is_clean(p)) else 0


if __name__ == "__main__":
    sys.exit(main())
