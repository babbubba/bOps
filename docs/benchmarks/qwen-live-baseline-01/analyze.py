"""QWEN-LIVE-BASELINE-01 analysis: deterministic metrics per run, from the artefacts of the run only.
Usage: python -I analyze.py <campaign root>   ->  <root>/analysis/<run>.json, <root>/analysis/summary.json, metrics.md
Reads tasks.db (persisted TaskState), http/*.json (bytes on the wire), audit.jsonl, harness-result.json. Judges nothing interpretive."""
import collections, glob, hashlib, json, os, re, sqlite3, statistics, sys

root = sys.argv[1]
out = os.path.join(root, 'analysis')
os.makedirs(out, exist_ok=True)


def phase_of(kind, rev, req):
    """Phase of one persisted model call. kind: 'plan' or 'step'."""
    if kind == 'plan':
        return 'planning' if rev == 0 else 'replanning'
    last = req['messages'][-1]
    text = str(last.get('content') or '')
    if last['role'] == 'user' and text.startswith(('Final answer not accepted', 'Runtime control instruction')):
        return 'rejected-final-continuation'
    if last['role'] == 'user' and text.startswith('Your last reply was tool-call or control-protocol'):
        return 'protocol-markup-retry'
    if last['role'] == 'tool' and '[skill-data' in text:
        return 'step-after-knowledge-read'
    if last['role'] == 'tool' and text.startswith('Not executed:'):
        return 'step-after-deferred-call-notice'
    if last['role'] == 'tool' and text.startswith('<<<BOPS_TOOL_OUTPUT'):
        return 'step-after-tool-result'
    if last['role'] == 'tool':
        return 'step-after-runtime-reply(' + text[:40].replace(chr(10), ' ') + ')'
    return 'step'


def run_metrics(rd):
    name = os.path.basename(rd)
    h = json.load(open(os.path.join(rd, 'harness-result.json'), encoding='utf-8'))
    m = {'run': name, 'harness': {k: h[k] for k in ('elapsedSeconds', 'status', 'terminalReason', 'watchdogFired', 'callCapReached',
                                                   'httpRequestsSent', 'harnessException', 'approvalRequests', 'taskId')}}
    # --- wire level (authoritative for tokens and finish_reason)
    wire = []
    for mp in sorted(glob.glob(os.path.join(rd, 'http', '*.meta.json'))):
        i = os.path.basename(mp).split('.')[0]
        meta = json.load(open(mp, encoding='utf-8'))
        rp = os.path.join(rd, 'http', i + '.response.json')
        rq = open(os.path.join(rd, 'http', i + '.request.json'), encoding='utf-8').read()
        e = {'index': int(i), 'elapsedMs': meta['elapsedMs'], 'status': meta['status'], 'transportFailure': meta['transportFailure'],
             'requestChars': len(rq), 'requestSha256': meta['requestSha256']}
        if os.path.exists(rp):
            try:
                r = json.load(open(rp, encoding='utf-8'))
                ch = r['choices'][0]
                msg = ch['message']
                e.update(finishReason=ch.get('finish_reason'), promptTokens=r['usage']['prompt_tokens'], completionTokens=r['usage']['completion_tokens'],
                         reasoningChars=len(msg.get('reasoning_content') or ''), contentChars=len(msg.get('content') or ''),
                         toolCalls=[t['function']['name'] for t in msg.get('tool_calls') or []])
            except Exception as ex:  # keep the failure visible, never drop the call
                e['parseError'] = type(ex).__name__
        wire.append(e)
    # --- persisted state
    db = sqlite3.connect(os.path.join(rd, 'tasks.db'))
    row = db.execute('select state_json from tasks').fetchone()
    st = json.loads(row[0])
    by_sha = {w['requestSha256']: w for w in wire}
    calls = []

    def add(kind, rev, idx, mc):
        req = json.loads(mc['RequestJson'])
        sha = hashlib.sha256(mc['RequestJson'].encode('utf-8')).hexdigest().upper()
        w = by_sha.get(sha)
        calls.append({'phase': phase_of(kind, rev, req), 'where': f'{kind}{rev if kind == "plan" else idx}', 'wireIndex': w and w['index'],
                      'outcome': mc['Outcome'], 'finish': mc['FinishReason'], 'usage': mc['Usage'], 'durationMs': mc['DurationMs'],
                      'attempt': mc.get('ModelAttempt')})

    for p in st['Plans']:
        for mc in p['ModelCalls']:
            add('plan', p['Revision'], None, mc)
    for s in st['Steps']:
        for mc in s['ModelCalls']:
            add('step', None, s['Index'], mc)
    m['plans'] = [{'revision': p['Revision'], 'rationale': p['Rationale'],
                   'steps': [{'tool': x['ExpectedTool'], 'args': x.get('ExpectedArguments'), 'desc': x['Description']} for x in p['Steps']]}
                  for p in st['Plans']]
    steps = []
    for s in st['Steps']:
        tc = s.get('ToolCall')
        obs = s.get('Observation') or ''
        steps.append({'index': s['Index'], 'planRevision': s.get('PlanRevision'), 'plannedStep': s.get('PlannedStepIndex'),
                      'tool': tc and tc['ToolName'], 'args': tc and tc['Arguments'], 'toolNameError': tc and tc.get('ToolNameError'),
                      'argumentsError': tc and tc.get('ArgumentsError'),
                      'outcome': (s.get('Result') or {}).get('Outcome'), 'completeness': (s.get('Result') or {}).get('Completeness'),
                      'observationHead': obs[:300], 'observationChars': len(obs), 'classification': s.get('ExecutionClassification')})
    m['steps'] = steps
    m['finalText'] = next((s['Observation'] for s in reversed(st['Steps']) if not s.get('ToolCall') and s.get('Observation')), None)
    m['status'] = h['status']
    m['terminalReason'] = st.get('TerminalReason')
    m['accounting'] = st.get('Accounting')
    m['replans'] = max(0, len(st['Plans']) - 1)
    # --- audit
    ev = collections.Counter()
    reads = []
    for line in open(os.path.join(rd, 'audit.jsonl'), encoding='utf-8'):
        try:
            e = json.loads(json.loads(line)['EventJson'])
        except Exception:
            continue
        ev[e.get('eventType')] += 1
        if e.get('eventType') in ('knowledgeRead', 'evidenceRead') or 'Read' in str(e.get('eventType')):
            reads.append({k: e.get(k) for k in ('eventType', 'Operation', 'Skill', 'Resource', 'ResultCode', 'Repeat', 'Step', 'Source', 'ReturnedLength') if k in e})
    m['auditEventTypes'] = dict(ev)
    m['readEvents'] = reads
    m['redundantReads'] = sum(1 for r in reads if r.get('Repeat') is True)
    keys = collections.Counter((r.get('eventType'), r.get('Skill'), r.get('Resource'), r.get('Step')) for r in reads)
    m['duplicateReadKeys'] = sum(c - 1 for c in keys.values() if c > 1)
    # --- derived
    m['calls'] = calls
    m['wire'] = wire
    m['llmCallsWire'] = len(wire)
    m['llmCallsByPhase'] = dict(collections.Counter(c['phase'] for c in calls))
    m['promptTokensWire'] = sum(w.get('promptTokens', 0) for w in wire)
    m['completionTokensWire'] = sum(w.get('completionTokens', 0) for w in wire)
    m['finishLength'] = sum(1 for w in wire if w.get('finishReason') == 'length')
    m['failedCalls'] = sum(1 for w in wire if w['status'] != 200 or w.get('parseError') or w['transportFailure'])
    m['prematureFinals'] = sum(1 for s in st['Steps'] if not s.get('ToolCall') and (s.get('Observation') or '').startswith('Final answer not accepted'))
    m['toolCallsRejectedByRuntime'] = sum(1 for s in st['Steps'] if s.get('ToolCall') and (s.get('Observation') or '').startswith('ERROR (validation)'))
    m['llmSeconds'] = round(sum(w['elapsedMs'] for w in wire) / 1000, 1)
    m['replay'] = {'served': sum(1 for c in h['replayLedger'] if c['Verdict'] == 'SERVED'),
                   'unsupported': sum(1 for c in h['replayLedger'] if c['Verdict'] != 'SERVED'), 'ledger': h['replayLedger']}
    return m


