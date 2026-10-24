#!/usr/bin/env python3
"""Metrics, A/B comparison, evaluation sheets and token/time estimates for the MAF CLI benchmark (BOPS-0125).

Pure functions over the evidence a campaign captured (see maf_cli_benchmark.py). Nothing here talks to a model, a
network or the repository. Every metric carries a class so a reader never mistakes an estimate for a measurement:

* C  certain      - read verbatim from persisted state, audit or the process (counts, token usage reported by the provider);
* E  estimate     - derived by a stated approximation (characters/4 token estimate, cost from a configured price);
* H  heuristic    - a text or structure heuristic (limitation wording, numbers not found in tool output);
* Q  qualitative  - a human judgement recorded in an evaluation sheet; PENDING until someone fills it.
"""
from __future__ import annotations

import csv
import io
import json
import pathlib
import re
import statistics
import zipfile
from collections import Counter, defaultdict

TASK_STATUS = {0: "Running", 1: "Completed", 2: "MaxStepsReached", 3: "BudgetExceeded", 4: "PolicyBlocked",
               5: "ReplanLimitReached", 6: "Failed", 7: "Cancelled"}
AUTH_AUTOMATIC, AUTH_USER_APPROVED, AUTH_USER_REJECTED, AUTH_POLICY_DENIED, AUTH_UNKNOWN_TOOL = range(5)
RISK_READ = 0
EVIDENCE_SERVED = {0, 1}  # Success, EndOfEvidence
EVIDENCE_TOOL = "runtime.evidence_read"
COMPLETENESS_PARTIAL, COMPLETENESS_UNAVAILABLE = 2, 3
CHARS_PER_TOKEN = 4  # estimate only

LIMITATION_MARKERS = re.compile(
    r"troncat|parzial|non dispon|non è stato possibile|limitazion|incompleto|non verificabil|non ho potuto|"
    r"truncat|partial|unavailable|not available|limitation|could not|unable to|incomplete", re.IGNORECASE)
HYPOTHESIS_MARKERS = re.compile(
    r"probabil|potrebbe|potrebbero|ipotesi|sembra|suggerisc|plausibil|verosimil|likely|might|may be|could be|suggests|hypothes|appears to",
    re.IGNORECASE)
CAUSAL_MARKERS = re.compile(
    r"causa|a causa di|perch[eé]|dovut|root cause|caused|due to|because|responsabile|colpevole", re.IGNORECASE)
NUMBER_TOKEN = re.compile(r"(?<![\w.])(?:0x[0-9a-fA-F]+|\d{2,}(?:[.,]\d+)?)(?![\w])")

