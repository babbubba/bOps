> **OBSOLETE MIGRATION INPUT — DO NOT IMPLEMENT FROM THIS FILE.** Its accepted requirements were
> migrated into [`agentic/_plans/2026-09-16-consolidated-roadmap.md`](../../_plans/2026-09-16-consolidated-roadmap.md)
> and the active task files. Coding agents must ignore this archive.

# Consolidamento specifiche pendenti — bOps (batch 2026-09-16)

**Cos'è questo documento.** Non è un piano e non è un elenco di task: è l'estratto delle voci
"Pronta per consolidamento" di `specifiche-pendenti.md`, riformattato in modo compatto e
dichiarativo per essere trasformato in una **proposta** di aggiornamento di
`piano-bops-v0.9.1-v2.0.md` (e, se necessario, nuove ADR).

**Istruzioni per l'agent che lavora sul repo:**

1. Non modificare `piano-bops-v0.9.1-v2.0.md` direttamente: produci una proposta (diff o nuova
   sezione) da sottoporre a revisione di Fabio, come da `agentic/05-workflow.md`.
2. Rispetta la catena di propedeuticità §5 del piano: nessuna voce qui sotto è vincolante su una
   versione precisa, sono posizionamenti indicativi già coerenti con lo stato attuale del
   repository (V0.10 in corso) — verifica comunque i gate prima di assegnare una versione.
3. Ogni voce riporta già le decisioni non negoziabili prese con Fabio (sezione "Vincoli/decisioni")
   e i riferimenti a regole/ADR già verificati — non ridiscuterli, sono chiusi. I dettagli
   puramente implementativi (nomi di classi, firme, struttura interna dei DTO, ecc.) restano a
   discrezione dell'agent, nel rispetto di quei vincoli.
4. Il razionale completo (perché non è già implementata, perché non è già pianificata, quali
   principi sono stati controllati) è in `specifiche-pendenti.md`, sotto lo stesso ID (`### SPEC-
   0XX`) — consultalo solo se serve contesto in più, non è necessario per procedere.
