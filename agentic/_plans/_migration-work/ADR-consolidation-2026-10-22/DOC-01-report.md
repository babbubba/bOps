> **Artefatto di lavoro non normativo.** Evidenza della fase DOC-01 del piano `agentic/_plans/ADR-consolidation-2026-10-22.md`, conservata per la continuità tra sessioni. Non è documentazione corrente, non è un contratto e non va caricata dal bootstrap. Base: HEAD `6f6c870dab406f0dedc1f679e5bd4cf339bc7562`. Appendice di percorsi: `DOC-01-appendix-paths.tsv` (stessa cartella). Le classificazioni sono provvisorie fino a GATE-01.

# DOC-01 — Report: inventario documentale e gate di precedenza

Ho eseguito solo la lettura. Il working tree è pulito (0 modifiche) e non ho creato commit, push o PR. L'appendice con tutti i percorsi sta fuori dal repository: `/tmp/claude-0/-home-user-bOps/ba3abde0-5b43-5d2e-a7e5-2f7dcc99f3d6/scratchpad/DOC-01-appendix-paths.tsv`.

**Verdetto: `READY FOR PLAN APPROVAL`, con riserve.**
- La base Git è verificata e l'inventario dei percorsi è completo (288 su 288).
- Non ho trovato blocchi non docs-only: nessuno script, CI o test legge ADR, `06-decisions.md` o `01-architecture-rules.md`.
- La lettura integrale è parziale per 46 ADR su 55, e la classificazione di quelle 46 è quindi provvisoria. Dettagli al §2.
- Le decisioni del §12 sono prerequisiti di GATE-01. Non passo a DOC-02.

---

## 1. Stato Git

| Voce | Esito |
|---|---|
| Branch / HEAD | `docs/adr-consolidation-2026-10-22` @ `6f6c870dab406f0dedc1f679e5bd4cf339bc7562` (coincide con `origin/<branch>`) |
| Working tree | pulito |
| Baseline `e0e9de7a74b8…` | antenata di HEAD |
| Differenza baseline..HEAD | 2 commit (`f5ae974`, `6f6c870`), 1 file, +258 righe: solo `agentic/_plans/ADR-consolidation-2026-10-22.md` |
| `main` remoto (`git ls-remote`) | `e0e9de7a74b8…`, identico alla baseline, nessuna divergenza |
| PR #124 | esistono ref remoti (`refs/pull/124/head` = `b8a3fe7…`), mai scaricati né letti |

Tre note di Git, tutte relative alle fasi successive:
- **Identità locale:** `user.name` globale è "Claude" e l'email locale è `noreply@anthropic.com`. `CLAUDE.md` impone `Fabio Cavallari <fcavallari@bsoftsolutions.it>` in config locale prima di ogni commit.
- **Autore dei due commit del branch:** risulta "Babbubba", ma vengono dal flusso GitHub dell'operatore. La storia non si riscrive.
- **Trailer di attribuzione:** il trailer `Co-Authored-By: Claude` richiesto dall'harness è in tensione con "mai committare come Claude" di `CLAUDE.md`.

**Baseline del piano:**
- Confermate: 55 ADR, `06-decisions.md` 1.222 righe (il piano dice 1.223) e `01-architecture-rules.md` 679 righe (il piano dice 680); la differenza è di una riga, per convenzione di conteggio.
- Confermati gli stati: 0052 Accepted, 0053 e 0054 Proposed.

## 2. Copertura effettiva

- **Percorsi:** `git ls-files` copre 1.833 file tracciati. Sotto `agentic/` e `docs/` sono 288 (127 + 161), tutti censiti con righe, byte, classe e azione nell'appendice.
- **Entry point istruzionali:** `CLAUDE.md` è l'unico file di istruzioni.
  - Assenti: `AGENTS.md`, `.github/copilot-instructions.md`, `.codex/`, istruzioni annidate.
  - Presenti ma senza contenuto istruzionale: `.claude/launch.json`, `.github/` (workflow e dependabot).
  - `.claude/skills/` è ignorato da `.gitignore`.
