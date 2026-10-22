# bOps — Razionalizzazione documentale e consolidamento architetturale

**Piano operativo — revisione GATE-01, 22 ottobre 2026**  
**Stato:** GATE-01 **approvato dall'operatore il 2026-10-22**. Prossima fase: **DOC-02**, in un'unica sessione (§7.2). Nessuna ADR va archiviata o rimossa prima di GATE-02.  
**Task:** `BOPS-0121` `DOC-CONSOL-01` — `IN_PROGRESS`, `authorized: true` (register: `agentic/_tasks/register.json`; Issue pubblica #125).  
**Repository:** `babbubba/bOps` · **Branch:** `docs/adr-consolidation-2026-10-22` (solo questo; nessun altro branch)  
**Baseline di `main`:** `e0e9de7a74b8bb33e07c7b1f8c92ab87832986de`  
**Evidenza DOC-01:** `agentic/_plans/_migration-work/ADR-consolidation-2026-10-22/DOC-01-report.md` e `DOC-01-appendix-paths.tsv` (commit `9137043`)  
**Esclusione:** PR #124 e qualunque suo commit, branch, merge, cherry-pick o contenuto. Non va letta neppure per dedurre decisioni o completare file mancanti.

> Questo piano è una **specifica temporanea di migrazione**, non un'autorità architetturale. Contiene tutto ciò che serve a una nuova sessione per eseguire le fasi successive senza conoscere le conversazioni precedenti. In caso di dubbio prevalgono, nell'ordine: le decisioni dell'operatore registrate al §2, poi il resto di questo piano, poi gli artefatti DOC-01.

## 0. Avvio rapido per una nuova sessione

1. **Git.** Verificare branch `docs/adr-consolidation-2026-10-22`, working tree pulito, `git fetch origin docs/adr-consolidation-2026-10-22` e allineamento solo per fast-forward (`git merge --ff-only`). Mai rebase, reset, force-push o merge di altri branch.
2. **Identità.** `git config --local user.name "Fabio Cavallari"` e `git config --local user.email "fcavallari@bsoftsolutions.it"`; mai `--global`. Dopo ogni commit: `git show -s --format="Author: %an <%ae>%nCommitter: %cn <%ce>" HEAD`. **Nessun trailer `Co-Authored-By`.**
3. **Governance.** `python scripts/governance/validate_register.py --summary`: BOPS-0121 deve risultare `IN_PROGRESS` su questo branch. Leggere l'entry BOPS-0121 (inclusi `sync_pending` e `open_gates`) e `HANDOFF.md`.
4. **Letture obbligatorie per DOC-02:** questo piano per intero; il report DOC-01 (§5 contraddizioni, §8 riparto provvisorio, §9 matrice delle invarianti critiche); il TSV DOC-01 come indice dei percorsi; poi **le 55 ADR per intero** (§7.2). Non rifare l'inventario DOC-01.
5. **Eseguire solo la fase indicata** dall'operatore: oggi DOC-02 (§7.2). DOC-03 e DOC-04 richiedono GATE-02 approvato.

## 1. Problema, obiettivo e limiti

### 1.1 Baseline verificata (DOC-01)

- `docs/architecture/adr/`: **55 ADR** (15.895 righe, 1,23 MB). 48 Accepted, 7 Proposed: 0050, 0053, 0054, 0055, 0056, 0058, 0059. Gli ID 0003, 0004, 0007–0010 non esistono.
- `agentic/06-decisions.md`: 1.222 righe. Decision log D-001…D-050, in parte ridondante con le ADR e incompleto (C3 nel report DOC-01).
- `agentic/01-architecture-rules.md`: 679 righe. Regole con ID A/B/C/D, citate da 51 file di codice; il §B duplica tipi del codice e ha derivato.
- `agentic/03-security-rules.md`: regole S1–S11, citate da 25 file di codice.
- Sei formulazioni divergenti della precedenza normativa, in roadmap §1, `agentic/README.md`, `01`, `PROCESS.md` §1, `CLAUDE.md` e `docs/development/claude-code-skills.md` (report DOC-01 §4).
- Circa 370 file di codice citano ID di ADR; 12 file di codice citano ID D. **Nessuno script, test o CI legge ADR, `01`, `03` o `06`.**
- Inventario: 288 su 288 percorsi tracciati sotto `agentic/` e `docs/`. Unico file di istruzioni: `CLAUDE.md`. Assenti: `AGENTS.md`, `.github/copilot-instructions.md`, `.codex/`, istruzioni annidate.

### 1.2 Risultato atteso

1. **Un'unica autorità per ogni decisione strutturale corrente:** esattamente cinque contratti in `docs/architecture/current/`.
2. **Un solo bootstrap per gli agenti:** `agentic/00-bootstrap.md`. Lettura selettiva del task attivo e dei contratti pertinenti; nessuna scansione ordinaria di ADR o registri storici.
3. **Governance operativa intatta:** register, stati, handoff, Issue e Project, workflow e gate restano riconoscibili.
4. **Documentazione tecnica e funzionale utile preservata.** Le specifiche tecniche indispensabili, se non rappresentate altrove, restano in documentazione tecnica corrente.
5. **Storia consultabile ma senza autorità corrente:** uno ZIP dei 57 originali ritirati, con manifest; storia Git non riscritta.
6. **Nessuna perdita semantica,** dimostrata dai controlli CP-02 prima di qualunque archiviazione.

**Non è un obiettivo** ridurre `docs/` e `agentic/` a pochi file, eliminare tutta la documentazione storica o completare la convergenza a Microsoft Agent Framework (MAF).

### 1.3 Confini di modifica

**Consentito, nelle fasi e nei limiti indicati al §7:** file esclusivamente documentali e istruzionali in `docs/`, `agentic/`, root (`CLAUDE.md`, `AGENTS.md`, `README.md`, `CONTRIBUTING.md`, `SECURITY.md`); generazione di ZIP e manifest; aggiornamento dei collegamenti documentali.

**Eccezione di governance (G1-12):** commit di bookkeeping **separati** dai contenuti, limitati a `agentic/_tasks/register.json` e `HANDOFF.md`, per BOPS-0121 soltanto, sempre con `validate_register.py` verde. Lo stesso vale per Issue #125 e per l'item del Project #6.

**Non consentito:** codice eseguibile, runtime, package, API, CLI, test, CI, script, manifest di tool, entitlement, policy applicative, database, release workflow, configurazioni di produzione. Non si modificano i commenti nel codice. È ammessa l'ispezione in sola lettura di codice e test, per verificare l'implementazione.

Se un loader o uno script richiedesse una modifica eseguibile: **fermarsi** e proporre un task separato.

## 2. Decisioni GATE-01 approvate (2026-10-22)

Le sigle G1-xx sono distinte dalle decisioni del register (D1…D11) e dalle decisioni storiche di `06` (D-001…D-050).

| ID | Decisione approvata | Precisazioni vincolanti dell'operatore |
|---|---|---|
| G1-01 | Task **BOPS-0121** `DOC-CONSOL-01`, `authorized: true`, branch dedicato | Fatto con il commit di bookkeeping `1156b72`. Issue #125 creata. Item del Project #6 ancora dovuto: vedi `sync_pending`. |
| G1-02 | ADR 0050, 0053, 0054, 0055, 0056, 0058, 0059 **restano Proposed** | Le ADR Accepted 0057 e 0060 **non promuovono** le decisioni Proposed che raffinano. Stato decisionale e stato implementativo restano distinti (§3.4). |
| G1-03 | `01-architecture-rules.md` e `03-security-rules.md` diventano **indici di compatibilità** che conservano gli ID A/B/C/D e S1–S11. `06-decisions.md` diventa un redirect storico con una tabella D-ID → destinazione. | Il testo normativo vive **solo** nei contratti, in particolare le invarianti di sicurezza nel contratto 02. Nessuna sesta autorità. |
| G1-04 | Opzione **(A) con estrazione selettiva** | I contratti contengono le invarianti. Le specifiche tecniche indispensabili, se non rappresentate altrove, vanno in documentazione tecnica corrente. `docs/architecture/reference/` è ammessa solo quando nessuna guida esistente è una destinazione appropriata. Nessuna nuova autorità normativa autonoma. **Nessun limite arbitrario di righe** che causi perdita di semantica. |
| G1-05 | ZIP con **esattamente 57 originali**: 55 ADR, `01-architecture-rules.md`, `06-decisions.md` | Nessuna aggiunta. `07-plan-corrections.md`, `arch-02*`, `know-*` e `af-turn-01b` restano dove sono. |
| G1-06 | Riferimenti storici invariati | Restano come sono task packet, `agentic/obsolete/`, `docs/handoff-archive/`, piani chiusi, `CHANGELOG.md` e **commenti nel codice**. In DOC-03 si correggono solo i link della documentazione corrente. |
| G1-07 | Precedenza unica e **bootstrap canonico `agentic/00-bootstrap.md`** | `CLAUDE.md` e `AGENTS.md` sono router brevi. `02-coding-standards`, `04-testing-rules`, `05-workflow` e `PROCESS.md` conservano solo la propria autorità ingegneristica e procedurale. |
| G1-08 | Fatti obsoleti corretti solo nel perimetro documentale approvato | Elenco al §7.3, passo 7. Il resto diventa follow-up (§9). |
| G1-09 | La regola storica "0.x fino a V1.0" (A12, D-012) è **decaduta** | Non si inventa una nuova policy di versioning: si registra la regola come `historical-only (expired)`. |
| G1-10 | Il report `bench00-maf-compatibility-report.md` mancante **non si ricostruisce** | Va registrato come anomalia residua nel manifest e nel report DOC-04. |
| G1-11 | Collocazione delle regole di ingegneria | `02`, `04`, `05` e `PROCESS.md` sono regole ingegneristiche e procedurali (livello B), non contratti architetturali. `03-security-rules.md` non resta un'àncora normativa: diventa un indice (G1-03). |
| G1-12 | Eccezione di governance al §1.3 | Vedi §1.3. |
| G1-13 | **Contratti in inglese** | Le fonti in italiano (es. ADR-0057) si traducono nel contratto senza perdita di semantica. |
| G1-14 | Autore e committer `Fabio Cavallari <fcavallari@bsoftsolutions.it>` | **Nessun trailer `Co-Authored-By`.** |
| G1-15 | Branch locale allineato solo per fast-forward | Vedi §0. |

## 3. Modello documentale di destinazione

### 3.1 Livelli e precedenza

| Livello | Contenuto | Autorità |
|---|---|---|
| **A — Contratti architetturali correnti** | Esattamente cinque file `docs/architecture/current/0N-*.md` | Unica fonte per le decisioni strutturali *approvate* e per le invarianti di sicurezza. Nessuna ADR storica ha precedenza. |
| **B — Regole di ingegneria e governance** | `agentic/02-coding-standards.md`, `04-testing-rules.md`, `05-workflow.md`, `agentic/_tasks/PROCESS.md`, `00-project-spec.md` (perimetro di prodotto) | Autorità solo nel proprio ambito: codice, test, workflow, stati dei task, perimetro. Non istituiscono decisioni architetturali. |
| **B — Stato operativo** | Register, task packet, `HANDOFF.md`, roadmap e piani attivi | Autorità sul processo e sullo stato. Un task dettaglia l'esecuzione ma non contraddice i contratti. |
| **C — Riferimenti tecnici** | Guide di tool, API, CLI, deployment, runbook, `docs/security/*`, `docs/knowledge/*` ed eventuali `docs/architecture/reference/*` | Specificano comportamento e uso nel loro ambito, vincolati ai contratti. Non sono registri alternativi di decisioni. |
| **D — Storico** | ZIP, `agentic/obsolete/`, `docs/handoff-archive/`, report conclusi, piani chiusi | Provenienza e consultazione su richiesta esplicita; mai normativo, mai letto dal bootstrap. |

**Indici di compatibilità (non normativi):** `agentic/01-architecture-rules.md`, `agentic/03-security-rules.md`, `agentic/06-decisions.md` e `docs/architecture/adr/README.md`. Mappano gli ID storici verso il contratto e la sezione correnti, così che i commenti nel codice restino risolvibili. Non contengono testo di regola.

**Catena di precedenza unica,** da scrivere in DOC-03 nel bootstrap:

1. Contratti correnti (A). Un conflitto tra due contratti è un blocco: stop e domanda all'operatore.
2. Regole di ingegneria e governance (B), ciascuna nel proprio ambito.
3. Roadmap e piano attivo.
4. Task packet.
5. Riferimenti tecnici (C), nel loro ambito.
6. Storico (D): mai.

Le decisioni dell'operatore modificano i contratti; finché non lo fanno restano pending.

`CLAUDE.md` e `AGENTS.md` sono **router**: identità Git, regole d'oro brevi, rimando al bootstrap. Non sono un livello normativo concorrente.

Quando due fonti divergono non si sceglie per data o per nome. Si verificano lo stato (Accepted/Proposed), il codice e l'approvazione dell'operatore. Se il conflitto non si risolve con l'evidenza, la regola va in `REVIEW` e il suo trasferimento è bloccato.

### 3.2 I cinque contratti

| File | Ambito | ADR di provenienza (provvisorio, DOC-01 §8; da confermare in DOC-02) |
|---|---|---|
| `docs/architecture/current/01-maf-agent-infrastructure.md` | MAF come infrastruttura, mai autorità operativa; turni tramite broker governato; contesto e compattazione; provider (wire contract, pin, fallback); contenimento dei fallimenti delle chiamate al modello; finalizzazione. Convergenza da attivare distinta dalla produzione effettiva. | 0001, 0005, 0013, 0038, 0039, 0045, 0052. Pending (D1): 0053, 0054 |
| `docs/architecture/current/02-tools-safety-authority.md` | Confini dei package; niente esecuzione generica; classi di rischio, policy, approval, entitlement, allow-list, post-verifica, privilegi, delega; segreti e vault; plugin; filesystem, rete, SSRF. **Contiene tutte le invarianti di sicurezza S1–S11.** | 0002, 0006, 0012, 0015, 0016, 0020, 0022, 0023, 0024, 0025, 0026, 0027, 0028, 0029, 0030, 0031, 0033, 0036, 0037, 0044, 0049. Solo invarianti, il resto in guide: 0021, 0034, 0035, 0048 |
| `docs/architecture/current/03-task-state-resume-mutations.md` | Autorità di `TaskState`, piano e cursore, routing per passo, replan transazionale, fencing, resume, journal d'intento, esiti incerti, audit, budget, retry | 0014, 0017, 0040, 0046, 0047, 0051. Pending (D4): 0050. 0060: qui o nel 04, da decidere in DOC-02 |
| `docs/architecture/current/04-evidence-and-diagnosis.md` | Osservazioni grezze vs fatti di evidenza, completezza, filtri, troncamento, `EvidenceRead`, osservazione/ipotesi/causa, limiti dichiarati, grounding | 0032, 0041, 0042, 0061 (e 0060) |
| `docs/architecture/current/05-interfaces-knowledge-and-hosts.md` | SDK framework-neutral, confine open-core, API, CLI e browser (autenticazione, CSRF, sessione), Knowledge e Skill Pack, contenuti esterni come dati, tenant e pinning | 0011, 0018, 0019, 0043, 0057. Pending (D5): 0055, 0056, 0058, 0059 |

Il riparto interno può cambiare in DOC-02 se la lettura integrale lo giustifica: va motivato nella matrice. **Mai un sesto contratto.**

### 3.3 Struttura obbligatoria di ogni contratto

Ogni contratto è scritto in inglese e contiene, nell'ordine:

1. **Intestazione:** stato del documento (`Candidate — pending GATE-02` in DOC-02; `Current` solo dopo DOC-03), ambito, data, base Git.
2. **Scope e confini** con gli altri contratti.
3. **Binding rules.** Ogni regola ha:
   - un ID stabile `Cn-Rmm` (es. `C02-R07`);
   - l'elenco degli ID storici che sostituisce (A/B/C/D, S, D-xxx, `ADR-NNNN §x`);
   - lo **stato decisionale**: `Accepted (ADR/D-xxx)`, `Operator decision (data)` oppure `Expired`;
   - lo **stato implementativo**: `Implemented — evidence file:line`, `Partially implemented` (con evidenza), `Not implemented / target` oppure `Not verified`.
4. **Pending decisions — non-binding.** Le regole che dipendono da ADR Proposed, con il rimando a D1, D4 o D5. Nessuna formulazione imperativa che le faccia sembrare vigenti.
5. **Technical references.** Collegamenti alle guide di livello C che portano il dettaglio.
6. **Sources.** Elenco ADR/§ e documento/§ consolidati in questo contratto, coerente con la matrice DOC-02.

**Lunghezza:** 60–120 righe è solo un orientamento. Le invarianti di sicurezza e governance non si abbreviano per rispettarlo (G1-04). Non si duplicano tipi e API verificabili nel codice; si descrivono le proprietà che devono valere.

### 3.4 Decisioni non approvate e ADR che le raffinano

- Le 7 ADR Proposed non diventano Accepted. Le loro regole compaiono **solo** nelle sezioni "Pending decisions — non-binding", anche se il codice le implementa. Il codice non prova l'accettazione.
- **ADR-0057** è Accepted (rev. 2, D-049) ma "autorizza esclusivamente lo sviluppo di M1" e raffina 0054, 0055 e 0056, che sono Proposed. **ADR-0060** è Accepted e raffina la 0050, Proposed. Per entrambe:
  - diventano binding solo le clausole che hanno significato senza il testo Proposed, con la portata dichiarata (es. "M1 scope only");
  - le clausole che presuppongono la ADR Proposed vanno in Pending, con il rimando alla decisione del register;
  - ogni scelta è motivata nella matrice.
- Lo stato decisionale e lo stato implementativo sono due colonne distinte nella matrice e nei contratti. Non si dichiara mai "implementato" senza evidenza `file:riga` letta nella sessione.

## 4. Perimetro dell'inventario documentale

DOC-01 è concluso (288 su 288 percorsi). Il risultato è nel TSV e **non va rifatto**. Riclassificazioni approvate a GATE-01:

| Percorso | Classe e azione approvate | Fase |
|---|---|---|
| `agentic/01-architecture-rules.md` | Indice di compatibilità A/B/C/D; originale nello ZIP | DOC-03 |
| `agentic/03-security-rules.md` | Indice di compatibilità S1–S11; testo nel contratto 02. Non va nello ZIP: resta nella storia Git | DOC-03 |
| `agentic/06-decisions.md` | Redirect storico con tabella D-ID → destinazione; originale nello ZIP | DOC-03 |
| `agentic/00-project-spec.md` | B (perimetro di prodotto); UPDATE: le affermazioni strutturali diventano rimandi ai contratti, si corregge il fatto obsoleto su V1.3-M | DOC-03 |
| `agentic/02-coding-standards.md`, `04-testing-rules.md` | B (ingegneria); UPDATE minimo (G1-08) | DOC-03 |
| `agentic/05-workflow.md` | B; UPDATE: niente più "scrivi una ADR" né tabelle ADR obsolete; le nuove decisioni strutturali modificano un contratto, solo con approvazione dell'operatore | DOC-03 |
| `agentic/07-plan-corrections.md` | D storico; KEEP, fuori dal bootstrap | DOC-03 (solo rimozione dalle letture) |
| `docs/architecture/arch-02a`, `arch-02r`, `arch-02s`, `af-turn-01b`, `know-01a`, `know-01b`, `know-disc-01`, `know-m1-b-fix01` | D storico (evidenza di implementazione); **KEEP senza condizioni**, il destino non dipende da D1 o D5 | — |
| `agentic/_plans/2026-10-11-maf-knowledge-convergence.md` | F; KEEP **senza modifiche**; tabella di stato in contrasto con `PROCESS.md` §1 → follow-up | — |
| `HANDOFF.md`, `agentic/_tasks/register.json` | B; UPDATE solo come bookkeeping BOPS-0121 (G1-12) | ogni fase |
| `agentic/_plans/_migration-work/**` | D (evidenza della migrazione); KEEP; fuori dal bootstrap e dallo ZIP | — |
| `docs/security/threat-model.md`, `docs/knowledge/knowledge-pack-format.md` e le guide `docs/*.md` | C; possibili destinazioni dell'estrazione tecnica (G1-04) | DOC-02 (estrazione), DOC-03 (link) |

Esclusioni dall'archiviazione: `agentic/_tasks/**`, `agentic/_plans/**`, `agentic/_quality/**`, `agentic/obsolete/**`, `docs/handoff-archive/**`, `docs/benchmarks/**`, `HANDOFF.md`, guide e fixture di test. Non spostare né rinominare i file letti da test o script, elencati al report DOC-01 §7:

- `docs/benchmarks/know-bench-01a/` e `know-bench-01d/`;
- `docs/knowledge/examples/windows-event-triage-skills/`;
- `docs/agents/delegation-policy.md`;
- `docs/benchmarks/replay-*` e `qwen-*`.

## 5. Archivio storico

### 5.1 Contenuto (G1-05)

Percorso: `docs/architecture/archive/legacy-adr-and-decisions-2026-10-22.zip`. **Esattamente 57 file**, con il percorso relativo originale:

- le 55 ADR `docs/architecture/adr/*.md`;
- `agentic/01-architecture-rules.md` e `agentic/06-decisions.md`, nella versione **precedente** alla riduzione a indice.

Nessun altro file. Stima di DOC-01: 1,36 MB grezzi, circa 526 KB compressi.

### 5.2 Manifest, mappe e accesso

Dentro lo ZIP va un `MANIFEST.md`, più un elenco meccanico CSV non normativo, con:

- SHA completo della baseline `main` e del commit della migrazione;
- per ogni originale: percorso, SHA-256, dimensione, categoria, motivo del ritiro e destinazione delle regole (`contract §`, `technical reference`, `pending-Dn`, `historical-only`);
- la mappa ADR-ID → contratto/sezione, ricavata dalla matrice DOC-02 senza inventare corrispondenze;
- le anomalie note (G1-10, `bench00`).

**Mappe di risolvibilità fuori dallo ZIP** (DOC-03), tutte non normative e senza testo di regola:

- `docs/architecture/adr/README.md`: 55 righe ADR-ID → contratto/sezione, oppure stato;
- lo stub di `06`: D-ID → destinazione;
- gli indici `01` e `03`: ID → regola `Cn-Rmm`.

L'indice esterno `docs/architecture/archive/README.md` è breve: dichiara che lo ZIP non ha autorità e spiega come recuperare un originale solo su richiesta esplicita.

Le ADR Markdown escono da `docs/architecture/adr/` **solo** dopo GATE-02 e dopo CP-03a. Storia Git mai riscritta.

## 6. Bootstrap e governance (DOC-03)

- **`agentic/00-bootstrap.md`, bootstrap canonico.** Contiene la catena di precedenza del §3.1 e la lettura minima: router, task attivo (register e packet), `PROCESS.md` solo per i cambi di stato, uno o più contratti **pertinenti**, riferimenti tecnici su richiesta. Nessuna lettura automatica di ZIP, ADR storiche, `06` o degli indici.
- **`CLAUDE.md` e `AGENTS.md`** (quest'ultimo da creare): router brevi con identità Git, regole d'oro e rimando al bootstrap. `AGENTS.md` non duplica `CLAUDE.md`. Si toglie da `CLAUDE.md` il piano di hardening indicato come "attivo" (C7).
- **`agentic/README.md`:** indice delle cartelle, senza ordine di lettura; il criterio di selezione dei task si allinea a `PROCESS.md` (C8).
- **`agentic/_tasks/PROCESS.md`:** si cambia **soltanto** la riga "Architectural decisions" del §1 (contratti correnti più decisioni pendenti nel register). Nessuna modifica a gate, register, Project #6, sincronizzazione e stati.
- **Roadmap `agentic/_plans/2026-09-16-consolidated-roadmap.md`:** si aggiornano solo §1 (precedenza), §16 e §17 (letture e obblighi di ADR).
- **Altri documenti da allineare:** `agentic/_plans/2026-09-22-v1.3-coding-agent-execution.md` (solo i rimandi), `agentic/obsolete/README.md`, `README.md`, `CONTRIBUTING.md`, `SECURITY.md`, `docs/development/claude-code-skills.md` ("the ADRs win").

## 7. Sequenza di esecuzione e gate

### 7.1 DOC-01 e GATE-01 — completati

- **DOC-01:** inventario in sola lettura; evidenza nel commit `9137043`.
- **GATE-01:** approvato il 2026-10-22. Le decisioni sono al §2. Bookkeeping BOPS-0121 nel commit `1156b72`; revisione di questo piano nel commit successivo.

### 7.2 DOC-02 — Consolidamento canonico e matrice di copertura (**un'unica sessione**)

**Organizzazione (decisione dell'operatore).** DOC-02 è **un solo incarico completo, eseguito in un'unica sessione con contesto esteso**, per mantenere una visione globale delle 55 ADR e delle loro dipendenze. **Non** va diviso in più sessioni. **Non** ha checkpoint manuali né approvazioni intermedie tra le fasi interne. **Non** ha un commit per ogni ADR o gruppo di ADR. Gli artefatti in `_migration-work/` si aggiornano **progressivamente**, così che il lavoro sia verificabile e recuperabile in caso di interruzione. Questo non crea punti di arresto.

**Input:** questo piano; gli artefatti DOC-01; le 55 ADR; `agentic/01-architecture-rules.md`, `03-security-rules.md`, `06-decisions.md`, `00-project-spec.md`, `07-plan-corrections.md`; le guide `docs/` pertinenti; il codice e i test in sola lettura, per l'evidenza implementativa.

**Scrittura consentita in DOC-02:**

- `agentic/_plans/_migration-work/ADR-consolidation-2026-10-22/DOC-02-*` (artefatti, §7.2.2);
- i cinque contratti `docs/architecture/current/0N-*.md`, con stato `Candidate — pending GATE-02`;
- l'estrazione tecnica (G1-04) in guide `docs/` esistenti, oppure in `docs/architecture/reference/*.md` solo se nessuna guida è appropriata;
- `HANDOFF.md`, solo per il prossimo passo (GATE-02).

**Vietato in DOC-02:**

- modificare, spostare o rimuovere ADR, `01`, `03`, `06`;
- toccare bootstrap e router (`00-bootstrap`, `CLAUDE.md`, `AGENTS.md`, `agentic/README`, `05`, `PROCESS`, roadmap);
- creare lo ZIP;
- correggere link della documentazione corrente (si fa in DOC-03);
- toccare codice, test, CI, script.

#### 7.2.1 Fasi interne (in sequenza, senza stop)

1. **Lettura integrale delle 55 ADR.** Ogni ADR si legge dalla prima all'ultima riga. Per i file lunghi si usano letture a intervalli contigui, senza salti; il read-ledger registra gli intervalli letti. Si registrano righe, SHA-256, sezioni di primo e secondo livello, stato e relazioni (raffina, sostituisce, emenda, preserva). La copertura non si dichiara mai conclusa sotto 55/55.
2. **Emendamenti, contraddizioni e supersessioni.** Si verificano almeno le catene note:
   - 0014 ← emendamenti HARDEN-8 e 0046;
   - 0039 §8 ← 0046;
   - 0029 ← 0045 (parziale);
   - 0042 §6 ← AF-05H;
   - 0032 (circa 400 righe di emendamenti nel corpo);
   - 0022;
   - 0052 §27–31;
   - 0050 ← 0060;
   - 0054, 0055, 0056 ← 0057;
   - 0053 → 0054;
   - D-028…D-048 contro ADR 0032…0052;
   - D-049 (0057) e D-050.

   Si aggiungono le catene emerse dalla lettura. Le contraddizioni irrisolvibili con l'evidenza vanno in `REVIEW`, senza essere scelte.
3. **Ricostruzione della regola vigente** per ogni prescrizione: testo efficace dopo emendamenti e supersessioni, stato decisionale, stato implementativo con evidenza.
4. **Matrice di tracciabilità** per ADR/sezione/prescrizione, più le fonti non ADR (`01`, `03`, `06`, `00-project-spec`).
5. **Redazione dei cinque contratti** secondo il §3.3, in inglese.
6. **Estrazione delle specifiche tecniche indispensabili** non rappresentate altrove (G1-04). Priorità: la tabella ADR dense del §7.2.3, in particolare 0051 e 0042, che non hanno controparte in `docs/`.
7. **Verifica di copertura** normativa e semantica: CP-02a…CP-02e (§7.2.4).
8. **Report GATE-02.**

#### 7.2.2 Artefatti da produrre

Sotto `agentic/_plans/_migration-work/ADR-consolidation-2026-10-22/`. Tutti sono **non normativi** e iniziano con la stessa nota di non autorità del report DOC-01.

| File | Contenuto |
|---|---|
| `DOC-02-read-ledger.tsv` | Una riga per ADR (55): `adr`, `path`, `lines`, `bytes`, `sha256`, `status`, `sections`, `ranges_read`, `read_complete` (yes/no), `relations`, `notes` |
| `DOC-02-effective-rules.md` | Catene di emendamento e supersessione, contraddizioni, regola vigente ricostruita, voci `REVIEW` |
| `DOC-02-traceability-matrix.tsv` | Una riga per prescrizione: `source`, `section`, `prescription_id`, `summary`, `normative` (yes/no), `disposition`, `destination` (`path#section` o `rule Cn-Rmm`), `decision_status`, `implementation_status`, `evidence` (`file:line`), `rationale`. `disposition` vale `CONTRACT`, `TECH-REF`, `PENDING` o `HISTORICAL` (dettaglio sotto). |
| `DOC-02-id-coverage.tsv` | Ogni ID A/B/C/D di `01`, S1–S11 di `03`, D-001…D-050 di `06` e ADR-0001…0061 → regola `Cn-Rmm`, guida, `pending-Dn` oppure `historical-only` con motivazione. È la base delle mappe DOC-03. |
| `DOC-02-normative-extraction.tsv` | Estrazione meccanica degli enunciati normativi per fonte e sezione (MUST, MUST NOT, SHALL, never, always, only, required, forbidden, deve, mai, sempre, vietato, obbligatorio, solo), ciascuno collegato a una riga della matrice |
| `DOC-02-dense-adr-coverage.md` | Per ogni ADR densa (§7.2.3): disposizione sezione per sezione |
| `DOC-02-uncovered-normative-content.md` | Contenuti normativi non rappresentati altrove prima di DOC-02 e la sede scelta (contratto o guida), con motivazione |
| `DOC-02-checks.md` | Per CP-02a…CP-02e: comando o metodo, output, esito PASS/FAIL |
| `DOC-02-GATE-02-report.md` | Report per l'operatore (§7.2.5) |

Valori di `disposition`:

- `CONTRACT`: trasferito a un contratto canonico;
- `TECH-REF`: trasferito o già presente in documentazione tecnica corrente;
- `PENDING`: non vincolante;
- `HISTORICAL`: historical-only, con motivazione.

Gli script di supporto per le verifiche meccaniche restano nello scratchpad della sessione: **non** si aggiungono a `scripts/`. I comandi e gli output si riportano in `DOC-02-checks.md`.

**Artefatti fuori da `_migration-work/`:** i cinque contratti; le guide tecniche aggiornate o create per l'estrazione; `HANDOFF.md` aggiornato.

#### 7.2.3 ADR dense: verifica sezione per sezione

| ADR | Righe | Motivo |
|---|---:|---|
| 0042 | 1.254 | Ragionamento sull'evidenza e limiti; §6 sostituito da AF-05H; nessuna guida `docs/` |
| 0044 | 1.171 | Autorizzazione delle deleghe dipendente dalla richiesta |
| 0057 | 950 | Indice di discovery Knowledge; Accepted con portata M1, raffina ADR Proposed; in italiano |
| 0043 | 941 | Sessione web del browser, CSRF, autenticazione |
| 0051 | 807 | Journal d'intento delle mutazioni, esiti incerti, riconciliazione; nessuna guida `docs/` |
| 0032 | 764 | Eventi di sistema; circa 400 righe di emendamenti |
| 0045 | 664 | Pin e fallback del provider |
| 0052 | 642 | Confine MAF/bOps; §27–31 sull'evidenza |
| 0041 | 557 | Evidenza tipizzata di stabilità |
| 0014 | 437 | Pianificazione e replan; emendamenti |
| 0040 | 428 | Macchina a stati del resume |
| 0037 | 398 | Ciclo di vita dei plugin |

#### 7.2.4 Controlli obbligatori DOC-02

Ogni contenuto rilevante va classificato come `CONTRACT`, `TECH-REF`, `PENDING` (non vincolante) oppure `HISTORICAL` (con motivazione).

| ID | Controllo | Criterio di superamento |
|---|---|---|
| **CP-02a** | Lettura integrale | 55/55 ADR con `read_complete = yes` nel ledger e intervalli letti che coprono tutte le righe. Ogni sezione di primo livello di ogni ADR ha almeno una riga in matrice. |
| **CP-02b** | Destinazione di ogni prescrizione | **Meccanico:** ogni enunciato di `DOC-02-normative-extraction.tsv` è collegato a una riga della matrice con `disposition` valorizzata; zero orfani. **Semantico:** per ogni riga `CONTRACT` o `TECH-REF`, la destinazione esprime la stessa regola (soggetto, condizione, effetto, eccezioni) senza indebolirla. Le differenze sono motivate in `rationale`. |
| **CP-02c** | Identificativi | Tutti gli ID A/B/C/D, S1–S11, D-001…D-050 e le 55 ADR sono in `DOC-02-id-coverage.tsv` con una destinazione. Il confronto con gli ID estratti meccanicamente dai file originali dà 0 mancanti e 0 duplicati ambigui. |
| **CP-02d** | ADR dense | Per ogni ADR del §7.2.3, ogni sezione e sottosezione ha una disposizione in `DOC-02-dense-adr-coverage.md`; il dettaglio tecnico necessario ha una sede. |
| **CP-02e** | Onestà e sicurezza | (1) Nessuna regola da ADR Proposed nelle sezioni binding; 0057 e 0060 trattate come al §3.4. (2) Ogni `Implemented` ha un'evidenza `file:line` verificata nella sessione. (3) Le invarianti di sicurezza (S1–S11, policy fail-closed, `Critical` sempre vietato, niente esecuzione generica, verifica post-azione, entitlement rivalutato, deleghe che riducono i privilegi, approvazione umana, redazione dei segreti, audit completo, output dei tool come dato, path policy e SSRF al momento dell'operazione, journal ed esito incerto mai ripetuto) sono tutte presenti nei contratti con forza almeno pari all'originale. (4) Nessuna nuova regola inventata. |

#### 7.2.5 Commit, interruzione e report GATE-02

- **Un solo commit DOC-02 finale**, se e solo se CP-02a…CP-02e sono PASS. Il commit contiene artefatti, contratti, guide ed `HANDOFF.md`. Prima del commit: staging esplicito per percorso, revisione del diff, `validate_register.py` verde, controllo che non ci siano file fuori dal perimetro del §7.2. Il push sul branch avviene solo se il prompt di avvio di DOC-02 lo autorizza.
- **Limiti effettivi di esecuzione o contesto:** si preserva il lavoro verificato. Gli artefatti dichiarano la copertura raggiunta e quella residua (ADR, sezioni, controlli non completati). Il report GATE-02 ha verdetto `BLOCKED`. Se l'operatore autorizza il commit, si committa questo stato parziale, con i contratti marcati `Draft — incomplete`. Non si dichiara mai completata una copertura parziale.
- **Contenuto del `DOC-02-GATE-02-report.md`:**
  - esito di CP-02a…CP-02e;
  - i cinque contratti, con numero di regole e regole pending;
  - le estrazioni tecniche e le loro sedi;
  - le voci `REVIEW` e le contraddizioni;
  - le divergenze tra decisione e implementazione;
  - le decisioni richieste all'operatore;
  - il verdetto `READY FOR GATE-02 APPROVAL` oppure `BLOCKED`.
- **Stato del task:** BOPS-0121 resta `IN_PROGRESS`. HANDOFF indica come prossimo passo GATE-02.

### 7.3 GATE-02 — Approvazione obbligatoria dell'operatore

L'operatore esamina contratti, matrice, ledger e report. **Nessuna ADR può essere rimossa, spostata o archiviata prima del superamento di GATE-02.** L'esito si registra in questo piano, nella stessa forma del §2 (decisioni G2-xx).

### 7.4 DOC-03 — Archiviazione, bootstrap e riferimenti (dopo GATE-02)

Passi, nell'ordine:

1. **Congelare gli originali.** Generare ZIP e manifest con i 57 file del §5.1 e verificare **CP-03a**.
2. **Rimuovere** le 55 ADR Markdown da `docs/architecture/adr/` e creare `docs/architecture/adr/README.md` con la mappa (§5.2).
3. **Ridurre** `01` e `03` a indici di compatibilità e `06` a stub con tabella D-ID, a partire da `DOC-02-id-coverage.tsv`.
4. **Promuovere** i contratti a `Current`.
5. **Aggiornare** bootstrap, router e precedenze (§6).
6. **Correggere i link** della documentazione corrente che puntano alle ADR. Sono le righe del TSV DOC-01 con azione "KEEP / UPDATE links", più gli entry point del §6 e i 4 report `know-*` solo se puntano a file ADR. Packet, archivi, piani chiusi e codice restano invariati (G1-06).
7. **Correggere i fatti obsoleti approvati (G1-08):**
   - `00-project-spec`: stato V1.3-M e rimandi D-xxx;
   - `05-workflow`: "unwritten ADRs" e tabelle ADR;
   - `04-testing-rules`: `examples/` inesistente;
   - `02-coding-standards`: la copia di `Directory.Build.props` diventa un rimando al file;
   - A12/D-012: `historical-only (expired)` (G1-09);
   - `CLAUDE.md`: piano di hardening "attivo" (C7);
   - `agentic/README`: criterio di selezione dei task (C8).
8. **Verificare CP-03b.**

| ID | Controllo | Criterio |
|---|---|---|
| **CP-03a** | Integrità dello ZIP | 57 file; percorsi originali; SHA-256 e dimensioni identici al manifest e ai file della baseline; estrazione di prova identica byte per byte; nessun file in più. |
| **CP-03b** | Risolvibilità degli ID | Ogni `ADR-NNNN`, `D-NNN` e ID di `01`/`03` citato in `src/`, `tests/`, `web/`, `scripts/`, nei documenti correnti e nei packet si risolve in una riga di mappa (adr README, stub `06`, indici `01`/`03`). Verifica meccanica, zero ID non risolti; i riferimenti già rotti in origine (es. `Audit.cs:673`) vanno elencati a parte. |

**Gate DOC-03:**

- ZIP integro;
- nessun documento ritirato prima della copertura;
- nessun link normativo attivo ambiguo;
- bootstrap minimo;
- governance preservata.

Se uno script o il codice caricassero file storici: stop e task separato.

### 7.5 DOC-04 — Accettazione statica e PR documentale

1. **Diff:** solo file attesi; nessuna contaminazione da altri branch o da PR #124; nessuna modifica a codice, test, CI, script, packet, quality.
2. **ZIP:** CP-03a ripetuto sul commit finale.
3. **Coverage:** ogni ADR e ogni ID ha una destinazione; nessuna Proposed promossa.
4. **Bootstrap:** nessuna istruzione attiva impone ADR storiche, `06` o archivi; un solo ordine di lettura.
5. **Riferimenti:** nessun link corrente rotto introdotto dalla migrazione; i riferimenti storici sono distinti; CP-03b ripetuto.
6. **Governance:** `validate_register.py` verde; `PROCESS.md` cambiato solo nella riga autorizzata; register, HANDOFF, Issue e Project coerenti, oppure `sync_pending` dichiarato.
7. **Unicità:** nessun documento strutturale concorrente in `agentic/`, `docs/` o nelle istruzioni.
8. **CP-04, smoke documentale:** si simulano un task ordinario e un task architetturale e si registrano i file che verrebbero letti. Nessuna lettura ordinaria di ZIP, ADR storiche, `06` o indici.

Esito: report `PASS` o `BLOCKED`. Dopo l'autorizzazione esplicita dell'operatore, commit e PR docs-only verso `main`, con `Refs #125` e mai `Closes`; nessun merge automatico. A quel punto BOPS-0121 passa a `VALIDATION` con il bookkeeping previsto (register, Issue, Project, HANDOFF).

La convergenza MAF resta un task separato, dopo il merge.

## 8. Exit criteria non negoziabili

- [x] DOC-01: inventario completo dei percorsi e dei punti di ingresso istruzionali (288 su 288).
- [x] GATE-01: decisioni approvate e registrate (§2). BOPS-0121 autorizzato.
- [ ] CP-02a…CP-02e PASS e GATE-02 approvato prima di qualunque archiviazione.
- [ ] Esattamente cinque contratti correnti in inglese; nessuna sesta autorità; `01`, `03` e `06` ridotti a indici o redirect non normativi.
- [ ] Ogni regola dei contratti ha uno stato decisionale e uno implementativo distinti; nessuna ADR Proposed promossa.
- [ ] Le specifiche tecniche indispensabili hanno una sede tecnica corrente.
- [ ] ZIP con esattamente 57 originali, SHA e manifest coerenti (CP-03a); ID storici risolvibili (CP-03b).
- [ ] Un solo bootstrap (`agentic/00-bootstrap.md`); `CLAUDE.md` e `AGENTS.md` sono router; nessuna istruzione impone la lettura di ADR, `06` o archivi.
- [ ] Register, `PROCESS.md`, HANDOFF, Issue #125 e Project #6 coerenti con il loro ruolo.
- [ ] Nessuna modifica a codice, test, CI, script, package o commenti nel codice; nessuna importazione da PR #124.
- [ ] Smoke CP-04 superato; PR docs-only mai mergiata automaticamente.

## 9. Anomalie note e follow-up fuori perimetro

| Voce | Trattamento |
|---|---|
| Project #6: l'Issue #125 non è ancora un item | `sync_pending` su BOPS-0121; da fare con `sync_remote.py` o dalla UI web, con l'account `babbubba`. Mai dichiararlo fatto senza una rilettura. |
| BOPS-0120: PR #123 già nella baseline, ma il register la riporta in `VALIDATION` (C9) | Bookkeeping di BOPS-0120, fuori da questa migrazione |
| PR #124 (BOPS-0111) potrebbe allocare progressivi in conflitto con BOPS-0121 | Non verificato, per vincolo di esclusione. Se emerge un conflitto al merge, decide l'operatore; i progressivi non si rinumerano. |
| `Audit.cs:673` cita `agentic/architecture/adr/0013` (percorso inesistente) | Codice: task separato |
| `docs/benchmarks/bench00-maf-compatibility-report.md` assente | Non si ricostruisce (G1-10); lo si annota nel manifest |
| Link relativo rotto in ADR-0057 verso `know-m1-b-fix01…` | La ADR va nello ZIP; non si corregge |
| Piano MAF/Knowledge: tabella di stato in contrasto con `PROCESS.md` §1 (C10) | Follow-up |
| Lingua: il bootstrap prescrive l'inglese per i file normativi; alcune fonti sono in italiano (C13) | I contratti sono in inglese (G1-13); il resto resta com'è |
| Nessun controllo CI dei link documentali (C11) | DOC-04 usa un controllo statico proprio; un eventuale controllo in CI è un task separato |

## 10. Regole di lavoro

- **Efficienza:** l'inventario completo dei file non equivale a caricare tutti i file nel contesto. DOC-02, invece, **richiede** la lettura integrale delle 55 ADR.
- **Niente false certezze:** una decisione scritta in Markdown non prova che il runtime la implementi; uno ZIP non impedisce letture automatiche se bootstrap e loader non sono corretti.
- **Se una regola blocca il lavoro:** fermarsi, dire quale regola, cosa serviva e cosa si propone. Non aggirarla.
- **Git:** commit solo con staging esplicito per percorso e revisione del diff; push solo sul branch `docs/adr-consolidation-2026-10-22` e solo se autorizzato per la fase; nessuna PR né merge senza autorizzazione esplicita.
