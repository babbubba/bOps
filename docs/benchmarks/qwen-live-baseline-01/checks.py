"""Deterministic claim checks over the final texts (QWEN-LIVE-BASELINE-01). Judges nothing interpretive; the report does."""
import glob, json, os, re, sys
root = sys.argv[1]
fx = {os.path.basename(p): json.load(open(p, encoding='utf-8')) for p in glob.glob(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'fixtures', 'F*.json'))}
pack = open(os.path.join(root, 'shared', 'packs', 'bsoft.windows-event-runbooks', '1.0.0', 'windows-event-triage', 'references', 'interpretation.md'), encoding='utf-8').read()
res = {}
for p in sorted(glob.glob(os.path.join(root, 'analysis', 'T*-r*.json'))):
    m = json.load(open(p, encoding='utf-8'))
    run = m['run']
    h = json.load(open(os.path.join(root, 'runs', run, 'harness-result.json'), encoding='utf-8'))
    visible = set(h['visibleTools'])
    text = m.get('finalText') or ''
    r = {'finalChars': len(text), 'hasAcceptedAnswer': m['status'] == 'Completed' and bool(text)}
    r['temperatureOrFanNumbersInFinal'] = re.findall(r'\d+(?:\s*-\s*\d+)?\s*(?:°C|rpm|RPM|%)', text)
    named = set(re.findall(r'\b(?:system|network|service|process|storage|identity|fs|web|docker|firewall|scheduler|security)\.[a-z_.]+\b', text))
    r['toolNamesMentioned'] = sorted(named)
    r['toolNamesNotInVisibleSurface'] = sorted(n for n in named if n not in visible)
    r['unsupportedReplayCalls'] = m['replay']['unsupported']
    r['servedReplayCalls'] = m['replay']['served']
    r['knowledgeReadEvents'] = [e.get('Operation') for e in m['readEvents'] if e['eventType'] == 'knowledgeRead']
    r['knowledgeResourceRead'] = any(e.get('Resource') == 'references/interpretation.md' for e in m['readEvents'])
    if run.startswith('T08') and text:
        info = fx['F14-system.info.json']['result']['Output']
        host = re.search(r'^Host: (\S+)', info, re.M).group(1)  # the capture host's name, read from the (private) fixture
        r['systemInfoFactsQuoted'] = {'host': host in text, 'model21N5': '21N5' in text, 'uptimeMatchesFixture': 'Uptime: 0d 1h 30m' in info and '1h 30m' in text}
    if run.startswith('T10') and text:
        pts = [int(x) for x in re.findall(r'punt[io]\s+(\d+)(?:\s*e\s*(\d+))?', text, flags=re.I) for x in x if x]
        r['packPointsCited'] = sorted(set(pts))
        r['packPointsCitedExist'] = all(1 <= x <= 11 for x in pts)
        r['citesPackLinksOnlyFromPack'] = all(u in pack for u in re.findall(r'https://[^)\s]+', text))
    res[run] = r
json.dump(res, open(os.path.join(root, 'analysis', 'deterministic-checks.json'), 'w', encoding='utf-8'), indent=1, ensure_ascii=False)
for k, v in res.items():
    print(k, {a: b for a, b in v.items() if a not in ('toolNamesMentioned',)})