- **Loader e script:** nessuno carica ADR, `06`, `01` o `agentic/0*.md`. Gli unici consumatori eseguibili di `docs/` e `agentic/` sono elencati al §7.
- **Lettura integrale:**
  - Il piano; `CLAUDE.md`, `HANDOFF.md`, `README` e `PROCESS.md`.
  - `agentic/00-bootstrap`, `00-project-spec`, `01` … `07` (incluso `06` per intero) e `_tasks/README.md`.
  - La roadmap (§1, §16, §17 e indice) e il piano MAF/Knowledge.
  - Le ADR 0050, 0052, 0053, 0054, 0055 e 0056.
- **Lettura parziale:**
  - Le ADR 0057, 0058, 0059: testata e indice delle sezioni.
  - Le altre 46 ADR: metadati, stato, indice delle sezioni e grep di supersessione/emendamenti; non le ho lette per intero.
  - Gli 87 packet in `_tasks`, i 62 file in `benchmarks` e le guide in `docs/` radice: solo intestazioni e riferimenti.
  - `register.json`: letto via script (stato, decisioni, task), non per intero.
- **Verifiche meccaniche:**
  - Link e percorsi interni su 1.249 riferimenti.
  - Riferimenti entranti per ogni ADR, per area.
  - Lettura di documenti da parte di test e script.
  - Hash dei fixture duplicati.
  - `validate_register.py --summary`: 0 errori, 120 task.

## 3. Conteggi per famiglia

| Famiglia | File | Byte |
|---|---:|---:|
| `agentic/` radice (`00`–`07`, README) | 10 | 186.585 |
| `agentic/_tasks` (87 packet + PROCESS + README + `register.json`) | 90 | 1.117.932 |
| `agentic/_plans` | 7 | 197.992 |
| `agentic/_quality` | 11 | 72.047 |
| `agentic/obsolete` | 9 | 202.748 |
| `docs/architecture/adr` | 55 | 1.230.977 (15.895 righe) |
| `docs/architecture` (non ADR) | 9 | 114.421 |
| `docs/benchmarks` | 62 | 499.113 |
| `docs/` radice (guide operative/tool) | 17 | 167.744 |
| `docs/agents`, `knowledge`, `security`, `operations`, `plugins`, `development`, `maintenance` | 3, 5, 2, 2, 1, 1, 1 | 35.236, 32.801, 35.729, 27.939, 15.769, 4.389, 45.535 |
| `docs/handoff-archive` | 3 | 76.661 |
| **Totale** | **288** | **4.063.618** |

**Classi provvisorie sui 288 file:**

| Classe | File | Contenuto |
|---|---:|---|
| A | 49 | 48 ADR + `03-security-rules` |
| A/E | 1 | `00-project-spec` |
| B/F | 1 | `06-decisions` |
| C | 64 | storici, benchmark, obsolete, archivi |
| C/E | 1 | piano di hardening (chiuso) |
| C/F | 8 | `docs/architecture` non ADR, legati a ADR Proposed |
| D | 46 | riferimenti tecnici |
| E | 110 | governance operativa, task, piani, quality |
| F | 8 | 7 ADR Proposed + `01-architecture-rules` |

**Carico di lettura attuale.** Il percorso "required read order" pesa circa 249 KB e 3.788 righe (stima ~62k token), senza contare `register.json` (143 KB) né le ADR (1,23 MB).

## 4. Autorità, precedenze e letture obbligatorie

Oggi esistono sei formulazioni non allineate della precedenza:

1. **Roadmap §1 e `agentic/README`:** ADR accettata > `agentic/00–07` > roadmap > task.
2. **`01-architecture-rules`:** "questo file vince" su roadmap e task.
3. **`PROCESS.md` §1:** decisioni architetturali = "ADR e `06-decisions.md`".
4. **`CLAUDE.md`:** tratta `agentic/` come vincolante, non nomina le ADR e indica `06` come "settled questions".
5. **`docs/development/claude-code-skills.md`:** "in bOps the ADRs win".
6. **`agentic/obsolete/README`:** rimanda a `00–07` e alle "accepted ADRs".

Letture obbligatorie o implicite verso ADR e decisioni storiche:
- `00-bootstrap` (passi 6 e 8).
- `05-workflow` (passo 3, più la regola "scrivi una ADR").
- Roadmap §16 e §17 (passo 3).
- `agentic/README`.
- `CONTRIBUTING.md` (passo 2).
- `CLAUDE.md` (tabella).
- `_tasks/README` ("ADR scritta e accettata prima dell'implementazione").

Non c'è alcun trigger automatico: sono tutte istruzioni testuali.

## 5. Contraddizioni e ridondanze

| # | Evidenza | Trattamento proposto |
|---|---|---|
| C1 | Sei precedenze diverse (§4). | Un'unica catena, decisa a GATE-01. |
| C2 | "Un'ADR accettata non si modifica" (`05-workflow`, roadmap §16). Nella pratica le ADR 0014 (HARDEN-8), 0022, 0032 (~400 righe di emendamenti), 0042 (AF-05H sostituisce §6), 0046 e 0052 (§27–31 "evidence") portano emendamenti nel corpo. La 0029 è parzialmente superata dalla 0045; la 0046 sostituisce parti di 0014 e 0039 §8. | Motivo forte per consolidare. Il contratto riporta lo stato efficace. |
| C3 | `06` si presenta come registro dei "settled", ma ADR 0050, 0053–0056, 0059, 0060, 0061 non hanno numero D. La 0060 dichiara "06 non viene modificato". Due numerazioni coesistono: `D-001…D-050` e `D1…D11` nel register/HANDOFF. L'intestazione dice "tutte Accepted tranne D-050", ma D-049 poggia su ADR Proposed. | `06` perde autorità. Nei contratti le decisioni pendenti usano una sigla distinta. |
| C4 | ADR Proposed ma implementate nel codice. Per la 0050 esistono `ExpectedArguments`, `SemanticMismatch` e `ConditionalSteps.cs`; per 0055/0056/0058/0059 esistono `bOps.Knowledge`, `TaskState.KnowledgeBinding` e il pilota CLI. Le ADR Accepted 0057 e 0060 le raffinano. | Nei contratti: sezione "Pending decision", fuori dalle regole vincolanti. Il codice non si cita come prova di accettazione. |
| C5 | Fatti obsoleti nei file normativi. `00-project-spec` dice che V1.3-M non è mergiato, mentre bootstrap, roadmap e HANDOFF dicono che A–M sono mergiati. `05-workflow` dice che le ADR 0001… sono "unwritten as of V0.9", ma esistono. `04` cita `examples/`, che non esiste. `02` incorpora una copia di `Directory.Build.props` ormai divergente (mancano `CS1591` e `CA1707`). `A12/D-012` dice "0.x fino a V1.0", ma il README riporta `1.3.0-preview.4` e D-026 `1.2.0-preview.1`. | Correggere dove si tocca l'autorità. Il resto è un elenco di follow-up. |
| C6 | `01-architecture-rules` §B duplica i tipi del codice e ha derivato. `AuthorizationKind` non ha `EntitlementDenied` (presente nel codice). `ToolCallResult` non riporta `FailureKind` e gli altri campi aggiunti. | Non duplicare tipi nei contratti. Vedi D3. |
| C7 | `CLAUDE.md` indica il piano di hardening come "Active… one at a time", ma piano, bootstrap, roadmap e `_tasks/README` lo dichiarano chiuso. | UPDATE di `CLAUDE.md`. |
| C8 | `agentic/README` dice che `_tasks/README` è "Executable task index; select the first dependency-ready task". `PROCESS.md` dice che solo `authorized: true` autorizza e che la fonte è il register. | Riallineare a `PROCESS.md`. |
| C9 | `HANDOFF.md` è dietro la baseline: indica `main` @ `f1cd5e7`, BOPS-0120 come task corrente e 119 task. La baseline `e0e9de7` contiene già #123 e il register ha 120 task. Il validator passa. | Bookkeeping, fuori perimetro del piano. Lo segnalo soltanto. |
| C10 | `PROCESS.md` vieta tabelle di stato altrove. Il piano MAF/Knowledge (in italiano, "ACTIVE") ha una sequenza con "NEXT". | REVIEW sul piano, nessuna archiviazione. |
| C11 | Il bootstrap cita "documentation-alignment checks" nella validazione finale. In CI c'è solo `validate_register`, senza controllo di link. | DOC-04 userà un controllo statico proprio. |
| C12 | Riferimenti pendenti: `docs/benchmarks/bench00-maf-compatibility-report.md` (citato da ADR-0058, ADR-0061 e `know-disc-01`) non è nell'albero. Un link relativo in ADR-0057 a `know-m1-b-fix01…` è rotto. `Audit.cs:673` cita `agentic/architecture/adr/0013`, percorso inesistente. | Non introdotti dalla migrazione, ma da documentare. |
| C13 | Lingua: il bootstrap prescrive inglese per i file normativi `agentic/`. ADR-0057 (Accepted), il piano MAF/Knowledge e il piano di consolidamento sono in italiano. | Minore, da annotare. |
| C14 | Governance: questa migrazione non ha un id `BOPS-NNNN` né `authorized` nel register. `CLAUDE.md` lo richiede; il piano vieta di toccare il register. | Decisione 1. |