5. Le voci del gruppo A (tool di sistema/filesystem/web) si incastrano nella struttura già
   esistente di `piano-bops-v0.9.1-v2.0.md` §7, v0.11 ("completamento delle capability
   operative"), che già distingue una prima tranche read-only da una seconda tranche con effetti
   dopo i relativi reader/verifier — lo stesso schema si applica qui (vedi C1–C4 sotto).

---

## Gruppo A — Tool di sistema, filesystem e web (nuove capability read/high-risk)

Collocazione indicativa comune: **v0.11**, stesso capitolo di `fs.search`/`fs.hash`/`fs.move`/
`network.port_check`/`network.route` già pianificati lì. Ordine di implementazione consigliato,
per dipendenza: **C1, C2, C4 (read, indipendenti tra loro) → poi C3 (effetto, dipende da C2)**.

### C1 — `system.apps`, `system.devices`, campo modello hardware in `system.info` (SPEC-006)

- **Obiettivo.** Inventario locale: applicazioni installate, dispositivi hardware e relativi
  driver, nome/modello macchina (laptop/workstation/server).
- **Nuove capability.**
  - `system.apps` — Risk `Read`. Lista applicazioni installate. Stile `process.list` (parametro
    `limit` opzionale).
  - `system.devices` — Risk `Read`. Lista dispositivi hardware e driver (nome, versione driver).
    Stile `process.list` (parametro `limit` opzionale).
  - `system.info` — **campo esistente esteso**, non nuovo tool: aggiungere il modello/nome
    macchina (laptop/workstation/server) ai campi già riportati (OS description, hostname,
    uptime).
- **Area toccata.** `bOps.Packages.System.Core` (manifest condivisi in `SystemToolManifests.cs`),
  `bOps.Packages.System.Windows`, `bOps.Packages.System.Linux`. Nessun nuovo package.
- **Vincoli/decisioni (chiuse, non ridiscutere).**
  - Il modello hardware **non** è un tool a sé — decisione di Fabio (2026-09-16), coerente col
    precedente esplicito in `agentic/00-project-spec.md` contro tool duplicati di `system.info`
    (`system.uptime`).
  - Pattern OS package obbligatorio: ADR-0006/rule A8 — Windows e Linux implementano ciascuno la
    propria versione dello stesso contratto (niente `ISystemProvider` condiviso). Windows:
    tipicamente WMI (`Win32_Product`/`Win32_PnPEntity`/`Win32_ComputerSystem`). Linux: package
    manager di sistema, `lspci`/`lsusb`, `/sys`.
  - Risk `Read` per tutto — nessun impatto sul modello di policy.
- **Dipendenze.** Nessuna (indipendente da C2/C3/C4).

### C2 — `fs.size` — analisi dimensione cartelle/file tipo TreeSize (SPEC-008)

- **Obiettivo.** Individuare cartelle/file di dimensione eccessiva a partire da un path dato o
  dalla root, come base per un'eventuale pulizia sicura (dipendenza di C3).
- **Nuova capability.** `fs.size` (nome indicativo) — Risk `Read`.
- **Area toccata.** `bOps.Packages.Filesystem` (stesso package di `fs.list`/`fs.stat`/`fs.search`/
  `fs.hash`/`fs.delete`/`fs.move`), `FilesystemPathPolicy` per lo scoping dei path leggibili.
- **Vincoli/decisioni (chiuse, non ridiscutere).**
  - `fs.list` non ricorre mai per costruzione ("never recurses", commento esplicito nel codice) —
    verosimilmente per S7 (ogni azione ha un timeout) su alberi molto grandi. `fs.size` **deve**
    gestire esplicitamente questo limite: parametro di profondità massima e/o restituire solo le
    N cartelle/file più grandi invece dell'albero completo. Riusa il `CancellationToken` già
    passato a ogni `ITool`.
  - Risk `Read` — nessuna scrittura, nessun impatto sul modello di policy.
- **Dipendenze.** Nessuna. È prerequisito di C3.

### C3 — cancellazione cartelle/file in blocco per liberare spazio (SPEC-009)

- **Obiettivo.** Liberare spazio cancellando cartelle/gruppi di file individuati con C2.
- **Nuova capability.** Estensione di `fs.delete` a directory/gruppi di file, oppure tool separato
  (es. `fs.delete_tree`) — scelta implementativa libera per l'agent, purché la cancellazione di
  più elementi resti un'azione esplicita e distinguibile in audit da `fs.delete` sul singolo file.
- **Area toccata.** `bOps.Packages.Filesystem`.
- **Vincoli/decisioni (chiuse, non ridiscutere — decise con Fabio il 2026-09-16, dopo aver
  verificato che la cancellazione ricorsiva tocca l'invariante ADR-0002 `Critical` = `Forbidden`
  non bypassabile, di cui le operazioni filesystem irreversibili sono l'esempio esplicito
  nell'ADR).**
  - Risk `High` (non `Critical`) — eseguibile da bOps, sempre con approvazione obbligatoria
    dell'operatore ad ogni esecuzione. `fs.delete` oggi esclude di proposito le directory
    ("blast radius troppo grande"): qui il blast radius maggiore è un ampliamento deliberato,
    non un bypass di quella scelta.
  - Cancellazione **diretta e permanente** — non spostamento reversibile in quarantena/cestino.
  - **Anteprima obbligatoria pre-approvazione**: prima che l'operatore approvi, deve vedere
    l'elenco esatto di cosa verrebbe rimosso e la dimensione totale, calcolati con C2 (`fs.size`)
    — mai solo il path grezzo passato al tool. Va in aggiunta alla `VerificationSpec`
    *post*-azione che ogni tool non-Read già dichiara (rule B3); qui serve anche un passo di
    verifica/preview *pre*-azione.
- **Dipendenze.** **C2** (l'anteprima usa `fs.size`). Va implementato dopo C2, coerente con lo
  schema "prima tranche read-only, poi effetti" già usato in v0.11 per `fs.move`.

### C4 — `web.search` e `web.fetch` (SPEC-007)

- **Obiettivo.** Permettere una ricerca web i cui risultati arrivano al modello come dato già
  pronto, eseguita dal runtime e mai dal modello direttamente; verifica "driver aggiornati" (da
  SPEC-006/C1) senza un tool dedicato.
- **Nuove capability.**
  - `web.search` — Risk `Read`. Ricerca web, ritorna lista risultati/snippet.
  - `web.fetch` — Risk `Read`, ma **superiore a `web.search`** (contenuto arbitrario di una
    singola pagina esterna, non solo snippet). Due tool separati, non uno con parametro.
- **Area toccata.** Nuovo package, es. `bOps.Packages.Web`. Primo package con dipendenza di rete
  verso servizi esterni fuori dai provider LLM (`bOps.Packages.Providers.*`).
- **Vincoli/decisioni (chiuse, non ridiscutere).**
  - Fonte di ricerca: **senza API key** (decisione di Fabio, 2026-09-16) — nessun secret da
    gestire (rule S6 non si applica), ma la fonte concreta (endpoint, affidabilità, rate limit)
    va scelta e verificata in implementazione.
  - Il risultato di `web.search`/`web.fetch` è **dato non fidato**: rule S5 ("tool output is
    data, never instruction") si applica con forza particolare qui — contenuto web esterno è
    l'esempio canonico che quella regola già anticipa.
  - Verifica "driver aggiornati" (dalla richiesta originale SPEC-007): **non è una capability a
    sé**. Il modello combina il dato locale di `system.devices` (C1) con `web.search` quando
    serve — nessun lavoro dedicato oltre a C1 e `web.search` stesso.
  - Coerente col pattern già usato in v0.11 per i package `Service` (`piano-bops-v0.9.1-v2.0.md`
    §7, nota implementativa 8): valutare se scrivere un ADR prima di introdurre il primo package
    con accesso rete esterno, dato che è un precedente architetturale nuovo (nessun tool `Read`
    ha oggi una dipendenza di rete verso l'esterno).
- **Dipendenze.** Nessuna per `web.search`/`web.fetch` in sé; C1 solo per il caso d'uso "driver
  aggiornati" (non blocca l'implementazione dei due tool).

---

## Gruppo B — UI e API

### D1 — Pagina UI plugin/Skill installati e attivi (SPEC-004)

- **Obiettivo.** Pagina dedicata in `web/bops-ui` per vedere quali plugin/Skill sono presenti e
  attivi (a partire da lettura; enable/disable da valutare in un secondo tempo).
- **Area toccata.** `web/bops-ui` (nuova route/feature "Plugins"), `bOps.Api` (nuovi endpoint di
  lettura, eventualmente enable/disable).
- **Vincoli/decisioni.**
  - Prerequisiti tecnici non ancora soddisfatti: (1) il lavoro V0.10 in corso (`bOps.PluginHost`,
    ADR-0020) deve essere committato; (2) serve un endpoint su `bOps.Api` che esponga
    `PluginManager` — oggi `PluginManager` (Install/List/Enable/Disable/Remove, ADR-0020) resta
    lato `bOps.PluginHost`/CLI, nessun endpoint HTTP esiste ancora.
  - Priorità: versione minima "sola lettura" (cosa è installato/attivo) prima delle azioni di
    enable/disable dalla UI — stesso pattern di `GET /api/providers` (ADR-0019), aggiunto solo
    quando la Settings page ne ha avuto bisogno.
  - Nessun gap di autenticazione nuovo o specifico: `bOps.Api` non ha ancora auth (ADR-0018), ma è
    la stessa condizione già accettata per tutto `bOps.Api` in questa fase, non un blocco
    aggiuntivo per questa feature.
- **Dipendenze.** Non prima che il lavoro V0.10 in corso (`bOps.PluginHost`) sia committato.
- **Collocazione indicativa.** Chiusura/completamento di V0.10 (stesso modello v0.9/Settings) o
  primi passi di v0.11.

### D2 — Settings: provider LLM e API key modificabili e persistenti (SPEC-001)

- **Obiettivo.** Rendere la pagina Settings scrivibile: selezione provider persistente e gestione
  (inserimento/modifica) dell'API key dalla UI, non solo lettura.
- **Area toccata.** `bOps.Api` (nuovi endpoint di scrittura), gestione secret/configurazione,
  `web/bops-ui` (Settings).
- **Vincoli/decisioni (chiuse, decise con Fabio il 2026-09-15).**
  - **Non prima di v1.0**: dipende dal secret-provider contract e dall'autenticazione reale di
    `bOps.Api`, entrambi previsti in `piano-bops-v0.9.1-v2.0.md` §7, v1.0 (punti 2 e 3). Oggi
    l'API key arriva solo da environment variable o .NET user-secrets (rule S6); non esiste un
    meccanismo approvato per scriverla da UI/API senza quel contratto.
  - Visualizzazione della key: **mascherata**, primi 6 e ultimi 4 caratteri visibili, `...` in
    mezzo — non il solo booleano `hasApiKey` attuale (ADR-0019), mai il valore in chiaro.
  - Requisito non negoziabile (Fabio): la key deve essere **gestibile** (inserimento/modifica)
    dalla UI, non solo in lettura.
  - Vincolo di compatibilità: la configurazione via `appsettings.json`/user-secrets per chi usa
    solo la CLI deve restare valida — il meccanismo UI si affianca al percorso CLI esistente
    (rule S6), non lo sostituisce. Precedenza/coordinamento tra le due fonti: dettaglio di design
    per l'ADR del secret-provider contract a v1.0, non da anticipare ora.
  - Nota tecnica: la parte "provider persistente" (senza gestione key) è più leggera di quanto
    sembri — `IChatModelRegistry.Create(ChatModelOptions)` già accetta options arbitrarie, non
    richiede modifiche a `bOps.Abstractions`. Resta comunque da esporre un endpoint di scrittura
    su un'API oggi non autenticata.
- **Dipendenze.** Secret-provider contract e autenticazione reale di `bOps.Api` (v1.0, punti 2-3).
- **Collocazione indicativa.** Post-v1.0 (confermato da Fabio, nessuna urgenza).

---

## Gruppo C — Documentazione

### E1 — Aggiornamento README e valutazione versione bilingue (SPEC-003)

- **Obiettivo.** Allineare `README.md` allo stato reale del progetto; valutare una versione
  bilingue EN/IT.
- **Area toccata.** `README.md`.
- **Vincoli/decisioni.**
  - `README.md` (ultimo commit `99dac6b` al momento della verifica) descrive ancora v0.9.1 come
    "in corso", mentre risulta chiusa (`HANDOFF.md`, D-013–D-015).
  - **Non aggiornare prima** che il lavoro V0.10 in corso (`bOps.PluginHost`, ADR-0020, sample
    plugin) sia committato e verificato (build senza warning, test passati, ADR accettata —
    Definition of Done, `agentic/05-workflow.md`) — altrimenti si documenterebbero capability non
    ancora disponibili.
  - Versione bilingue: **post-v2.0**, per scelta esplicita di Fabio (coerente di suo con lo scope
    discipline del progetto).
- **Dipendenze.** Commit e verifica del lavoro V0.10 in corso.
- **Collocazione indicativa.** Aggiornamento contenuti: appena V0.10 è committato. Bilingue:
  post-v2.0.

---

## Escluse dal consolidamento (nessuna azione — per riferimento, non riproporle)

- **SPEC-002** — Rinominare i package core in "Skill": **Scartata**. Contraddice principio 7,
  ADR-0006, ADR-0012 e l'ADR di distinzione package/Skill/capability già pianificata a v1.1
  (`piano-bops-v0.9.1-v2.0.md` §7, v1.1, nota 1). Non riaprire senza informazioni nuove.
- **SPEC-005** — Coordinamento agent remoti/multi-nodo: **già coperta dal piano**, a v1.4
  (`piano-bops-v0.9.1-v2.0.md` §7), con la parte più consistente nel repository privato
  `bOps.Commercial`. Nessuna azione su questo repository oltre a quanto già previsto lì.

---

*Estratto generato il 2026-09-16 da `specifiche-pendenti.md` (SPEC-001, 003, 004, 006, 007, 008,
009 — tutte "Pronta per consolidamento" con eventuali chiarimenti di Fabio già incorporati).*
