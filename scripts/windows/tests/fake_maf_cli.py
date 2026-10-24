#!/usr/bin/env python3
"""Scriptable stand-in for `bops "<goal>"`, used only by the offline tests of maf_cli_benchmark.py (a test double, not a measurement).

It reads the same environment keys the real CLI reads, prints the EXPERIMENTAL line on standard error when
Cli__Experimental__MafGovernedTurn is "true" (unless the script says otherwise), and persists a task state plus a hash-chained audit file
in the paths the harness configured. It never opens a network connection.
"""
import hashlib
import json
import os
import pathlib
import sqlite3
import sys
import time

MARKER = "EXPERIMENTAL: each model turn is hosted by a Microsoft Agent Framework agent (Cli:Experimental:MafGovernedTurn=true); bOps keeps all execution authority."


def main(argv):
    script = {}
    if "--script" in argv:
        index = argv.index("--script")
        script = json.loads(pathlib.Path(argv[index + 1]).read_text(encoding="utf-8"))
        argv = argv[:index] + argv[index + 2:]
    goal = argv[-1] if argv else ""
    maf_value = os.environ.get("Cli__Experimental__MafGovernedTurn")
    if maf_value is not None and maf_value.lower() not in ("true", "false"):
        sys.stderr.write("'Cli:Experimental:MafGovernedTurn' must be 'true' or 'false'\n")
        return 2
    variant = "B" if (maf_value or "false").lower() == "true" else "A"
    if variant == "B" and not (script.get("omitMarker") or {}).get("B"):
        sys.stderr.write(MARKER + "\n")
    for name in script.get("echoEnv", []):
        sys.stdout.write(f"ENV {name}={os.environ.get(name)}\n")
    sys.stdout.write(f"CWD={os.getcwd()}\n")
    memory = os.environ.get("Memory__FilePath")
    if goal.startswith("preflight mode probe") or script.get("noState") or not memory:
        return script.get("exitCode", 1)
    pre_existing = 0
    if os.path.exists(memory):
        pre_existing = sqlite3.connect(memory).execute("SELECT count(*) FROM tasks").fetchone()[0]
    sys.stdout.write(f"PRE_EXISTING_TASKS={pre_existing}\n")
    spec = (script.get("states") or {}).get(variant) or script.get("state") or {}
    audit_events = spec.get("_audit", [])
    state = {k: v for k, v in spec.items() if k != "_audit"}
    task_id = state.setdefault("Id", "00000000-0000-0000-0000-%012d" % (int(hashlib.sha256((goal + variant).encode()).hexdigest(), 16) % 10**12))
    leak_env = script.get("leakEnv")
    if leak_env:
        state.setdefault("Steps", [{}])[0].setdefault("Result", {})["Output"] = "leaked " + os.environ.get(leak_env, "")
        sys.stdout.write("echo " + os.environ.get(leak_env, "") + "\n")
    db = sqlite3.connect(memory)
    db.execute("CREATE TABLE IF NOT EXISTS tasks (id TEXT PRIMARY KEY, state_json TEXT, status INTEGER)")
    db.execute("CREATE TABLE IF NOT EXISTS task_mutation_journal (task_id TEXT, execution_attempt INTEGER, sequence INTEGER)")
    db.execute("INSERT OR REPLACE INTO tasks VALUES (?,?,?)", (task_id, json.dumps(state), state.get("Status", 0)))
    db.commit()
    db.close()
    prev = "0" * 64
    with open(os.environ["Audit__FilePath"], "wb") as audit:
        for seq, event in enumerate(audit_events, start=1):
            body = json.dumps(dict(event, TaskId=task_id))
            digest = hashlib.sha256((prev + body).encode()).hexdigest()
            audit.write((json.dumps({"Seq": seq, "PrevHash": prev, "Hash": digest, "EventJson": body}) + "\n").encode())
            prev = digest
    if script.get("sleep"):
        sys.stdout.flush()
        time.sleep(script["sleep"])
    sys.stdout.write(f"Task {task_id} - done\n")
    return script.get("exitCode", 0 if state.get("Status") == 1 else 1)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