**Ridondanze:**
- **`06-decisions`:** le D-028…D-048 ripetono per larga parte le sezioni Decision/Consequences delle ADR 0032…0052. Le D-001…D-012 duplicano `00-project-spec` e `07`.
- **`07-plan-corrections`:** 66 righe di storia che spiegano le correzioni al piano obsoleto.
- **Ordini di lettura:** tre ordini diversi in `CLAUDE.md`, bootstrap e `agentic/README`.
- **Fixture:** `windows-event-triage/{SKILL.md,references/interpretation.md}` esistono in 3 posizioni con SHA-256 identico. Sono legati ai test, quindi si lasciano come sono.

## 6. Candidati a sostituzione o archiviazione, e documenti ambigui

Le ADR da sole sono circa 1,2 MB contro circa 300–600 righe previste per i cinque contratti. Per i 7 ADR più densi (0042, 0044, 0051, 0043, 0057, 0029, 0037) vedi D4.

| Percorso | Classe | Azione | Motivazione | Destinazione delle regole vive | Riferimenti entranti |
|---|---|---|---|---|---|
| `docs/architecture/adr/*.md` (48 Accepted) | A | CONSOLIDATE + ARCHIVE | Regole sparse in 55 file con emendamenti nel corpo. | Contratti 01–05 (§8). Dettaglio per-tool solo dove manca altrove: vedi D4. | 105 percorsi-link in 64 file esterni più 52 link tra ADR. ID citati in 360 file `src`, 236 `tests`, 35 `web`, 54 task. Nessun consumatore eseguibile. |
| ADR 0050, 0053, 0054, 0055, 0056, 0058, 0059 (Proposed) | F | REVIEW + ARCHIVE storico | Stato pendente (D1/D4/D5). | Sezione "pending, non vincolante". Nessuna promozione. | come sopra; 0054 citata da 4 report in `docs/architecture` |
| `agentic/06-decisions.md` | B/F | REPLACE con stub di reindirizzamento + copia nello ZIP | Registro duplicato e incompleto (C3). | Contratti. Gli id D restano risolvibili tramite ZIP/manifest. | 12 file di codice (13 righe) e `Directory.Build.props`; `SECURITY.md`, `CONTRIBUTING.md`, `README`, `CLAUDE.md`, `agentic/README`, bootstrap, `05`, `PROCESS`, roadmap §17, 11 packet |
| `agentic/01-architecture-rules.md` | F | REVIEW: ridurre a indice di invarianti con ID stabili (A/B/C/D) + copia originale nello ZIP | Il §B ha derivato dal codice (C6), ma gli ID di regola sono ancorati nel codice. | Contratti + codice per i tipi | 51 file di codice (65 righe) citano le sue regole. `README`, `CONTRIBUTING`, bootstrap, `05`, `CLAUDE.md` lo richiamano. |
| `agentic/07-plan-corrections.md` | C | KEEP, oppure ARCHIVE (candidato ZIP opzionale) | Storico, spiega la relazione con `obsolete/`. | `historical-only` | 3 file di codice, `01`, `agentic/README`, `CLAUDE.md` |
| `docs/architecture/arch-02a`, `arch-02r`, `arch-02s` | C/F | KEEP. Il destino è legato a D1. | Evidenza della ADR-0054 (Proposed); gli spike non sono mai stati mergiati. | — | ADR-0054, ADR-0053 |
| `docs/architecture/know-01a`, `know-01b`, `know-disc-01`, `know-m1-b-fix01`, `af-turn-01b` | C/F | KEEP. Il destino è legato a D5. | Rapporti di implementazione delle ADR 0055–0057. | — | ADR e `docs/knowledge/*` |
| `agentic/_plans/2026-09-22-v1.3-f-filesystem-troubleshooting.md` | C | KEEP: i piani sono esclusi dall'archiviazione automatica | Orfano (nessun riferimento), compito completato. | — | 0 |

