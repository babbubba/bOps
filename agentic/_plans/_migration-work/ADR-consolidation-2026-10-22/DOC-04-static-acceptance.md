# DOC-04 — static acceptance report

Non-normative working artifact of the DOC-04 phase of `agentic/_plans/ADR-consolidation-2026-10-22.md`. It is migration evidence, not current documentation and not a contract; the bootstrap must not load it.

**Verdict: PASS — READY FOR PR REVIEW.** CI, the delivery PR and the operator's final approval are **not** claimed: none of them exists yet.

## 1. Git state and baseline

| | |
|---|---|
| Task | `BOPS-0121` / `DOC-CONSOL-01` — `IN_PROGRESS`, `authorized: true`, `branch` = this branch (unchanged by DOC-04) |
| Branch | `docs/adr-consolidation-2026-10-22` |
| Start of DOC-04 | local was `6f6c870`, strict ancestor of `origin/docs/adr-consolidation-2026-10-22` = `f5a390980f56334c7f2407c8b2bc84ac46f53336`; fast-forwarded with `git merge --ff-only`; working tree clean afterwards |
| Baseline of `main` | `e0e9de7a74b8bb33e07c7b1f8c92ab87832986de`; `git merge-base HEAD origin/main` = baseline (`origin/main` has not moved) |
| Identity | repository-local `Fabio Cavallari <fcavallari@bsoftsolutions.it>`; no `Co-Authored-By` |
| Preflight | GATE-01 and GATE-02 recorded as approved in plan §2/§7.3; DOC-03 report present (CP-03a, CP-03b PASS); ZIP, `MANIFEST.md`, `MANIFEST.csv`, `.sha256` and five `Current` contracts present; `validate_register.py`: 121 tasks, 0 errors |
| PR #124 | not read, fetched or used. The branch holds only the commits of this migration (`git log e0e9de7..HEAD`: 10 commits; the two oldest, `f5ae974` and `6f6c870`, carry the author name `Babbubba` — historical, not rewritten) |

## 2. P5 — readability of the nine long rule paragraphs

Nine paragraphs above 4,000 characters were rewritten as Markdown lists in `02-tools-safety-authority.md` (R20, R21, R22, R25, R27, R28), `04-evidence-and-diagnosis.md` (R14) and `05-interfaces-knowledge-and-hosts.md` (R04, R07):

| Rule | Longest line before → after (chars) |
|---|---|
| C02-R20 | 7,522 → 524 |
| C02-R21 | 4,837 → 579 |
| C02-R22 | 6,059 → 525 |
| C02-R25 | 4,442 → 450 |
| C02-R27 | 9,894 → 493 |
| C02-R28 | 4,331 → 617 |
| C04-R14 | 6,025 → 574 |
| C05-R04 | 4,585 → 527 |
| C05-R07 | 4,538 → 606 |

**Method.** The change is purely structural: line breaks and `- ` list markers only; **no word, number, symbol, punctuation mark or label was added, removed or reordered.** Sentences (split at `. ` before a capital letter, abbreviations such as `incl.` protected) became top-level items; a sentence introduced by a colon in its first 250 characters became a parent item whose `;`-separated clauses are nested items; sentences without such a lead-in were split at `;` into **sibling** items, so coordinate clauses are never made to look subordinate. Semicolons and the final full stop stay at the end of each item. Headings, rule order, metadata bullets (supersedes, decision status, implementation status, evidence) and anchors were not touched. Before editing, the five files were copied to a scratch directory; the baseline for the comparison is the Git blob at `f5a3909`.

**Equivalence checks** (`p5check.py`, Appendix A, run against `f5a3909`):

1. For all 105 rules (21 + 29 + 24 + 18 + 13) the normalized text of each rule section (only line-initial list markers dropped, whitespace collapsed) is identical before and after.
2. The sequence of whitespace-delimited tokens is identical, so no negation, subject, condition, numeric limit, exception or logical connective changed.
3. The preamble of each contract and the ordered list of rule headings are identical; the set of all headings and `<a id>` anchors is identical in each of the five files (27/36/30/24/19 entries, all equal), so no link anchor moved.
4. `Decision status`, `Implementation status`, `Evidence` and `Supersedes` bullets are byte-identical for all rules.
5. Only the nine named rules differ at the byte level; no line above 4,000 characters remains.
6. **Negative control:** deleting the word `never` in C02-R20 made the check fail (exit 1, `DIFF … C02-R20`); the file was restored.

