# ROADMAP-RECONCILIATION-01 — stato reale di bOps e scelta del prossimo sviluppo

Data: 2026-10-21 · Base: `main` @ `09902a2` (verificato con `git rev-parse`; nessun commit successivo in locale) · Attività di sola lettura: nessun codice, test, ADR, roadmap o configurazione modificati; nessun branch/commit/push/PR; nessuna chiamata LLM; nessun benchmark live. Il file `src/core/bOps.Api/appsettings.json` ha una modifica locale preesistente (tuning timeout) che non è stata toccata.

Convenzioni: **[CODICE]** verificato leggendo il sorgente di `main` in questa sessione · **[TEST]** eseguito in questa sessione · **[DOC]** riportato da documentazione/benchmark, non riverificato · **[MASTER]** riportato da `bOps_Continuita_Master_2026-10-18.md` (Downloads; il report `PROMPT-RUNTIME-AUDIT-01` citato dal master non è stato riaperto).

---

## 1. Executive summary

**Stato del prodotto.** bOps `main` è un runtime agentico solido sul lato *governance*: policy, approvazioni, audit, journal delle mutazioni, circa 100 tool tipizzati Windows/Linux (102 visibili al planner nelle campagne), nessun tool generico di esecuzione. Il lato *flusso agentico* (finali premature, repliche troncate, risposte vuote, `length`) è stato corretto in modo sostanziale e misurato: con Qwen3.8 su 9 run replay 9/9 consegnano una risposta (Qwen3.6: 6/9). **Test eseguiti ora [TEST]**, Debug, Windows 11, variabili `BOPS_*` rimosse dal processo, `git status` identico prima/dopo: Architecture 210/210, Runtime 2301/2301, Knowledge 308/308, AgentFramework 615 passati + 8 ignorati (tutti `LiveModel`/operatore), System.Core 481/481. I due fallimenti storici di `bOps.Runtime.Tests` sono risolti (`3fe07db`). Non eseguiti: suite completa, Linux/WSL, CI (verde per dichiarazione dell'operatore), Packages.* Windows, Api, UI.

**Problemi principali (dimostrati).**
1. **La correttezza del contenuto delle diagnosi non è né verificata né misurata.** Con Qwen3.6 le finali `Completed` di T02-C sono risultate 2 FAIL + 1 PARTIAL (cascata presentata come fatto, `0x193`=NTFS, somma 492 invece di 490, 43 invece di 42); in RELEASE-VALIDATION-01 nessuno scenario diagnostico valutabile ha avuto PASS pieno. Il solo verificatore è lo *stesso LLM*, limitato ai fatti tipizzati di 5 famiglie di tool (events, stability, crashes, storage health, dump) e a un massimo di 12 fatti; ha restituito `contradictions:[]` in 4/4 casi con errori dimostrabili [MASTER]. Nessun test automatico misura queste classi di errore (AE-1/PA-1 misurano il *flusso* e il *testo dei prompt*, non le affermazioni).
2. **`Completed` non dice nulla sulla base probatoria.** L'esito del grounding e della disclosure vive solo nel marcatore testuale dell'ultimo step; non è esposto da API/CLI/UI [CODICE: nessun consumatore di `EvidenceGroundingOutcome` fuori da `bOps.Runtime`].
3. **La MAF non governa nulla di ciò che conta in produzione.** In produzione c'è solo `MafContextProjector` (proiezione/compattazione del contesto); `MafGovernedTurnBroker`, `ContextGate`, `GovernedChatClient` esistono e sono testati ma sono costruiti solo nei test [CODICE]. ADR-0053/0054 sono *Proposed*; ADR-0054 stesso stima ≈0 righe di codice di autorità rimovibili oggi.
4. **Il Knowledge non ha alcuna misura di beneficio.** Infrastruttura ampia (3.384 righe in `bOps.Knowledge`, decine di file di test/harness KNOW-BENCH), ma il confronto A/B/C non ha mai prodotto un risultato; il pilota è solo CLI, opt-in, ADR-0058/0059/D-050 *Proposed*; nessun playbook, nessun Experience Pack.
5. **Residui concreti e riproducibili nel codice:** rifiuto del passo semantico con messaggio statico (`AgentRunner.cs:1453`); nessun default di parametro nel catalogo del planner (`ToolParameter` non ha `DefaultValue`) e `SummarizeDescription` ancora diviso su `". "` (`AgentRunner.cs:5179`); `runtime.evidence_read` senza deduplica né indicatore di ripetizione (`EvidenceReadBudget.cs`, `EvidenceReadAuditEvent`); `process.list` ordinato solo per memoria, nessun ranking CPU; `service.list` senza startup type; `MaxObservationCharacters=4000` negli `appsettings` distribuiti mentre ogni validazione live usa 24000.

**Prossimo sviluppo raccomandato (2,5–4 giorni): `DIAG-ASSURE-01` — base probatoria deterministica accanto a `Completed`.** Una proiezione pura e senza modifiche di autorità, calcolata dagli step persistiti (completezza dell'evidenza, step falliti/non eseguiti, troncamenti non letti, esito del grounding e copertura dei fatti tipizzati), esposta in API/CLI/audit con codici motivo chiusi. Non rende le diagnosi più vere: rende *oggettivamente* distinguibile un `Completed` solido da uno qualificato/non verificato, elimina la dipendenza dal giudizio manuale di un singolo revisore per l'esito "diagnostico", ed è il prerequisito per ogni decisione successiva (modello, Knowledge, contratto di claim strutturati) senza altre settimane di benchmark. Alternative e motivazione in §6.

---

## 2. Matrice delle milestone

Stati: **CV** COMPLETATO E VALIDATO · **IN** IMPLEMENTATO, MA NON VALIDATO ADEGUATAMENTE · **PI** PARZIALMENTE IMPLEMENTATO · **NI** NON IMPLEMENTATO · **BL** BLOCCATO · **SO** SUPERATO/OBSOLETO · **DA** DECISIONE ARCHITETTURALE APERTA.
"Prova" distingue: *off* = test deterministici offline; *rep* = Qwen reale con output dei tool in replay; *live* = tool reali; *—* = nessuna.
Nessun ADR è dichiarato *Accepted* per via del merge o della presenza del codice: lo stato formale è quello scritto nel file dell'ADR.

### A. MAF e runtime agentico

| Voce | Stato | Prova | Evidenza | Dipendenze residue / decisione |
|---|---|---|---|---|
| AF-01 inferenza governata (`ModelInvocationBroker` + `MafContextProjector`) | **CV** | off + rep + live | `CliRunnerComposition.cs`, `ExecutionRunnerFactory.cs:88-90`; è l'unico percorso modello (AF-05 ha rimosso `BoundedHistory`) | ADR-0052 **Accepted**. Nessuna. |
| AF-02 compattazione del contesto | **IN** | off | `MafContextProjector.cs`; contesto non saturo nelle run live (max ≈16,7K su ≈24,6K) [MASTER] | Il beneficio non è mai stato isolato. H1 (operating range) deliberatamente non in `main`. |
| AF-03 summarization governata | **IN** | off | `GovernedSummarizationChatClient`, `ModelSummarization`; composta solo se configurata (`ExecutionRunnerFactory.cs:77`) | Nessuna misura di beneficio. Cross-provider nel CLI non fatto [DOC]. |
| AF-04 evidence cleanup / `runtime.evidence_read` | **IN** | off + rep | `EvidenceRead.cs`, `EvidenceIndex.cs` | Letture ridondanti ancora possibili (vedi AF-EVID-01). |
| AF-05 regressione/tuning/pulizia | **IN** | off (fixture) | Risultati di classe *deterministic-fixture* [DOC] | Valutazione live mai completata per questo gate. |
| AF-05G addressability + grounding | **PI** | off + rep | `EvidenceGrounding.cs`; grounding = giudizio dello stesso modello, ≤12 fatti, 5 famiglie di tool | Obiettivo (verifica delle affermazioni) non raggiunto: vedi §4.1. |
| AF-05H H2 (replan su finale prematuro) | **IN** | off + rep + live parziale | ADR-0046 emendata; `EarlyFinalRecovery.cs`; AE-1 | Qwen3.8: finali premature 1/3 su T10, ma R3 si è ripresentato (19 chiamate, 14 step pendenti) in PROMPT-ROUTING-VALIDATION-01 [DOC]. |
| AF-05H H4 (disclosure deterministica) | **CV** (meccanismo) | off | `FinalResponse.AppendedMarker`, `EvidenceLimitationsDigest.cs`; aggiunta osservata nelle run T02-C [DOC] | **AF-05H4-VERIFY (ablation): SO** — gate nato per A/B tra branch ormai integrati; il flag `EvidenceDisclosureEnabled` resta, ma la domanda non decide nulla. |
| AF-05H1/H3/H5 | **SO** (H1, H3: per decisione operatore) / **NI** (H5: differito) | — | assenti: `OperatingRange`, `CorrectionConflict`, `EvidenceReadGuidance` non compaiono in `src/` né `tests/` [CODICE]; backup cifrato e tag `backup/20261012/*` | H5 solo se emergono evidenze di beneficio. |
| AF-05H2-R1/R2 (replan troncato, continuazione) + LIVE-05 | **IN** | off + live parziale | `EarlyFinalRecovery.cs:21-78`; task live `41710469` ha percorso il recupero (cursore 0) [DOC] | Cursore 1 non rivalidato; BENCH-01 (1×3 mirato) mai eseguito. |
| AF-PLAN-01 / AF-TOOLROUTE-01 | **IN** | off + rep | `ToolRoutingRegressionTests`, AE1-P1; 0 chiamate a tool non offerti nelle run replay recenti [DOC] | n≤3 per campagna. |
| LIVE-F1 (finale senza contenuto) `06cd4aa` | **CV** | off | 79 test, 20 falliscono senza la guardia [MASTER] | Nessun rerun live (non necessario per la proprietà deterministica). |
| AF-CONTEXT-01 (`ContextOrigin`, `ContextGate`, provenance) | **PI** | off | `ContextGate.cs` usato solo da `GovernedChatClient.cs:125` | Implementato e testato, **non composto in alcun host**. |
| AF-TURN-01A/B (`MafGovernedTurnBroker`, `BopsContextProvider`) | **PI** | off | `MafGovernedTurn.cs`; costruito solo in `tests/` [CODICE] | Idem. Il doc `af-turn-01b-context-skills-bridge.md` è parzialmente superato (il runner oggi serve `runtime.knowledge_read` nel pilota CLI). |
| ARCH-01 (ADR-0053) | **DA** | — | ADR **Proposed** | Decisione dell'operatore (§7). |
| ARCH-02 (spike MAF) | **CV** come spike (sintetico, offline) | off | Report in `docs/architecture/arch-02*.md`; **codice solo in tag/branch locali** `archive/arch-02{a,r,s}-spike` | Riferimenti a `spikes/…` pendenti nei report. |
| ARCH-03 (ADR-0054, B+) | **DA** | — | ADR **Proposed**; §5/§6 descrivono come futuro codice già in `main` | Decisione dell'operatore (§7). |
| Planning/step authority: ADR-0046, 0047 | **CV** | off + rep + live | ADR **Accepted and implemented**; `AgentRunner.cs` | — |
| ADR-0050 (esecuzione semantica degli step) | **DA** | off + rep | ADR **Proposed**, **nessun D-number**, ma il codice la applica (`plan.SemanticContractVersion == 1`, `SemanticExpectedArguments`, `AgentRunner.cs:1431-1456`) | Un meccanismo centrale del runtime poggia su un ADR non accettato (§7). |
| Governance dei tool (policy, approval, journal ADR-0051, entitlement, audit) | **CV** | off + live | Architecture 210/210 [TEST]; ADR-0051 **Accepted** | La sessione manuale M1–M24 del journal F-25 risulta "da registrare" in HANDOFF (2026-10-10): non verificato se chiusa. |

### B. Knowledge

| Voce | Stato | Prova | Evidenza | Dipendenze / decisione |
|---|---|---|---|---|
| KNOW-01A fondazione fidata (pack, verifier, trust, admission) | **IN** | off | `bOps.Knowledge` 308/308 [TEST]; `KnowledgePackVerifier.cs` | ADR-0055 **Proposed**. |
| KNOW-01B `runtime.knowledge_read` governato (ADR-0056) | **IN** | off + rep | `KnowledgeReadBridge.cs` (1.318 righe), `KnowledgeRunnerIntegrationTests` | ADR **Proposed**. |
| KNOW-DISC-01 indice (ADR-0057) M1 T1–T8 | **IN** | off | ADR **Accepted rev. 2** (solo sviluppo M1); gate M1 offline PASS dopo T8-FIX01 [DOC] | **B1 BL** (revoca grant/chiave durante il task); **B2** (due messaggi `user` consecutivi sul server reale) non verificato. M2 (manifest v2) e M3 (misura) **NI**. |
| Pilota Local Agent CLI (ADR-0058/D-050) | **IN** | off + rep | `CliKnowledge.cs`, `LocalKnowledgeComposition.cs`; `Knowledge:Enabled=false` di default | ADR e D-050 **Proposed**. API: nessuna composizione [CODICE]. |
| Ripresa dai rifiuti (ADR-0059) | **IN** | off | `KnowledgeRefusalRecoveryTests` | **Proposed**. |
| Trust/signing/pinning | **IN** | off | verifica firma, pin a inizio task | Rotazione/revoca live = B1. |
| Activation atomica, hot reload, più pack, binding per goal | **NI** | — | ADR-0058 §6–7 "designed only" | — |
| KNOW-02 playbook diagnostici / Experience Pack | **NI** | — | nessun interprete di playbook in `src/`; un solo pack di esempio (`windows-event-triage`) | Dipende dal beneficio misurato (KNOW-03). |
| KNOW-03 / KNOW-BENCH A/B/C | harness **IN**, misura **NI** | off | `KnowledgeBenchmark*.cs`; l'unico tentativo A/B (FIX12) è girato su un commit senza indice [DOC: `knowledge-discovery-investigation.md`] | Nessun risultato di qualità prodotto. |
| Evidenza d'uso con Qwen | — | rep | T10: Qwen legge `K1.R1` correttamente (3/3 Qwen3.6; 3/3 Qwen3.8); in T02-C r2 "il runbook letto non impedisce l'errore" [DOC] | Il beneficio *diagnostico* resta non dimostrato. |

### C. Affidabilità diagnostica

| Voce | Stato | Prova | Evidenza | Dipendenze / decisione |
|---|---|---|---|---|
| BENCH-00 collector (Python, PR #102) | **IN** | off (test del collector su 2 OS) | `scripts/windows/live_benchmark.py`, CI | Il 3×3 ufficiale **mai eseguito**; **gate come progettato SO** (confrontava due varianti ora entrambe in `main`). |
| BENCH-MATRIX-02 | **NI** (nessun risultato) | — | `scripts/windows/BENCH-MATRIX-02.md` | Da riprogettare sul `main` attuale. |
| LIVE-01 (taglio osservazioni) | **PI** | rep | smoke a 24000; default distribuito 4000 [CODICE] | Vedi §4.5 e §7-D3. |
| LIVE-01B `PlanningGuidance` (ADR-0060 **Accepted**) | **IN** | off + live parziale | `ToolParameter.PlanningGuidance`; guidance osservata nel planner | Efficacia generale non dimostrata. |
| LIVE-02 (ragionamento degenerato) | **NI** (sospeso) | — | — | Qwen3.8: 0 `length` nelle 74 richieste [DOC]. |
| LIVE-03 (classificazione `TruncatedBeforeContent`) | **PI** | off | `ModelOutputClassifier.cs:36` classifica; esito/recovery a livello task non definiti [MASTER] | Bassa priorità con Qwen3.8. |
| LIVE-04A/04C (`sampleMessageSemantics`, `messageDistribution`) | **IN** | off + 1 caso live | prima lettura corretta su WHEA in T02 [MASTER] | Un solo caso. LIVE-04B (grounding semantico) **NI**. |
| LIVE-05 | **IN** | off + live parziale | `EarlyFinalRecovery.ContinuationTurn` | vedi sopra. |
| SYSTEM-EVENTS-SOURCE-MATCH (ADR-0061 **Accepted**) | **IN** | off + WSL | `SourceFilterEvidence.cs`; 481/481 System.Core [TEST] | Nessun rerun T03 live. |
| PROMPT-ROUTING-FINALIZATION (PRA-R2…R5, P1) | **IN** | off + rep n=1/caso | `PromptRoutingFinalizationTests`; PROMPT-ROUTING-VALIDATION-01 | R3 non dimostrato efficace; percorso RC1/RC2 live non esercitato. |
| PROMPT-RUNTIME-AUDIT-01 | **CV** come audit | — | Risolti R2–R5, P1. **Aperti:** M1 (startup type), M3 (catalogo), M4 (CPU ranking), M5 (`fs.*` offerti ma non usabili), M6–M8, R5 (EvidenceIndex), V1 (grounding) | Confluiscono nel backlog (§5). |
| PA-1 (assemblaggio richieste) / AE-1 (scenari scriptati) | **CV** come caratterizzazione | off | 12 scenari golden PA-1; 13 casi AE-1 (T02 ×5 incluse varianti e controllo, T05 ×2, T06 ×2, T08 ×2, T10, P1) | Misurano il *flusso*; **nessun caso T01/T03/T04/T-claims**. |
| `LiveRegressionHarness` | **CV** | off | È una fixture con modello scriptato (non live, nonostante il nome) [CODICE] | — |
| AF-EVID-01 | **NI** | — | `EvidenceReadBudget` conta solo letture servite (`MaxAttempts=4`); `EvidenceReadAuditEvent` senza `Repeat`; `EvidenceIndex` elenca ogni step indirizzabile anche se già visibile per intero [CODICE] | Costo, non correttezza. |
| EVIDENCE-INDEX-VISIBILITY (PRA-R5) | **NI** | — | idem | idem |
| GROUNDING-FACTS e controllo delle contraddizioni | **PI** | off | fatti tipizzati solo per events/stability/crashes/storage-health/dump-analyze; check = stesso LLM | §4.1. |
| AGENT-QUALITY-GATE (master §7.8) | **PI** | off | livelli 1–2 presenti (suite CT/PA in CI); livello 3 solo per il flusso; `agentic/_quality/active-milestone.json` ancora a **V1.3-H** | Manca il gate sul contenuto. |

### D. Funzionalità sistemistiche

| Area | Stato | Evidenza / limite |
|---|---|---|
| Eventi, stabilità, crash, dump | **IN** | `system.events/stability/crashes/dump_analyze`; KD reale validato una volta (`100626-20984-01.dmp`), DumpChk in timeout al proprio limite [DOC]. Nessun mapping codici bugcheck nel package. |
| CPU/RAM/swap/IO | **PI** | `system.cpu` = una percentuale; `system.memory` = totale/disponibile; `system.io` solo throughput; nessun fatto tipizzato (T01). |
| Processi | **PI** | `process.list` ordinato per working set; `process.metrics` per un solo PID; **nessun ranking CPU** (PRA-M4). |
| Servizi | **PI** | `service.config` ha `startupType`, `service.list` no (PRA-M1). |
| Rete, firewall, TLS, DNS | **IN** | 15+ tool; `network.interfaces` rumoroso, `firewall.rules` con rischio di falsi negativi sul filtro porta (P5) [MASTER]. |
| Docker | **IN** | `docker.*`; Linux con Docker reale in CI (HARDEN-14). Output di inspect/log da arricchire (P10). |
| Filesystem | **IN** | `fs.*` offerti al planner anche quando `ReadPatterns` vuoti li rende inutilizzabili (PRA-M5). |
| Sensori/GPU/temperature | **NI** | nessun tool; T08 (dato non disponibile) gestito: Qwen3.8 3 PASS su 3, Qwen3.6 1 PASS / 1 PARTIAL / 1 non valutabile [DOC]. |
| Prestazioni storiche (contatori, Diagnostics-Performance) | **NI** | `system.events` accetta `channel`, ma nessun tool fornisce storia di carico; solo campioni istantanei. |
| Skill DBA PostgreSQL/SQL Server | **NI** (fuori repo) | Prerequisiti OSS: host foundation già disponibile; i tool DB-nativi restano nel repo commerciale. Requisito trasversale: ogni skill che produce misure deve emettere fatti tipizzati (vedi §5, HOST-SATURATION-01). |

---

## 3. Architettura effettiva

**Cosa fa realmente la MAF.** `Microsoft.Agents.AI [1.24.0]` è usata in produzione solo attraverso `MafContextProjector`: proietta e compatta la storia (AF-02) prima della chiamata del broker; opzionalmente `GovernedSummarizationChatClient`. Il turno non è ospitato da `ChatClientAgent`: `MafGovernedTurnBroker`, `BopsContextProvider`, `ContextGate` e `GovernedChatClient` esistono in `bOps.AgentFramework` ma sono costruiti solo nei test. L'unica altra parte della MAF in produzione è il bridge Knowledge (`KnowledgeReadBridge` via `KnowledgeTaskHost`, composto solo dal CLI quando `Knowledge:Enabled=true`), che invoca le funzioni skills del framework dal lato bOps. Pianificazione, esecuzione, validazione, policy, approvazioni, journal, resume e finalizzazione restano interamente in `AgentRunner` (5.821 righe) e nei suoi collaboratori.

**Cosa governa `AgentRunner`.** Piano (revisioni, cursore, step con `ExpectedTool` e argomenti attesi, ADR-0046/0047/0050); un solo tool operativo per step più al massimo due funzioni di controllo (`runtime.evidence_read`, `runtime.knowledge_read`); finale prematuro → replan (ADR-0046 emendata) con continuazione LIVE-05; guardie sulla risposta (`FinalAnswerContent`, vuota, artefatto di protocollo); disclosure deterministica delle limitazioni; check di grounding + una correzione; budget (step, replan, token, durata); tutta la telemetria e l'audit.

**Come funziona il Knowledge.** Pack firmati e pinnati a inizio task → indice deterministico pubblicato solo nelle richieste di step (non nel planning) → lettura per riferimento `K1`/`K1.R1` tramite `runtime.knowledge_read` → contenuto in turno `Tool` delimitato, mai `System`. Autorità rivalutata a ogni lettura; letture/rifiuti/caratteri a budget; rifiuti recuperabili (ADR-0059). Il contenuto è contesto da corroborare, mai evidenza.

**Non collegato (verificato nel codice).** Knowledge nell'API; `MafGovernedTurnBroker` in qualunque host; playbook; Experience Pack; manifest v2 (applicabilità firmata); ranking dell'indice per piattaforma; fatti tipizzati per CPU/RAM/processi/servizi/rete; esposizione dell'esito del grounding a consumatori esterni.

**Divergenza tra configurazione validata e configurazione distribuita [CODICE + DOC].** Ogni campagna live recente usa `MaxObservationCharacters=24000` (e contesti 32K/64K, timeout 4–11 min); gli `appsettings` di API e CLI distribuiscono `4000`, e i timeout CLI di default (120/150/300 s) sono incoerenti con un modello locale a ~22–60 token/s (HANDOFF 2026-10-17). Il prodotto *come distribuito* non è mai stato validato con un modello reale su evidenze di dimensione reale (un aggregato `system.events` reale è 22.254 caratteri).

---

## 4. Gap funzionali e qualitativi

### 4.1 Difetti dimostrati
Per ciascuno: stato nel codice corrente · evidenza · cosa *non* è dimostrato.

| # | Problema osservato | Stato in `main` | Evidenza | Limite della prova |
|---|---|---|---|---|
| 1 | **PC lento, CPU/RAM**: conclusione di "pressione RAM" con CPU ~1,9%, RAM ~54%, 14,8 GB liberi; somma errata di processi | **Aperto.** Nessun fatto tipizzato per cpu/memory/process; `system.memory` non espone percentuali; nessun caso AE/PA | RV-01 T01 [MASTER] | n=1, Qwen3.6, tool reali; errore di interpretazione che nessun campo di tool può escludere da solo |
| 2 | **Crash/WHEA**: filtro `source` non canonico → falso "zero WHEA" (126 record) | **Mitigato** dalla disclosure `sourceFilter` (ADR-0061) | RV-01 T03; `SourceFilterEvidence.cs`; 481 test | nessun rerun live; la disclosure informa, non impedisce l'inferenza |
| 3 | **Affermazioni errate in finali `Completed`** (T02-C: cascata come fatto, `0x193`→NTFS, 492≠490, 43≠42, reported≠occurred) | **Aperto.** Il check non rileva errori su fatti non tipizzati; una correzione è accettata se la citazione contestata sparisce, anche lasciando altri errori | `t02-grounding-01.md` (12 test di caratterizzazione `KnownDefect_*`) | n=3 Qwen3.6; Qwen3.8 sulla stessa evidenza: 3 PARTIAL con errori minori (nessuno sulle conclusioni) |
| 4 | **Finali premature / passi omessi** | **Largamente corretto.** H2, R1/R2, LIVE-05, F1, PRA-R3. Residuo: Knowledge-only goal con piano operativo (T10) | Qwen3.6 T10: 3 finali respinte per run, 2/3 senza risposta consegnata; Qwen3.8: 1/3 e 9/9 consegnate; post-PRA su Qwen3.8 R3 ricomparso una volta | n≤3; solo con Knowledge opt-in |
| 5 | **Replan/reasoning loop e token** | **Mitigato.** Qwen3.6: 8/72 chiamate `length` (11%), ~29% dei completion [MASTER]; Qwen3.8: 0/74 | `qwen-live-baseline-01.md`, `qwen38-direct-comparison-01.md` | cambiano insieme modello, contesto, `max_tokens`, timeout: non isolato |
| 6 | **Letture Evidence ridondanti** | **Aperto.** 39 richieste proposte vs 16 (B02 AF-05H); T02-r2 3 letture identiche; R5 non fatto | `agentic/_plans/…maf-knowledge-convergence.md`; `EvidenceReadBudget.cs` | proposte ≠ servite; costo, non correttezza |
| 7 | **Rifiuto di step opaco**: "arguments did not match the current planned step" | **Aperto** (`AgentRunner.cs:1453`) | 7 occorrenze Qwen3.6 con chiamate identiche ripetute; 5 con Qwen3.8, una volta scartando l'URL *corretto* a favore di quello inventato dal piano | |
| 8 | **Argomenti/default ignoti al planner** (finestra "24h" assunta contro default 1 h in T04; `maxOutputBytes`; `windowDays` vs `windowMinutes`) | **Aperto.** `ToolParameter` senza `DefaultValue`; 19 descrizioni troncate a 120 caratteri, 3 con sommario identico [MASTER] | T04, T02 | catalogo ≈85% del system prompt |
| 9 | **Tool non disponibili/non autorizzati/insufficienti** | **Aperto in parte.** `fs.*` offerti ma inutilizzabili; nessun CPU ranking; nessun sensore; `web.fetch` è `Read` ma fa egress | T06, T08, T05 | |
| 10 | **Risposte parziali presentate come complete** | **Mitigato nel testo** (disclosure deterministica), **non** nei dati strutturati | `EvidenceLimitationsDigest.cs`; T02-C r1 | vedi §4.2 |
| 11 | **Qwen locale, budget limitati** | Modello più forte = leva maggiore osservata; Qwen3.8 ~2,7× più lento (22 vs 59 tok/s), 4 chiamate >240 s | `qwen38-direct-comparison-01.md` | n=3, un revisore, replay con 25 `UNSUPPORTED_REPLAY` |

### 4.2 `Completed` ≠ successo diagnostico
Le campagne T02-C (3/3 `Completed`: 0 PASS, 1 PARTIAL, 2 FAIL) e RV-01 (nessun PASS diagnostico pieno tra gli scenari valutabili) lo mostrano. Il runtime possiede già, in forma persistita e deterministica, tutti gli ingredienti per dire *su cosa* poggia un `Completed`: `ToolResultCompleteness` per step, `Outcome` e `FailureKind`, marcatori di disclosure/grounding dell'ultimo step, `PlannedStepExecutionClassification`, l'indice delle osservazioni accorciate. Oggi nessuno li aggrega né li espone.

### 4.3 Limiti delle prove disponibili (da non dimenticare nelle decisioni)
- Tutti i giudizi diagnostici dei report T02-C / Qwen3.8 / PROMPT-ROUTING-VALIDATION sono di **un solo revisore** (l'autore del report), **n=1–3** per caso, temperatura 1.0, seed casuale.
- Le campagne 2026-10-18/19 usano **replay degli output dei tool**: i tool reali non sono eseguiti; 7–25 `UNSUPPORTED_REPLAY` per campagna alterano la copertura. T02 su Qwen3.8 non ha mai ricevuto `system.events`.
- La suite deterministica AE-1/PA-1 usa modelli scriptati: prova la *meccanica*, non il comportamento di Qwen.
- Nessuna campagna ha usato la configurazione distribuita (4000 caratteri di osservazione).
- Il contesto temporale congelato non è dichiarato al modello nel replay.

### 4.4 Ipotesi (non dimostrate)
- Fatti tipizzati aggiuntivi per cpu/memory *migliorerebbero* le diagnosi di tipo T01: plausibile, ma l'errore osservato è interpretativo e il checker è lo stesso LLM.
- Il passaggio a Qwen3.8 abbassa l'incidenza degli errori T02-C: coerente con 9/9 vs 6/9 e con le correzioni qualitative, ma confondibile (budget, contesto, timeout).
- Il Knowledge migliora la diagnosi: nessuna evidenza oltre al caso Knowledge-only T10.

### 4.5 Rischio di configurazione
Con `MaxObservationCharacters=4000`, un aggregato reale da ~22K caratteri arriva al modello per ~18%; `runtime.evidence_read` ha al più 4 letture servite per chiamata logica (`EvidenceRead.MaxAttempts=4`); il limite di lunghezza per lettura non è stato verificato, quindi il recupero completo di un aggregato così grande non è dimostrato. I fatti tipizzati sono calcolati dal risultato del tool (non dall'osservazione), il che mitiga per le 5 famiglie coperte e non per le altre. L'impatto sul prodotto distribuito **non è misurato**.

---

## 5. Backlog aggiornato

Priorità: **P0** difetto che invalida la fiducia nelle diagnosi o la sicurezza · **P1** alto impatto, effort contenuto · **P2** utile/costo · **P3** rinviabile. Effort in giorni di sviluppo (singolo sviluppatore), intervallo.

| ID | Attività | P | Valore e evidenza | Effort | Dipendenze | Gate |
|---|---|---|---|---|---|---|
| **DIAG-ASSURE-01** | Base probatoria deterministica accanto a `Completed` (§6) | **P0** | §4.2; abilita misure oggettive e decisioni | 2,5–4 | nessuna | test su stati reali (§6.10) |
| PLAN-FIDELITY-01 | Rifiuto di step informativo; `DefaultValue` additivo nel catalogo (con conformance manifest↔default); `SummarizeDescription` robusto e sommari non identici | P1 | §4.1 #7, #8 | 3–4,5 | nessuna (PA-1 golden cambiano deliberatamente) | conteggio rifiuti/chiamate ripetute su artefatti congelati ↓; nessuna regressione AE/PA |
| AF-EVID-01 (ridefinito) | `Repeat`/`Duplicate` in `EvidenceReadAuditEvent`; segnare in `EvidenceIndex` gli step già visibili per intero; deduplica *non bloccante* delle letture identiche entro la stessa chiamata logica | P2 | §4.1 #6 (costo) | 1,5–2,5 | DIAG-ASSURE-01 (motivo `ObservationTruncated` richiede la nozione di "visibile per intero") | proposte/servite/duplicate misurabili; coverage non cala |
| HOST-SATURATION-01 | `process.list` con campione CPU (due letture) o tool dedicato; `startupType` in `service.list`; fatti tipizzati per cpu/memory/swap/io e `process.*`; `system.memory` con percentuali e indicatori di pressione neutrali | P1 | T01, T05; PRA-M1/M4 | 4–6 (Win+Linux, conformance) | DIAG-ASSURE-01 (per mostrare "non verificato" prima e "verificato sui fatti" dopo) | conformance su entrambe le piattaforme |
| LIVE-GATE-01 | Campagna live a tool **reali** con Qwen3.8 sulla configurazione *distribuita*: T01–T10 di RV-01, n≥3, esito via `DIAG-ASSURE` + revisione cieca | P1 | §4.3 (evidenza attuale = replay, n=1–3, config diversa) | 1 (sviluppo) + tempo macchina | DIAG-ASSURE-01; autorizzazione operatore per le chiamate live | decide DIAG-CLAIMS e Knowledge |
| DIAG-CLAIMS-01 | Esperimento minimo (prima di qualunque ADR): corpus di *claim ledger* indipendenti dal revisore su finali già persistite (T02-C ×3, Qwen3.8 ×3, T01/T03/T04), misura offline del recall dell'attuale grounding (risposte del checker già registrate) | P1 | §4.1 #3 | 2–3 | LIVE-GATE-01 (opzionale), DIAG-ASSURE-01 | GO/NO-GO al contratto di claim strutturati (proposta di `t02-grounding-01.md`) |
| KNOW-VALUE-01 | Misura A/B/C minima del beneficio Knowledge su 2 scenari con tool reali | P2 | §3: nessuna misura | 2 + tempo macchina | LIVE-GATE-01 | decide se KNOW-02/03 proseguono |
| KNOW-DISC B1/B2, M2/M3 | Revoca durante il task; verifica B2 sul server; manifest v2; misura | P2 | ADR-0057 | 3–5 | KNOW-VALUE-01 | — |
| MAF-CONVERGENCE (ARCH-03 / AF-TURN in produzione) | Adozione di `ChatClientAgent` come host del turno | P3 | ADR-0054: ≈0 righe di autorità rimovibili oggi | 5–10 | decisione §7-D1; G2 | ADR-0054 gate §4 |
| QUALITY-GATE-DOCS | Aggiornare `agentic/_quality` oltre V1.3-H e riallineare roadmap/handoff | P2 | §8 | 1 | — | — |
| TOOL-CONTRACT P1–P16 residui | Per rischio, non in blocco (firewall.rules, sockets, Docker inspect, scheduler…) | P2–P3 | [MASTER] §8 | variabile | — | — |
| LIVE-02, LIVE-03, LIVE-04B | Sospesi | P3 | Qwen3.8 non li manifesta | — | LIVE-GATE-01 | riaprire solo con nuova prova |
| H5 | Guida EvidenceRead | P3 | differito | — | AF-EVID-01 misurato | evidenza di beneficio |
| Commercial DBA skills | Fuori repo | — | — | — | HOST-SATURATION-01 (fatti tipizzati come contratto) | — |

---

## 6. Prossimo task raccomandato

### DIAG-ASSURE-01 — Base probatoria deterministica del task (affiancata a `Completed`)

**6.1 Identificativo e nome.** `DIAG-ASSURE-01 — Evidence assurance projection`.

**6.2 Problema.** Un task `Completed` non comunica su quale base probatoria poggia la risposta. In T02-C 3/3 run erano `Completed` con diagnosi 0 PASS / 1 PARTIAL / 2 FAIL; in RV-01 nessuno scenario diagnostico valutabile ha avuto PASS pieno (1 PASS = controllo di policy, 6 PARTIAL, 1 FAIL, 1 inconcludente). Ogni valutazione ha richiesto un revisore umano. Chi usa bOps (CLI/API/UI) e chi lo misura (benchmark, gate) non ha un indicatore *deterministico* e *non dipendente dal modello* che distingua "evidenza completa e controllata" da "evidenza parziale/non controllata".

**6.3 Evidenza nel codice attuale [CODICE].** `FinalResponse.cs` codifica l'esito di disclosure/grounding in 16 stringhe nel campo `Description` dell'ultimo step; `EvidenceGroundingOutcome` ha 4 valori e nessun consumatore fuori da `bOps.Runtime`; `TaskTerminalKind.Completed` è l'unico segnale persistito/auditato (`FinishAsync`, `AgentRunner.cs:2730`); `ToolResultCompleteness`, `FailureKind` e la classificazione degli step sono persistiti ma non aggregati; né `bOps.Api` né `bOps.Cli` né l'UI ne parlano.

**6.4 Conseguenze se non implementata.** Il giudizio sulla qualità resta manuale, a campione e di un solo revisore; ogni futuro confronto (modello, Knowledge, prompt) resta non riproducibile; l'operatore non ha un segnale per decidere quanto fidarsi di un `Completed`.

**6.5 Soluzione tecnica minima.**
- Funzione **pura** `DiagnosticAssurance.From(TaskState)` in `bOps.Runtime` (come `ExecutionPlanProjector`): nessuna scrittura, nessuna modifica di stato, nessun cambio di prompt/policy/autorità; vale quindi anche per i task storici.
- Output: `Level` ∈ {`Qualified`, `Unchecked`, `Checked`, `NotApplicable`} e una lista di **codici motivo chiusi** con conteggi (nessun testo di modello o di tool):
  `PlannedStepFailed`, `PlannedStepNotExecuted`, `EvidencePartial`, `EvidenceUnavailable`, `ObservationShortenedNotRead`, `NoTypedFactsForStep` (step il cui tool non produce fatti tipizzati), `GroundingNotChecked`, `GroundingNoContradictionCited` (specificato come *giudizio del modello*), `GroundingCorrected`, `GroundingCheckUnavailable`, `FinalAfterRecovery`.
  Regola: `Checked` solo se nessun motivo di qualifica e ogni step di raccolta ha fatti tipizzati controllati; `Qualified` se esiste evidenza parziale/non disponibile/step fallito/correzione; `Unchecked` se l'evidenza è completa ma le affermazioni non sono controllabili (tool senza fatti); `NotApplicable` per task senza tool diagnostici (es. solo Knowledge, policy-denied).
- Esposizione: campo additivo in API (DTO del task), riga nel riepilogo del CLI, e aggiunta al `TaskLifecycleAuditEvent` di `ExecutionTerminal` (campo opzionale `Assurance`), tutti nullable. **Esclusa** la UI Angular (follow-up).
- Il nome dei livelli e il testo non devono suggerire verità ("verified"): il livello più alto significa *"nessuna limitazione rilevata dal runtime"*, non *"diagnosi corretta"*.

**6.6 Componenti e file presumibilmente interessati.** `src/core/bOps.Abstractions/` (nuovi tipi `DiagnosticAssurance`, `AssuranceReason`; campo opzionale in `Audit.cs` ~r.842-870); `src/core/bOps.Runtime/` (nuovo `DiagnosticAssurance.cs`; uso in `AgentRunner.FinishAsync`, `AgentRunner.cs:2730`); `src/core/bOps.Api/Contracts.cs` e il controller dei task; `src/core/bOps.Cli/` (stampa); `src/core/bOps.Audit/AuditJsonContext.cs` (serializzazione); `docs/` (una sezione in `operator-configuration.md` o `system-stability.md`) — nessun nuovo ADR se resta una proiezione di sola lettura; se si aggiunge un campo persistito va valutato l'emendamento ad ADR-0042 (da decidere in fase di revisione).

**6.7 Dipendenze.** Nessuna sul codice. Per la verifica con stati reali servono i `tasks.db` già conservati in `C:\source\repos\babbubba\bOps-backups\` (fuori repo, sensibili: usare copie minimizzate/fixture sintetiche nei test committati).

**6.8 Invarianti da preservare.** Nessun tool nel core (regola A1): i motivi dipendono da `Completeness`/`FailureKind`/presenza di `EvidenceFact`, mai da nomi di tool o pacchetti. L'output è dato, mai istruzione, e non entra nel contesto del modello. Nessun effetto su policy, approvazione, journal, resume, budget. L'esito non cambia lo `Status` del task. Audit: solo codici e conteggi. Il campo è opzionale: nessun cambio di schema per i consumatori esistenti.

**6.9 Test da aggiungere.**
1. Unit su `DiagnosticAssurance.From` per ogni motivo e per le combinazioni (tabella-guidata).
2. Stati sintetici derivati da casi reali: T02-C r1/r2/r3 (evidenza Partial, grounding Corrected/NoContradiction) → `Qualified` con motivi attesi; controllo "tutto Complete + fatti tipizzati + nessuna contraddizione" → `Checked`; T01 (cpu/memory senza fatti) → `Unchecked/NoTypedFactsForStep`; T10 Knowledge-only → `NotApplicable`; T05 recuperato da `>`/F1 → `FinalAfterRecovery`.
3. Proprietà: pura e deterministica (stessa uscita live e dopo resume); indipendente dal testo del modello (mutazione del testo finale non cambia il livello).
4. Architettura: `bOps.Architecture.Tests` — nessun riferimento a nomi di tool/pacchetti in `DiagnosticAssurance`.
5. Contract: serializzazione API/audit (campo assente per task senza progetto; presente per `Completed`).
6. Mutazioni mirate (almeno 3): ignorare `Partial`, ignorare `GroundingCorrected`, trattare `NoContradictionCited` come `Checked`.

**6.10 Criteri di accettazione misurabili.**
- Almeno 10 scenari di test (tabella §6.9-2) verdi; 3 mutazioni su 3 uccise.
- Su ciascuno dei 12 stati reali minimizzati (T02-C ×3, Qwen3.8 T02 ×3, RV-01 T01/T03/T04/T05/T06/T10 dove disponibili) il livello coincide con la valutazione attesa scritta *prima* di eseguire (`Checked` mai assegnato a un caso giudicato FAIL; falsi `Checked` = 0).
- `dotnet build` 0 warning; Architecture, Runtime, AgentFramework, Api, Cli test verdi; nessun golden PA-1/AE-1 cambiato (nessun cambio di prompt).
- `Completed` e tutti gli altri `Status`/`TerminalKind` invariati su tutte le fixture esistenti.

**6.11 Effort.** Nominale 3 giorni; intervallo 2,5–4. Incertezze: forma del DTO API/CLI (una mezza giornata se serve un controller test in più), eventuale emendamento ADR-0042 (+0,5), reperibilità di stati reali minimizzabili (+0,5). Esclusa la UI.

**6.12 Rischi e regressioni.** (a) Falso senso di sicurezza: un `Checked` percepito come "vero" → denominazione e documentazione esplicite, mai "verified"; (b) fatica da allarme se quasi tutto è `Unchecked/Qualified` — è un dato (oggi lo è davvero) e rende visibile la priorità di HOST-SATURATION-01; (c) serializzazione audit: campo opzionale e `WhenWritingNull`; (d) deriva tra motivo e realtà se si aggiungono tool con fatti: i motivi sono derivati dalla presenza di `EvidenceFact`, non da una lista.

**6.13 Validazione.** Offline: come §6.9-6.10. Gate live successivo = `LIVE-GATE-01` (autorizzato dall'operatore): `Assurance` come colonna oggettiva accanto alla revisione cieca; nessuna chiamata LLM è necessaria per DIAG-ASSURE-01 stesso.

**6.14 Perché ha priorità sulle alternative.**
- **Struttura del contratto di claim (DIAG-CLAIMS/ADR)**: è il rimedio vero al difetto #3, ma `t02-grounding-01.md` stesso dimostra che richiede schema, responsabilità core/package e regola di rendering → ADR + oltre 5 giorni; farlo senza misura è esattamente il rischio che si vuole evitare. DIAG-ASSURE-01 ne fornisce la misura.
- **PLAN-FIDELITY-01**: difetto reale, ma l'effetto sul modello non è dimostrabile offline (resta un testo); riduce chiamate, non rende verificabile la correttezza.
- **HOST-SATURATION-01**: aumenta l'utilità (T01), ma il beneficio sulla correttezza è ipotetico finché la base probatoria non è misurabile; è più sensato farlo subito dopo.
- **AF-EVID-01**: costo, non correttezza.
- **Convergenza MAF / ARCH-03**: nessun difetto osservato la richiede; ADR-0054 stima ≈0 righe di autorità rimovibili.
- **Knowledge (KNOW-02/03)**: beneficio mai misurato; investire in altri pack/playbook prima è speculativo.
- **Cambio modello**: è la leva più forte osservata ma è una *decisione di configurazione* (§7-D2), non un'attività di sviluppo; DIAG-ASSURE-01 + LIVE-GATE-01 servono a confermarla.

**Cosa non è**: non verifica le affermazioni, non riduce gli errori del modello, non sostituisce il grounding. Lo dichiara.

### Sequenza successiva (3–5 attività)
1. **PLAN-FIDELITY-01** (dopo o in parallelo; indipendente).
2. **HOST-SATURATION-01** (dopo DIAG-ASSURE-01, per mostrare l'effetto "Unchecked → Checked").
3. **LIVE-GATE-01** (richiede autorizzazione live dell'operatore; dopo 1 o 2 se si vuole misurare la versione migliorata).
4. **DIAG-CLAIMS-01** (dopo LIVE-GATE-01; esperimento con criteri di uscita: GO se almeno il 60% dei claim errati nel corpus è rilevabile deterministicamente con precisione ≥95% su 20 controlli negativi; altrimenti NO-GO e si resta sulla riduzione della superficie di claim lato tool).
5. **AF-EVID-01** e **KNOW-VALUE-01** (in base ai risultati di LIVE-GATE-01).

---

## 7. Decisioni che richiedono approvazione

| # | Decisione | Opzioni | Conseguenze | Raccomandazione |
|---|---|---|---|---|
| D1 | **ADR-0054 / ADR-0053 (B+ MAF)**: restano *Proposed* mentre il codice di AF-CONTEXT/TURN esiste non composto | (a) lasciare *Proposed* e dichiarare in roadmap che nessun host adotta `MafGovernedTurnBroker` finché un difetto misurato lo richieda; (b) accettare B+ condizionale; (c) archiviare lo spike code e il bridge | (a) non blocca nulla ed evita falsa impressione di convergenza; (b) vincola il codice futuro ai gate G2; (c) elimina codice testato ma inutilizzato | (a) |
| D2 | **Modello locale di riferimento e default di timeout** | (a) Qwen3.8-Flash-Next-Smart provvisorio con timeout 11/22 min e contesto 64K; (b) restare su Qwen3.6; (c) rinviare a LIVE-GATE-01 | evidenza oggi: 9/9 vs 6/9 consegne, ma confondenti e n=3; latenza ×2–2,7 | (c) con (a) come ipotesi di lavoro; correggere i default CLI incoerenti è comunque un tema di documentazione/config |
| D3 | **`MaxObservationCharacters` distribuito (4000)** | (a) invariato; (b) alzare per profilo di contesto; (c) misurare in LIVE-GATE-01 | cambia il comportamento di ogni deployment; ADR-0042 lo cita come default | (c), poi decidere |
| D4 | **ADR-0050 *Proposed* ma implementato** | (a) accettare formalmente con D-number; (b) lasciare e annotare; (c) superare | ADR-0046/0047/0060 e il codice poggiano su di esso | (a) dopo review, senza modificare retroattivamente il testo |
| D5 | **ADR-0055/0056/0058/0059 e D-050 (Knowledge)** | (a) accettare come sperimentali; (b) lasciare *Proposed* fino a KNOW-VALUE-01; (c) congelare lo sviluppo Knowledge | (b) coerente con l'assenza di misure; (a) abilita l'API | (b) |
| D6 | **Autorizzazione di campagne live** per LIVE-GATE-01 | sì/no, con budget e modello | senza di essa il quadro resta replay/n=1–3 | sì, dopo DIAG-ASSURE-01 |

---

## 8. Documenti da aggiornare successivamente (nessuno modificato ora)

**Roadmap e piani**
- `agentic/_plans/2026-09-16-consolidated-roadmap.md`: intestazione e §3 ("V1.3.y implementato localmente", "V1.4–V2.0 non iniziati") non riflettono né la linea MAF/Knowledge né l'attuale `main`.
- `agentic/_plans/2026-10-11-maf-knowledge-convergence.md`: la tabella sequenziale è storica — BENCH-00 3×3 A/B (varianti ora integrate), "AF-05H2-R1 NEXT" (fatto), "ARCH-02 non avviato" (fatto), "non procedere al merge della PR #100" (chiusa/superata), AF-EVID-01 e AF-05H4-VERIFY (riclassificati in §5), "Nessun subagent".
- `agentic/_plans/2026-09-21-v1.4-d-…` (833 righe): da riallineare agli ADR-0053/0054 (già richiesto da ADR-0054).
- Mancano task packet per AF-CONTEXT-01, AF-TURN-01, KNOW-01/02/03, LIVE-*, PRA-*: sono tracciati solo in HANDOFF/ADR/benchmark.

**ADR e decisioni (solo annotazioni successive, mai riscrittura retroattiva)**
- ADR-0054 §5 ("not implemented here") e §6 (roadmap futura già in `main`); puntatore `spike code lives only in commits` → tag locali non pubblicati.
- ADR-0055/0056: testo "ADR-0054 still Proposed on `docs/arch-03…`" ora in `main`.
- ADR-0050 (*Proposed*, nessun D-number), ADR-0053/0054/0055/0056/0058/0059 e D-050: stati formali da decidere (§7).
- `agentic/06-decisions.md`: intestazione ("tutte Accepted tranne D-050") e rimandi a ADR-0054 "da riconciliare con ADR-0057".
- ADR-0042: nessuna modifica, ma da valutare un emendamento se DIAG-ASSURE-01 aggiunge un campo persistito.

**Handoff, changelog, qualità**
- `HANDOFF.md`: le prime sezioni descrivono commit "locali, non pushati" su `codex/know-bench-01a` e un "prossimo task" già superato; elenca 2 fallimenti Runtime (risolti da `3fe07db`: Runtime 2301/2301 [TEST]) e 3 fallimenti Filesystem **diversi** da quelli in `agentic/_quality/known-failures.json` (`FsSize_ReturnsCompleteBoundedSummary…`, `DeleteTree_FailsClosedBeforeDeleting…`, `ApprovalBinding_ExpiresWithoutDeleting` contro `FsSize_StreamsTenThousandFiles`, `PrepareAndPage_TenThousandFiles…`, `ApprovalFlow_BlocksThenExecutes…`): registro e realtà non coincidono.
- `agentic/_quality/active-milestone.json` e `reports/` fermi a **V1.3-H**.
- `docs/architecture/af-turn-01b-context-skills-bridge.md`: "The runner does not offer or serve `runtime.knowledge_read`" è superato per il pilota CLI.
- `docs/benchmarks/*`: riferimenti a worktree/branch `bOps-knowbench01a` / `codex/know-bench-01a` (branch ancora esistente a `3fe07db`, integrato); `bench00-maf-compatibility-report.md` resta non tracciato (contiene percorsi locali).
- `docs/maintenance/` (non tracciato): `local-work-audit-01.md` e `repository-consolidation-01.md` descrivono lo stato pre-merge (`origin/main = e507bd3/2f961f7`).
- `CHANGELOG.md`: verificare la voce per `DIAG-ASSURE-01` quando implementato.
- `docs/operator-configuration.md`: configurazione del modello locale (timeout CLI/API, `MaxObservationCharacters`, contesto) rispetto alle configurazioni effettivamente validate.

---

## Appendice A — Verifiche eseguite in questa sessione

- `git status`, `git rev-parse`, `git worktree list`, `git branch`, `git tag`: HEAD `09902a2`; worktree `bOps` + `bOps-knowbench01a` (`3fe07db`); tag di backup locali presenti; modifica locale preesistente a `appsettings.json` non toccata.
- Lettura del sorgente: `AgentRunner.cs` (porzioni 190-215, 1425-1470, 2730-2760, 5118-5191), `EvidenceReadBudget.cs`, `EvidenceRead.cs` (costanti), `Audit.cs` (eventi), `EarlyFinalRecovery.cs`, `CompletedChecks.cs`, `EvidenceGrounding.cs`, `FinalResponse.cs`, `EvidenceIndex.cs`, `CliRunnerComposition.cs`, `ExecutionRunnerFactory.cs`, `Tools.cs` (`ToolParameter`), `SystemToolManifests.cs`, `ScriptedAgentic/AeCases.cs`, `Regression/LiveRegressionHarness.cs`; ricerca di `MafGovernedTurn`, `ContextGate`, `OperatingRange`, `CorrectionConflict`, `EvidenceReadGuidance`, `TruncatedBeforeContent`, consumatori di `GroundingOutcome`.
- Documentazione: HANDOFF, roadmap consolidata (testa), piano MAF/Knowledge, ADR-0042/0046/0050/0052–0061 (stati), ADR-0054 (matrice), `06-decisions.md` (testa), `agentic/_quality/*`, tutti i report in `docs/benchmarks/` citati, `docs/maintenance/*`, master di continuità (§3–§10), CI workflow.
- Test eseguiti (Debug, `--filter "Category!=LiveModel"`, `BOPS_*` rimosse dal processo, risultati in cartella temporanea di sessione, `git status` invariato prima/dopo; `bin/obj` rigenerati nelle cartelle già ignorate da Git): Architecture 210/210; Runtime 2301/2301; Knowledge 308/308; AgentFramework 615 passati, 8 ignorati; System.Core 481/481.

## Appendice B — Non verificato

CI remota (dichiarata verde dall'operatore); suite completa e Linux/WSL; `PROMPT-RUNTIME-AUDIT-01-report.md` e `RELEASE-VALIDATION-01-report.md` (citati tramite il master); artefatti grezzi delle campagne in `bOps-backups`; qualsiasi comportamento live. Le affermazioni segnate [DOC]/[MASTER] ereditano i limiti dei documenti sorgente (revisore singolo, n piccolo, replay). Le stime di effort sono giudizi, non misure.
