# MAF + Knowledge convergence — piano aggiornato dopo BENCH-00 live

**Stato: ACTIVE / AF-05H BLOCCATO DAL GATE LIVE (2026-10-12).** BENCH-00 1×3 su AF-05G e AF-05H completato e analizzato; **BENCH-00 3×3 non ancora eseguito**. Non procedere a merge di PR #100 né alla convergenza MAF/Knowledge fino ai gate sotto. Nessun subagent. Nessuna release senza consenso esplicito.

## Decisione sperimentale (2026-10-12)

- AF-05G: commit `97507aa7a914`, 3/3 task con stato `Completed`, 36 chiamate LLM, **239.916 token**. Non equivale a successo diagnostico: B03 finalizza prematuramente con almeno un controllo pianificato ineseguito.
- AF-05H (solo H2+H4): commit `07e422a4c9c6`, 2/3 `Completed`, B03 `Failed`, 47 chiamate LLM, **318.591 token (+32,8%)**.
- B01: **11.254 → 30.201** token; B02: **130.370 → 244.016**; B03: **98.292 → 44.374** token, ma il B03 post-fix è fallito, quindi non chiamare quest'ultimo risparmio.
- B03 AF-05H: H2 rifiuta una risposta finale con step ancora pendenti; due generazioni di replan terminano con `finish_reason=length`, 4.096 token output ciascuna, senza piano valido; `ModelProtocolException` porta a `Failed`.
- B02 AF-05H: 39 richieste `runtime_evidence_read` proposte contro 16 AF-05G; le chiamate LLM salgono da 19 a 33. Distinguere **proposte** da letture realmente servite. Il costo della fase finale B02 cala da 67.141 a 57.676 token, ma non attribuirlo causalmente a H4 senza ablation.
- BENCH-00 ha preservato payload originali, audit e SQLite; versione collector con fix enum numerici `946c85a`. Rimane difetto minore nell'hash della policy temporanea dovuto a normalizzazione CRLF/LF, da correggere in un commit separato prima del benchmark ufficiale.
- I campioni sono **n=1 per scenario e variante**; non inferire statistiche significative o causalità da un singolo run. L'ambiente e il modello locale possono cambiare nel tempo; comparare manifest, snapshot, tool-policy e capacità a ogni round.

## Sequenza vincolante (aggiornata)

| Ordine | Task / fase | Deliverable e gate per avanzare |
|---|---|---|
| 0 | **BENCH-00 live smoke 1×3** | **DONE**: AF-05G e AF-05H raccolti, verificati, analizzati. ZIP sensibili **mai committati**. |
| 1 | **AF-05H2-R1 — replan robusto dopo early-final** | **NEXT.** Fix minimo e isolato del fallimento B03: generazione `length`, retry/risorse limitati, fallback solo se sicuro sul piano esistente, nessuna falsa finalizzazione. Task dettagliato: `agentic/_tasks/2026-10-12-af-05h2-replan-truncation-recovery.md`. |
| 2 | **BENCH-01 — regressione mirata 1×3** | Test offline + Windows/Linux CI e replay sintetici. Eseguire live 1×3 sul candidato solo dopo controllo della configurazione e del collector; confrontare con i due ZIP esistenti. Gate: B03 non deve fallire per replan malformato se il piano in sospeso può proseguire in sicurezza; nessuna `Completed` con passi obbligatori irrisolti; motivare differenze di copertura/costi. |
| 3 | **AF-EVID-01 — EvidenceRead anti-ripetizione** | Separato da R1; verificare conteggio di `proposed`/`served`/`duplicate`, deduplica/riuso deterministico per chiave task+evidence ID+range, gestione limiti senza loop, su dati originali. Non rendere EvidenceRead inutilizzabile per approfondimenti genuini. |
| 4 | **AF-05H4-VERIFY — ablation disclosure** | Confrontare H4 on/off con H2 fissato, identico input/model/config quando possibile. Verificare disclosure append deterministicamente, precisione dei conteggi (es. bugcheck), alterazione del testo finale, output non duplicato e costi. H1/H3/H5 restano **deferred**. |
| 5 | **BENCH-00 ufficiale 3×3 A/B** | Solo dopo i gate mirati: tre round seriali per variante, 9 task ciascuna, stessi scenari, configurazione congelata, stesso collector, SHA/provenance, hash della policy corretto e fixture comparabili; comparazione esterna (non nel collector). |
| 6 | **Decisione PR #100** | PR #100 rimane intatta e aperta fino a review/CI e approvazione dell'operatore. Nessun merge implicito degli ulteriori branch AF-05H o BENCH-00. |
| 7 | **ARCH-01** | **DONE (documentale, 2026-10-12, ADR-0053 Proposed, in attesa di review operatore)**: ADR: **unico motore OSS** Local + Coordinator, contratti Knowledge/Experience/Playbook, versionamento/pinning, adapter commerciali, modifica coerente roadmap/V1.4-D. Matrice di ownership, invarianti I-1…I-10 e confini in `docs/architecture/adr/0053-maf-convergence-architecture-boundaries.md`. |
| 8 | **ARCH-02** | Spike MAF Agent/Workflow su parità delle garanzie bOps; niente sostituzione preventiva del runner. Piano e gate: `agentic/_tasks/2026-10-12-arch-02-maf-parity-spike.md` (non avviato). |
| 9 | **ARCH-03** | Decisione documentata di convergenza solo con parità e regressioni dimostrate. |
| 10 | **KNOW-01** | Knowledge/Experience Pack firmati e playbook dichiarativi indipendenti, contract OSS, validazione/trust store e activation atomica. |
| 11 | **KNOW-02** | Playbook Windows crash read-only, correlazione temporale e confutazione ipotesi, eventi/WHEA/dump senza attribuzioni causali non supportate. |
| 12 | **KNOW-03** | A/B/C model-only vs knowledge vs knowledge+playbook su modelli multipli; misurare costo e qualità. |
| 13 | **V1.4-D adapted** | Coordinator privato come host/adapter (tenant/RBAC, pgvector, memory, entitlement) senza duplicare engine OSS. |