**Semantic check of clauses.** Because the token sequence is identical, the semantic risk is limited to *scope by nesting*. All 27 parent items were reviewed (lead-in line and last child): each parent is a sentence-initial lead-in ending in a colon written by the original author and the nested items are exactly its `;`-separated continuation up to the sentence's full stop. Where a sentence had no colon lead-in, its clauses were emitted as siblings. **Differences found: none.** Residual note: whether the original author meant the last clause of a colon sentence to belong to the colon list is expressed only by the original punctuation, which was preserved.

No new consolidation, no new technical specification extracted, no rule status changed.

## 3. CP-03a — independent re-verification of the ZIP: PASS

Re-run from versioned files only. `cp03a.py` / `cp03b.py` are **not versioned**; they were re-created in a scratch directory outside the repository from the fenced `python` blocks of Appendix A/B of `DOC-03-verification-report.md` (extractor reproduced in that report) and run with `python3 -I` at the repository root. Nothing from the DOC-03 session was reused.

Results (12 checks, 0 failures): exactly 57 entries; no duplicates; path set = the 55 baseline ADR files + `agentic/01-architecture-rules.md` + `agentic/06-decisions.md`; no metadata or extra entry; `testzip` clean; every entry identical to `git cat-file blob e0e9de7…:<path>`; `MANIFEST.csv` 57 rows with the same path set; `MANIFEST.md` 57 rows consistent with the CSV; test extraction into an empty directory byte-identical for all 57; sidecar SHA-256 matches the archive (`sha256sum -c`: `legacy-adr-and-decisions-2026-10-22.zip: OK`); manifests contain neither the archive hash nor each other's or their own hash; consistent with the DOC-02 ledger (SHA-256 and status) and with the 55 rows of `DOC-02-id-coverage.tsv`.

Additional DOC-04 check: reading the ADRs inside the ZIP, exactly seven have status Proposed — 0050, 0053, 0054, 0055, 0056, 0058, 0059 — the same seven listed as **Proposed** in `docs/architecture/adr/README.md`; none was promoted. The ZIP was not regenerated.

## 4. CP-03b — identifiers and links: PASS

`cp03b.py` re-run on the final tree (exit 0; negative control `NEG=1`: exit 1).

| Item | Result |
|---|---|
| Index rows with existing destination | ADR README 55; `01` 33 (A/B/C/D); `03` 11 (S); `06` 50 (D-ID); 0 destination problems |
| Occurrences of ADR/D-ID/A–D/S identifiers checked | 3,070 over 133 unique identifiers (code 1,917; current docs 227; historical docs 926) |
| Resolved before and after | 3,066 |
| **Newly unresolved identifiers** | **0** |
| Pre-existing unresolvable identifiers | 4 (`ADR-0003/0004/0007/0008` in `agentic/obsolete/plans/piano-bops.md:1170-1175`, never existed) |
| Pre-existing path-style citation to a path that never existed | 1 (`src/core/bOps.Abstractions/Audit.cs:673` → `agentic/architecture/adr/0013`; code, separate task) |
| Markdown links, current docs | 73 files, **0 broken** (0 at baseline); 0 newly broken; 0 broken in files added by the migration |
| Historical documents | 154 files; 37 links to retired ADR files left unchanged on purpose (G1-06; the identifiers resolve through the indexes); no other new break |
| Contract anchors after P5 | unchanged (section 2, check 3); links into the contracts still resolve |

## 5. CP-04 — documentation smoke test of the new bootstrap

Method: a static trace of what the routers and the bootstrap instruct, plus a mechanical scan of the files on each path for links to forbidden material (`adr/`, `06-decisions`, `01-architecture-rules`, `03-security-rules`, `architecture/archive`, `_migration-work`, `handoff-archive`, `obsolete`). No task was created in the register.

### Scenario A — ordinary task: implement/fix a Windows/Linux diagnostic tool

