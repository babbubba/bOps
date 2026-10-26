#!/usr/bin/env python3
"""Metrics, A/B/C/D comparison, evaluation sheets and token/time estimates for the MAF CLI benchmark (BOPS-0125).

Four conditions are compared: A = MAF off / Knowledge off, B = MAF on / Knowledge off (the historical A/B of the harness, unchanged),
C = MAF off / Knowledge on, D = MAF on / Knowledge on. The four pairs that answer the questions of the campaign are A-B (MAF without
Knowledge), C-D (MAF with Knowledge), A-C (Knowledge without governed MAF) and B-D (Knowledge with governed MAF).

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

VARIANTS = {
    "A": {"label": "MAF OFF / Knowledge OFF", "maf": False, "knowledge": False},
    "B": {"label": "MAF ON / Knowledge OFF", "maf": True, "knowledge": False},
    "C": {"label": "MAF OFF / Knowledge ON", "maf": False, "knowledge": True},
    "D": {"label": "MAF ON / Knowledge ON", "maf": True, "knowledge": True},
}
PAIRS = [("A", "B", "MAF without Knowledge"), ("C", "D", "MAF with Knowledge"),
         ("A", "C", "Knowledge without governed MAF"), ("B", "D", "Knowledge with governed MAF")]
FAILURE_KIND = {"Unknown": 0, "Transient": 1, "RateLimited": 2, "Timeout": 3, "Unreachable": 4, "Authentication": 5, "QuotaExceeded": 6,
                "InvalidRequest": 7, "ContextOverflow": 8, "MalformedResponse": 9}
KNOWLEDGE_READ_SUCCESS, KNOWLEDGE_READ_CONTEXT_BUDGET = 0, 13
KNOWLEDGE_INDEX_PUBLISHED = 0
CONTEXT_SATURATION_PERCENT = 90.0
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
    "outcomeClass": ("C", "completed | timeout | budget-exceeded | context-saturation | protocol-error | provider-failure | failed | no-state"),
    "knowledgeIndexPublished": ("C", "Knowledge index publications (audit): the knowledge was AVAILABLE to the model"),
    "knowledgeReadsAudited": ("C", "Knowledge read attempts in the audit trail"),
    "knowledgeReadsServed": ("C", "Knowledge reads served: the knowledge was USED"),
    "knowledgeReadsRefused": ("C", "Knowledge reads refused (any cause)"),
    "knowledgeReadsContextBudget": ("C", "Knowledge reads refused because they did not fit the context budget"),
    "knowledgeDistinctResources": ("C", "Distinct skill/resource items served"),
    "knowledgeResourcesRead": ("C", "The skill/resource items served"),
    "knowledgeEstimatedTokensServed": ("E", "Estimated tokens (bytes/4) of the knowledge served"),
    "knowledgeUsed": ("C", "1 when at least one read was served, else 0 (available is not used)"),
    "maxPromptTokens": ("C", "Largest prompt reported by the provider for one call"),
    "maxContextUsagePercent": ("C", "Largest share of the context window one prompt took (provider figure when reported, else estimate)"),
    "compactedToolGroups": ("C", "Tool-result groups replaced by placeholders (context compaction)"),
    "droppedTurns": ("C", "Turns dropped by the sliding window or truncation"),
    "contextOverflows": ("C", "Model attempts failed with a provider-reported context overflow"),
    "timeoutFailures": ("C", "Model attempts failed by timeout"),
    "protocolErrors": ("C", "Model attempts failed as invalid request or malformed response"),
    "contextGateRejections": ("C", "Calls refused by the MAF final context gate"),
    "liveDumpTreatedAsCrash": ("H", "Sentences that equate a live dump (0x193) with a BSOD, crash or restart without a negation"),
    "hardwareCauseUnhedged": ("H", "Sentences that declare a hardware component faulty or the cause with no hedge"),
    "wheaIdsEquated": ("H", "Sentences that present different WHEA IDs as the same thing"),
    "eventsAsIndependentFailures": ("H", "Event counts presented as independent failures although no episode/cluster is mentioned"),
    "diagnosticFlags": ("H", "Total of the heuristic diagnostic flags above"),
    # BOPS-0126 P1: the deterministic MAF evaluation written by tests/bOps.DiagnosticEvaluation to auto-evaluation/<blindCode>.json.
    # None when the campaign was not evaluated, or when the run has no final answer (M1-M3) or no Knowledge (M4): never a 0.
    "autoEvaluated": ("C", "1 when the run was scored by tests/bOps.DiagnosticEvaluation (auto-evaluation/<blindCode>.json)"),
    "autoChecksFailed": ("H", "MAF checks failed: finalized, plan-within-budget, knowledge-pertinent, live-dump-kept-distinct, counts-consistent, limitations-disclosed"),
    "autoFinalized": ("C", "M5: Completed with a persisted final answer"),
    "autoCountAccuracy": ("H", "M2: exact counts / (exact + wrong family + unverified), against the run's typed facts"),
    "autoCountsExact": ("H", "M2: numbers equal to a typed count of the family named next to them"),
    "autoCountsWrongFamily": ("H", "M2: numbers equal to the headline count of a family the named one is confused with"),
    "autoCountsUnverified": ("H", "M2: numbers attributed to a family that no fact or tool output holds"),
    "autoLiveDumpAsCrash": ("H", "M1: sentences equating live dumps with crashes, plus live-dump counts given as crashes"),
    "autoCrashAsLiveDump": ("H", "M1: crash counts given as live dumps"),
    "autoLimitationsUndisclosed": ("H", "M3: partial sources exist and the answer has no limitation wording"),
    "autoKnowledgePrecision": ("C", "M4: applicable entries read / entries read"),
    "autoKnowledgeRecallListed": ("C", "M4: applicable entries read / applicable entries the index listed"),
    "autoKnowledgeReadsAnswerTurn": ("C", "M4: Knowledge reads served in the answer turn"),
    "autoKnowledgeReadsNotExecuted": ("C", "M4: Knowledge calls answered 'Not executed' (first call wins)"),
    "autoKnowledgeCitedNotLoaded": ("H", "M4: listed entries the answer cites (Kn or skill name) but never read"),
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

# --------------------------------------------------------------------------------------------------------------------
# Diagnostic-quality heuristics (class H): run on the final answer only, never on the knowledge. They flag a sentence for a human
# reader; they never decide that a diagnosis is right.
# --------------------------------------------------------------------------------------------------------------------

_LIVEDUMP = re.compile(r"0x0*193\b|\b193\b|livekernelevent|live ?dump|dxgkrnl", re.IGNORECASE)
_CRASHLIKE = re.compile(r"bsod|schermat[ae] blu|blue ?screen|stop code|bugcheck|riavvi|reboot|restart|crash di sistema|system crash", re.IGNORECASE)
_NEGATION = re.compile(r"\bnon\b|\bnessun|\bsenza\b|\bnot\b|\bno\b|n't\b|distint|differen|divers", re.IGNORECASE)
_COMPONENT = re.compile(r"\bgpu\b|scheda video|\bcpu\b|processore|\bram\b|memoria|alimentator|\bpsu\b|\bssd\b|\bdisco\b|scheda madre", re.IGNORECASE)
_CERTAIN = re.compile(r"guast|difettos|defective|failing|faulty|e la causa|\u00e8 la causa|is the cause|causa certa|conferm|dimostr|certamente|sicuramente|definitivamente", re.IGNORECASE)
_HEDGE = re.compile(r"probabil|potrebbe|potrebbero|possibil|ipotesi|compatibil|da verificare|non dimostrat|non confermat|non provat|"
                    r"may\b|might|could|suggest|likely|unconfirmed|not proven|non vi sono prove|non ci sono prove|nessuna prova|senza prove|non indica|non permette",
                    re.IGNORECASE)
_WHEA = re.compile(r"whea", re.IGNORECASE)
_SAME = re.compile(r"stess[ao]|uguali|equival|identic|same|interchange|intercambiabil", re.IGNORECASE)
_EPISODES = re.compile(r"episod|cluster|raffica|burst|incident", re.IGNORECASE)
_INDEPENDENT = re.compile(r"guasti indipendenti|independent failures|(\d+)\s+(?:guasti|failures)\b", re.IGNORECASE)


def diagnostic_flags(answer: str) -> dict:
    """Heuristic counts (class H) of the diagnostic mistakes the campaign cares about; the sentences are for a human to read."""
    sentences = [p.strip() for p in re.split(r"(?<=[.!?])\s+|\n+", answer or "") if p.strip()]
    live = sum(1 for t in sentences if _LIVEDUMP.search(t) and _CRASHLIKE.search(t) and not _NEGATION.search(t))
    hardware = sum(1 for t in sentences if _COMPONENT.search(t) and _CERTAIN.search(t) and not _HEDGE.search(t) and not _NEGATION.search(t))
    whea = sum(1 for t in sentences if _WHEA.search(t) and _SAME.search(t) and len(re.findall(r"(?<![\w.])\d{1,2}(?![\w.])", t)) >= 2
               and not _NEGATION.search(t))
    mentions_episodes = bool(_EPISODES.search(answer or ""))
    independent = 0 if mentions_episodes else sum(1 for t in sentences if _INDEPENDENT.search(t) and not _NEGATION.search(t))
    return {"liveDumpTreatedAsCrash": live, "hardwareCauseUnhedged": hardware, "wheaIdsEquated": whea,
            "eventsAsIndependentFailures": independent, "diagnosticFlags": live + hardware + whea + independent}


def _failure_kind(event):
    kind = event.get("FailureKind")
    if isinstance(kind, str):
        return FAILURE_KIND.get(kind)
    return kind if isinstance(kind, int) else None


def outcome_class(status_name, run_info, metrics) -> str:
    """Why a run ended the way it did, in the classes an operator acts on (class C, from persisted state, audit and the process)."""
    if status_name == "NoTaskState":
        return "timeout" if (run_info or {}).get("timedOut") else "no-state"
    if status_name == "Completed":
        return "completed"
    if (run_info or {}).get("timedOut") or metrics.get("timeoutFailures"):
        return "timeout"
    if status_name == "BudgetExceeded":
        return "budget-exceeded"
    if metrics.get("contextOverflows") or (metrics.get("maxContextUsagePercent") or 0) >= CONTEXT_SATURATION_PERCENT:
        return "context-saturation"
    if metrics.get("protocolErrors") or metrics.get("contextGateRejections"):
        return "protocol-error"
    calls = metrics.get("modelCalls") or 0
    if calls and metrics.get("modelCallFailures") == calls:
        return "provider-failure"
    return "failed"


def add_knowledge_and_context_metrics(out, audit_events, answer) -> None:
    """Fills the Knowledge, context-window and heuristic metrics from the audit trail and the final answer (in place)."""
    reads = [e for e in audit_events if e.get("eventType") == "knowledgeRead"]
    served = [e for e in reads if e.get("ResultCode") in (KNOWLEDGE_READ_SUCCESS, "Success")]
    resources = sorted({f"{e.get('Skill')}/{e.get('Resource')}" if e.get("Resource") else f"{e.get('Skill')}#{e.get('Operation')}"
                        for e in served if e.get("Skill")})
    out["knowledgeIndexPublished"] = sum(1 for e in audit_events if e.get("eventType") == "knowledgeIndex"
                                         and e.get("Status") in (KNOWLEDGE_INDEX_PUBLISHED, "Published"))
    out["knowledgeReadsAudited"] = len(reads)
    out["knowledgeReadsServed"] = len(served)
    out["knowledgeReadsRefused"] = len(reads) - len(served)
    out["knowledgeReadsContextBudget"] = sum(1 for e in reads if e.get("ResultCode") in (KNOWLEDGE_READ_CONTEXT_BUDGET, "ContextBudget"))
    out["knowledgeDistinctResources"] = len(resources)
    out["knowledgeResourcesRead"] = resources
    out["knowledgeEstimatedTokensServed"] = sum(e.get("EstimatedTokens") or 0 for e in served)
    out["knowledgeUsed"] = 1 if served else 0

    model_events = [e for e in audit_events if e.get("eventType") == "modelCall"]
    contexts = [e.get("Context") for e in model_events if e.get("Context")]
    out["maxPromptTokens"] = max(((e.get("Usage") or {}).get("PromptTokens") or 0 for e in model_events), default=None)
    out["maxContextUsagePercent"] = max((c.get("ContextUsagePercent") or 0.0 for c in contexts), default=None)
    out["compactedToolGroups"] = sum(c.get("CompactedToolGroups") or 0 for c in contexts) if contexts else None
    out["droppedTurns"] = sum(c.get("DroppedTurns") or 0 for c in contexts) if contexts else None
    kinds = [_failure_kind(e) for e in model_events if e.get("Outcome") == 1]
    out["contextOverflows"] = sum(1 for k in kinds if k == FAILURE_KIND["ContextOverflow"])
    out["timeoutFailures"] = sum(1 for k in kinds if k == FAILURE_KIND["Timeout"])
    out["protocolErrors"] = sum(1 for k in kinds if k in (FAILURE_KIND["InvalidRequest"], FAILURE_KIND["MalformedResponse"]))
    out["contextGateRejections"] = sum(1 for e in model_events if e.get("ContextGateRejection"))
    out.update(diagnostic_flags(answer))


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
        out["outcomeClass"] = outcome_class("NoTaskState", run_info, out)
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
    add_knowledge_and_context_metrics(out, audit_events, answer)
    out["outcomeClass"] = outcome_class(out["terminalStatus"], run_info, out)
    return out


# --------------------------------------------------------------------------------------------------------------------
# Evaluation sheets (qualitative, blind)
# --------------------------------------------------------------------------------------------------------------------

# Blind questions for the stability scenarios: judged by a human; the heuristic flag is only a pointer to the sentences to read.
SCENARIO_CHECKLIST = {
    "liveDumpDistinctFromBsod": ("Is a live dump (0x193 / LiveKernelEvent) kept distinct from a BSOD or a restart?", "liveDumpTreatedAsCrash"),
    "categoriesNotSummedWithoutCorrelation": ("Are different anomaly categories kept apart instead of added into one count without correlation?", None),
    "wheaIdsNotConfused": ("Are the different WHEA IDs (for example corrected 19 and any other) kept distinct?", "wheaIdsEquated"),
    "burstNotIndependentFailures": ("Is a burst of events treated as one episode and not as independent failures?", "eventsAsIndependentFailures"),
    "hardwareCauseNotConfirmedWithoutProof": ("Is a hardware cause stated as confirmed only when the evidence proves it?", "hardwareCauseUnhedged"),
    "uncertaintyAndLimitsDisclosed": ("Are partial or missing sources and open hypotheses stated?", None),
    "nextChecksUseful": ("Are the suggested next checks concrete, discriminating and non-mutating?", None),
}

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
    flags = diagnostic_flags(answer)
    return {"code": code, "scenario": scenario_id, "verdictVocabulary": list(VERDICTS), "rubric": SHEET_RUBRIC,
            "scenarioChecklist": {key: {"question": question, "autoFlags": flags.get(flag) if flag else None, "judgement": None}
                                  for key, (question, flag) in SCENARIO_CHECKLIST.items()},
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

NUMERIC_COMPARE = [name for name in METRICS if name not in {"terminalStatus", "terminalKind", "auditChainOk", "answerDisclosesLimitation",
                                                            "timedOut", "outcomeClass", "knowledgeResourcesRead"}]


def _numeric(value):
    if isinstance(value, bool):
        return float(value)
    return float(value) if isinstance(value, (int, float)) else None


def _stats(values):
    values = [v for v in values if v is not None]
    if not values:
        return None
    return {"n": len(values), "mean": round(statistics.fmean(values), 3), "min": min(values), "max": max(values),
            "stdev": round(statistics.stdev(values), 3) if len(values) > 1 else None}


AUTO_EVALUATION_DIR = "auto-evaluation"
AUTO_EVALUATION_SCHEMA = "bops.diagnostic-evaluation/1"


def auto_evaluation_metrics(document: dict | None) -> dict:
    """The BOPS-0126 metrics of one run from its auto-evaluation document (None or another schema: every metric None)."""
    out = {name: None for name in METRICS if name.startswith("auto")}
    if not document or document.get("schema") != AUTO_EVALUATION_SCHEMA:
        return out
    checks = document.get("checks") or []
    completion = document.get("m5Completion") or {}
    out.update({"autoEvaluated": 1, "autoChecksFailed": sum(1 for c in checks if c.get("passed") is False),
                "autoFinalized": completion.get("finalized")})
    counts = document.get("m2Counts")
    if counts:
        out.update({"autoCountAccuracy": counts.get("accuracy"), "autoCountsExact": counts.get("exact"),
                    "autoCountsWrongFamily": counts.get("wrongFamily"), "autoCountsUnverified": counts.get("unverified")})
    classification = document.get("m1Classification")
    if classification:
        out["autoLiveDumpAsCrash"] = len(classification.get("liveDumpSentencesAsCrash") or []) + (classification.get("liveDumpCountAsCrash") or 0)
        out["autoCrashAsLiveDump"] = classification.get("crashCountAsLiveDump")
    support = document.get("m3Support")
    if support:
        out["autoLimitationsUndisclosed"] = support.get("partialSourcesUndisclosed")
    knowledge = document.get("m4Knowledge")
    if knowledge:
        out.update({"autoKnowledgePrecision": knowledge.get("precision"), "autoKnowledgeRecallListed": knowledge.get("recallListed"),
                    "autoKnowledgeReadsAnswerTurn": knowledge.get("readsInAnswerTurn"),
                    "autoKnowledgeReadsNotExecuted": knowledge.get("readsNotExecuted"),
                    "autoKnowledgeCitedNotLoaded": len(knowledge.get("citedNotLoaded") or [])})
    return out


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
        auto_path = campaign_dir / AUTO_EVALUATION_DIR / f"{entry['blindCode']}.json"
        metrics.update(auto_evaluation_metrics(_read_json(auto_path) if auto_path.is_file() else None))
        sheet_path = campaign_dir / "evaluation" / f"{entry['blindCode']}.json"
        sheet = _read_json(sheet_path) if sheet_path.is_file() else None
        runs.append({**entry, "info": info, "metrics": metrics, "executed": True, "taskId": task_id,
                     "evaluation": evaluation_summary(sheet)})
    return manifest, runs


def _pair_rows(left_runs, right_runs):
    rows = {}
    for name in NUMERIC_COMPARE:
        sl = _stats([_numeric(r["metrics"][name]) for r in left_runs])
        sr = _stats([_numeric(r["metrics"][name]) for r in right_runs])
        delta = pct = None
        if sl and sr:
            delta = round(sr["mean"] - sl["mean"], 3)
            pct = round(100.0 * delta / sl["mean"], 1) if sl["mean"] else None
        rows[name] = {"class": METRICS[name][0], "left": sl, "right": sr, "delta": delta, "deltaPct": pct}
    return rows


def compare(manifest: dict, runs: list[dict]) -> dict:
    cells = defaultdict(list)
    for run in runs:
        if run["executed"]:
            cells[(run["scenario"], run["variant"])].append(run)
    scenarios = sorted({s for s, _ in cells})
    present = sorted({v for _, v in cells})
    rounds_per_cell = max((len(v) for v in cells.values()), default=0)
    comparison = {}
    for scenario in scenarios:
        a, b = cells.get((scenario, "A"), []), cells.get((scenario, "B"), [])
        # The historical A/B shape (A and B columns), kept so earlier reports and tools read as they did.
        rows = {name: {"class": row["class"], "A": row["left"], "B": row["right"], "delta": row["delta"], "deltaPct": row["deltaPct"]}
                for name, row in _pair_rows(a, b).items()}
        comparison[scenario] = {
            "runsA": [r["runDir"] for r in a], "runsB": [r["runDir"] for r in b], "metrics": rows,
            "terminalA": [r["metrics"]["terminalStatus"] for r in a], "terminalB": [r["metrics"]["terminalStatus"] for r in b],
            "evaluationA": [r["evaluation"] for r in a], "evaluationB": [r["evaluation"] for r in b],
        }

    matrix = {}
    for scenario in scenarios:
        matrix[scenario] = {}
        for variant in VARIANTS:
            group = cells.get((scenario, variant), [])
            if not group:
                continue
            matrix[scenario][variant] = {
                "label": VARIANTS[variant]["label"], "runs": [r["runDir"] for r in group],
                "terminal": [r["metrics"]["terminalStatus"] for r in group], "outcomeClass": [r["metrics"]["outcomeClass"] for r in group],
                "metrics": {name: _stats([_numeric(r["metrics"][name]) for r in group]) for name in NUMERIC_COMPARE},
                "evaluation": [r["evaluation"] for r in group],
                "knowledgeResources": dict(Counter(res for r in group for res in (r["metrics"]["knowledgeResourcesRead"] or []))),
                "knowledgeAvailableRuns": sum(1 for r in group if (r["metrics"]["knowledgeIndexPublished"] or 0) > 0),
                "knowledgeUsedRuns": sum(1 for r in group if r["metrics"]["knowledgeUsed"]),
            }
    pairs = {}
    for left, right, title in PAIRS:
        by_scenario = {}
        for scenario in scenarios:
            l, r = cells.get((scenario, left), []), cells.get((scenario, right), [])
            if l and r:
                by_scenario[scenario] = {"runsLeft": [x["runDir"] for x in l], "runsRight": [x["runDir"] for x in r],
                                         "metrics": _pair_rows(l, r)}
        pairs[f"{left}-{right}"] = {"title": title, "left": left, "right": right, "scenarios": by_scenario}

    safety = []
    for run in runs:
        if not run["executed"]:
            continue
        m, info = run["metrics"], run["info"]
        knowledge_ok = info.get("knowledgeVerified", True)
        safety.append({"runDir": run["runDir"], "variant": run["variant"], "modeVerified": info.get("modeVerified"),
                       "knowledgeVerified": knowledge_ok,
                       "mutationsExecuted": m["mutationsExecuted"], "approvalEvents": m["approvalEvents"],
                       "auditChainOk": m["auditChainOk"], "secretLeakScan": info.get("secretLeakScan"),
                       "ok": bool(info.get("modeVerified")) and bool(knowledge_ok) and not m["mutationsExecuted"] and not m["approvalEvents"]
                             and bool(m["auditChainOk"]) and info.get("secretLeakScan") != "leak"})
    fingerprints = manifest.get("configFingerprintByVariant") or {}
    knowledge_fingerprints = manifest.get("knowledgeFingerprintByVariant") or {}
    knowledge_values = {v for v in (knowledge_fingerprints.get(k) for k in ("C", "D")) if v}
    config = {"identicalAcrossVariants": bool(fingerprints) and len(set(fingerprints.values())) == 1 and len(knowledge_values) <= 1,
              "fingerprints": fingerprints, "knowledgeFingerprints": knowledge_fingerprints}
    drift = []
    for run in runs:
        if run["executed"] and run["info"].get("hostBefore"):
            drift.append({"runDir": run["runDir"], "variant": run["variant"], **run["info"]["hostBefore"]})
    failures = [{"runDir": r["runDir"], "scenario": r["scenario"], "variant": r["variant"], "terminalStatus": r["metrics"]["terminalStatus"],
                 "outcomeClass": r["metrics"]["outcomeClass"]}
                for r in runs if r["executed"] and r["metrics"]["outcomeClass"] != "completed"]
    missing = [{"runDir": r["runDir"], "scenario": r["scenario"], "variant": r["variant"], "round": r["round"]} for r in runs if not r["executed"]]
    return {"schema": 2, "testDouble": bool(manifest.get("testDouble")), "campaignId": manifest.get("campaignId"),
            "commit": manifest.get("commit"), "model": manifest.get("model"), "knowledge": manifest.get("knowledge"),
            "roundsPerCell": rounds_per_cell, "exploratory": rounds_per_cell < 3, "variantsPresent": present,
            "configuration": config, "scenarios": comparison, "matrix": matrix, "pairs": pairs, "failures": failures, "missingRuns": missing,
            "safety": safety, "hostBeforeEachRun": drift, "notAvailable": [{"metric": n, "limit": t} for n, t in NOT_AVAILABLE],
            "metricClasses": {k: {"class": c, "description": d} for k, (c, d) in METRICS.items()}}


def _cell(stat):
    if not stat:
        return "-"
    return f"{stat['mean']:g}" if stat["n"] == 1 else f"{stat['mean']:g} ({stat['min']:g}..{stat['max']:g})"


def _signed(value, suffix=""):
    return "-" if value is None else f"{value:+g}{suffix}"


HEADLINE_METRICS = ["terminalStatus", "outcomeClass", "wallSeconds", "accountedTokens", "promptTokens", "completionTokens", "reasoningTokens",
                    "modelCalls", "replans", "knowledgeIndexPublished", "knowledgeReadsServed", "knowledgeDistinctResources",
                    "knowledgeReadsContextBudget", "maxContextUsagePercent", "contextOverflows", "timeoutFailures", "protocolErrors",
                    "truncatedResponses", "diagnosticFlags", "answerNumbersNotInEvidence",
                    "autoChecksFailed", "autoFinalized", "autoCountAccuracy", "autoCountsWrongFamily", "autoCountsUnverified",
                    "autoLiveDumpAsCrash", "autoCrashAsLiveDump", "autoKnowledgeRecallListed", "autoKnowledgePrecision",
                    "autoKnowledgeReadsAnswerTurn", "autoKnowledgeCitedNotLoaded"]


def _dispersion(stat):
    if not stat:
        return "-"
    spread = f" sd {stat['stdev']:g}" if stat.get("stdev") is not None else ""
    return f"{stat['mean']:g} ({stat['min']:g}..{stat['max']:g}{spread}, n={stat['n']})"


def render_markdown(report: dict) -> str:
    lines = ["# MAF CLI A/B/C/D benchmark report", ""]
    if report["testDouble"]:
        lines += ["> **TEST DOUBLE - not a measurement.** The CLI was replaced by a scripted stand-in.", ""]
    lines += [f"- Campaign: `{report['campaignId']}`", f"- Commit: `{report['commit']}`",
              f"- Model: `{json.dumps(report['model'], ensure_ascii=False)}`",
              "- Variants: **A** = MAF off / Knowledge off, **B** = MAF on / Knowledge off, **C** = MAF off / Knowledge on, **D** = MAF on / Knowledge on.",
              f"- Variants present: {', '.join(report.get('variantsPresent') or [])}",
              f"- Knowledge pack: `{json.dumps(report.get('knowledge'), ensure_ascii=False)}`",
              f"- Rounds per cell: **{report['roundsPerCell']}**"
              + (" - **exploratory: one or two rounds support no statistical conclusion.**" if report["exploratory"] else ""),
              f"- Effective configuration identical across variants (apart from the MAF and Knowledge switches): **{report['configuration']['identicalAcrossVariants']}**",
              "", "Classes: **C** certain, **E** estimate, **H** heuristic, **Q** qualitative (human, blind). No evidence text is in this report.",
              "Knowledge *available* (index published) is reported apart from knowledge *used* (reads served): offering it proves nothing about use.", ""]
    lines += ["## Failed and missing runs", ""]
    if report["failures"] or report["missingRuns"]:
        lines += ["| run | scenario | variant | status | class |", "|---|---|---|---|---|"]
        lines += [f"| {f['runDir']} | {f['scenario']} | {f['variant']} | {f['terminalStatus']} | {f['outcomeClass']} |" for f in report["failures"]]
        lines += [f"| {m['runDir']} | {m['scenario']} | {m['variant']} | NOT EXECUTED | missing |" for m in report["missingRuns"]]
    else:
        lines += ["None: every planned run was executed and completed."]
    lines += ["", "## Safety and integrity", "", "| run | variant | mode verified | knowledge verified | mutations | approvals | audit chain | secret scan | ok |",
              "|---|---|---|---|---|---|---|---|---|"]
    for row in report["safety"]:
        lines.append(f"| {row['runDir']} | {row['variant']} | {row['modeVerified']} | {row['knowledgeVerified']} | {row['mutationsExecuted']} | "
                     f"{row['approvalEvents']} | {row['auditChainOk']} | {row['secretLeakScan']} | {'yes' if row['ok'] else '**NO**'} |")
    for scenario, cells in report["matrix"].items():
        lines += ["", f"## {scenario}: the four conditions", "", "| metric | " + " | ".join(f"{v} ({d['label']})" for v, d in cells.items()) + " |",
                  "|---|" + "---|" * len(cells)]
        lines.append("| outcome per run | " + " | ".join(", ".join(d["outcomeClass"]) for d in cells.values()) + " |")
        lines.append("| terminal status | " + " | ".join(", ".join(d["terminal"]) for d in cells.values()) + " |")
        for name in HEADLINE_METRICS:
            if name in ("terminalStatus", "outcomeClass"):
                continue
            if all(d["metrics"].get(name) is None for d in cells.values()):
                continue
            lines.append(f"| {name} ({METRICS[name][0]}) | " + " | ".join(_dispersion(d["metrics"].get(name)) for d in cells.values()) + " |")
        lines.append("| knowledge available / used (runs) | " + " | ".join(f"{d['knowledgeAvailableRuns']} / {d['knowledgeUsedRuns']} of {len(d['runs'])}"
                                                                          for d in cells.values()) + " |")
        used = {v: d["knowledgeResources"] for v, d in cells.items() if d["knowledgeResources"]}
        for variant, resources in used.items():
            lines.append("")
            lines.append(f"Knowledge resources read in {variant}: " + ", ".join(f"`{res}` x{n}" for res, n in sorted(resources.items())))
        for variant, d in cells.items():
            statuses = [e["status"] for e in d["evaluation"]]
            lines.append("")
            lines.append(f"Qualitative evaluation (Q), {variant}: " + ", ".join(statuses))
    lines += ["", "## Pairwise comparisons", "",
              "Each pair answers one question. The delta is right minus left on the mean of the runs; with fewer than three rounds it is not an effect.", ""]
    for key, pair in report["pairs"].items():
        lines += [f"### {key}: {pair['title']} ({pair['left']} -> {pair['right']})", ""]
        if not pair["scenarios"]:
            lines += ["No scenario has runs in both conditions.", ""]
            continue
        for scenario, data in pair["scenarios"].items():
            lines += [f"**{scenario}**", "", f"| metric | class | {pair['left']} | {pair['right']} | delta | delta % |", "|---|---|---|---|---|---|"]
            for name in HEADLINE_METRICS:
                row = data["metrics"].get(name)
                if not row or (row["left"] is None and row["right"] is None):
                    continue
                lines.append(f"| {name} | {row['class']} | {_dispersion(row['left'])} | {_dispersion(row['right'])} | {_signed(row['delta'])} | {_signed(row['deltaPct'], '%')} |")
            lines.append("")
    lines += ["## Metrics not available or limited", ""] + [f"- **{n['metric']}** - {n['limit']}" for n in report["notAvailable"]]
    lines += ["", "## Reading rules", "",
              "- A difference smaller than the run-to-run variance of the model is not an effect of MAF or of Knowledge; with fewer than three rounds the variance is unknown.",
              "- `Completed` is a runtime outcome, not a statement that the diagnosis is correct. Judge the blind evaluation sheets, not the status.",
              "- Host load before each run is recorded in the JSON (`hostBeforeEachRun`); the live Windows tools read a real machine whose state can change between runs.",
              "- The heuristic diagnostic flags (class H) point at sentences to read; they decide nothing.",
              "- `auto*` metrics come from the deterministic MAF evaluation (BOPS-0126 P1, `tests/bOps.DiagnosticEvaluation`): counts and classes are"
              " checked against the run's own typed facts, not the machine's true state, and are empty until the evaluator has been run on the campaign.",
              "- BENCH-00 measured the API; this campaign measures the CLI. The two are not interchangeable evidence for BOPS-0122.", ""]
    return "\n".join(lines)


def render_csv(report: dict) -> str:
    buffer = io.StringIO()
    writer = csv.writer(buffer, lineterminator="\n")
    # The historical A/B block first, with its original header, so earlier tooling reads the file as it always did.
    writer.writerow(["scenario", "metric", "class", "A_n", "A_mean", "A_min", "A_max", "B_n", "B_mean", "B_min", "B_max", "delta", "delta_pct"])
    for scenario, data in report["scenarios"].items():
        for name, row in data["metrics"].items():
            a, b = row["A"] or {}, row["B"] or {}
            writer.writerow([scenario, name, row["class"], a.get("n", ""), a.get("mean", ""), a.get("min", ""), a.get("max", ""),
                             b.get("n", ""), b.get("mean", ""), b.get("min", ""), b.get("max", ""),
                             "" if row["delta"] is None else row["delta"], "" if row["deltaPct"] is None else row["deltaPct"]])
    writer.writerow([])
    writer.writerow(["scenario", "variant", "metric", "class", "n", "mean", "min", "max", "stdev"])
    for scenario, cells in report["matrix"].items():
        for variant, data in cells.items():
            for name, stat in data["metrics"].items():
                if stat:
                    writer.writerow([scenario, variant, name, METRICS[name][0], stat["n"], stat["mean"], stat["min"], stat["max"],
                                     "" if stat.get("stdev") is None else stat["stdev"]])
    writer.writerow([])
    writer.writerow(["pair", "scenario", "metric", "class", "left_mean", "right_mean", "delta", "delta_pct"])
    for key, pair in report["pairs"].items():
        for scenario, data in pair["scenarios"].items():
            for name, row in data["metrics"].items():
                writer.writerow([key, scenario, name, row["class"], (row["left"] or {}).get("mean", ""), (row["right"] or {}).get("mean", ""),
                                 "" if row["delta"] is None else row["delta"], "" if row["deltaPct"] is None else row["deltaPct"]])
    return buffer.getvalue()


def console_summary(report: dict) -> list:
    """A compact reading of a campaign for the terminal: what ran, what failed, what the four conditions did, what Knowledge did."""
    lines = [f"Campaign {report['campaignId']}  commit {report['commit']}", f"Model: {json.dumps(report['model'], ensure_ascii=False)}"]
    planned = sum(len(c["runs"]) for cells in report["matrix"].values() for c in cells.values()) + len(report["missingRuns"])
    lines.append(f"Runs executed {planned - len(report['missingRuns'])} of {planned}; failed or not completed {len(report['failures'])}; "
                 f"missing {len(report['missingRuns'])}; rounds per cell {report['roundsPerCell']}"
                 + ("  (EXPLORATORY: fewer than three rounds)" if report["exploratory"] else ""))
    unsafe = [row["runDir"] for row in report["safety"] if not row["ok"]]
    lines.append("Safety/integrity: " + ("all runs ok (mode and knowledge verified, no mutation, no approval, audit chain intact, no secret in evidence)"
                                          if not unsafe else "PROBLEM in " + ", ".join(unsafe)))
    for failure in report["failures"]:
        lines.append(f"  not completed: {failure['runDir']} -> {failure['terminalStatus']} / {failure['outcomeClass']}")
    for missing in report["missingRuns"]:
        lines.append(f"  missing: {missing['runDir']}")
    for scenario, cells in report["matrix"].items():
        lines.append("")
        lines.append(f"{scenario}")
        for variant, data in cells.items():
            def mean(name):
                stat = data["metrics"].get(name)
                return "-" if not stat else f"{stat['mean']:g}" + (f" sd {stat['stdev']:g}" if stat.get("stdev") is not None else "")
            lines.append(f"  {variant} {data['label']:<26} n={len(data['runs'])} outcomes={','.join(data['outcomeClass'])} wall={mean('wallSeconds')}s "
                         f"tokens={mean('accountedTokens')} flags={mean('diagnosticFlags')} knowledge available/used={data['knowledgeAvailableRuns']}/{data['knowledgeUsedRuns']}")
    lines.append("")
    lines.append("Pairs (right minus left, mean of the runs):")
    for key, pair in report["pairs"].items():
        for scenario, data in pair["scenarios"].items():
            row = data["metrics"]
            parts = []
            for name in ("wallSeconds", "accountedTokens", "knowledgeReadsServed", "diagnosticFlags"):
                parts.append(f"{name} {_signed(row[name]['delta'])}")
            lines.append(f"  {key} {pair['title']:<34} {scenario}: " + "; ".join(parts))
    lines.append("")
    lines.append("Qualitative correctness is PENDING until the blind sheets in evaluation/ are filled; Completed is not a verdict on the diagnosis.")
    return lines


def write_report(campaign_dir: pathlib.Path) -> dict:
    manifest, runs = load_campaign(campaign_dir)
    report = compare(manifest, runs)
    out = campaign_dir / "report"
    out.mkdir(exist_ok=True)
    (out / "ab-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (out / "ab-report.csv").write_text(render_csv(report), encoding="utf-8", newline="")
    (out / "ab-report.md").write_text(render_markdown(report), encoding="utf-8")
    # The A/B/C/D reading of the same data under its own name; the historical file names above are kept for earlier tooling.
    (out / "abcd-report.md").write_text(render_markdown(report), encoding="utf-8")
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