# Metric registry: name -> (class, description). The order is the order of the report.
METRICS = {
    "terminalStatus": ("C", "Persisted terminal task status"),
    "terminalKind": ("C", "Persisted terminal reason kind"),
    "exitCode": ("C", "CLI process exit code"),
    "wallSeconds": ("C", "Wall-clock duration of the CLI process"),
    "timedOut": ("C", "The run was stopped by the harness timeout"),
    "plannedSteps": ("C", "Steps of the latest plan revision"),
    "plannedStepsExecuted": ("H", "Distinct planned steps of the latest revision that were executed"),
    "plannedStepsPending": ("H", "Planned steps of the latest revision never executed"),
    "planRevisions": ("C", "Plan revisions produced (initial + replans)"),
    "replans": ("C", "Replans (revisions after the first)"),
    "toolStepsExecuted": ("C", "Persisted steps that carry an executed tool call (EvidenceRead excluded)"),
    "modelCalls": ("C", "Model-call records persisted in the task state"),
    "modelCallsAudit": ("C", "Model-call events in the audit trail"),
    "modelCallFailures": ("C", "Model-call records with a failure outcome"),
    "modelRetries": ("C", "Model-call records that are a retry (attempt > 1)"),
    "providerRetries": ("C", "Model-call records with provider attempt > 1"),
    "fallbacks": ("C", "Model-call records produced by a fallback (ordinal > 0)"),
    "truncatedResponses": ("C", "Responses that ended with finish reason length"),
    "payloadTruncated": ("C", "Recorded payloads truncated by the runtime"),
    "promptTokens": ("C", "Prompt tokens reported by the provider (null usage not counted)"),
    "completionTokens": ("C", "Completion tokens reported by the provider"),
    "reasoningTokens": ("C", "Reasoning tokens reported by the provider"),
    "usageMissingCalls": ("C", "Successful model calls with no provider usage"),
    "accountedTokens": ("C", "TaskState.Accounting.TokensUsed"),
    "estimatedTokens": ("E", "(request + response characters) / 4 over every call"),
    "estimatedCostUsd": ("E", "Provider EstimatedCostUsd where reported, else configured price"),
    "modelSeconds": ("C", "Sum of model-call durations"),
    "toolSeconds": ("C", "Sum of tool-call durations (audit)"),
    "overheadSeconds": ("E", "Wall time not explained by model or tool time"),
    "contextCompactionTurns": ("H", "Model calls whose context provenance names a summary or compaction"),
    "evidenceReadAudited": ("C", "EvidenceRead attempts in the audit trail"),
    "evidenceReadServed": ("C", "EvidenceRead attempts served (Success or EndOfEvidence)"),
    "evidenceReadRejected": ("C", "EvidenceRead attempts that were not served"),
    "evidenceReadDuplicates": ("C", "Served reads repeating an earlier (id, offset, length)"),
    "partialSources": ("C", "Tool results marked Partial or Unavailable"),
    "partialSourcesWithRead": ("H", "Partial or unavailable sources followed by a served EvidenceRead of that step"),
    "toolCallsAudited": ("C", "Tool-call events in the audit trail"),
    "toolCallsDenied": ("C", "Tool calls denied by policy"),
    "mutationsExecuted": ("C", "Executed calls above Read risk (must be 0)"),
    "approvalEvents": ("C", "Approval events (must be 0: no approval is automated)"),
    "auditEvents": ("C", "Audit events for the task"),
    "auditChainOk": ("C", "Audit sequence is contiguous and each PrevHash equals the previous Hash"),
    "factsCaptured": ("C", "EvidenceFact records attached to tool results"),
    "answerCharacters": ("C", "Length of the final answer"),
    "answerDisclosesLimitation": ("H", "The final answer uses limitation wording"),
    "undisclosedPartialSources": ("H", "Partial sources exist but the answer uses no limitation wording"),
    "answerNumbersNotInEvidence": ("H", "Numbers in the final answer found in no tool output or fact"),
    "answerNumbers": ("H", "Numbers in the final answer"),
}

NOT_AVAILABLE = [
    ("Correctness of the diagnosis", "Needs a human reading of the blind evaluation sheets; reported as PENDING until filled (class Q)."),
    ("Importance-weighted fact coverage", "No golden fact list exists per machine; 'partialSourcesWithRead' is only a structural proxy."),
    ("Provider token usage for a call with no usage", "Counted in usageMissingCalls; the estimate (characters/4) is the only figure then."),
    ("Cost for a local model", "No price is reported; cost is 0 by construction unless a price is configured in the profile."),
    ("Context compaction", "Detected only from context-provenance wording in the audit; the run records no dedicated compaction event."),
    ("Resume safety", "Not exercised live: the campaign never resumes a task. Mode-switch resume is covered by the offline BOPS-0122 suite."),
    ("MAF-internal telemetry", "The MAF host adds no tag or event (BOPS-0122 A6); the mode is proven by the EXPERIMENTAL line on standard error only."),
    ("Run-to-run model variance", "One round per cell cannot separate MAF effects from sampling noise; three rounds only bound it."),
]


# --------------------------------------------------------------------------------------------------------------------
# Loading
# --------------------------------------------------------------------------------------------------------------------