| # | Read | Why | Applicable rules |
|---|---|---|---|
| 1 | `CLAUDE.md` (or `AGENTS.md`) | Router; always-rules; identity; "Start here" → bootstrap | The always-rules mirror C02-R01, R09, R11, R19 |
| 2 | `agentic/00-bootstrap.md` | Single precedence chain; reading rules; stop conditions | — |
| 3 | `validate_register.py --summary`, `HANDOFF.md`, the task entry in `register.json` (by `BOPS-NNNN`, `authorized: true`, dependencies `DONE`), its packet and plan | Identify the authorized task | PROCESS.md only if a status changes |
| 4 | **Contract 02** | Bootstrap: "always before adding or changing a tool" | C02-R01 (core names no tool), R02 (serializable boundary), R03/R04 (packages, host services), R05 (risk/policy), R06 (manifest, validation, no clamping), R09 (verification; a `Read` tool needs none), R10 (timeout), R11 (output is data), R16 (path policy, if files), R19/R20 (no generic execution; fixed process invocations; bounded network/process probes), R28/R29 (prerequisite readiness, registry/probing) |
| 5 | **Contract 04**, only if the output becomes diagnostic evidence | Routing row "Evidence, diagnosis, disclosure" | C04-R06 (typed results, completeness), R08/R09 (filtering, result contracts), R17 (output is data) |
| 6 | `02-coding-standards.md`, `04-testing-rules.md` | Level-2 engineering rules for C# and tests | Error model, forbidden patterns, TDD on the core, real targets for packages |
| 7 | The technical guide named in Contract 02 §4 (`docs/process-diagnostics.md`, `docs/network-diagnostics.md`, `docs/windows-dump-analysis.md`, …) | Only the area the task touches | Subordinate to the contracts |

Contradictions found: none between these files. **Not loaded:** the 55 ADRs, the ZIP and manifests, `agentic/06-decisions.md`, `agentic/01-architecture-rules.md`, `agentic/03-security-rules.md`, the ADR README, `agentic/obsolete/`, `docs/handoff-archive/`, `_migration-work/`, `07-plan-corrections.md`, Contracts 01, 03 and 05 (unless the task touches models, task state/resume or hosts), `PROCESS.md`. Size of what is read: routers and bootstrap ≈ 15 KB, Contract 02 ≈ 116 KB, Contract 04 ≈ 62 KB (conditional), engineering rules ≈ 15 KB, one guide ≈ 10 KB, against 394 KB for all five contracts. The scan of the files on this path found no link to forbidden material; the only mentions are the bootstrap's own "do not read" list and the *Sources* paragraph of each contract (provenance text, not an instruction to read). An index is opened only to resolve an identifier actually met in code or an old document.

### Scenario B — architectural change: "add a `shell.run` tool for advanced diagnostics" (a generic execution tool; the same path applies to relaxing Critical-never-executable or letting the core name a tool)

1. `CLAUDE.md` rule 2 ("No generic execution tool") and the bootstrap. Precedence level 1 says the contracts are the only source of approved structural decisions and safety invariants.
2. The bootstrap routing row sends tool/safety work to **Contract 02**. The pertinent rule is **C02-R19** (maps S1; *Decision status* Accepted; *Implementation status* Implemented, enforced by `NoGenericExecutionToolTests.cs:23`); related: C02-R05 for the risk model. C02-R19 forbids the tool "not behind a flag, not behind Critical, not behind approval, under any name".
3. The agent cannot change this authority: bootstrap *Stop and ask when* — "the change would alter a structural decision or a safety invariant. That is a change to a contract and needs operator approval; do not write an ADR and do not edit a contract as part of a feature task". `CLAUDE.md` ("A structural decision is a change to a current contract and needs the operator") and `05-workflow.md` (structural decisions are operator-approved contract changes) say the same.
4. Approval needed: an explicit operator decision changing Contract 02; for a Proposed topic (D1, D4, D5) the pending decision stays pending. The agent stops, names the rule (C02-R19 / S1), says what the task needed and proposes the alternative the rule itself gives (a new typed tool with a manifest). It touches nothing before the answer.

Files read in B: `CLAUDE.md`, bootstrap, register/HANDOFF, Contract 02 (the rule). No ADR, ZIP, `06`, index or migration artifact is needed to reach the stop.

**Pass conditions:** neither scenario requires the 55 ADRs, the ZIP, `06-decisions.md`, the historical indexes, the migration artifacts, or all five contracts. **PASS.**

**Consistency of `CLAUDE.md`, `AGENTS.md`, bootstrap, `PROCESS.md`, roadmap and contracts.** Same precedence (contracts first), same task rule (`authorized: true`, dependencies `DONE`, `Refs` never `Closes`), same stop rule, same "ignore history" rule. Three demonstrated problems were corrected (section 6); one is left for the operator (section 9).

## 6. Uniqueness of authority