runs = sorted(d for d in glob.glob(os.path.join(root, 'runs', '*')) if os.path.exists(os.path.join(d, 'harness-result.json')))
summary = {}
for rd in runs:
    try:
        m = run_metrics(rd)
    except Exception as ex:
        m = {'run': os.path.basename(rd), 'analysisError': repr(ex)}
    json.dump(m, open(os.path.join(out, m['run'] + '.json'), 'w', encoding='utf-8'), indent=1, ensure_ascii=False)
    summary[m['run']] = m

def stats(vals):
    return f'median {statistics.median(vals)}, range {min(vals)}-{max(vals)}' if vals else 'n/a'


by_scn = collections.defaultdict(list)
for n, m in summary.items():
    if 'analysisError' not in m:
        by_scn[n.split('-')[0]].append(m)
srows = ['| scenario | runs | LLM calls | prompt+completion tokens | wall seconds | premature finals | replans |', '|---|---|---|---|---|---|---|']
for k, ms in sorted(by_scn.items()):
    srows.append(f"| {k} | {len(ms)} | {stats([x['llmCallsWire'] for x in ms])} | {stats([x['promptTokensWire'] + x['completionTokensWire'] for x in ms])} | "
                 f"{stats([x['harness']['elapsedSeconds'] for x in ms])} | {stats([x['prematureFinals'] for x in ms])} | {stats([x['replans'] for x in ms])} |")
open(os.path.join(out, 'scenario-stats.md'), 'w', encoding='utf-8').write(chr(10).join(srows) + chr(10))
print(chr(10).join(srows))
rows = ['| run | exec status | s | LLM calls | by phase | prompt tok | compl tok | length | failed | replans | prem. finals | tools executed | redundant reads | unsupported replay |', '|' + '---|' * 14]
for n, m in summary.items():
    if 'analysisError' in m:
        rows.append(f'| {n} | ANALYSIS ERROR {m["analysisError"]} |')
        continue
    tools = [s['tool'] for s in m['steps'] if s['tool']]
    rows.append(f"| {n} | {m['status']} | {m['harness']['elapsedSeconds']} | {m['llmCallsWire']} | {m['llmCallsByPhase']} | {m['promptTokensWire']} | "
                f"{m['completionTokensWire']} | {m['finishLength']} | {m['failedCalls']} | {m['replans']} | {m['prematureFinals']} | {tools} | "
                f"{m['redundantReads']}+{m['duplicateReadKeys']} | {m['replay']['unsupported']} |")
open(os.path.join(out, 'metrics.md'), 'w', encoding='utf-8').write('\n'.join(rows) + '\n')
print('\n'.join(rows))
