#!/usr/bin/env python3
"""Offline consistency validator for agentic/_tasks/register.json (PROJECT-GOVERNANCE-01).

Checks the task register, the task packets it points to and the machine-readable block in HANDOFF.md.
It needs no network, no GitHub token and no secret; it runs on Windows and Linux CI.
Remote reconciliation (Issues, Project #6) is a separate concern (PROJECT-GOVERNANCE-02).

    python scripts/governance/validate_register.py            # validate, exit 1 on any error
    python scripts/governance/validate_register.py --summary  # also print the operational summary
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

STATUSES = ["PLANNED", "READY", "IN_PROGRESS", "VALIDATION", "BLOCKED", "DONE", "DEFERRED", "SUPERSEDED"]
LEVELS = ["OFFLINE_TESTS", "REPLAY_REAL_MODEL", "LIVE_REAL_TOOLS", "CI_VERIFIED", "OPERATOR_GATE"]
PRIORITIES = ["P0", "P1", "P2", "P3"]
CREATED_BASES = ["packet-first-add", "first-mention", "packet-subitem", "document-date", "unknown"]
ID_RE = re.compile(r"^BOPS-(\d{4})$")
NO_ACTIVE = "NO ACTIVE IMPLEMENTATION TASK"
SYNC_BEGIN, SYNC_END = "<!-- register-sync:begin -->", "<!-- register-sync:end -->"
TERMINAL = {"DONE", "SUPERSEDED", "DEFERRED"}


def frozen_digest(tasks: list[dict], through: str) -> str:
    rows = [f'{t["id"]}|{t["legacy_id"]}|{t["created"]["at"]}' for t in tasks if t["id"] <= through]
    return hashlib.sha256("\n".join(rows).encode()).hexdigest()


def task_title(task: dict) -> str:
    """Issue title for a task: BOPS-NNNN | LEGACY-ID — Description (description = title minus a leading legacy id)."""
    desc = task["title"]
    if desc.upper().startswith(task["legacy_id"].upper()):
        desc = desc[len(task["legacy_id"]):].lstrip(" :-—")
    return f'{task["id"]} | {task["legacy_id"]} — {desc}'.rstrip(" —")


def _find_cycle(graph: dict[str, list[str]]) -> list[str] | None:
    state: dict[str, int] = {}
    stack: list[str] = []

    def visit(n: str) -> list[str] | None:
        state[n] = 1
        stack.append(n)
        for m in graph.get(n, []):
            if state.get(m) == 1:
                return stack[stack.index(m):] + [m]
            if state.get(m) is None:
                found = visit(m)
                if found:
                    return found
        stack.pop()
        state[n] = 2
        return None

    for node in graph:
        if state.get(node) is None:
            found = visit(node)
            if found:
                return found
    return None


def parse_handoff(text: str) -> dict | None:
    if SYNC_BEGIN not in text or SYNC_END not in text:
        return None
    block = text.split(SYNC_BEGIN, 1)[1].split(SYNC_END, 1)[0]
    fields = {}
    for line in block.splitlines():
        m = re.match(r"^\s*([A-Za-z ]+):\s*(.*?)\s*$", line)
        if m:
            fields[m.group(1).strip().lower()] = m.group(2)
    return fields


def validate(reg: dict, root: Path, handoff_text: str | None) -> list[str]:
    errs: list[str] = []
    e = errs.append

    if reg.get("schema_version") != 1:
        e("schema_version must be 1")
    vocab = reg.get("vocabulary", {})
    if vocab.get("status") != STATUSES:
        e("vocabulary.status differs from the closed status vocabulary")
    if vocab.get("validation_levels") != LEVELS:
        e("vocabulary.validation_levels differs from the closed validation vocabulary")
    if vocab.get("priority") != PRIORITIES:
        e("vocabulary.priority differs from P0..P3")
    cols = reg.get("board_columns", {})
    if sorted(cols) != sorted(STATUSES):
        e("board_columns must map every status exactly once")

    tasks = reg.get("tasks", [])
    ids: list[str] = []
    by_id: dict[str, dict] = {}
    legacy: set[str] = set()
    issues: set = set()
    items: set = set()
    for t in tasks:
        tid = t.get("id", "")
        if not ID_RE.match(tid):
            e(f"{tid or '<missing id>'}: id must match BOPS-NNNN")
            continue
        if tid in by_id:
            e(f"{tid}: duplicate progressive id")
            continue
        ids.append(tid)
        by_id[tid] = t
        lid = t.get("legacy_id")
        if not lid:
            e(f"{tid}: legacy_id missing")
        elif lid in legacy:
            e(f"{tid}: legacy_id {lid} already used by another task")
        else:
            legacy.add(lid)
        if not t.get("title"):
            e(f"{tid}: title missing")
        c = t.get("created", {})
        if c.get("basis") not in CREATED_BASES:
            e(f"{tid}: created.basis {c.get('basis')!r} not allowed")
        if not c.get("at") and c.get("basis") != "unknown":
            e(f"{tid}: created.at missing (use basis 'unknown' only with at=null)")
        for field, label in (("issue", "issue"), ("project_item", "project_item")):
            v = t.get(field)
            if v is not None:
                seen = issues if field == "issue" else items
                if v in seen:
                    e(f"{tid}: {label} {v} assigned to more than one task")
                seen.add(v)

    nums = sorted(int(ID_RE.match(i).group(1)) for i in ids)
    if nums and nums != list(range(1, len(nums) + 1)):
        e("progressive ids must be contiguous from BOPS-0001 (ids are never deleted or reused)")
    numbering = reg.get("numbering", {})
    if nums and numbering.get("next") != nums[-1] + 1:
        e(f"numbering.next must be {nums[-1] + 1}")
    through = numbering.get("frozen_through")
    if through:
        if through not in by_id:
            e("numbering.frozen_through is not a task id")
        elif frozen_digest(tasks, through) != numbering.get("frozen_mapping_sha256"):
            e("the frozen id mapping (id, legacy_id, creation instant) changed: it is immutable once approved")

    milestone_ids = {m["id"] for m in reg.get("milestones", [])}
    if reg.get("active_milestone") not in milestone_ids:
        e("active_milestone is not a declared milestone")

    for tid, t in by_id.items():
        st = t.get("status")
        if st not in STATUSES:
            e(f"{tid}: status {st!r} not in the closed vocabulary")
            continue
        pr = t.get("priority")
        if pr is not None and pr not in PRIORITIES:
            e(f"{tid}: priority {pr!r} invalid")
        if t.get("milestone") not in milestone_ids:
            e(f"{tid}: milestone {t.get('milestone')!r} is not declared")
        for ref in [t.get("packet"), *t.get("companion_docs", [])]:
            if ref and not (root / ref).is_file():
                e(f"{tid}: referenced file {ref} does not exist")
        spec = t.get("spec")
        if spec and not (root / spec.split("#", 1)[0]).is_file():
            e(f"{tid}: spec file {spec} does not exist")
        for d in t.get("depends_on", []):
            if d not in by_id:
                e(f"{tid}: depends on unknown task {d}")
            elif d == tid:
                e(f"{tid}: depends on itself")
        v = t.get("validation", {})
        ach, req, opened = v.get("achieved", []), v.get("required", []), v.get("open_gates", [])
        for lvl in [*ach, *req]:
            if lvl not in LEVELS:
                e(f"{tid}: validation level {lvl!r} not allowed")
        missing = [x for x in req if x not in ach]
        deps = [by_id[d] for d in t.get("depends_on", []) if d in by_id]
        if st == "DONE":
            if not ach:
                e(f"{tid}: DONE without any validation evidence level")
            if missing:
                e(f"{tid}: DONE but required gates are missing: {', '.join(missing)}")
            if opened:
                e(f"{tid}: DONE but open_gates is not empty")
            if not (t.get("evidence") or t.get("prs") or t.get("commits") or t.get("packet")):
                e(f"{tid}: DONE without evidence (evidence, prs, commits or packet)")
            bad = [d["id"] for d in deps if d["status"] not in ("DONE", "SUPERSEDED")]
            if bad:
                e(f"{tid}: DONE while dependencies are not complete: {', '.join(bad)}")
        if st == "VALIDATION" and not (opened or missing):
            e(f"{tid}: VALIDATION needs a readable open gate (open_gates) or a missing required gate")
        if st == "BLOCKED" and not t.get("blocked_reason"):
            e(f"{tid}: BLOCKED without blocked_reason")
        if st == "DEFERRED" and not t.get("reopen_condition"):
            e(f"{tid}: DEFERRED without reopen_condition")
        if st == "SUPERSEDED" and not t.get("note"):
            e(f"{tid}: SUPERSEDED without a note saying what replaced it")
        if st in ("READY", "IN_PROGRESS"):
            bad = [d["id"] for d in deps if d["status"] != "DONE"]
            if bad:
                e(f"{tid}: {st} while dependencies are not DONE: {', '.join(bad)}")
            if not (t.get("packet") or t.get("spec")):
                e(f"{tid}: {st} without a packet or spec reference")
        if st == "IN_PROGRESS" and t.get("authorized") is not True:
            e(f"{tid}: IN_PROGRESS without operator authorization (authorized=true)")
        if st in ("READY", "IN_PROGRESS", "VALIDATION") and t.get("milestone") not in milestone_ids:
            pass  # reported above
        mid = t.get("milestone")
        if mid and any(m["id"] == mid and m["status"] == "DONE" for m in reg.get("milestones", [])) and st not in TERMINAL:
            e(f"{tid}: milestone {mid} is DONE but the task is {st}")

    active = [t for t in tasks if t.get("status") == "IN_PROGRESS"]
    if len(active) > 1:
        e("serial workflow: more than one IN_PROGRESS task (" + ", ".join(t["id"] for t in active) + ")")

    cycle = _find_cycle({i: [d for d in t.get("depends_on", []) if d in by_id] for i, t in by_id.items()})
    if cycle:
        e("dependency cycle: " + " -> ".join(cycle))

    for d in reg.get("decisions", []):
        for tid in d.get("tasks", []):
            if tid not in by_id:
                e(f"decision {d.get('id')}: unknown task {tid}")

    if handoff_text is None:
        e("HANDOFF.md not found")
    else:
        fields = parse_handoff(handoff_text)
        if fields is None:
            e(f"HANDOFF.md lacks the {SYNC_BEGIN} ... {SYNC_END} block")
        else:
            want = active[0]["id"] if active else NO_ACTIVE
            got = fields.get("active implementation task", "")
            if not got.startswith(want):
                e(f"HANDOFF active implementation task is {got!r}, register says {want!r}")
            ready = sorted(t["id"] for t in tasks if t.get("status") == "READY")
            listed = sorted(re.findall(r"BOPS-\d{4}", fields.get("ready", "")))
            if ready != listed:
                e(f"HANDOFF ready list {listed} differs from register READY set {ready}")
            if fields.get("next number") != f'BOPS-{numbering.get("next", 0):04d}':
                e("HANDOFF next number differs from numbering.next")
    return errs


def summary(reg: dict) -> str:
    tasks = reg["tasks"]
    count = {s: sum(1 for t in tasks if t["status"] == s) for s in STATUSES}
    active = [t for t in tasks if t["status"] == "IN_PROGRESS"]
    out = ["status counts: " + ", ".join(f"{s}={n}" for s, n in count.items() if n),
           "active: " + (", ".join(f'{t["id"]} {t["legacy_id"]}' for t in active) if active else NO_ACTIVE),
           f'next progressive id: BOPS-{reg["numbering"]["next"]:04d}']
    for s in ("READY", "BLOCKED"):
        for t in tasks:
            if t["status"] == s:
                extra = f' — {t["blocked_reason"]}' if s == "BLOCKED" else (" (not authorized)" if not t.get("authorized") else "")
                out.append(f'{s}: {t["id"]} {t["legacy_id"]}{extra}')
    rs = reg.get("remote_sync", {})
    out.append(f'remote sync: {rs.get("state", "UNKNOWN")} — {rs.get("reason", "")}')
    out.append("pending decisions: " + ", ".join(d["id"] for d in reg.get("decisions", []) if d.get("status") == "PENDING"))
    return "\n".join(out)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    ap.add_argument("--summary", action="store_true")
    args = ap.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    root: Path = args.root
    reg = json.loads((root / "agentic" / "_tasks" / "register.json").read_text(encoding="utf-8"))
    hp = root / "HANDOFF.md"
    errs = validate(reg, root, hp.read_text(encoding="utf-8") if hp.is_file() else None)
    for m in errs:
        print("ERROR:", m)
    if args.summary:
        print(summary(reg))
    print(f"register: {len(reg['tasks'])} tasks, {len(errs)} error(s)")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