| Check | Result |
|---|---|
| Exactly five current contracts | `docs/architecture/current/` holds exactly `01`…`05`, each `Status: Current`; rule counts 21/29/24/18/13 = 105; no other file declares itself `Current` |
| `01`, `03`, `06`, ADR README non-normative | each opens with "Non-normative compatibility index … no rule text and no authority"; `03` carries topic labels only |
| Bootstrap is the only precedence chain | `CLAUDE.md` and `AGENTS.md` route to it and restate none (`CLAUDE.md` wording corrected, below); `agentic/README.md` is a folder index without a reading order |
| Technical guides create no decisions | the two files in `docs/architecture/reference/` state "technical reference, not a decision register … the contract wins"; the `know-*`, `arch-*` and `af-turn-*` reports are evidence documents ("adds no decision") |
| Migration artifacts outside normal reads | bootstrap forbids `_migration-work/`, archive, handoff archive and obsolete; no file on the routes of section 5 links to them |
| Proposed ADRs stay Proposed | seven Proposed ADRs unchanged in the ADR README and the ZIP; `register.json` decisions D1, D4, D5 remain `PENDING` (register not modified by DOC-04) |
| C05-R13 limited to M1 | rule heading and the contract scope note limit Knowledge to Discovery M1 development and testing (ADR-0057, D-049); the rest is Proposed and non-binding |
| S1–S11 and safety invariants | invariant map in Contract 02 lists S1…S11 (11 rows); rule metadata and text byte-identical to `f5a3909` apart from list markers |
| D1, D4, D5 not closed implicitly | stated open in the ADR README, `06`, the contracts' pending sections and the register |

**Corrections made (only demonstrated conflicts):**

1. `CLAUDE.md`: the sentence "only a router and adds no normative layer" contradicted the rules restated below it; it now says the file adds no architectural authority and that the binding text is in the contracts, bootstrap and `PROCESS.md`.
2. `CLAUDE.md`: "Details: hardening plan §19" pointed from the entry router to a closed plan. The PowerShell variant of the `gh` token handling exists only there, so the information is kept as a pointer, now labelled provenance only with the statement that `CLAUDE.md` prevails.
3. `agentic/07-plan-corrections.md` named `01-architecture-rules.md` and `06-decisions.md` (now indexes) as the documents that supersede the archived plan; it now names the current contracts and the roadmap.
4. `agentic/_plans/2026-09-16-consolidated-roadmap.md`: two forward-looking sentences said that vault cryptography and Coordinator packaging "require an ADR"; the bootstrap forbids writing ADRs. They now say "an operator-approved change to a current contract (ADRs are no longer written)". Past-tense mentions of ADRs in the roadmap are history and were left.

## 7. Active links

Covered by CP-03b (section 4): 73 current Markdown files, every file link and `#anchor` resolved, 0 broken, 0 new. Links to retired ADR files exist only in historical documents (closed plans, task packets, handoff archive; 37) and are intentionally left; their identifiers resolve through the four indexes. The residual `ADR-NNNN` mentions in current technical guides (e.g. `docs/agents/delegation-policy.md`: "ADR-0044") are identifiers, not links, and resolve through the ADR README.

## 8. Governance

| Check | Result |
|---|---|
| `validate_register.py` | 121 tasks, 0 errors (before and after the DOC-04 edits) |
| BOPS-0121 | `IN_PROGRESS`, `authorized: true`; not moved to `VALIDATION`; no `CI_VERIFIED`/`OPERATOR_GATE`/`DONE` assigned; `register.json` **not modified by DOC-04** |
| Issue #125 / Project #6 | `sync_pending` unchanged; no remote write, no PR, no merge |
| Whole migration vs baseline `e0e9de7`, forbidden paths | **no change** to `src/`, `tests/`, `scripts/`, `.github/`, `web/`, `*.csproj`, `*.props`, `*.targets`, `*.sln(x)`, `package*.json`, `nuget.config`, `global.json`, `appsettings*.json`, Dockerfiles, compose, YAML (CI) files |
| Non-Markdown files changed vs baseline | only migration data: five DOC-01/02 `.tsv`, `MANIFEST.csv`, the ZIP and its `.sha256`, and `agentic/_tasks/register.json` (BOPS-0121 registration, GATE records) |
| Deleted files | 55 (`git diff --diff-filter=D`), exactly the 55 ADR entries of the ZIP and exactly the 55 files of `docs/architecture/adr/` at baseline; 0 renames; no other original deleted (`01`, `03`, `06` kept as indexes) |
| `PROCESS.md` | changed in the whole migration only in the "Architectural decisions" row of §1 (DOC-03); DOC-04 did not touch it |
| DOC-04-only diff vs `f5a3909` | see section 10 |

