# DOC-02 — Mandatory checks CP-02a…CP-02e

Non-normative working artifact of the DOC-02 phase of `agentic/_plans/ADR-consolidation-2026-10-22.md`. It is migration evidence, not current documentation, not a contract, and must not be loaded by the bootstrap.

Scripts: the mechanical checks were run by session-local scripts kept in the session scratchpad (plan §7.2.2: they are not added to `scripts/`). Their method is stated in each section; the numbers below are their output on the final tree.

| Check | Result |
|---|---|
| CP-02a | PASS |
| CP-02b | PASS |
| CP-02c | PASS |
| CP-02d | PASS |
| CP-02e | PASS |


## CP-02a — Full reading (55/55) and section coverage

Method: parse `DOC-02-read-ledger.tsv`; for each ADR check read_complete = yes, that `ranges_read` covers lines 1..N with no gap, and that the SHA-256 equals the SHA-256 of the file on disk now; then check that every first- and second-level heading of every ADR (outside code fences) falls inside the line range of a matrix row.

- ledger rows: 55 (expected 55); incomplete or with gaps: none; SHA-256 mismatches against the files on disk: none
- ADR headings of level 1–2 checked: 401; without a matrix row: 0 
- matrix rows from ADRs: 818; ADRs with rows: 55

**Result: PASS**

## CP-02b — Destination of every prescription

Mechanical method: every line of the 55 ADRs and of `agentic/01`, `03`, `06`, `00-project-spec`, `07` containing one of the keywords of plan §7.2.2 is written to `DOC-02-normative-extraction.tsv` and linked to the matrix row whose line range contains it.

- extracted statements: 2619; linked to a row with a disposition: 2619; orphans: 0
- by disposition: {'CONTRACT': 1954, 'HISTORICAL': 389, 'TECH-REF': 67, 'PENDING': 209}
- rows with an empty disposition/destination/rationale: 0 ; destinations that do not exist (rule id or file): 0 

Semantic method: (1) each `CONTRACT` row is mapped to rule(s) whose text was written from the full row text; (2) a token check compares identifiers, dotted names, code spans and measured values of the ADR lines of each CONTRACT row with the text of the mapped rules, then searches the missing tokens in the repository (`DOC-02-uncovered-normative-content.md`, parts B–C); (3) the TECH-REF rows were compared with their destination guides before the sections were written (`techchk`).

- CONTRACT ADR rows with identifiable tokens: 502; tokens 6892; not in the mapped rules 3630; absent from rules and repository 361; unexplained after re-check 0 (77 residual tokens classified in part C; 1 gap closed in `docs/system-events.md`)
- differences between source and destination are recorded in `rationale` of the row and in `DOC-02-effective-rules.md` (chains, REVIEW entries); strengthening or weakening is not silent: B-section type shapes of `01` are deliberately not duplicated (plan §3.3), the properties are.

**Result: PASS**

## CP-02c — Identifiers

Method: IDs are extracted mechanically from the original files (headings `### An —`/`### Bn —` and the numbered items of §C and the §D heading of `01`; `## Sn —` of `03`; `### D-nnn —` of `06`; the file names of the ADRs) and compared with `DOC-02-id-coverage.tsv`, which is written from the matrix.

- extracted IDs: 149 = {'A': 13, 'B': 10, 'C': 9, 'D': 1, 'S': 11, 'D-nnn': 50, 'ADR': 55}
- duplicated IDs in the extraction: none; duplicated in the coverage file: none; missing from the coverage: none; in the coverage but not required: ['P1-P7']; without a destination: none
- the ADR numbers 0003, 0004, 0007, 0008, 0009 and 0010 do not exist as files; the set is 55 ADRs

**Result: PASS**

## CP-02d — Dense ADRs

Method: for the 12 ADRs of plan §7.2.3, every heading of level 1–4 outside code fences is listed in `DOC-02-dense-adr-coverage.md` with the row, disposition and destination covering its line.

| ADR | headings | without a disposition | matrix rows |
|---|---:|---:|---:|
| 0042 | 63 | 0 | 39 |
| 0044 | 40 | 0 | 30 |
| 0057 | 68 | 0 | 45 |
| 0043 | 47 | 0 | 26 |
| 0051 | 56 | 0 | 45 |
| 0032 | 38 | 0 | 33 |
| 0045 | 23 | 0 | 18 |
| 0052 | 52 | 0 | 35 |
| 0041 | 21 | 0 | 21 |
| 0014 | 12 | 0 | 29 |
| 0040 | 29 | 0 | 22 |
| 0037 | 16 | 0 | 17 |

- TECH-REF rows of dense ADRs whose technical-detail seat does not exist: none

**Result: PASS**

## CP-02e — Honesty and safety

**(1) No rule from a Proposed ADR in the binding sections; 0057 and 0060 as in plan §3.4.**

- binding rules whose decision status does not start with Accepted/Operator decision: none
- CONTRACT rows sourced from the seven Proposed ADRs (0050, 0053, 0054, 0055, 0056, 0058, 0059): none
- CONTRACT rows from ADR-0057: [('A0057-040', ['C02-R12'])]; from ADR-0060: 9 rows → ['C03-R24']
- PENDING rows: 88; every one has a `C0n#pending-Dn` destination: True

**(2) Every `Implemented` claim has verified `file:line` evidence.**

- implementation status of the 104 rules: {'Implemented': 99, 'Partially implemented': 2, 'Not implemented / target in the product composition': 1, 'Not verified': 2}
- evidence anchors: 211, each located with a fixed-string search in the file at the time of generation (a miss aborts the generation); Implemented/Partially rules without any anchor: none