> **Stato Knowledge a 2026-10-17 (riconciliazione; nessun gate cambia).** Implementato e validato **solo offline**: indice Knowledge (ADR-0057 M1 T1–T7, T8/gate
> M1 non chiuso, B1/B2 aperti), harness KNOW-BENCH con corpus attestato D1–D3 e diagnosi protetta/revisione cieca D7 (`docs/benchmarks/know-bench-*`), pilota CLI
> (ADR-0058, Proposed), ripresa dai rifiuti con `InfrastructureFailure` (ADR-0059, Proposed), `Agent:MaxOutputTokens` e telemetria di output (strumentazione, non
> sostituisce AF-05H2-R1). **Nessuna validazione live Knowledge è stata completata**; il Knowledge Index non è pubblicato al planner; l'API non compone Knowledge; MAF non governa
> planning né esecuzione dei tool. Lo stato per intervento è in `HANDOFF.md`.
>
> **Eccezione sperimentale circoscritta (2026-10-17, ADR-0058 / D-050 proposto).** Per istruzione dell'operatore esiste un pilota Knowledge opt-in,
> **solo CLI e solo Local Agent**, disattivato per impostazione predefinita. Non supera nessun gate di questa tabella (BENCH-00 3×3, BENCH-01, AF-EVID-01,
> AF-05H4-VERIFY, ARCH-01/02/03, KNOW-01/02/03 restano come sopra), non tocca l'API e non produce alcuna misura di qualità. L'estensione all'API richiede i
> criteri di ADR-0058 §6.

## Gate e metrica del lavoro immediato

1. **Safety invariants**: no auto-approval, nessuna mutazione non autorizzata, niente bypass di policy/entitlement, unknown outcome non ripetuto, history+audit+token accounting originali.
2. **Early final**: la risposta prematura non diventa `Completed`; conservare plan revision/cursor, step e ModelCall; distinguere step condizionali già risolti da step eseguibili pendenti. Un nuovo piano di 0 step è ammissibile solo se l'assenza di controlli necessari è giustificata e verificabile.
3. **Replan malformed/truncated**: distinguerlo da HTTP errori e failure generica; retry limitato e economicamente giustificato. Se il piano precedente offre un prossimo step safe e autorizzabile, è consentita una continuazione deterministica **solo per la classe early-final**, soggetta a budget e anti-loop. Se non esiste continuazione sicura, fail closed con causa e limitazioni persistite, senza dichiarare successo.
4. **EvidenceRead**: monitorare letture proposte, realmente servite e ripetute. Conservare tracciabilità forense. La riduzione token da sola non costituisce accettazione se la coverage cala.
5. **Qualità diagnosi**: separare fatti osservati, correlazioni, ipotesi e limiti. Non equiparare WHEA/Kernel-Power/live dump a prova di causa hardware. Copertura delle osservazioni obbligatorie e fondatezza prima di `Completed`.
6. **Confrontabilità**: stesso snapshot `ConfigSourceRoot`, stesso provider LlamaCpp/modello/endpoint, stessi prompt B01/B02/B03, read-only policy, stesso collector e manifest SHA. Salvataggio integrale dei tre run è requisito, ma non più sufficiente per il merge.
7. **Prestazioni**: registrare calls, input/output token, tool, evidence reads, replans, tempo, finish reason, stato terminale e costo per fase. Un incremento >20% di token a parità di copertura deve essere spiegato ed esplicitamente accettato, non nascosto dietro un miglioramento qualitativo presunto.

## Architettura futura — invarianti invariati

- `bOps` Apache-2.0 è proprietario del contratto generico Knowledge/Experience Pack, validazione/caricamento, retrieval, interprete playbook dichiarativo, MAF adapter. `bOps.Commercial` estende via adapter/versioni pubblicate, senza fork o duplicazioni.
- I pack sono firmati, versionati, installabili/aggiornabili e ripristinabili indipendentemente da Core e Skills entro closure compatibile; non sono assembly né script eseguibili. Ogni task pinna pack ID/version/hash; gli upgrade non cambiano task in corso.
- Knowledge recuperata è contesto da corroborare, **mai** prova corrente. Memory cliente resta tenant-private. Tutte le azioni restano soggette alla policy e all'autorità bOps.
- ARCH-02 confronta MAF con le garanzie di plan/step authority, validation, approval, entitlement, audit, mutation journal, unknown-outcome reconciliation, fencing, resume, evidence, budgets, provider fallback; niente grande rewrite prima di ARCH-03.
- Context budgeting H1 (soglie 70/60%) e gestione conflitti H3 non vengono introdotti di nascosto nei fix H2/H4. Il loro merito è da validare a parte.

## Regole di delivery

Ogni task: branch/commit isolato, test mirati, evidenza e gate esplicito. Nessun subagent, nessun cambiamento di runtime non inerente al fix, nessun push di ZIP/task DB/vault/master key/configurazione personale. Non modificare o mergiare PR #100 senza consenso.
