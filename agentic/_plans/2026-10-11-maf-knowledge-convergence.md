# MAF + Knowledge convergence — piano successivo a BENCH-00

**Stato: PLANNED.** NON implementare prima della baseline BENCH-00. Repository OSS `bOps` e futuro `bOps.Commercial`. Non usare subagenti. PR #100 (AF-01→AF-05G) resta aperta finché i due blocchi critici non sono risolti e verificati.

## Sequenza vincolante

| Ordine | Fase | Obiettivo / gate |
|---|---|---|
| 0 | BENCH-00 | Harness autoelevante per 3 scenari × 3 esecuzioni, sorgenti DB/audit integrali task-scoped, ZIP privato, nessuna diagnosi nel collector. Baseline pre-fix obbligatoria. |
| 1 | AF-05H-small | Solo early-final con piano incompleto → replan governato; disclosure delle limitations aggiunta deterministicamente senza riscrivere la final response. Test sintetici sul terzo e secondo caso live. Niente redesign contesto in questa fase. |
| 2 | BENCH-00 post-fix | Identiche 9 esecuzioni, stesso harness e contesto effettivo ove possibile; seconda acquisizione completa, successiva analisi in ChatGPT. |
| 3 | Merge PR #100 | Windows/Linux CI e gate dei due bug funzionali verificati; merge solo dopo consenso operatore. |
| 4 | ARCH-01 | ADR di **unico motore OSS** per Local Host e Coordinator, knowledge/playbook contract condivisi, update pack indipendenti, pin/hash per task, estensioni private senza duplicazione. Allineare roadmap/ADR-0052/V1.4-D. |
| 5 | ARCH-02 | Spike isolato MAF Agent/Workflow come vero orchestratore: misurare parity di plan/replan, step authority, validation, approvals, entitlement, audit, journal, unknown outcome, fencing, resume. Non sostituire il runner in produzione nello spike. |
| 6 | ARCH-03 | Decisione documentata e comparazione con BENCH-00: mantenere i confini bOps safety/evidence, eliminare eventuale duplicazione generica con MAF solo dove parità dimostrata. |
| 7 | KNOW-01 | Knowledge Pack + Experience Pack e playbook dichiarativi aggiornabili indipendentemente; importer/validation/retrieval contract OSS comune, no SQL/vector DB obbligatorio in local; MAF context adapter isolato. |
| 8 | KNOW-02 | Prima verticalità diagnostica Windows crash/read-only (Kernel-Power, WHEA, WER, dump, stability), fonti/versioni e test di evidenza corroborante/confutante. |
| 9 | KNOW-03 | Benchmark A/B/C: model-only, retrieval knowledge, retrieval+playbook su più modelli; confronto con stessi prompt, dati, configurazione e harness. |
| 10 | V1.4-D adapted | Coordinator implementa tenant/RBAC, PostgreSQL/pgvector, operational memory, distribuzione/entitlement e knowledge privata **come adapter/host** del motore OSS condiviso. |

## Distribuzione e versioni

- `bOps` Apache-2.0 possiede ABI/schema pack generico, validation/load/activation, retrieval contract, playbook interpreter e MAF adapter. `bOps.Commercial` non deve duplicare questi moduli, ma referenziare versioni pubblicate. V1.4-D oggi colloca troppo contratto semantico nel Coordinator privato: serve amendment prima dell'implementazione, non fork del sistema.
- `.bopsknowledge` già progettato include Knowledge/Experience Pack firmati, versionati, indipendenti dall'app. Estenderlo con playbook **dichiarativi** e validazione capability/tool senza consentire script arbitrari o esecuzione di prompt pack.
- Skill commerciale distribuito indipendentemente dal Core può dichiarare dependences min/max schema/engine/pack; Knowledge e Playbook possono essere inclusi nel bundle commerciale ma hanno identità/versione autonome e aggiornamento/rollback autonomi se compatibili. Full dependency closure con activation atomica; nessun upgrade parziale incompatibile.
- Task esistente pinna pack ID/version/content hash per ripresa e diagnosi riproducibile; un update non modifica le conoscenze di un task in corso.
- Riutilizzare trust store/signature e staging sicuro del plugin lifecycle laddove corretto; pack dati **non** sono assembly. Firma/verifica pack deve essere implementata prima dell'attivazione anche se il distribution service completo è roadmap V1.4-E.
- L'operational memory dei clienti rimane tenant-private, non viene promossa automaticamente in pack distribuiti. Knowledge recuperata è contesto/ipotesi, **mai current evidence**. Le operazioni del nodo rimangono subordinate alla policy bOps.

## Runner vs MAF — criterio sperimentale

Non assumere a priori che MAF sostituisca AgentRunner con garanzie equivalenti. L'attuale ADR-0052 delega a MAF contesto/compaction e conserva bOps plan/state/authority. ARCH-02 deve misurare possibilità reali di MAF agent loop/function calling/workflows/context providers con gli stessi invarianti: tool visible solo nello step autorizzato; validazione; policy e privilegi non ampliabili; approval; mutation intent prima del side effect; unknown outcome non auto-retry; execution-attempt fencing; resume; per-call audit/tokens; EvidenceRead, completeness, limitations; deadline/budget; provider fallback. Test contro fixture + benchmark reale. Documentare equivalenze, lacune e costi; nessun grande rewrite prima di ARCH-03.

## Performance e qualità cross-model

- Evitare la falsa promessa che RAG renda identica la competenza di modelli differenti. Knowledge aiuta con contenuto; playbook guidano procedura; runtime preserva correctness e safety. Verificare empiricamente su almeno un modello locale grande e uno più piccolo.
- Context budgeting high/low watermarks proporzionali a finestre note, ma 50–70% non è soglia universalmente dimostrata. Dedicare benchmark, non inserirlo implicitamente in AF-05H-small. Router con context window ignota non deve inventarla.
- Metrica comparativa sempre calcolata **fuori da BENCH-00** dai dati originali: coverage tool, sequence, early final, unsupported claims verificabili, model calls, costs, latenza e variabilità dei 3 round. Documentare eventuale environment drift e router model selection.

## Deliverable documentali / gate

- ARCH-01 aggiorna formalmente gli ADR Accepted interessati (senza riscrivere decisioni passate in-place), il consolidated roadmap e il V1.4-D plan; prima registra in GitHub questo piano e il BENCH-00 packet.
- Ogni fase ha branch/commit separati, test, evidence bundle e decisione esplicita di avanzamento; niente codice Commercial nel repo OSS.
- Non utilizzare subagent; non fare merge né pubblicazione di nuove release senza autorizzazione esplicita.