## 9. Pre-existing anomalies and residual items (not fixed here)

- `docs/benchmarks/bench00-maf-compatibility-report.md` does not exist; it was not reconstructed (G1-10, noted in the manifest).
- Four ADR identifiers in `agentic/obsolete/plans/piano-bops.md` never existed; `Audit.cs:673` cites a path that never existed (code; separate task).
- ADR-0057's link to `know-m1-b-fix01…` was already broken inside the ADR; the ADR is archived unmodified.
- `agentic/_tasks/PROCESS.md` line 6 still says that changing "an accepted ADR" is a normative change. Editing that line is outside the authorized `PROCESS.md` change; it needs the operator. The sentence is harmless (ADRs are archived; the bootstrap and `CLAUDE.md` name current contracts) but stale.
- `agentic/_tasks/README.md` (closed hardening packet map) and the closed plans keep their ADR file links and wording as history.
- BOPS-0120 is still `VALIDATION` in the register although PR #123 is in the baseline (C9); plan MAF/Knowledge status table vs `PROCESS.md` (C10); no CI check of documentation links (C11); mixed Italian/English sources (C13). All outside this migration.
- The first two commits of the branch carry the author name `Babbubba`; per `CLAUDE.md` history is not rewritten.
- Implementation-evidence `file:line` anchors in the contracts were not re-verified (no code claim changed).

## 10. Files modified by DOC-04

Relative to `f5a3909`:

- `docs/architecture/current/02-tools-safety-authority.md`, `docs/architecture/current/04-evidence-and-diagnosis.md`, `docs/architecture/current/05-interfaces-knowledge-and-hosts.md` — P5 list formatting only.
- `CLAUDE.md` — two wording corrections (section 6).
- `agentic/07-plan-corrections.md` — pointer to the current contracts.
- `agentic/_plans/2026-09-16-consolidated-roadmap.md` — two sentences on ADRs.
- `agentic/_plans/ADR-consolidation-2026-10-22.md` — status, §0 rewritten for DOC-04, §8 checkboxes.
- `agentic/_plans/_migration-work/ADR-consolidation-2026-10-22/DOC-03-verification-report.md` — reproduction instructions only (the scripts are not versioned and are obtained from the appendices; Appendices A/B and results untouched).
- `HANDOFF.md` — DOC-04 done, next step operator review (84 lines).
- `agentic/_plans/_migration-work/ADR-consolidation-2026-10-22/DOC-04-static-acceptance.md` — this report.

## 11. Residual risks

- Contract 02 is ≈ 116 KB; every tool task loads it whole. This is the cost of one authoritative safety contract; splitting it is a structural change for the operator.
- P5 improves readability; some items are still long (up to ≈ 620 characters) because the original sentences are dense. Tables or sub-clauses would have required adding words and were avoided.
- The reference checks are static: they prove that identifiers resolve and links work, not that every contract clause matches the running code.
- CI, the PR review and the operator's approval of the contracts as written have not happened. The cp scripts are not versioned and live only in the appendices of the reports.
- D1, D4, D5 remain open by design; BOPS-0074/0075 stay blocked until D1.
- Issue #125 / Project #6 are not synchronized.

## 12. Verdict

**PASS.** CP-03a PASS, CP-03b PASS, CP-04 PASS (both scenarios), P5 equivalence PASS (105 rules compared, 9 reformatted, 0 differences), uniqueness of authority PASS, governance PASS (register valid, BOPS-0121 unchanged, no forbidden path touched). Next step: operator review; the PR requires a separate explicit authorization.

## Appendix A — `p5check.py`

Not versioned. Create it from this block in a scratch directory and run it at the repository root: `python3 -I p5check.py f5a3909`. A deliberate one-word change in a rule makes it exit 1.

