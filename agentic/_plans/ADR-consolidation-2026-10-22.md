# bOps — Consolidamento radicale delle decisioni architetturali

**Piano operativo preliminare alla convergenza MAF — 22 ottobre 2026**

**Fonte:** `babbubba/bOps`, branch `main`, commit `e0e9de7a74b8bb33e07c7b1f8c92ab87832986de`. **Esclusione:** PR #124 e qualunque suo commit. Questo file è una proposta esecutiva, non una modifica già pubblicata né un'approvazione automatica di decisioni ancora Proposed.

## 1. Perché serve

Audit in sola lettura su `main`:
- `docs/architecture/adr/`: **55 ADR** in testo Markdown, comprese ADR-0053/0054 ancora `Proposed`; riferimenti incrociati e amendment storici.
- `agentic/README.md`: gli ADR Accepted **prevalgono** sui documenti normativi e sul task.
- `CLAUDE.md`: tabella iniziale impone `agentic/06-decisions.md`; il workflow prevede la lettura di «plan and ADR» prima di iniziare.
- `agentic/00-bootstrap.md`: «Read ... `06-decisions.md` ... relevant accepted ADRs».
- `agentic/05-workflow.md`: impone un **nuovo ADR** prima di una serie molto ampia di cambiamenti.
- `agentic/06-decisions.md`: **1.223 righe**, include decisioni successivamente emendate e iterazioni storiche.
- `agentic/01-architecture-rules.md`: **680 righe**, rimanda a ADR come regole normative.
- `agentic/_tasks/PROCESS.md`: attribuisce autorità architetturale ad ADR + 06-decisions.
- Molti task storici, README, file sorgenti e documenti linkano gli URL delle vecchie ADR: la migrazione va gestita, non solo cancellata.

La sola creazione di uno ZIP non impedisce agli agenti di riaprire gli originali: bisogna eliminare la precedenza normativa e la lettura obbligatoria dai file di bootstrap. La storia Git resta comunque consultabile; l'obiettivo è **nessun recupero automatico di decisioni storiche**, non una promessa impossibile di cancellazione da ogni contesto.

## 2. Proposta: massimo cinque contratti correnti

Scrivere **cinque nuovi documenti consolidati**, volutamente brevi (obiettivo 60–120 righe ciascuno). Non scegliere soltanto cinque ADR preesistenti: sono lunghi, incrociati e alcuni contengono amendment successivi. I documenti consolidati sono l'unica fonte di decisioni strutturali contemporanee:

1. `docs/architecture/current/01-maf-agent-infrastructure.md`: MAF come infrastruttura agentica di default; turni `ChatClientAgent` attraverso broker governato; distinzione fra percorso di produzione effettivo e obiettivo da attivare; nessun FICC auto-invoke, nessun tool eseguito direttamente da MAF; contesto e compattazione, isolamento provider e controllo finale.
2. `docs/architecture/current/02-tools-safety-authority.md`: confini dei package, no shell generica, cinque rischi, policy/entitlement/approval, tool allow-list, post-verifica, privilegi e delega.
3. `docs/architecture/current/03-task-state-resume-mutations.md`: `TaskState` autorevole, piano/cursore, fencing, resume, registrazione intenzione di mutazione e gestione esiti incerti, audit, budget e retry.
4. `docs/architecture/current/04-evidence-and-diagnosis.md`: dati grezzi vs `EvidenceFact`, completezza e limitazioni, truncation, EvidenceRead, osservazione vs ipotesi/causa, niente affermazioni fattuali create da sommari o knowledge.
5. `docs/architecture/current/05-interfaces-knowledge-and-hosts.md`: SDK framework-neutral, confini OSS/commerciale, host CLI/API, Knowledge/Skill Packs e contenuti non affidabili, tenant e pin, estensioni future subordinate a task autorizzati.

Il livello normativo resta compatibile con `agentic/_tasks/PROCESS.md` per la gestione dei task, e le regole generali di sicurezza/coding/testing possono continuare ad esistere in file brevi, ma **non** come ulteriori registri architetturali in competizione. I dettagli di tipi, API e parametri sono documentati dal codice e dai test, non ricopiati in prosa.

**MAF B+:** ADR-0052 è Accepted; 0053 e 0054 sono Proposed, mentre l'utente ha espresso la volontà di attivare la convergenza. Non convertire tacitamente `Proposed` in `Accepted`: la migrazione documentale deve registrare l'eventuale approvazione dell'operatore e l'ambito specifico (turni MAF, non auto-invocazione né rinuncia a policy/journal), senza delegare a Claude una decisione strutturale.

## 3. Archivio ZIP

- Eseguire tutto su un **nuovo branch creato esclusivamente da `main`**, verificandone lo SHA prima della prima modifica; non usare PR #124.
- Generare `docs/architecture/archive/legacy-adr-and-decisions-2026-10-22.zip` con le **55 ADR originali** e copie di `agentic/06-decisions.md` e `agentic/01-architecture-rules.md` prima della sostituzione/riduzione, includendo nel pacchetto un `MANIFEST.md` con SHA del baseline, elenco sorgenti e checksum.
- L'archivio non ha autorità normativa. Non decomprimerlo né leggerlo nei task ordinari; consultazione solo per richiesta esplicita dell'operatore e solo per il caso rilevante.
- Dopo aver creato, verificato ed incluso l'archivio nel commit, rimuovere i 55 Markdown dalla directory `docs/architecture/adr/`; sostituire quest'ultima con un `README.md` breve che indirizzi ai cinque documenti correnti e all'archivio storico. **Mai alterare la storia Git**.
- Conservare una mappatura `ADR-ID → documento corrente + sezione` nell'indice di archivio per dare una destinazione ai vecchi link; non creare 55 stub normativi con testi storici. Gestire gli eventuali riferimenti documentali rotti in modo automatico/meccanico dove opportuno; non leggere 55 ADR durante ogni task successivo.

