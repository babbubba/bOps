# bOps — Razionalizzazione documentale e consolidamento architetturale

**Piano operativo revisionato — 22 ottobre 2026**  
**Stato:** proposta da verificare in DOC-01; nessuna migrazione o approvazione implicita  
**Repository:** `babbubba/bOps`  
**Branch di preparazione già creato dall'operatore:** `docs/adr-consolidation-2026-10-22`  
**Percorso canonico del piano nel branch:** `agentic/_plans/ADR-consolidation-2026-10-22.md`  
**Baseline dichiarata dell'audit precedente:** `main` a `e0e9de7a74b8bb33e07c7b1f8c92ab87832986de`  
**Esclusione:** PR #124 e qualunque suo commit, merge, cherry-pick o contenuto non già presente nella baseline ammessa.

> Questo documento aggiorna il piano iniziale **prima dell'esecuzione**: la riduzione delle ADR, da sola, non risolve la proliferazione di autorità documentali in `agentic/`, `docs/` e nelle istruzioni degli agenti. Le quantità riportate sotto sono **risultati dell'audit precedente sulla baseline dichiarata**, non nuove verifiche sul repository remoto. DOC-01 deve accertarle. Il piano non autorizza promozioni di ADR `Proposed`, implementazioni MAF o modifiche di codice.

## 1. Problema, obiettivo e limiti

### 1.1 Baseline nota da riconfermare

L'audit precedente su `main` indicava:

- `docs/architecture/adr/`: **55 ADR Markdown**, incluse ADR-0053 e ADR-0054 `Proposed`.
- `agentic/06-decisions.md`: **1.223 righe** e decisioni emendate nel tempo.
- `agentic/01-architecture-rules.md`: **680 righe** e rimandi normativi alle ADR.
- `agentic/README.md`, `agentic/00-bootstrap.md`, `agentic/05-workflow.md`, `CLAUDE.md` e `agentic/_tasks/PROCESS.md`: precedenze o letture obbligatorie di ADR/decisioni storiche.
- Riferimenti a ADR in piani, task, README, documentazione e codice.

Potrebbe esistere ulteriore documentazione ridondante, obsoleta o di fatto normativa sotto `agentic/`, `docs/` e nei file di instruction. La sua estensione **non è ancora dimostrata**: va censita prima di scegliere cosa ritirare.

### 1.2 Risultato atteso

1. **Un'unica autorità per ogni decisione strutturale corrente:** al massimo cinque contratti in `docs/architecture/current/`.
2. **Un solo percorso di bootstrap degli agenti**, breve, con lettura selettiva del task attivo e dei contratti pertinenti: nessuna scansione ordinaria di ADR o registri storici.
3. **Governance operativa intatta:** registri, stati task, handoff, issue/progetto, workflow e gate restano riconoscibili e utilizzabili.
4. **Documentazione tecnica e funzionale utile preservata:** specifiche di tool/API, runbook, deployment, benchmark, test, guide e piani attivi non vengono rimossi solo per ridurre il numero di file.
5. **Storia consultabile ma priva di autorità corrente:** ZIP degli originali effettivamente ritirati, con manifest e riferimenti; storia Git non riscritta.
6. **Nessuna perdita semantica:** tutte le invarianti valide rimangono reperibili e verificabili prima di archiviare gli originali.

**Non è un obiettivo** ridurre `docs/` e `agentic/` a cinque file, eliminare tutta la documentazione storica o completare in questa PR la convergenza a Microsoft Agent Framework (MAF).

### 1.3 Confini di modifica

**Consentito, dopo i gate:** Markdown e altri file *esclusivamente documentali/istruzionali* in `docs/`, `agentic/`, root, `.github/`, `.claude/`, `.codex/` e `AGENTS.md` dove presenti; generazione di ZIP e manifest; aggiornamento di collegamenti documentali. 

**Non consentito:** modifiche a codice eseguibile, runtime, package, API, CLI, test, CI, script, manifest di tool, entitlement, policy applicative, database, release workflow, registri task/status o automazioni operative, salvo nuova autorizzazione esplicita con task separato. È ammessa l'ispezione mirata in sola lettura del codice e dei test per verificare l'implementazione corrente. Se un loader o uno script carica documenti obsoleti e necessita una modifica eseguibile, segnalarlo come **blocco o follow-up separato**; non aggirare il vincolo docs-only.