```python
"""P5 equivalence check. Usage (repo root): python3 -I p5check.py <base-rev>
Compares each rule section of docs/architecture/current/0N-*.md at <base-rev> with the working tree."""
import re,subprocess,sys,glob
base=sys.argv[1]
def sections(t):
    parts=re.split(r'(?m)^(### C\d\d-R\d+\b.*)$',t)
    head=parts[0]; secs=[(parts[i],parts[i+1]) for i in range(1,len(parts),2)]
    return head,secs
def strip_md(t):
    t=re.sub(r'(?m)^\s*[-*]\s+','',t)      # list markers only (line-initial)
    return re.sub(r'\s+',' ',t).strip()
bad=0; tot=0; reformatted=[]
for f in sorted(glob.glob('docs/architecture/current/0*.md')):
    old=subprocess.check_output(['git','show',f'{base}:{f}'],text=True); new=open(f).read()
    ho,so=sections(old); hn,sn=sections(new)
    ok=(strip_md(ho)==strip_md(hn) and [h for h,_ in so]==[h for h,_ in sn])   # same preamble, same rule IDs in same order
    for (h1,b1),(h2,b2) in zip(so,sn):
        tot+=1
        same_raw=(b1==b2)
        eq=strip_md(b1)==strip_md(b2)
        # token-level: identical sequence of words, numbers, operators once markdown list syntax is removed
        t1=re.findall(r'\S+',strip_md(b1)); t2=re.findall(r'\S+',strip_md(b2))
        if not (eq and t1==t2): ok=False; bad+=1; print('DIFF',f,h1[:40])
        if not same_raw: reformatted.append((h1.split(' — ')[0].replace('### ',''),max(map(len,b1.split('\n'))),max(map(len,b2.split('\n')))))
    if not ok: bad+=1
    print(('OK  ' if ok else 'FAIL'),f,len(sn),'rules')
print('rules compared',tot,'reformatted',len(reformatted),'failures',bad)
for r in reformatted: print('  ',r[0],'longest line',r[1],'->',r[2])
print('lines >4000 chars remaining:',sum(1 for f in glob.glob('docs/architecture/current/0*.md') for l in open(f) if len(l)>4000))
sys.exit(1 if bad else 0)
```

## Appendix B — one-off formatter `fmt.py`

Not versioned; it was run once (`apply`) to produce the list formatting. It asserts internally that its output equals the input after list markers are removed.

```python
import re,sys,glob,json
ABBR=('incl','e.g','i.e','vs','approx','etc','cf','no','resp')
def depth0_split(s,seps):
    """split s at positions where predicate matches at paren/backtick depth 0; returns list"""
    out=[];cur='';d=0;bt=False;i=0
    while i<len(s):
        c=s[i]
        if c=='`': bt=not bt
        if not bt:
            if c in '([': d+=1
            elif c in ')]': d=max(0,d-1)
        cur+=c
        if d==0 and not bt and seps(s,i):
            out.append(cur);cur=''
        i+=1
    if cur: out.append(cur)
    return [x.strip() for x in out if x.strip()]
def sent_end(s,i):
    if s[i] not in '.!?': return False
    if i+2>=len(s) or s[i+1]!=' ': return False
    nxt=s[i+2]
    if not (nxt.isupper() or nxt in '(`*'): return False
    m=re.search(r'([A-Za-z.]+)$',s[:i])
    w=m.group(1).lower().rstrip('.') if m else ''
    if w.split('.')[-1] in ABBR or w in ABBR: return False
    if len(w)==1: return False
    return True
def semi(s,i): return s[i]==';' and i+1<len(s) and s[i+1]==' '
def colon_lead(s):
    parts=depth0_split(s,lambda t,i:t[i]==':' and i+1<len(t) and t[i+1]==' ')
    if len(parts)>1 and len(parts[0])<=250 and len(s)>500: return parts[0],s[len(parts[0])+1:].strip()
    return None
def fmt(p):
    sents=depth0_split(p,sent_end)
    lines=[]
    for s in sents:
        if len(s)<=450:
            lines.append('- '+s); continue
        cl=colon_lead(s)
        if cl:
            lead,rest=cl; kids=depth0_split(rest,semi)
        else:
            kids=depth0_split(s,semi)
            for k in kids: lines.append('- '+k)   # coordinate clauses of one sentence stay siblings
            continue
        if not kids: lines.append('- '+s); continue
        lines.append('- '+lead)
        for k in kids: lines.append('  - '+k)
    return '\n'.join(lines)
def norm(t):
    t=re.sub(r'(?m)^\s*- ','',t)
    return re.sub(r'\s+',' ',t).strip()
if __name__=='__main__':
    mode=sys.argv[1]
    manifest=[]
    for f in sorted(glob.glob('docs/architecture/current/0*.md')):
        L=open(f).read().split('\n'); ch=False
        out=[]
        for i,l in enumerate(L):
            if len(l)>4000:
                new=fmt(l)
                assert norm(new)==norm(l),(f,i)
                manifest.append((f,i+1,len(l),new.count('\n')+1))
                out.append(new); ch=True
            else: out.append(l)
        if ch and mode=='apply': open(f,'w').write('\n'.join(out))
    print(json.dumps(manifest))
```