## 4. Modifiche obbligatorie al bootstrap (stessa PR di archivio)

- `CLAUDE.md`: lettura predefinita minima di istruzioni, **solo task attivo**, `PROCESS.md` per gli stati, e uno dei cinque contratti se pertinente. Vietare il caricamento di archive/ADR/06-decisions storici in bootstrap o per deduzione da link storici.
- `AGENTS.md` (crearlo su `main` se assente): stesso router breve, riferito a `CLAUDE.md`, non duplicazione del suo contenuto. Il fatto che esista in PR #124 non autorizza a importare quella PR.
- `agentic/00-bootstrap.md`, `agentic/README.md`, `agentic/05-workflow.md`: rimuovere l'obbligo di consultazione di vecchie ADR e `06-decisions.md`, la precedenza normativa dell'ADR storico e la regola che impone una nuova ADR per ogni feature. Per nuove decisioni architetturali modificare il contratto corrente **solo con approvazione operatore**, non creare ADR a catena.
- `agentic/_tasks/PROCESS.md`: cambiare solo la riga dell'autorità sulle decisioni architetturali, senza indebolire i gate, il registro, il progetto #6 e le regole di sincronizzazione.
- `agentic/01-architecture-rules.md`: ridurre a un breve indice/invarianti univoci verso i cinque contratti; non duplicare 680 righe. I dettagli su API pubbliche restano validi nel codice.
- `agentic/06-decisions.md`: conservare originale nello ZIP; sostituire con `README` storico o rimuovere dal bootstrap, evitando che torni ad essere l'autorità.
- `README.md`, `CONTRIBUTING.md`, `.github/copilot-instructions.md` (se presenti) e ogni file di instruction AGENT con globs/rules: allineare solo ingressi e precedenze. Controllare anche `.claude`, `.codex`, subdirectory AGENTS, e script che generano o allegano automaticamente contesto; non dichiarare la migrazione completa senza questa verifica.
- Il codice esistente può ancora contenere riferimenti storici (`ADR-xxxx` nei commenti): restano citazioni di provenienza, non richieste implicite di leggere il relativo archivio. Correggere link rotti destinati a utenti, non riscrivere in massa i commenti del codice per puro stile.

## 5. Task eseguibili (nessuna ricerca ricorsiva)

### DOC-01 — Inventory and precedence gate
- Input solo branch `main`; confermare SHA, 55 Markdown, read-order e principali riferimenti.
- Identificare regole e decisioni correnti che i cinque contratti devono preservare (nessuna esclusione dei controlli di sicurezza).
- Output: checklist di copertura, massima 1–2 pagine, non una nuova ADR.

### DOC-02 — Canonical contract consolidation
- Scrivere i cinque contratti, prendendo il comportamento corrente dal codice e le invarianti valide dalle fonti già note.
- Ogni regola contiene il **comportamento richiesto**, non la storia delle opzioni scartate; indicare esplicitamente decisioni ancora da approvare.
- Output: cinque file brevi senza contraddizioni e matrice di tracciabilità ADR → sezione.

### DOC-03 — Archive and bootstrap cleanup
- Creare/verificare ZIP + manifest, rimuovere ADR Markdown storiche dall'albero navigabile.
- Ridurre i punti di ingresso `CLAUDE.md`, `AGENTS.md`, `agentic/*`, README e normative precedence. Eliminare i trigger automatici alla ricerca delle vecchie ADR.
- Output: un solo bootstrap autorevole, non più di cinque documenti architetturali correnti.

### DOC-04 — Static acceptance and PR
- Test mirati: riferimenti di bootstrap, nessun percorso attivo verso l'archivio, checksum ed elenco ZIP, numero documenti correnti, assenza di regressioni nei processi/gate.
- Non effettuare massicce batterie di test; una PR **docs-only** da `main`, con diff limitato. Nessun merge automatico.
- Non modificare Runtime, API, CLI o package; non contaminare con PR #124 o task MAF attivo.
- Una volta integrata la PR e validato il suo commit su `main`, creare **separatamente** il branch di integrazione MAF dal nuovo `main` e far seguire a Claude il piano MAF esistente.

## 6. Exit criteria non negoziabili

- [ ] Nessuno dei 55 documenti storici `.md` è rimasto nella directory ADR attiva; presenti solo indirizzamento e ZIP.
- [ ] ZIP contiene tutti i 55 originali e i due documenti di decisioni/architettura storico-normativi, con manifest/checksum verificabili.
- [ ] Esistono al massimo cinque contratti architetturali correnti, in linguaggio operativo.
- [ ] Nessuna istruzione di bootstrap, precedence o code-agent guidance ordina di leggere ADR o `06-decisions.md` storici.
- [ ] `agentic/_tasks/register.json`, Issue, Kanban, `HANDOFF` rimangono le fonti previste per stato e avanzamento; la loro governance non è indebolita.
- [ ] Nessuna decisione di sicurezza, policy, privilege containment, entitlement, mutazione/resume o confine MAF è stata persa durante la compressione.
- [ ] Nessuna operazione su PR #124, nessuna implementazione assurance e nessun cambiamento di codice produzione.
- [ ] Una successiva sessione Claude Cloud legge solo bootstrap + task + il contratto pertinente, senza scorrere decine di ADR.

**Nota:** GitHub conserva i commit precedenti, quindi un agente con Git può sempre recuperare la storia intenzionalmente. Nessuna istruzione può garantire impossibilità tecnica di accesso; il successo è eliminare la lettura ordinaria, automatica o erroneamente vincolante.