## 2. Baseline e isolamento Git

- Il branch di preparazione **esiste già**: `docs/adr-consolidation-2026-10-22`. Non ricrearlo né spostarlo automaticamente.
- Prima di qualunque modifica, leggere branch/HEAD/status e verificare che la baseline dichiarata di `main` sia antenata del branch e che l'eventuale differenza contenga solo il caricamento/modifica di questo piano. Distinguere **HEAD del branch** dal commit di base.
- Confrontare la `main` remota corrente con la baseline dichiarata; se è avanzata, riportare SHA e divergenza. Non rebase, merge, reset o cherry-pick automatici. Richiedere decisione dell'operatore prima di proseguire su una base diversa.
- DOC-01 opera sul contenuto tracciato nel branch preparatorio (base `main` più il piano), **non** importa contenuti da altre PR/branch/worktree. Non usare PR #124 neppure per dedurre decisioni o completare file mancanti.
- Non interpretare il piano stesso come nuova autorità architetturale: è una specifica temporanea di migrazione.
- Non cambiare stato Git, creare commit, push o PR in DOC-01. Nelle fasi successive, commit e pubblicazione solo con autorizzazione esplicita, verifica staging e diff; nessun merge automatico in `main`.

## 3. Modello documentale di destinazione

### 3.1 Livelli e precedenza

| Livello | Contenuto | Autorità / utilizzo |
|---|---|---|
| **A — Contratti architetturali correnti** | Fino a cinque file `docs/architecture/current/*.md` | Unica fonte per decisioni strutturali *approvate*. Nessuna ADR storica ha precedenza. |
| **B — Governance operativa** | `agentic/_tasks/PROCESS.md`, task attivo, registro, handoff e integrazioni issue/progetto | Autorità **sul processo e stato**. Non istituisce autonomamente nuove decisioni di architettura. Un task dettaglia l'esecuzione ma non può contraddire i contratti. |
| **C — Riferimenti tecnici/funzionali** | Documentazione di tool, API, CLI, guide, specifiche, test, deployment, runbook | Specificano comportamenti e uso nel loro ambito; non diventano registri alternativi di decisioni strutturali. Da allineare solo se in conflitto. |
| **D — Materiale storico** | ADR ritirate, decision log storici, report conclusi, piani inattivi, ZIP | Provenienza e consultazione su richiesta esplicita; **non** fonte normativa, né bootstrap di default. |

`CLAUDE.md` e `AGENTS.md` sono **router di lettura** verso queste fonti, non un quinto livello normativo concorrente. Regole brevi generali di sviluppo/sicurezza possono rimanere se non duplicano le decisioni strutturali e se la loro precedenza è univoca.

Quando due fonti correnti divergono, **non scegliere la più recente solo per data o nome**: verificare stato Accepted/Proposed, comportamento nel codice e approvazione dell'operatore. Se il conflitto non è risolvibile con evidenza, classificarlo `REVIEW` e bloccare il trasferimento di quella regola.

### 3.2 I cinque contratti architetturali proposti

Obiettivo indicativo **60–120 righe per file**, non vincolo rigido che giustifichi omissioni di sicurezza. Ogni contratto contiene regole operative attuali, confini, invarianti e stato della decisione; niente lunga cronaca di opzioni respinte né duplicazione di API/tipi già verificabili nel codice.

1. `docs/architecture/current/01-maf-agent-infrastructure.md` — MAF come infrastruttura agentica obiettivo/default dove approvato; turni `ChatClientAgent` tramite broker governato; distinguere produzione effettiva da convergenza ancora da attivare; niente FICC auto-invoke né tool operativi eseguiti direttamente da MAF; contesto/compattazione, provider e controllo della finalizzazione.
2. `docs/architecture/current/02-tools-safety-authority.md` — confini package, niente shell generica, classi di rischio, policy, entitlement, approval, allow-list, post-verifica, privilegi e delega.
3. `docs/architecture/current/03-task-state-resume-mutations.md` — autorità `TaskState`, piano/cursore, fencing, resume, journal/intento di mutazione, esiti incerti, audit, budget, retry.
4. `docs/architecture/current/04-evidence-and-diagnosis.md` — osservazioni grezze vs `EvidenceFact`, completezza, filtri, truncation, `EvidenceRead`, osservazione/ipotesi/causa, grounding senza invenzione da sommari/Knowledge.
5. `docs/architecture/current/05-interfaces-knowledge-and-hosts.md` — SDK framework-neutral, confini OSS/commerciale, CLI/API, Knowledge/Skill Packs, contenuti non attendibili, tenant/pinning, estensioni future governate.