def _read_json(path: pathlib.Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def load_task_state(capture_dir: pathlib.Path):
    """Return (state_dict | None, task_id | None) for the single task of an isolated run."""
    tasks = capture_dir / "tasks"
    if not tasks.is_dir():
        return None, None
    for folder in sorted(p for p in tasks.iterdir() if p.is_dir()):
        state_file = folder / "state_json.utf8"
        if state_file.is_file():
            return json.loads(state_file.read_bytes().decode("utf-8-sig")), folder.name
    return None, None


def load_audit(audit_path: pathlib.Path):
    """Return (events, chain) for an audit JSON-lines file; unparseable lines are counted, never raised."""
    events, envelopes, bad = [], [], 0
    if audit_path.is_file():
        for line in audit_path.read_bytes().split(b"\n"):
            if not line.strip():
                continue
            try:
                envelope = json.loads(line)
                events.append(json.loads(envelope["EventJson"]))
                envelopes.append(envelope)
            except (ValueError, KeyError, TypeError):
                bad += 1
    ok = bad == 0
    previous_hash, previous_seq = None, None
    for envelope in envelopes:
        seq = envelope.get("Seq")
        if previous_seq is not None and seq != previous_seq + 1:
            ok = False
        if previous_hash is not None and envelope.get("PrevHash") != previous_hash:
            ok = False
        previous_hash, previous_seq = envelope.get("Hash"), seq
    return events, {"ok": ok, "unparseableLines": bad, "events": len(events)}


# --------------------------------------------------------------------------------------------------------------------
# Per-run metrics
# --------------------------------------------------------------------------------------------------------------------

def _model_calls(state):
    for step in state.get("Steps") or []:
        for call in step.get("ModelCalls") or []:
            yield call
        for call in step.get("EarlyFinalReplanCalls") or []:
            yield call
    for plan in state.get("Plans") or []:
        for call in plan.get("ModelCalls") or []:
            yield call


def _parse_timespan_seconds(value) -> float:
    if isinstance(value, (int, float)):
        return float(value)
    if not isinstance(value, str):
        return 0.0
    match = re.fullmatch(r"(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)", value.strip())
    if not match:
        return 0.0
    days, hours, minutes, seconds = match.groups()
    return int(days or 0) * 86400 + int(hours) * 3600 + int(minutes) * 60 + float(seconds)


def final_answer(state) -> str:
    """The last persisted step that carries no tool call: the answer the CLI prints."""
    for step in reversed(state.get("Steps") or []):
        if not step.get("ToolCall") and step.get("Observation"):
            return step["Observation"]
    return ""


def _evidence_text(state) -> str:
    parts = []
    for step in state.get("Steps") or []:
        result = step.get("Result") or {}
        if result.get("Output"):
            parts.append(result["Output"])
        for fact in result.get("Facts") or []:
            parts.append(json.dumps(fact, ensure_ascii=False))
        if step.get("ToolCall") and step["ToolCall"].get("Arguments"):
            parts.append(json.dumps(step["ToolCall"]["Arguments"], ensure_ascii=False))
    return "\n".join(parts)


def _normalise_number(token: str) -> str:
    return token.replace(",", ".").lower()


def compute_metrics(state, audit_events, audit_chain, run_info, price=None):
    """Return {metric: value} for one run; keys are those of METRICS. Missing evidence yields None, never a guess."""
    out = {name: None for name in METRICS}
    run_info = run_info or {}
    out["exitCode"] = run_info.get("exitCode")
    out["wallSeconds"] = run_info.get("wallSeconds")
    out["timedOut"] = bool(run_info.get("timedOut"))
    if state is None:
        out["terminalStatus"] = "NoTaskState"
        return out
    out["terminalStatus"] = TASK_STATUS.get(state.get("Status"), str(state.get("Status")))
    out["terminalKind"] = (state.get("TerminalReason") or {}).get("Kind")

    plans = state.get("Plans") or []
    steps = state.get("Steps") or []
    latest = max(plans, key=lambda p: p.get("Revision", 0)) if plans else None
    out["planRevisions"] = len(plans)
    out["replans"] = max(0, len(plans) - 1)
    if latest is not None:
        planned = {s.get("Index") for s in latest.get("Steps") or []}
        done = {s.get("PlannedStepIndex") for s in steps
                if s.get("ToolCall") and s.get("PlannedStepIndex") is not None and s.get("PlanRevision", 0) == latest.get("Revision", 0)}
        out["plannedSteps"] = len(planned)
        out["plannedStepsExecuted"] = len(planned & done)
        out["plannedStepsPending"] = len(planned - done)

    out["toolStepsExecuted"] = sum(1 for s in steps if s.get("ToolCall") and s["ToolCall"].get("ToolName") != EVIDENCE_TOOL and s.get("Result"))

    calls = list(_model_calls(state))
    out["modelCalls"] = len(calls)
    out["modelCallFailures"] = sum(1 for c in calls if c.get("Outcome") == 1)
    out["modelRetries"] = sum(1 for c in calls if (c.get("ModelAttempt") or 1) > 1)
    out["providerRetries"] = sum(1 for c in calls if (c.get("ProviderAttempt") or 1) > 1)
    out["fallbacks"] = sum(1 for c in calls if (c.get("FallbackOrdinal") or 0) > 0)
    out["truncatedResponses"] = sum(1 for c in calls if str(c.get("FinishReason") or "").lower() == "length")
    out["payloadTruncated"] = sum(1 for c in calls if c.get("PayloadTruncated"))
    usages = [c.get("Usage") for c in calls if c.get("Usage")]
    out["promptTokens"] = sum(u.get("PromptTokens") or 0 for u in usages)
    out["completionTokens"] = sum(u.get("CompletionTokens") or 0 for u in usages)
    out["reasoningTokens"] = sum((c.get("Output") or {}).get("ReasoningTokens") or 0 for c in calls)
    out["usageMissingCalls"] = sum(1 for c in calls if c.get("Outcome") == 0 and not c.get("Usage"))
    out["accountedTokens"] = (state.get("Accounting") or {}).get("TokensUsed")
    out["estimatedTokens"] = sum((len(c.get("RequestJson") or "") + len(c.get("ResponseJson") or "")) // CHARS_PER_TOKEN for c in calls)
    reported_cost = [u.get("EstimatedCostUsd") for u in usages if u.get("EstimatedCostUsd") is not None]
    if reported_cost:
        out["estimatedCostUsd"] = round(sum(reported_cost), 6)
    elif price and price.get("promptUsdPerMTok") is not None and price.get("completionUsdPerMTok") is not None:
        out["estimatedCostUsd"] = round(out["promptTokens"] * price["promptUsdPerMTok"] / 1e6
                                        + out["completionTokens"] * price["completionUsdPerMTok"] / 1e6, 6)
    out["modelSeconds"] = round(sum(c.get("DurationMs") or 0 for c in calls) / 1000.0, 3)

    model_events = [e for e in audit_events if e.get("eventType") == "modelCall"]
    tool_events = [e for e in audit_events if e.get("eventType") == "toolCall"]
    evidence_events = [e for e in audit_events if e.get("eventType") == "evidenceRead"]
    out["auditEvents"] = len(audit_events)
    out["auditChainOk"] = bool(audit_chain and audit_chain.get("ok"))
    out["modelCallsAudit"] = len(model_events)
    out["toolCallsAudited"] = len(tool_events)
    out["toolSeconds"] = round(sum(_parse_timespan_seconds(e.get("Duration")) for e in tool_events), 3)
    out["toolCallsDenied"] = sum(1 for e in tool_events if e.get("Authorization") in (AUTH_POLICY_DENIED, AUTH_UNKNOWN_TOOL))
    out["mutationsExecuted"] = sum(1 for e in tool_events if (e.get("Risk") or 0) > RISK_READ
                                   and e.get("Authorization") in (AUTH_AUTOMATIC, AUTH_USER_APPROVED))
    out["approvalEvents"] = sum(1 for e in audit_events if e.get("eventType") == "approval")
    if out["wallSeconds"] is not None:
        out["overheadSeconds"] = round(max(0.0, out["wallSeconds"] - out["modelSeconds"] - out["toolSeconds"]), 3)
    out["contextCompactionTurns"] = sum(
        1 for e in model_events
        if any(re.search(r"summar|compact", json.dumps(item.get("Provenance") or {}), re.IGNORECASE) for item in e.get("ContextProvenance") or []))

    seen, duplicates, served_steps = set(), 0, set()
    for event in evidence_events:
        if event.get("ResultCode") in EVIDENCE_SERVED:
            key = (event.get("EvidenceId"), event.get("Source"), event.get("Offset"), event.get("RequestedLength"))
            duplicates += key in seen
            seen.add(key)
            if event.get("StepIndex") is not None:
                served_steps.add(event["StepIndex"])
    out["evidenceReadAudited"] = len(evidence_events)
    out["evidenceReadServed"] = sum(1 for e in evidence_events if e.get("ResultCode") in EVIDENCE_SERVED)
    out["evidenceReadRejected"] = out["evidenceReadAudited"] - out["evidenceReadServed"]
    out["evidenceReadDuplicates"] = duplicates

    partial_steps = [s.get("Index") for s in steps
                     if (s.get("Result") or {}).get("Completeness") in (COMPLETENESS_PARTIAL, COMPLETENESS_UNAVAILABLE)]
    out["partialSources"] = len(partial_steps)
    out["partialSourcesWithRead"] = sum(1 for index in partial_steps if index in served_steps)
    out["factsCaptured"] = sum(len((s.get("Result") or {}).get("Facts") or []) for s in steps)

    answer = final_answer(state)
    out["answerCharacters"] = len(answer)
    discloses = bool(LIMITATION_MARKERS.search(answer))
    out["answerDisclosesLimitation"] = discloses
    out["undisclosedPartialSources"] = out["partialSources"] if (out["partialSources"] and not discloses) else 0
    evidence = _evidence_text(state).lower()
    numbers = {_normalise_number(m.group(0)) for m in NUMBER_TOKEN.finditer(answer)}
    out["answerNumbers"] = len(numbers)
    out["answerNumbersNotInEvidence"] = sum(1 for n in numbers if n not in evidence)
    return out


# --------------------------------------------------------------------------------------------------------------------
# Evaluation sheets (qualitative, blind)
# --------------------------------------------------------------------------------------------------------------------

VERDICTS = ("supported", "partially-supported", "unsupported", "contradicted", "hypothesis-flagged", "limitation-disclosed")
SHEET_RUBRIC = {
    "diagnosisCorrectness": "0 wrong | 1 mostly wrong | 2 partly right | 3 right, against the acquired evidence (not against the machine's true state)",
    "limitationDisclosure": "0 none | 1 vague | 2 specific, matches the partial/unavailable sources listed in evidenceIndex",
    "unsupportedClaims": "number of causal or factual claims not supported by the acquired evidence",
    "notes": "free text; do not paste evidence into the repository",
}


def split_claims(answer: str):
    pieces = re.split(r"(?<=[.!?])\s+|\n+", answer)
    return [p.strip(" -*\t") for p in pieces if len(p.strip(" -*\t")) >= 12]


def build_evaluation_sheet(code: str, scenario_id: str, state) -> dict:
    """A blind sheet: no variant, no run id. The campaign keeps the key separately."""
    answer = final_answer(state) if state else ""
    evidence = _evidence_text(state).lower() if state else ""
    claims = []
    for index, text in enumerate(split_claims(answer), start=1):
        numbers = sorted({_normalise_number(m.group(0)) for m in NUMBER_TOKEN.finditer(text)})
        kind = ("limitation" if LIMITATION_MARKERS.search(text) else "causal" if CAUSAL_MARKERS.search(text)
                else "hypothesis" if HYPOTHESIS_MARKERS.search(text) else "statement")
        if kind == "causal" and HYPOTHESIS_MARKERS.search(text):
            kind = "causal-hedged"
        claims.append({"id": f"C{index:02d}", "text": text, "autoKind": kind,
                       "numbersNotInEvidence": [n for n in numbers if n not in evidence],
                       "verdict": None, "evidenceSteps": [], "notes": None})
    index_rows = []
    for step in (state.get("Steps") if state else []) or []:
        call = step.get("ToolCall")
        if call:
            result = step.get("Result") or {}
            index_rows.append({"step": step.get("Index"), "tool": call.get("ToolName"), "completeness": result.get("Completeness"),
                               "outcome": result.get("Outcome"), "facts": len(result.get("Facts") or [])})
    return {"code": code, "scenario": scenario_id, "verdictVocabulary": list(VERDICTS), "rubric": SHEET_RUBRIC,
            "evidenceIndex": index_rows, "claims": claims,
            "overall": {"diagnosisCorrectness": None, "limitationDisclosure": None, "unsupportedClaims": None, "notes": None}}


def evaluation_summary(sheet: dict | None) -> dict:
    if not sheet:
        return {"status": "PENDING", "claims": 0}
    claims = sheet.get("claims") or []
    judged = [c for c in claims if c.get("verdict")]
    overall = sheet.get("overall") or {}
    complete = bool(claims) and len(judged) == len(claims) and overall.get("diagnosisCorrectness") is not None
    return {"status": "COMPLETE" if complete else "PENDING" if not judged else "PARTIAL", "claims": len(claims),
            "judged": len(judged), "verdicts": dict(Counter(c["verdict"] for c in judged)),
            "diagnosisCorrectness": overall.get("diagnosisCorrectness"),
            "limitationDisclosure": overall.get("limitationDisclosure"),
            "unsupportedClaims": overall.get("unsupportedClaims")}


# --------------------------------------------------------------------------------------------------------------------
# Campaign comparison and report
# --------------------------------------------------------------------------------------------------------------------

NUMERIC_COMPARE = [name for name in METRICS if name not in {"terminalStatus", "terminalKind", "auditChainOk",
                                                            "answerDisclosesLimitation", "timedOut"}]


def _numeric(value):
    if isinstance(value, bool):
        return float(value)
    return float(value) if isinstance(value, (int, float)) else None


def _stats(values):
    values = [v for v in values if v is not None]
    if not values:
        return None
    return {"n": len(values), "mean": round(statistics.fmean(values), 3), "min": min(values), "max": max(values)}


def load_campaign(campaign_dir: pathlib.Path):
    manifest = _read_json(campaign_dir / "campaign.json")
    runs = []
    for entry in manifest.get("plannedRuns", []):
        run_dir = campaign_dir / "runs" / entry["runDir"]
        info_path = run_dir / "run.json"
        if not info_path.is_file():
            runs.append({**entry, "info": None, "metrics": None, "executed": False})
            continue
        info = _read_json(info_path)
        state, task_id = load_task_state(run_dir / "capture")
        events, chain = load_audit(run_dir / "audit-full.jsonl")
        metrics = compute_metrics(state, events, chain, info, manifest.get("pricing"))
        sheet_path = campaign_dir / "evaluation" / f"{entry['blindCode']}.json"
        sheet = _read_json(sheet_path) if sheet_path.is_file() else None
        runs.append({**entry, "info": info, "metrics": metrics, "executed": True, "taskId": task_id,
                     "evaluation": evaluation_summary(sheet)})
    return manifest, runs


def compare(manifest: dict, runs: list[dict]) -> dict:
    cells = defaultdict(list)
    for run in runs:
        if run["executed"]:
            cells[(run["scenario"], run["variant"])].append(run)
    scenarios = sorted({s for s, _ in cells})
    rounds_per_cell = max((len(v) for v in cells.values()), default=0)
    comparison = {}
    for scenario in scenarios:
        a, b = cells.get((scenario, "A"), []), cells.get((scenario, "B"), [])
        rows = {}
        for name in NUMERIC_COMPARE:
            sa = _stats([_numeric(r["metrics"][name]) for r in a])
            sb = _stats([_numeric(r["metrics"][name]) for r in b])
            delta = pct = None
            if sa and sb:
                delta = round(sb["mean"] - sa["mean"], 3)
                pct = round(100.0 * delta / sa["mean"], 1) if sa["mean"] else None
            rows[name] = {"class": METRICS[name][0], "A": sa, "B": sb, "delta": delta, "deltaPct": pct}
        comparison[scenario] = {
            "runsA": [r["runDir"] for r in a], "runsB": [r["runDir"] for r in b], "metrics": rows,
            "terminalA": [r["metrics"]["terminalStatus"] for r in a], "terminalB": [r["metrics"]["terminalStatus"] for r in b],
            "evaluationA": [r["evaluation"] for r in a], "evaluationB": [r["evaluation"] for r in b],
        }
    safety = []
    for run in runs:
        if not run["executed"]:
            continue
        m, info = run["metrics"], run["info"]
        safety.append({"runDir": run["runDir"], "variant": run["variant"], "modeVerified": info.get("modeVerified"),
                       "mutationsExecuted": m["mutationsExecuted"], "approvalEvents": m["approvalEvents"],
                       "auditChainOk": m["auditChainOk"], "secretLeakScan": info.get("secretLeakScan"),
                       "ok": bool(info.get("modeVerified")) and not m["mutationsExecuted"] and not m["approvalEvents"]
                             and bool(m["auditChainOk"]) and info.get("secretLeakScan") != "leak"})
    fingerprints = manifest.get("configFingerprintByVariant") or {}
    config = {"identicalAcrossVariants": bool(fingerprints) and fingerprints.get("A") == fingerprints.get("B"),
              "fingerprints": fingerprints}
    drift = []
    for run in runs:
        if run["executed"] and run["info"].get("hostBefore"):
            drift.append({"runDir": run["runDir"], "variant": run["variant"], **run["info"]["hostBefore"]})
    return {"schema": 1, "testDouble": bool(manifest.get("testDouble")), "campaignId": manifest.get("campaignId"),
            "commit": manifest.get("commit"), "model": manifest.get("model"), "roundsPerCell": rounds_per_cell,
            "exploratory": rounds_per_cell < 3, "configuration": config, "scenarios": comparison, "safety": safety,
            "hostBeforeEachRun": drift, "notAvailable": [{"metric": n, "limit": t} for n, t in NOT_AVAILABLE],
            "metricClasses": {k: {"class": c, "description": d} for k, (c, d) in METRICS.items()}}


def _cell(stat):
    if not stat:
        return "-"
    return f"{stat['mean']:g}" if stat["n"] == 1 else f"{stat['mean']:g} ({stat['min']:g}..{stat['max']:g})"


def _signed(value, suffix=""):
    return "-" if value is None else f"{value:+g}{suffix}"


def render_markdown(report: dict) -> str:
    lines = ["# MAF CLI A/B benchmark report", ""]
    if report["testDouble"]:
        lines += ["> **TEST DOUBLE - not a measurement.** The CLI was replaced by a scripted stand-in.", ""]
    lines += [f"- Campaign: `{report['campaignId']}`", f"- Commit: `{report['commit']}`",
              f"- Model: `{json.dumps(report['model'], ensure_ascii=False)}`",
              "- Variants: **A** = `Cli:Experimental:MafGovernedTurn=false`, **B** = `true`; Knowledge off in both.",
              f"- Rounds per cell: **{report['roundsPerCell']}**"
              + (" - **exploratory: one or two rounds support no statistical conclusion.**" if report["exploratory"] else ""),
              f"- Effective configuration identical across variants (apart from the MAF key): **{report['configuration']['identicalAcrossVariants']}**",
              "", "Classes: **C** certain, **E** estimate, **H** heuristic, **Q** qualitative (human, blind). No evidence text is in this report.", ""]
    lines += ["## Safety and integrity", "", "| run | variant | mode verified | mutations | approvals | audit chain | secret scan | ok |",
              "|---|---|---|---|---|---|---|---|"]
    for row in report["safety"]:
        lines.append(f"| {row['runDir']} | {row['variant']} | {row['modeVerified']} | {row['mutationsExecuted']} | {row['approvalEvents']} | "
                     f"{row['auditChainOk']} | {row['secretLeakScan']} | {'yes' if row['ok'] else '**NO**'} |")
    for scenario, data in report["scenarios"].items():
        lines += ["", f"## {scenario}", "", f"Terminal status - A: {data['terminalA']} | B: {data['terminalB']}. "
                  "`Completed` is a runtime outcome, **not** a statement that the diagnosis is correct.", "",
                  "| metric | class | A | B | delta (B-A) | delta % |", "|---|---|---|---|---|---|"]
        for name, row in data["metrics"].items():
            if row["A"] is None and row["B"] is None:
                continue
            lines.append(f"| {name} | {row['class']} | {_cell(row['A'])} | {_cell(row['B'])} | {_signed(row['delta'])} | {_signed(row['deltaPct'], '%')} |")
        lines += ["", "Qualitative evaluation (Q): A " + ", ".join(e["status"] for e in data["evaluationA"])
                  + " | B " + ", ".join(e["status"] for e in data["evaluationB"])]
        for side, key in (("A", "evaluationA"), ("B", "evaluationB")):
            for ev in data[key]:
                if ev["status"] != "PENDING":
                    lines.append(f"- {side}: verdicts {ev.get('verdicts')}, correctness {ev.get('diagnosisCorrectness')}, "
                                 f"limitation disclosure {ev.get('limitationDisclosure')}, unsupported claims {ev.get('unsupportedClaims')}")
    lines += ["", "## Metrics not available or limited", ""] + [f"- **{n['metric']}** - {n['limit']}" for n in report["notAvailable"]]
    lines += ["", "## Reading rules", "",
              "- A difference smaller than the run-to-run variance of the model is not an effect of MAF; with fewer than three rounds the variance is unknown.",
              "- Host load before each run is recorded in the JSON (`hostBeforeEachRun`); a large difference between A and B runs of one scenario is an environment change, not a mode effect.",
              "- BENCH-00 measured the API; this campaign measures the CLI. The two are not interchangeable evidence for BOPS-0122.", ""]
    return "\n".join(lines)


def render_csv(report: dict) -> str:
    buffer = io.StringIO()
    writer = csv.writer(buffer, lineterminator="\n")
    writer.writerow(["scenario", "metric", "class", "A_n", "A_mean", "A_min", "A_max", "B_n", "B_mean", "B_min", "B_max", "delta", "delta_pct"])
    for scenario, data in report["scenarios"].items():
        for name, row in data["metrics"].items():
            a, b = row["A"] or {}, row["B"] or {}
            writer.writerow([scenario, name, row["class"], a.get("n", ""), a.get("mean", ""), a.get("min", ""), a.get("max", ""),
                             b.get("n", ""), b.get("mean", ""), b.get("min", ""), b.get("max", ""),
                             "" if row["delta"] is None else row["delta"], "" if row["deltaPct"] is None else row["deltaPct"]])
    return buffer.getvalue()


def write_report(campaign_dir: pathlib.Path) -> dict:
    manifest, runs = load_campaign(campaign_dir)
    report = compare(manifest, runs)
    out = campaign_dir / "report"
    out.mkdir(exist_ok=True)
    (out / "ab-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (out / "ab-report.csv").write_text(render_csv(report), encoding="utf-8", newline="")
    (out / "ab-report.md").write_text(render_markdown(report), encoding="utf-8")
    return report


# --------------------------------------------------------------------------------------------------------------------
# Estimates before the first live run
# --------------------------------------------------------------------------------------------------------------------

DEFAULT_HISTORY = {  # conservative placeholders used only when no BENCH-00 archive is available
    "B01": {"tokens": 60_000, "seconds": 240}, "B02": {"tokens": 330_000, "seconds": 1500}, "B03": {"tokens": 250_000, "seconds": 1200}}


def history_from_bench00(directory: pathlib.Path):
    """Aggregate per-scenario tokens and model time from local BENCH-00 archives (read-only; no content is returned)."""
    samples = defaultdict(list)
    for archive in sorted(directory.glob("*/*.zip")):
        try:
            with zipfile.ZipFile(archive) as z:
                names = set(z.namelist())
                roster = json.loads(z.read("run-roster.json"))
                for task in roster.get("tasks", []):
                    path = f"tasks/{task['taskId']}/state_json.utf8"
                    if path not in names:
                        continue
                    state = json.loads(z.read(path).decode("utf-8-sig"))
                    calls = list(_model_calls(state))
                    samples[task["scenarioId"]].append({"tokens": (state.get("Accounting") or {}).get("TokensUsed") or 0,
                                                        "modelSeconds": sum((c.get("DurationMs") or 0) for c in calls) / 1000.0,
                                                        "calls": len(calls)})
        except (OSError, ValueError, KeyError, zipfile.BadZipFile):
            continue
    result = {}
    for scenario, rows in samples.items():
        result[scenario] = {"samples": len(rows), "tokensMean": round(statistics.fmean(r["tokens"] for r in rows)),
                            "tokensMax": max(r["tokens"] for r in rows),
                            "modelSecondsMean": round(statistics.fmean(r["modelSeconds"] for r in rows)),
                            "modelSecondsMax": round(max(r["modelSeconds"] for r in rows)),
                            "callsMean": round(statistics.fmean(r["calls"] for r in rows), 1)}
    return result


def estimate_campaign(scenarios, variants, rounds, per_run_token_cap, task_timeout, history=None, price=None, tool_overhead_fraction=0.25):
    """Expected and worst-case totals. Worst case = every run uses its token cap and its full timeout."""
    runs = len(scenarios) * len(variants) * rounds
    rows, expected_tokens, expected_seconds = [], 0, 0.0
    for scenario in scenarios:
        h = (history or {}).get(scenario)
        tokens = h["tokensMean"] if h else DEFAULT_HISTORY.get(scenario, {"tokens": per_run_token_cap // 2})["tokens"]
        seconds = (h["modelSecondsMean"] * (1 + tool_overhead_fraction)) if h else DEFAULT_HISTORY.get(scenario, {"seconds": 600})["seconds"]
        source = f"BENCH-00 mean of {h['samples']} run(s) (API path, indicative only)" if h else "placeholder default, no history"
        capped = min(tokens, per_run_token_cap)
        rows.append({"scenario": scenario, "expectedTokensPerRun": capped, "expectedSecondsPerRun": round(seconds), "source": source})
        expected_tokens += capped * len(variants) * rounds
        expected_seconds += seconds * len(variants) * rounds
    worst_tokens = per_run_token_cap * runs
    worst_seconds = task_timeout * runs
    cost = None
    if price and price.get("promptUsdPerMTok") is not None and price.get("completionUsdPerMTok") is not None:
        blended = max(price["promptUsdPerMTok"], price["completionUsdPerMTok"])
        cost = {"expectedUsd": round(expected_tokens * blended / 1e6, 4), "worstCaseUsd": round(worst_tokens * blended / 1e6, 4),
                "basis": "all tokens priced at the higher of the two configured rates (upper bound)"}
    return {"runs": runs, "perScenario": rows, "expectedTokens": expected_tokens, "worstCaseTokens": worst_tokens,
            "expectedHours": round(expected_seconds / 3600, 2), "worstCaseHours": round(worst_seconds / 3600, 2),
            "cost": cost or {"expectedUsd": 0.0, "worstCaseUsd": 0.0, "basis": "no price configured: local model, no per-token cost"}}