**Nessun altro documento è un candidato a ritiro.** In particolare:
- `agentic/obsolete/` (9 file) è già archivio, tracciato in Git.
- `docs/handoff-archive/` è la copia verbatim creata il 2026-10-21.
- Il rapporto `docs/maintenance/roadmap-reconciliation-01.md` è citato da HANDOFF e roadmap.

**Gli 8 file F con conflitto o destino pendente** sono le 7 ADR Proposed e `01-architecture-rules`.

## 7. Documenti da conservare

- **KEEP integrale:**
  - `agentic/_tasks/**` (register, PROCESS, README, 87 packet): le 381 menzioni di ADR nei packet sono riferimenti storici deliberati.
  - `agentic/_plans/**` e `agentic/_quality/**` (letti da CI e script).
  - `agentic/obsolete/**` e `docs/handoff-archive/**`.
  - `docs/benchmarks/**`, `docs/knowledge/**`, `docs/security/**`, `docs/operations/**`.
  - Le 17 guide operative in `docs/` radice, `docs/plugins`, `docs/agents` e `docs/maintenance`.
  - `HANDOFF.md`, `CHANGELOG.md`.
  - `docs/architecture/suppressions.md`.
- **Vincolati ai test o agli script. Non spostare né rinominare:**
  - `docs/benchmarks/know-bench-01a/` (`scenario.txt`, `scenario-c-addendum.txt`, `README.md`, `pack-source/`).
  - `docs/benchmarks/know-bench-01d/` (pack firmato, `test-trust.json`). Gli script `Prepare-KnowBenchmarkInputs.ps1` e i test lo leggono.
  - `docs/knowledge/examples/windows-event-triage-skills/`: letto da `Publish-KnowledgePack.ps1` e `KnowledgePublisherToolingTests`.
  - `docs/agents/delegation-policy.md`: `DelegationPolicyGuideTests` ne esegue il YAML.
  - `docs/benchmarks/replay-*` e `qwen-*`: la capture di `ReplayFixtureCapture` scrive sotto `docs/`.