La classificazione effettiva dei file e la matrice di copertura di DOC-01 possono suggerire un migliore *riparto interno* tra i cinque, ma **non** aggiungere automaticamente un sesto contratto.

### 3.3 Decisioni ancora non approvate

L'audit precedente riportava ADR-0052 `Accepted`, ADR-0053 e ADR-0054 `Proposed`. DOC-01 deve riconfermarne status e contenuto nella baseline. La volontà di convergere a MAF B+ **non equivale** ad accettare implicitamente tutti i dettagli Proposed: nessuna promozione di stato, nessuna promessa di funzionalità già produttiva e nessuna deroga a policy/journal/approval senza decisione esplicita dell'operatore. Eventuali decisioni sospese restano marcate come tali **fuori dalle regole vincolanti** dei cinque contratti finché non approvate.

## 4. Perimetro dell'inventario documentale

### 4.1 Inventario completo dei percorsi, lettura selettiva dei contenuti

DOC-01 deve censire **tutti i file tracciati** sotto `agentic/` e `docs/` e, per le instruction/precedenze, i file root e le aree `.github/`, `.claude/`, `.codex/`, incluse istruzioni annidate (`AGENTS.md`, file con globs/rules) e riferimenti da generatori o script. Eseguire un inventario **percorso-percorso tramite indice Git** (ad esempio `git ls-files`): è ammissibile enumerare ricorsivamente *i nomi*; non è richiesto leggere indiscriminatamente *il contenuto completo* di ogni file.

Procedura a due passaggi:

1. **Censimento leggero di tutti i file:** percorso, estensione, dimensione, famiglia, apparente stato attivo/storico, referenze significative dove individuabili. Raggruppare i file palesemente operativi/tecnici; non aprire report enormi o artefatti binari senza motivo.
2. **Ispezione mirata dei contenuti:** leggere completamente file normativi, istruzioni/bootstrap, registri decisionali, file candidati a rimozione e documenti con conflitti/ridondanze; ispezionare sezioni puntuali di guide, piani e task solo quando servono a dimostrare autorità, dipendenze o perdita informativa. Verificare anche riferimenti entranti da file fuori dalle cartelle censite **per i candidati effettivi**, evitando scansioni costose e lettura di codice non pertinente.

Non ignorare un documento **solo perché non citato nel piano originale**. Se si incontrano directory troppo grandi per una verifica affidabile nella sessione, segnalarne espressamente la copertura parziale: nessun `READY` basato su un inventario incompleto.

### 4.2 Classificazione e azioni distinte

| Classe | Significato | Trattamento ordinario |
|---|---|---|
| **A — Normativo corrente** | Decisioni strutturali da preservare | `CONSOLIDATE` nei cinque contratti, quindi `REPLACE`/`ARCHIVE` dell'originale, previa copertura verificata. |
| **B — Ridondante/sostituito** | Informazioni duplicate o superate | `ARCHIVE` soltanto se il contenuto valido ha destinazione esplicita e le dipendenze sono corrette. |
| **C — Storico non normativo** | Report, ADR emendate, piani conclusi | `KEEP` se utili e innocui; `ARCHIVE` soltanto con motivazione, priorità e approvazione. |
| **D — Riferimento tecnico valido** | Contratti tool, API, guide, runbook, deployment | `KEEP`; `UPDATE` dei riferimenti/normative precedenze, non eliminazione generalizzata. |
| **E — Operativo attivo** | Processo, registro, task, roadmap, handoff e specifiche attive | `KEEP`, eventualmente `UPDATE` minimale; non migrare in massa né alterare stato e sincronizzazione. |
| **F — Ambiguo/misto** | Autorità contrastante, contenuto parzialmente valido | `REVIEW`; non archiviare/rimuovere prima della risoluzione. |