**(3) Security invariants present at least at original strength.**

Each invariant is checked against the text of the one rule that carries it (not against the whole contract).

| Invariant | Rule | Found | Matching text |
|---|---|---|---|
| S1 no generic execution tool | C02-R19 | yes | cution tool; fixed process invocations — There is no shell.run, no system.exec, no powershell.invoke and no 'escape hatch for ad |
| S2 arguments validated before execution | C02-R06 | yes | d blank or duplicate parameter names. The runtime validates a ToolCallRequest against the manifest BEFORE policy, approval or execution — required par |
| S3 policy fails closed | C02-R05 | yes |  is a declarative model, not a command blacklist. Policy fails closed: an unknown risk level, a missing policy entry, a |
| Critical is never executable | C02-R05 | yes | dentified resolves to Forbidden, never Automatic. Critical is Forbidden unconditionally and cannot be configured otherwise: PolicyConfigL |
| S4 verification fails closed (Inconclusive/Refuted are not success) | C02-R09 | yes | firmed. VerificationStatus is Confirmed, Refuted, Inconclusive or NotApplicable; Inconclusive and NotApplicable  |
| S5 tool output is data, never instruction | C02-R11 | yes | Tool output is data, never instruction — Any text originating outside the runtime — cont |
| S6 secrets never reach a log (redaction) | C02-R13 | yes | arameter.Sensitive marks an argument; the runtime redacts marked arguments before the audit event is const |
| S7 every action runs under a timeout | C02-R10 | yes | Every action runs under a timeout — Every ExecuteAsync is invoked with a linked Can |
| S8 in-process packages are trusted code, not a sandbox | C02-R15 | yes | e and local publisher trust establish provenance (not a sandbox); AssemblyLoadContext, manifest capabilities, dec |
| S9 audit complete: denials, rejections, unknown tools are events | C02-R14 | yes | nied calls are the ones an investigator wants, so denials, rejections and unknown tool names are events, not 'continue' statements (AuthorizationKind  |
| S10 least privilege for the host process | C02-R15 | yes | In-process packages are trusted code; least privilege — AssemblyLoadContext isolation isolates dependen |
| S11 path policy checked immediately before the operation | C02-R16 | yes | quested path against the configured glob patterns immediately before touching the filesystem, on the fully resolved path — after symlink resol |
| human approval requires a human identity | C02-R08 | yes |  policy marks Approval requires a decision from a human identity: IApprovalProvider returns an ApprovalDecision ca |
| entitlement re-evaluated immediately before execution | C02-R22 | yes |  (a current matching Allowed decision is required immediately before the operation executes); a missing assignment for an operation meant to  |
| delegation reduces privileges (child = parent ∩ role ∩ request) | C02-R24 | yes | adline), depth and the originating ActorIdentity. Child = parent ∩ role profile ∩ request per dimension (set intersection, minimum risk and |
| SSRF: every resolved address validated at connection time | C02-R18 | yes | onnection resolves the host through IDnsResolver, validates EVERY returned address against IpAddressPolicy (by default denying loopb |
| ambiguous mutation never automatically re-executed | C03-R21 | yes |  proof of non-execution; an ambiguous mutation is never automatically re-executed; a mutation proven or accepted as applied is neve |

- S1–S11 map to: [('S1', 'C02-R19'), ('S2', 'C02-R06'), ('S3', 'C02-R05'), ('S4', 'C02-R09'), ('S5', 'C02-R11,C02-R12'), ('S6', 'C02-R13'), ('S7', 'C02-R10'), ('S8', 'C02-R15'), ('S9', 'C02-R14'), ('S10', 'C02-R15'), ('S11', 'C02-R16')]
- strength: each S row was token-checked against its rules (identifiers, quoted markers, numbers) with the same tool as CP-02b; the original sentences with *never/always/only/must* are in the normative extraction linked to the same rules.

**(4) No invented rule.**

- rules without a source row in the matrix: none; every rule lists its historical IDs (derived from the matrix rows) under *Supersedes / consolidates* and its sources under *Sources*

**Result: PASS** (1: True, 2: True, 3: True, 4: True)

## Additional checks (structure, references, scope)

| Contract | Status line | Section order | Rules | IDs sequential | Technical references exist |
|---|---|---|---:|---|---|
| 01-maf-agent-infrastructure.md | yes | ok | 21 | yes | yes |
| 02-tools-safety-authority.md | yes | ok | 29 | yes | yes |
| 03-task-state-resume-mutations.md | yes | ok | 24 | yes | yes |
| 04-evidence-and-diagnosis.md | yes | ok | 18 | yes | yes |
| 05-interfaces-knowledge-and-hosts.md | yes | ok | 12 | yes | yes |

- changed or new paths: 27; outside the permitted set of plan §7.2: none; ADR, `01`/`03`/`06`, bootstrap/router, code, tests, CI or scripts touched: none
- permitted guide edits (G1-04): docs/agents/delegation-policy.md, docs/governed-recursive-deletion.md, docs/knowledge/local-agent-pilot.md, docs/network-diagnostics.md, docs/operator-configuration.md, docs/process-diagnostics.md, docs/system-events.md, docs/system-maintenance.md, docs/system-stability.md, docs/windows-dump-analysis.md
- `python scripts/governance/validate_register.py` → exit 0: `register: 121 tasks, 0 error(s)`

**Result: PASS**