- **UPDATE di accesso e precedenza** (nessun clone di contenuto):
  - Router e processo: `CLAUDE.md`, `agentic/README`, `00-bootstrap`, `05-workflow`, `PROCESS.md` (solo la riga "Architectural decisions"), roadmap §1/§16/§17, `agentic/_plans/2026-09-22-v1.3-coding-agent-execution.md`, `agentic/obsolete/README.md`.
  - Entry point pubblici: `README.md`, `CONTRIBUTING.md`, `SECURITY.md`, `docs/development/claude-code-skills.md`.
  - Link correnti verso file ADR in `docs/operator-configuration.md` (11), `deployment`, `system-events`, `system-maintenance`, `system-stability`, `process-diagnostics`, `network-diagnostics`, `windows-dump-analysis`, `docker-management`, `licensing`, `plugins/getting-started`, `knowledge/local-agent-pilot` e i 4 report `know-*`.
- **Nuovo file:** `AGENTS.md`, assente, da creare come punto d'ingresso breve che rimanda al router.

## 8. Stato e destinazione provvisoria delle 55 ADR

Il riparto è provvisorio: va confermato in DOC-02 leggendo ogni ADR per intero.

| Contratto | ADR |
|---|---|
| 01 MAF/infrastruttura | 0001, 0005, 0013, 0038, 0039, 0045, 0052. In sospeso (D1): 0053, 0054. |
| 02 Tool, safety, autorità | 0002, 0006, 0012, 0015, 0016, 0020, 0022, 0023, 0024, 0025, 0027, 0028, 0029, 0030, 0031, 0036, 0037, 0044, 0049. Con spec in `docs/`: 0026, 0033. Solo invarianti, il resto `historical-only` (spec nelle guide `docs/*.md`): 0021, 0034, 0035, 0048. |
| 03 Task/resume/mutazioni | 0014, 0017, 0040, 0046, 0047, 0051. In sospeso (D4): 0050. 0060 (raffina la 0050): 03 o 04, da decidere. |
| 04 Evidence/diagnosi | 0032, 0041, 0042, 0061 (e 0060). |
| 05 Interfacce/Knowledge/host | 0011, 0018, 0019, 0043, 0057. In sospeso (D5): 0055, 0056, 0058, 0059. |

Gli ID 0003, 0004, 0007–0010 non esistono: sono citati solo in `obsolete/piano-bops.md` come ADR mai scritte.

## 9. Matrice di copertura: invarianti critiche

La numerazione è provvisoria. Ogni riga deve avere una sezione assegnata nel contratto di destinazione prima di DOC-03.