**Azione ≠ classe**: `KEEP`, `UPDATE`, `CONSOLIDATE`, `REPLACE`, `ARCHIVE`, `REVIEW` sono raccomandazioni separate. Documenti con contenuti misti possono essere **aggiornati parzialmente** e mantenuti. Per ogni candidato `REPLACE` o `ARCHIVE` produrre percorso, ragione, riferimento di autorità, dipendenze/riferimenti e destinazione delle informazioni vive.

**Esclusioni predefinite dall'archiviazione automatica:** `agentic/_tasks/**`, `agentic/_plans/**`, `agentic/_tasks/register.json`, `HANDOFF.md`, roadmap attive, report di benchmark necessari a test ripetibili, documenti di uso/tool/API, codice e test. Sono possibili proposte puntuali per file storici ma richiedono decisione esplicita, e un file non deve sparire soltanto perché vecchio/lungo.

### 4.3 Ricerca di autorità e riferimenti nascosti

Ispezionare e riconciliare:

- Precedenze dichiarate in README, `CLAUDE.md`, `AGENTS.md`, `agentic/README.md`, `00-bootstrap.md`, `01-architecture-rules.md`, `05-workflow.md`, `06-decisions.md`, `PROCESS.md` e guide per Copilot/Codex/Claude.
- Obblighi di creare nuove ADR, leggere tutte le ADR, seguire automaticamente link storici, caricare `06-decisions.md`, recuperare documentazione storica come se fosse corrente.
- Sovrapposizioni e contraddizioni tra ADR, decision log, regole, piani, documentazione tecnica e instruction con precedenza implicita.
- Script/generatori di prompt o bootstrap che allegano automaticamente file: **solo ispezione**, eventuale correzione eseguibile è fuori scope.
- Link destinati agli utenti e riferimenti storico-normativi (`ADR-xxxx`, `docs/architecture/adr/...`) in Markdown, issue/task, README, commenti del codice e test: distinguerne il significato prima di correggere. Non riscrivere in massa commenti per stile.

## 5. Archivio storico con confini espliciti

### 5.1 Archivio minimo e ampliamento selettivo

Percorso proposto: `docs/architecture/archive/legacy-adr-and-decisions-2026-10-22.zip`.

Contenuto **minimo subordinato a verifica DOC-01**:

- Le 55 ADR `.md` originali della baseline dichiarata (o l'insieme esatto verificato e approvato se differisce).
- Copia originale di `agentic/06-decisions.md`.
- Copia originale di `agentic/01-architecture-rules.md`.

**Estensione approvabile:** altri documenti **realmente ritirati/sostituiti** trovati in `agentic/` o `docs/`, conservati con *percorso relativo originale* nello ZIP. Non aggiungere automaticamente tutti i piani/report storici; non includere artefatti giganti/binari o file con segreti. Se un file è solo aggiornato, basta la storia Git salvo motivazione concreta per conservarne copia nello ZIP.

### 5.2 Manifest, tracciabilità e accesso

All'interno dello ZIP inserire `MANIFEST.md` (eventualmente con elenco meccanico CSV/JSON come supporto non normativo) con:

- SHA completo di baseline Git usata e commit del branch della migrazione.
- Per ogni originale: percorso, SHA-256, dimensione, categoria, motivo di ritiro, destinazione della regola valida oppure `historical-only`.
- Mappa **ADR-ID → contratto/sezione** o `historical-only`/`pending-operator-decision`, senza inventare corrispondenze.
- Inventario ZIP e verifica automatica di checksum e numero degli originali.

Tenere un indice esterno **molto breve** (`docs/architecture/archive/README.md` oppure sezione del README ADR), che dichiari la non autorità dello ZIP e come recuperare uno specifico originale **solo su richiesta esplicita**. Non copiare la matrice completa dentro il bootstrap.

Solo **dopo** verifiche e autorizzazione alla migrazione: rimuovere le ADR Markdown dall'albero attivo `docs/architecture/adr/`, lasciando un `README.md` che indirizzi alla documentazione corrente e all'archivio. Nessuna riscrittura della storia Git, nessuno stub normativo per tutte le ADR. Verificare dimensione ZIP e limiti di repository; se incompatibile con il contesto, fermarsi e proporre un archivio alternativo approvato, senza eliminare originali.

## 6. Bootstrap e conservazione della governance

Aggiornare nella **stessa PR documentale** in cui gli originali vengono ritirati:

- `CLAUDE.md`: router corto; lettura predefinita minima di istruzioni, **task attivo**, `PROCESS.md` solo quando necessari stati/processo e solo contratti pertinenti; nessuna lettura automatica di archivio, vecchie ADR e `06-decisions.md`.
- `AGENTS.md` se assente: breve punto d'ingresso che rimanda al router, senza duplicarne norme; non importare la sua versione da PR #124.
- `agentic/00-bootstrap.md`, `agentic/README.md`, `agentic/05-workflow.md`: rimuovere vecchie precedence e obblighi indiscriminati di ADR; nuove decisioni strutturali modificano il contratto corrente **solo con approvazione operatore**, non tramite proliferazione automatica di ADR.
- `agentic/01-architecture-rules.md`: ridurre a indice/invarianti non duplicate, oppure sostituire secondo l'inventario approvato; prima salvare originale nell'archivio.
- `agentic/06-decisions.md`: rimuovere l'autorità, scegliere un redirect storico o la rimozione secondo dipendenze; prima salvare originale.
- `agentic/_tasks/PROCESS.md`: cambiare **soltanto** le attribuzioni d'autorità sulle decisioni architetturali; **non** alterare gate, registro, progetto #6, sincronizzazioni e gestione degli stati.
- Root README, CONTRIBUTING, `.github/copilot-instructions.md`, `.claude`, `.codex`, instruction annidate: allineare i punti di accesso e la precedenza, non clonare il contenuto dei contratti.
- Altri documenti selezionati in DOC-01: `UPDATE`/`CONSOLIDATE`/`ARCHIVE` solo secondo la matrice approvata; intervenire sui link effettivamente rotti nei documenti di uso corrente.

**Modello di lettura desiderato:** un normale task legge router + task attivo + eventuali istruzioni di processo strettamente necessarie + uno o più contratti **effettivamente pertinenti** + riferimenti tecnici su richiesta. Non imporre arbitrariamente di leggere esattamente un contratto quando il task ne richiede due; non caricare tutto `docs/architecture/current/` per default.

## 7. Sequenza di esecuzione e gate di approvazione

### DOC-01 — Full documentation inventory and precedence gate (**solo lettura**)

**Input:** esclusivamente branch preparatorio derivato da `main`, con questo piano; nessun contenuto da PR #124 o altri branch.

**Azioni:**

1. Verificare Git: HEAD, working tree, relazione con baseline, stato `main` remoto e differenze rispetto alla baseline attesa. Interrompere se la base non è verificabile o diverge senza approvazione.
2. Inventariare **tutti i percorsi tracciati** sotto `agentic/` e `docs/`; censire instruction root/annidate e loader pertinenti. Numero per directory, tipo e categoria provvisoria.
3. Analizzare approfonditamente **tutte** le fonti normative, istruzionali e i candidati concreti all'archiviazione; campionare/leggere selettivamente le altre fonti in base a contenuto e riferimenti.
4. Individuare decisioni valide, status Accepted/Proposed, contraddizioni, ridondanze, letture obbligatorie, link e dipendenze; confermare i vincoli di sicurezza/governance.
5. Classificare puntualmente i candidati `CONSOLIDATE`/`REPLACE`/`ARCHIVE`/`REVIEW`; per il resto fornire inventario completo dei percorsi e raggruppamento `KEEP/UPDATE`, senza produrre un report narrativo infinito.
6. Produrre una matrice **fonte → regola vigente → contratto corrente + sezione** per le invarianti critiche e una proposta di struttura finale.
7. Rilevare l'impatto sulla definizione di DOC-02/03/04, comprese eventuali dipendenze che richiederebbero modifiche non docs-only.

**Output in chat, senza file modificati:**

- Stato Git e copertura effettiva dell'inventario.
- Conteggi per directory/famiglia e distribuzione per categorie.
- Tabella **percorso, categoria, azione, motivazione, destinazione, riferimenti entranti** per i documenti da sostituire/archiviare e quelli ambigui.
- Documenti da conservare, almeno per gruppi omogenei; eccezioni nominative.
- Coverage delle invarianti e conflitti non risolti.
- Proposta dello ZIP esteso (elenco esatto dei **candidati aggiuntivi**, non cancellazioni approvate).
- Lista **breve e puntuale** delle decisioni richieste all'operatore.
- Verdetto `READY FOR PLAN APPROVAL` o `BLOCKED`, motivato.

Un'appendice tecnica di percorsi/contatori è ammessa per completezza: il sommario principale rimane sintetico. **Stop dopo DOC-01.** Nessun commit, push o PR.

### GATE-01 — Approvazione esplicita dell'operatore

L'operatore rivede report e proposte, decide l'elenco **esatto** dei file da archiviare/ritirare e le decisioni non risolte, approva eventuali modifiche di perimetro e autorizza la fase successiva. L'agente aggiorna **questo piano** con le scelte approvate, in un passaggio documentale tracciato; non sostituisce silenziosamente lo scopo durante DOC-01. Se emergono scelte strutturali non approvate, non passare a DOC-02.

### DOC-02 — Consolidamento canonico e matrice di copertura

- Lavorare soltanto sui documenti approvati in GATE-01.
- Redigere i cinque contratti distinguendo **comportamento corrente dimostrato**, **decisione Accepted** e **obiettivo non ancora attivato**.
- Non trasformare ADR Proposed in Accepted; eventuali scelte operatori devono essere registrate con portata e stato espliciti.
- Consolidare ogni regola valida dai documenti aggiuntivi classificati A/B/F, evitando doppie fonti normative.
- Produrre matrice tracciabile **documento/sezione di origine → documento/sezione corrente o storico soltanto**. Evidenziare ciò che richiede codice/test anziché dichiararlo già vero.
- Confrontare le invarianti di sicurezza, governance, recovery/evidence/Knowledge con le fonti e, dove necessario, col codice (sola lettura).

**Gate DOC-02:** nessuna regola di sicurezza scompare, nessun conflitto non deciso è inventato o mascherato, non più di cinque documenti architetturali correnti.

### DOC-03 — Archiviazione selettiva, bootstrap e riferimenti

- Congelare copie originali **prima** di ridurre/rimuovere file, generare e verificare ZIP/manifest con elenco approvato.
- Rimuovere le ADR storiche dall'albero attivo solo dopo verifica ZIP e copertura DOC-02.
- Aggiornare router/precedenze normative e altri file `agentic/`/`docs/` soltanto dove approvato.
- Mantenere integri task, registro, handoff, regole operative e documentazione tecnica; correggere i riferimenti rilevanti che puntavano agli originali ritirati.
- Documentare riferimenti ancora storici deliberati; nessun trigger di lettura automatica verso archivio o vecchi registri.
- Se script o codice caricano ancora file storici, fermarsi e segnalare un task separato (non modificare eseguibili nella PR docs-only).

**Gate DOC-03:** ZIP integro, nessun documento ritirato prima della copertura, nessun link normativo attivo ambiguo, bootstrap minimo e governance preservata.

### DOC-04 — Static acceptance e PR documentale

Verifiche **mirate** (non campagna di test runtime indiscriminata):

1. Diff Git: file modificati attesi, nessuna contaminazione da altri branch, PR #124 o modifiche a codice/test/script/registri operativi.
2. ZIP: esistenza, elenco originali, percorsi, checksum SHA-256, manifest e baseline; eventuali candidati aggiuntivi **tutti** approvati.
3. Coverage: tutte le ADR inventariate hanno destinazione; documenti ritirati hanno destinazione delle regole vive oppure marcatura `historical-only`; decisioni Proposed non promosse.
4. Bootstrap: nessuna istruzione attiva impone vecchie ADR, `06-decisions.md` o scansioni archivi; ordine di lettura minimale e gerarchia chiara.
5. Riferimenti: assenza di link **correnti** rotti introdotti dalla migrazione; riferimenti puramente storici esplicitamente distinti; nessuna generazione automatica di contesto storico non autorizzata.
6. Governance: `agentic/_tasks/PROCESS.md`, task/register/HANDOFF/issue/progetto #6 senza regressioni di autorità sul processo, gate, stato o sincronizzazione.
7. Assenza di documenti strutturali concorrenti all'interno di `agentic/`, `docs/` e instruction annidate *esaminati*; riferimenti tecnici validi rimasti accessibili.
8. Smoke documentale dell'agente: simulare la lettura di un **task ordinario** e di un **task architetturale** e registrare i file che verrebbero letti; nessuna lettura ordinaria di ZIP/ADR storici.

Produrre report finale `PASS/BLOCKED` con anomalie residue e lista delle modifiche. Dopo **autorizzazione esplicita**, creare commit controllati e PR **docs-only con destinazione `main`**, senza merge automatico. Se i vincoli non sono soddisfatti, **non pubblicare una PR dichiarata pronta**.

**Solo dopo merge approvato** e verifica del nuovo `main` può iniziare, su branch distinto, la convergenza MAF secondo un piano autonomo. La migrazione documentale non è una release di codice.

## 8. Exit criteria non negoziabili

- [ ] DOC-01 ha censito l'intero **indice dei file tracciati** in `agentic/`/`docs/` e i punti di ingresso istruzionali, dichiarando le eventuali lacune.
- [ ] Ogni documento normativo, ridondante, ambiguo o candidato al ritiro ha classificazione, motivazione, dipendenze e destinazione delle regole valide.
- [ ] Un elenco **approvato dall'operatore** definisce i file non-ADR aggiunti allo ZIP; nessuna eliminazione per anzianità/lunghezza.
- [ ] ZIP contiene l'insieme verificato di ADR originali più le copie pre-sostituzione dei due registri originariamente previsti e gli **ulteriori documenti effettivamente ritirati** e approvati; SHA/manifest coerenti.
- [ ] Le ADR storiche non restano documenti normativi attivi; la directory ADR ha soltanto un indice/redirect appropriato.
- [ ] Esistono al massimo cinque **contratti architetturali strutturali** correnti, senza registri paralleli contraddittori; la documentazione tecnica e operativa utile resta preservata.
- [ ] Nessuna instruction/bootstrap o tool di assemblaggio contesto ordina di leggere ADR/decisions storiche automaticamente.
- [ ] Sicurezza, policy, entitlement, approval, tool boundaries, resume/mutazioni, audit, grounding, Knowledge e confini MAF coperti senza promuovere Proposed.
- [ ] `agentic/_tasks/register.json`, `agentic/_tasks/PROCESS.md`, `HANDOFF.md`, Issue/progetto #6 e workflow restano coerenti con il rispettivo ruolo operativo.
- [ ] Nessuna modifica a codice, test, CI, script o package e nessuna importazione da PR #124.
- [ ] Link correnti significativi funzionanti; riferimenti storici hanno un percorso di consultazione controllato.
- [ ] Due smoke documentali mostrano bootstrap ridotto e nessuna lettura automatica dell'archivio.
- [ ] PR mirata, approvabile, **mai merged automaticamente**; convergenza MAF rimandata a un task separato.

## 9. Modalità di lavoro per Claude Code Cloud

- **Prima sessione:** usare il branch preparatorio già creato; leggere questo piano e completare **solo DOC-01**, in sola lettura. Niente scrittura del report nel repository e niente promesse di migrazione già completata.
- **Dopo il report:** sottoporre all'operatore solo i conflitti e le decisioni vere; approvare selezione documenti da ritirare e aggiornamento del piano.
- **Seconda sessione, solo se autorizzata:** DOC-02, DOC-03 e DOC-04 con checkpoint di integrità, diff circoscritti e PR subordinata alla verifica/autorizzazione.
- **Efficienza:** inventario completo dei file ≠ caricamento integrale di tutti i file nel contesto LLM. Usare Git per il censimento, ispezione mirata delle fonti normative, output meccanici di riferimento, e nessuna ricerca ricorsiva indiscriminata nel contenuto dell'intero repository.
- **Niente false certezze:** una decisione contenuta in Markdown non dimostra che il runtime la implementi; la sola presenza di uno ZIP non impedisce recuperi automatici se bootstrap/loader non vengono corretti. Il recupero intenzionale da Git resta tecnicamente possibile.