| Fonte (origine) | Regola vigente | Contratto |
|---|---|---|
| `01 A1`, ADR-0006 | Il core non nomina package, tool o provider (test in CI) | 02 · confini package |
| `01 A2`, ADR-0011 | Confine tool serializzabile, solo `ToolCallResult` | 02 |
| `01 A3–A6`, ADR-0011/0036 | `NodeId`, registri per-nodo, policy dove si esegue, audit locale | 02 / 03 |
| `01 A7–A11`, ADR-0020/0025 | Package dipendono solo da Abstractions, servizi host chiusi, `PackageId` assegnato dall'host | 02 |
| `01 A13`, ADR-0052, D-048 | MAF è infrastruttura, mai autorità operativa. Nulla di MAF in Abstractions; ogni inferenza passa dal broker. | 01 |
| `03 S1`, `00-spec`, test architetturale | Nessun tool di esecuzione generico, sotto nessun nome | 02 |
| `03 S3`, ADR-0002/0015 | Policy fail-closed; `Critical` sempre `Forbidden`, mai configurabile | 02 |
| `01 B3`, `03 S4`, ADR-0016 | Non-Read senza verifica non registrabile; Inconclusive non è successo | 02 |
| ADR-0036, D-032 | Entitlement rivalutato prima di ogni invocazione, mai riusato | 02 |
| ADR-0030/0031/0044 | Deleghe che riducono i privilegi, approvazione solo umana | 02 |
| ADR-0027, D-018/D-022 | Cancellazione distruttiva legata all'hash del manifest | 02 |
| `03 S6`, ADR-0029 | Segreti redatti al confine; vault con chiave esterna | 02 |
| `03 S7/S8`, ADR-0037 | Timeout per tool; package in-process fidati; ciclo di vita plugin | 02 |
| `03 S11`, D-023, ADR-0028 | Path policy controllata subito prima dell'operazione; SSRF al connect | 02 |
| ADR-0045 | Pin e fallback del provider, credenziali fuori dal pin | 01 |
| `01 C1`, ADR-0013/0039 | Nulla di lanciato esce dall'iterazione; timeout vs cancellazione; retry limitato | 01 / 03 |
| `01 C3`, ADR-0052 §6–12 | Contesto compattato da MAF; autorità protetta; sintesi ≠ evidenza | 01 |
| `01 C4–C6` | Denial ripetuti, budget, enum di stato | 03 |
| `01 C8`, ADR-0014/0046/0047 | Piano esplicito, vista tool a passo singolo, replan transazionale | 03 |
| ADR-0017/0040 | Resume come transizione atomica; fencing per tentativo | 03 |
| ADR-0051, D-047 | Journal di intento, esito ignoto mai ripetuto, riconciliazione da amministratore | 03 |
| `01 B8`, `03 S9`, ADR-0015 | Audit completo, tamper-evident | 03 |
| `03 S5` + "typed metadata", `01 C7` | L'output dei tool è dato, mai istruzione | 04 |
| `01 C9`, ADR-0042 | Limiti dell'evidenza elencati dal runtime e sempre dichiarati nel finale | 04 |
| ADR-0041/0032/0061 | `complete` richiede copertura; assenza di prova ≠ prova di assenza | 04 |
| `03 S5` (dato esterno), ADR-0057 | Il turno di riferimento esterno è dato, non istruzione | 05 |
| ADR-0055/0056/0058/0059 | Trust store, pin, tenant | 05, **sezione Pending (D5), non vincolante** |
| ADR-0018/0043 | Autenticazione API/browser, CSRF, sessione server-side | 05 |
| D-013/D-016, ADR-0053 | Confine open-core | 05 |

Conflitti non risolvibili con le sole evidenze: 0050 (D4), 0053/0054 (D1), 0055/0056/0058/0059 (D5). Restano `REVIEW`. Il trasferimento di queste regole è bloccato finché l'operatore non decide.

## 10. Proposta di struttura finale e ZIP

**Struttura finale:**
- `docs/architecture/current/` con i 5 contratti. Nessun sesto contratto.
- `docs/architecture/adr/README.md` come indice/reindirizzamento.
- `docs/architecture/archive/README.md` e `legacy-adr-and-decisions-2026-10-22.zip`.
- `01-architecture-rules.md` ridotto a indice di invarianti con gli ID attuali.
- `06-decisions.md` come stub.
- `CLAUDE.md` e `AGENTS.md` come router.

**Lettura ordinaria dopo la migrazione.** Il carico stimato passa da ~249 KB circa a:
- `CLAUDE.md` + HANDOFF (~14 KB);
- il packet del task;
- gli S1–S11 di `03` se serve;
- uno o due contratti pertinenti.

**ZIP — contenuto minimo (57 file):** le 55 ADR, `06-decisions.md`, `01-architecture-rules.md`. Stima: 1,36 MB grezzi, ~526 KB compressi. L'ho misurata comprimendo in scratchpad, senza toccare il repository.

**Candidati aggiuntivi:** nessuno obbligatorio. Opzionali, solo con decisione esplicita:
1. `agentic/07-plan-corrections.md`.
2. `arch-02a/02r/02s`, solo dopo D1.
3. Le 5 relazioni `know-*` e `af-turn-01b`, solo dopo D5.

Si raccomanda di non includerli e di tenerli dove sono.

## 11. Impatto su DOC-02/03/04

- **Nessun blocco non docs-only.** Dai grep non risulta alcun caricamento di ADR, `06` o `01` da script, CI, test, `src` o `web`. Le uniche letture di `docs/` sono i fixture del §7.
- **Commenti nel codice.** Circa 370 file citano ID di ADR (non percorsi), e 51 file citano ID di `01`. Si lasciano invariati, come da piano. Il manifest dello ZIP (ID → contratto) li rende risolvibili. Se `01` sparisse, resterebbero pendenti.
- **Test di integrità.** Nessun controllo CI verifica i link nei documenti. DOC-04 userà un controllo statico proprio. Ne ho già uno in scratchpad che ha censito 1.249 riferimenti.
- **Costo di DOC-02.** Richiede la lettura integrale delle 46 ADR non ancora lette, più i report collegati. È il costo principale.
- **Link da correggere in DOC-03.** Una ventina di documenti correnti puntano a file ADR (§7). Circa 45 packet, `handoff-archive` e `obsolete` restano come riferimenti storici deliberati.

## 12. Decisioni richieste all'operatore (GATE-01)

1. **Governance del lavoro.** Autorizzare un id `BOPS-NNNN` con `authorized: true` (modifica separata del register), oppure derogare esplicitamente. Prima di DOC-03 serve anche l'identità Git locale corretta e una scelta sul trailer `Co-Authored-By`.
2. **Proposed pendenti (D1, D4, D5 già nel register).** Lasciare ADR 0050, 0053–0056, 0058, 0059 non promosse e in sezione "pending, non vincolante" (raccomandato), oppure deciderle ora.
3. **`01-architecture-rules.md`.** Ridurla a indice con ID stabili (raccomandato, perché 51 file la citano) invece di sostituirla. `06-decisions.md` diventa uno stub, perché 12 file di codice, `SECURITY.md` e `CONTRIBUTING` citano gli id D.
4. **Livello di dettaglio.** Le ADR hanno specifiche dense (vault, sessione browser, ciclo di vita plugin, SSRF, digest di evidenza, journal, indice Knowledge). Decidere se:
   - **(A)** i contratti tengono solo gli invarianti e il dettaglio resta in codice, test e guide `docs/` (raccomandato, con prova per ciascuna in DOC-02);
   - **(B)** alcune ADR (0042, 0043, 0044, 0051, 0057 e simili) restano come riferimento tecnico fuori dallo ZIP.
   Vale per 0051 e 0042, che non hanno una controparte in `docs/`.
5. **Perimetro dello ZIP.** Confermare solo i 57 file, oppure includere anche `07-plan-corrections` o altri.
6. **Riferimenti storici.** Lasciare i link ADR nei packet, nell'archivio e nel piano di hardening come storici deliberati (raccomandato). Correggere solo i ~20 documenti correnti.
7. **Precedenza.** Autorizzare esplicitamente la modifica normativa delle catene del §4, incluse roadmap §1/§16/§17 e `PROCESS.md` §1 (limitata alla riga "Architectural decisions").
8. **Fatti obsoleti.** Quali correzioni del §5 (C5–C9, C12) entrano in DOC-03 e quali restano follow-up separati (`HANDOFF` e register sono bookkeeping).
9. **`A12/D-012`.** Confermare che la regola "0.x fino a V1.0" è decaduta (il codice usa `1.x.y-preview`), per scriverla correttamente nel contratto.
10. **Report mancante.** `bench00-maf-compatibility-report.md` è citato ma assente nell'albero: segnalo soltanto. Non lo ricostruisco.

---

**Mi fermo a GATE-01.** Non ho avviato DOC-02, non ho committato né aperto PR. Attendo la tua approvazione e le scelte sopra per aggiornare il piano in un passaggio tracciato.
