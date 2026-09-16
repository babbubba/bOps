> **OBSOLETE MIGRATION INPUT — DO NOT IMPLEMENT FROM THIS FILE.** Its accepted requirements were
> migrated into [`agentic/_plans/2026-09-16-consolidated-roadmap.md`](../../_plans/2026-09-16-consolidated-roadmap.md)
> and the active task files. Coding agents must ignore this archive.

# Specifiche pendenti — raccolta in attesa di consolidamento

Stato: **documento di lavoro, non normativo**. Non è un piano, non fa parte di `agentic/`,
nessun agent deve leggerlo come regola o come backlog autoritativo durante un task ordinario.

**Contesto operativo (dal 2026-09-15):** un agent di coding sta lavorando dal vivo sul repository, avanti su V0.10 (`bOps.PluginHost`, ADR-0020, sample plugin — non ancora committati al momento della verifica). Finché è così, questa chat **non esegue modifiche dirette al repo**: solo raccolta, verifica di coerenza e consolidamento successivo, per evitare conflitti con il lavoro in corso nella stessa working tree.

Contiene le richieste di modifica/funzionalità raccolte in chat con Fabio man mano che emergono,
in attesa di essere consolidate in un unico documento e girate all'agent che lavora sul repo per
trasformarle in una **proposta** di aggiornamento di `piano-bops-v0.9.1-v2.0.md` (e, se necessario,
di nuove ADR) — mai in una modifica diretta del piano approvato.

Riferimenti usati per la verifica di coerenza di ogni voce:
- `agentic/00-project-spec.md` — principi non negoziabili e fuori-scope
- `agentic/06-decisions.md` — decisioni già prese, non riproponibili senza informazioni nuove
- `piano-bops-v0.9.1-v2.0.md` §5 — catena di propedeuticità (v0.9.1→v0.10→v0.11→v1.0→...→v2.0);
  nessuna milestone si apre prima che il gate della precedente sia chiuso
- `piano-bops-v0.9.1-v2.0.md` §7 — backlog per versione, per capire se la richiesta è già prevista
- `docs/architecture/adr/` — ADR accettate

Ogni voce riceve una versione **candidata**, non definitiva: l'ordine reale con cui le richieste
diventano lavoro dipende dalla catena di propedeuticità, non dall'ordine in cui arrivano in chat.
L'assegnazione finale si fa in fase di consolidamento, guardando l'insieme delle voci raccolte.

---

## Come si legge una voce

- **Stato**: `Raccolta` (appena registrata) · `Da chiarire` (manca un'informazione da Fabio) ·
  `Pronta per consolidamento` (verificata, in attesa del batch) · `Consolidata` (inclusa in un
  estratto già girato all'agent per la proposta di aggiornamento del piano — non più pendente,
  tenuta come traccia; vedi il file di consolidamento indicato) · `Scartata` (in conflitto con
  decisioni/regole già prese, motivo registrato, non si riapre senza informazioni nuove) ·
  `Già coperta dal piano` (verificata: non serve consolidarla, è già prevista così com'è —
  tenuta solo come traccia della verifica fatta)
- **Verifica di coerenza**: già implementata? già prevista nel piano? in conflitto con principi,
  decisioni o ADR esistenti? fuori scope dichiarato?
- **Versione candidata**: ipotesi, da confermare rispettando §5 del piano

---

## Indice

- [SPEC-001 — Settings: provider LLM e API key modificabili e persistenti](#spec-001--settings-provider-llm-e-api-key-modificabili-e-persistenti) — Consolidata (2026-09-16) — vedi `consolidamento-2026-09-16.md`
- [SPEC-002 — Rinominare i package core in "Skill"](#spec-002--rinominare-i-package-core-in-skill) — Scartata
- [SPEC-003 — Aggiornare il README e valutare una versione bilingue](#spec-003--aggiornare-il-readme-e-valutare-una-versione-bilingue) — Consolidata (2026-09-16) — vedi `consolidamento-2026-09-16.md`
- [SPEC-004 — Pagina UI dedicata a plugin/Skill installati e attivi](#spec-004--pagina-ui-dedicata-a-pluginskill-installati-e-attivi) — Consolidata (2026-09-16) — vedi `consolidamento-2026-09-16.md`
- [SPEC-005 — Coordinamento agent remoti/multi-nodo](#spec-005--coordinamento-agent-remotimulti-nodo) — Già coperta dal piano
- [SPEC-006 — Tool di sistema: applicazioni installate e inventario hardware/driver](#spec-006--tool-di-sistema-applicazioni-installate-e-inventario-hardwaredriver) — Consolidata (2026-09-16) — vedi `consolidamento-2026-09-16.md`
- [SPEC-007 — Tool di ricerca web e verifica driver aggiornati (fonte esterna)](#spec-007--tool-di-ricerca-web-e-verifica-driver-aggiornati-fonte-esterna) — Consolidata (2026-09-16) — vedi `consolidamento-2026-09-16.md`
- [SPEC-008 — Tool di analisi dimensione cartelle/file (tipo TreeSize)](#spec-008--tool-di-analisi-dimensione-cartellefile-tipo-treesize) — Consolidata (2026-09-16) — vedi `consolidamento-2026-09-16.md`
- [SPEC-009 — Tool di cancellazione cartelle/file in blocco per liberare spazio](#spec-009--tool-di-cancellazione-cartellefile-in-blocco-per-liberare-spazio) — Consolidata (2026-09-16) — vedi `consolidamento-2026-09-16.md`

---

### SPEC-008 — Tool di analisi dimensione cartelle/file (tipo TreeSize)

- **Data**: 2026-09-16
- **Richiesta originale**: "ci vuole un ulteriore tool per capire la dimensione delle cartelle e
  dei file (tipo treesize) a partire da una cartella o dalla root...serve per analizzare
  eventuali cartelle eccessivamente grandi e per poter poi liberare spazio in sicurezza."
- **Area toccata**: `bOps.Packages.Filesystem` (stesso package di `fs.list`/`fs.stat`/
  `fs.search`/`fs.hash`/`fs.delete`/`fs.move`/`fs.read`/`fs.write`), `FilesystemPathPolicy` per lo
  scoping dei path leggibili.
- **Verifica di coerenza**:
  - Già implementata: **No.** `fs.list` elenca solo le voci immediate di una cartella e "never
    recurses" (commento esplicito nel codice sorgente); riporta una dimensione solo per i file,
    `-` per le directory. `fs.stat` riporta la dimensione solo per i file (`sizeBytes: null` per
    una directory). Nessun tool oggi calcola una dimensione aggregata/ricorsiva di una cartella.
  - Già pianificata: **No con questo scope preciso**, ma coerente con l'estensione della famiglia
    `fs.*` già prevista a v0.11 (`piano-bops-v0.9.1-v2.0.md` §7: stesso capitolo di `fs.search`,
    `fs.hash`, `fs.move` — nuovi reader per lo stesso package `Filesystem`).
  - Conflitti con principi/regole: **Nessuno.** `Risk = Read`, nessuna scrittura, nessun impatto
    sul modello di rischio. Nota tecnica: `fs.list` evita deliberatamente la ricorsione,
    verosimilmente per S7 ("every action runs under a timeout") su alberi di directory molto
    grandi — un tool ricorsivo tipo TreeSize deve gestire esplicitamente questo limite (es. un
    parametro di profondità massima, o restituire le N cartelle/file più grandi invece
    dell'albero completo), riusando lo stesso `CancellationToken` già passato a ogni `ITool`.
  - Fuori scope dichiarato: **No.**
- **Versione candidata**: da assegnare in consolidamento — naturale come completamento della
  famiglia `fs.*` a v0.11, stesso pacchetto/capitolo di `fs.search`/`fs.hash`/`fs.move`.
- **Stato**: Consolidata (2026-09-16) — inviata all'agent in `consolidamento-2026-09-16.md`, non più pendente.

### SPEC-009 — Tool di cancellazione cartelle/file in blocco per liberare spazio

- **Data**: 2026-09-16
- **Richiesta originale**: "...serve per liberare spazio in sicurezza... probabilmente serve un
  tool separato per fare il clean del file system."
- **Area toccata**: `bOps.Packages.Filesystem` — estensione/affiancamento di `fs.delete`,
  dipendente dal dato prodotto da SPEC-008.
- **Verifica di coerenza**:
  - Già implementata: **No.** `fs.delete` esiste ma cancella solo un singolo file, mai una
    directory: il commento nel codice lo dice esplicitamente — "Deliberately does not delete
    directories (recursive deletion is a materially larger blast radius and out of scope for
    this version)" — e a runtime rifiuta esplicitamente un path di tipo directory.
  - Già pianificata: **Non con uno scope preciso.** ADR-0002 (cinque livelli di rischio,
    `Critical` = `Forbidden` incondizionato e non bypassabile) cita in generale "irreversible
    filesystem or database operations" come primi candidati a `Critical`, "scoped for V1.6/V1.8"
    — ma verificando `piano-bops-v0.9.1-v2.0.md` §7, v1.6 e v1.8 sono in realtà solo
    "PostgreSQL remediation governata" e "SQL Server remediation governata": nessuna voce del
    piano copre oggi una remediation filesystem irreversibile. È quindi una richiesta reale, non
    ancora coperta da nessuna versione concreta, nonostante il riferimento generico nell'ADR.
  - Conflitti con principi/regole: **Punto centrale, risolto sotto con Fabio.** ADR-0002 rende
    `Critical` `Forbidden` in modo incondizionato e non bypassabile (enforced due volte: loader +
    `PolicyEngine.Evaluate`), e la regola S3 dà come risposta documentata a un "ma mi serve" che
    *l'operazione si fa fuori da bOps, a mano* — la cancellazione ricorsiva/in blocco era quindi
    un candidato plausibile a `Critical` (mai eseguibile da bOps), in alternativa a `High` come
    l'attuale `fs.delete` (eseguibile, sempre con approvazione).
  - Fuori scope dichiarato: **No esplicitamente**, ma toccava un'invariante architetturale
    (ADR-0002) — non una decisione già presa da riaprire, semmai una classificazione di rischio
    nuova da decidere con Fabio, ora fatta.
- **Chiarimenti di Fabio (2026-09-16)**:
  - Classificazione: **bOps cancella lui stesso, `Risk = High` con approvazione obbligatoria ogni
    volta** — stesso modello di `fs.delete` oggi, esteso esplicitamente a cartelle/gruppi di
    file (ampliamento deliberato del blast radius che `fs.delete` esclude di proposito, non un
    bypass implicito).
  - Modalità: **cancellazione diretta e permanente**, non uno spostamento reversibile in
    quarantena — stesso comportamento di `fs.delete` sul singolo file, esteso a più elementi.
  - Anteprima: **sempre obbligatoria prima dell'approvazione** — l'operatore deve vedere
    l'elenco esatto di cosa verrebbe rimosso e la dimensione totale (calcolati con SPEC-008),
    mai solo il path passato al tool. In pratica il flusso di verifica/approvazione include un
    passo di "dry-run" basato su SPEC-008 come dato mostrato all'operatore prima del sì/no,
    analogo nello spirito a come `fs.move`/`fs.write`/`fs.delete` dichiarano già una
    `VerificationSpec` post-azione — qui serve in aggiunta un'anteprima *pre*-azione.
- **Nota tecnica**: dipendenza diretta da SPEC-008 (l'anteprima usa il tool di analisi
  dimensioni); da valutare in consolidamento se estendere `fs.delete` per accettare anche
  directory, o introdurre un tool nuovo (es. `fs.delete_tree`) per tenere separato, ed
  esplicito in audit, il caso a blast radius maggiore.
- **Versione candidata**: da assegnare in consolidamento — dopo SPEC-008 (di cui dipende per il
  dato dell'anteprima), stessa area v0.11/`fs.*`, ma da valutare con più attenzione data la
  novità nel modello di rischio (primo tool `fs.*` `High` con blast radius su più elementi).
- **Stato**: Consolidata (2026-09-16) — inviata all'agent in `consolidamento-2026-09-16.md`, non più pendente.

### SPEC-006 — Tool di sistema: applicazioni installate e inventario hardware/driver

- **Data**: 2026-09-16
- **Richiesta originale**: "servono altri tool il prima possibile: tool per vedere le
  applicazioni installate; tool per ottenere il nome dell'hardware (tipo/modello laptop o
  workstation o server se presente); tool per ottenere l'hardware devices nel computer e
  relativi driver."
- **Area toccata**: `bOps.Packages.System.Core` (manifest condivisi, `SystemToolManifests.cs`),
  `bOps.Packages.System.Windows`, `bOps.Packages.System.Linux` (implementazioni per OS, coerente
  con ADR-0006/rule A8) — nessun nuovo package, sono estensioni della famiglia `system.*`
  esistente.
- **Verifica di coerenza**:
  - Già implementata: **No.** I tool `system.*` esistenti (`system.info`, `system.cpu`,
    `system.memory`, `system.disk`, `system.swap`, `system.io`, oltre a `process.*`) sono
    definiti in `SystemToolManifests.cs`; `system.info` riporta solo "OS description, hostname,
    and uptime" — nessun campo su applicazioni installate, modello macchina o dispositivi/driver.
  - Già pianificata: **No.** Non compaiono nel backlog di `piano-bops-v0.9.1-v2.0.md` §7 né in
    `agentic/07-plan-corrections.md`.
  - Conflitti con principi/regole: **Nessuno.** Sono tool a sola lettura (`Risk = Read`, stesso
    livello di `system.info`), non introducono esecuzione generica (coerente con S1), e seguono
    esattamente il pattern già stabilito da ADR-0006/rule A8: ogni OS package (Windows/Linux)
    implementa la propria versione dello stesso contratto (Windows: tipicamente WMI —
    `Win32_Product`/`Win32_PnPEntity`/`Win32_ComputerSystem`; Linux: equivalenti come i package
    manager di sistema, `lspci`/`lsusb`, `/sys`), senza bisogno di un `ISystemProvider` condiviso
    (già escluso da ADR-0006).
  - Fuori scope dichiarato: **No.**
  - Nota di design da chiarire in consolidamento: il "nome/modello hardware" potrebbe essere un
    campo aggiunto a `system.info` (che già esiste) oppure un tool nuovo (es. `system.hardware`).
    `agentic/00-project-spec.md` registra esplicitamente il precedente opposto — "`system.uptime`
    as a separate tool — `system.info` already reports uptime; a second tool for the same data is
    never added" — quindi la scelta va motivata: se il modello macchina è un dato "leggero" simile
    a hostname, andrebbe dentro `system.info`; se le applicazioni installate e i dispositivi/
    driver sono elenchi potenzialmente lunghi (come `process.list`), meritano tool propri (es.
    `system.apps`, `system.devices`) con lo stesso stile di `process.list` (parametro `limit`
    opzionale).
- **Chiarimenti di Fabio (2026-09-16)**:
  - Confermato: il modello/nome hardware (laptop/workstation/server) va aggiunto come campo di
    `system.info` esistente, non come tool separato — risolve la nota di design sopra restando
    coerente col precedente `system.uptime`. Restano invece tool a sé, come ipotizzato, le liste
    potenzialmente lunghe: applicazioni installate (`system.apps`) e dispositivi/driver
    (`system.devices`), sullo stesso stile di `process.list`.
- **Versione candidata**: da assegnare in consolidamento — collocazione naturale come completamento
  della famiglia `system.*` già esistente, compatibile con V0.10 (loader/plugin SDK, in corso) o
  V0.11 (`00-project-spec.md` la descrive come "completing the operational capabilities the
  historical plan and README had promised but never registered").
- **Stato**: Consolidata (2026-09-16) — inviata all'agent in `consolidamento-2026-09-16.md`, non più pendente.

### SPEC-007 — Tool di ricerca web e verifica driver aggiornati (fonte esterna)

- **Data**: 2026-09-16
- **Richiesta originale**: "tool per verificare se esistono driver aggiornati; tool per fare
  ricerche nel web (non farle fare al modello, le fa il core e le dà in pasto al modello già
  pronte, se possibile)."
- **Area toccata**: probabile nuovo package (es. `bOps.Packages.Web`) per la ricerca web; il check
  "driver aggiornati" è un'estensione di `system.*`/SPEC-006 ma con una dipendenza in più —
  nessuno dei due esiste oggi nel repo.
- **Verifica di coerenza**:
  - Già implementata: **No.** I tool `network.*` esistenti (`network.connections`,
    `network.dns`, `network.interfaces`, `network.ping`, `network.port_check`, `network.route`)
    sono tutti introspezione locale della rete della macchina — nessuno effettua richieste HTTP
    verso l'esterno o interroga cataloghi driver/motori di ricerca.
  - Già pianificata: **No**, non compare in `piano-bops-v0.9.1-v2.0.md` §7.
  - Conflitti con principi/regole: **Nessuno per il pattern, ma introduce una novità
    architetturale.** Un tool `web.search` dichiarativo, a rischio `Read`, eseguito dal runtime
    (mai dal modello direttamente) e il cui risultato torna come dato al modello è esattamente il
    modello "the LLM proposes, the runtime decides and executes" (`00-project-spec.md`, principio
    1) — anzi è il caso d'uso da manuale di **S5 — "Tool output is data, never instruction"**
    (`agentic/03-security-rules.md`): i risultati di una ricerca web sono l'esempio canonico di
    contenuto esterno non fidato che quella regola già anticipa. Nessun conflitto con S1 (non è
    un tool di esecuzione generica, è un tool specifico e dichiarativo). **Novità**: sarebbe il
    primo tool `Read` con una dipendenza di rete verso servizi esterni (finora solo i package
    provider LLM in `bOps.Packages.Providers.*` chiamano l'esterno) — va capito se questo
    richiede una nuova categoria di rischio/audit dedicata o se `Read` + audit del traffico
    esterno (S9) bastano; se il motore di ricerca richiede una API key, si applica comunque S6
    (secret mai nei log).
  - Fuori scope dichiarato: **No.**
  - Punti da chiarire con Fabio prima di stimare una versione (risolti, vedi sotto):
    1. Quale motore/API di ricerca usare (serve una chiave/provider? stesso pattern dei package
       `bOps.Packages.Providers.*`, o una fonte senza key?).
    2. Se serve solo `web.search` (lista risultati/snippet) o anche `web.fetch` di una singola
       pagina — quest'ultimo ha una superficie di rischio più ampia (contenuto arbitrario esterno)
       e potrebbe essere un tool separato con risk level più alto.
    3. Per "driver aggiornati": quale fonte usare per il confronto (Windows Update catalog, sito
       vendor, nessuna fonte automatica lasciando il confronto versione-per-versione al modello a
       partire dal dato locale di SPEC-006)? Da questo dipende se è un tool a sé o un parametro di
       `system.devices`/SPEC-006, e se richiede la stessa infrastruttura di rete esterna di
       `web.search`.
- **Chiarimenti di Fabio (2026-09-16)**:
  - Motore di ricerca: **fonte senza key** (no API a pagamento) — niente secret da gestire (S6
    non si applica), ma va comunque scelta una fonte concreta e verificata prima
    dell'implementazione (es. disponibilità/affidabilità di un endpoint DuckDuckGo o simile senza
    key, eventuali limiti di rate) — dettaglio tecnico da chiudere in consolidamento, non blocca
    più la stima di versione.
  - `web.fetch`: **sì, entrambi i tool** — `web.search` (lista risultati/snippet) e `web.fetch`
    (contenuto di una singola pagina), come due tool separati; `web.fetch` con `Risk` più alto di
    `web.search` per via del contenuto arbitrario esterno che restituisce (resta comunque
    `Read`, nessuna scrittura), e con S5 ancora più rilevante lì che su `web.search`.
  - Driver aggiornati: **nessuna fonte automatica dedicata** — niente Windows Update catalog né
    integrazioni vendor. Il tool `system.devices` (SPEC-006) riporta la versione driver locale;
    il confronto "è aggiornato?" lo fa il modello, quando richiesto, usando `web.search` su quel
    dato. Punto 4 della richiesta originale quindi **non genera un tool proprio**: è coperto
    dalla combinazione SPEC-006 (dato locale) + `web.search` (SPEC-007), senza lavoro
    aggiuntivo dedicato.
- **Area toccata (aggiornata)**: nuovo package, probabilmente `bOps.Packages.Web`, con due tool —
  `web.search` e `web.fetch` — entrambi `Risk = Read`. Nessun impatto su `system.*`/SPEC-006 oltre
  alla dipendenza logica già descritta per il caso "driver aggiornati".
- **Versione candidata**: da assegnare in consolidamento — nessun blocco residuo dopo i
  chiarimenti; stessa collocazione naturale ipotizzata per SPEC-006 (V0.10/V0.11), da confermare
  guardando la catena di propedeuticità (§5) insieme al resto del batch.
- **Stato**: Consolidata (2026-09-16) — inviata all'agent in `consolidamento-2026-09-16.md`, non più pendente.

### SPEC-005 — Coordinamento agent remoti/multi-nodo

- **Data**: 2026-09-15
- **Richiesta originale**: "Non sono certo che nel nuovo piano abbia incluso anche la gestione
  degli agent remoti. Lo scopo sarebbe quello di avere il core in esecuzione su un server o sul
  mio client e gli agent in esecuzione su diversi server/pc. deve essere possibile coordinare le
  operazioni dal mio client (via ui e magari anche via cli) indicando il server target quando si
  effettua una richiesta. l'agent sulla macchina dovrebbe comunicare con il mio pc per elaborare
  le informazioni. la comunicazione dovrebbe essere cifrata. credo che si possa fare solo dopo
  aver previsto la logica multiagente che se non erro è già nel piano. bisogna definire un modello
  di comunicazione affidabile e solido sia in lan che tra segmenti di rete differenti."
- **Area toccata**: topologia di rete/deployment, non una singola area di codice
- **Verifica di coerenza**:
  - Già implementata: No (topologia oggi è local-only, D-001).
  - Già pianificata: **Sì, in dettaglio, a v1.4** ("trasporto sicuro e Control Plane foundation",
    `piano-bops-v0.9.1-v2.0.md` §7), con dipendenza dichiarata da v1.3 (entitlement/commerciale) e
    quindi, a monte, da v1.2 (multi-agent) — l'intuizione di Fabio sulla propedeuticità è corretta,
    anche se v1.2 è esplicitamente "multi-agent **logico nello stesso processo**", non distribuito:
    il pezzo distribuito vero e proprio è v1.4, due versioni dopo, non v1.2 stessa.
  - Dettagli già decisi che rispondono punto per punto alla richiesta:
    - *Cifratura*: v1.4 nota 1, "connessione outbound dal nodo, autenticazione mutua [mTLS]... e
      revoca"; test previsti includono mTLS/identity, replay/downgrade, tenant isolation.
    - *Coordinamento con "server target"*: v1.4 nota 3, "target" è un'entità di prima classe insieme
      a tenant/user/role/agent/Skill/policy/task/approval/execution/audit.
    - *Modello di comunicazione affidabile*: v1.4 nota 6, "audit scritto prima localmente e poi
      spedito con idempotenza, ordering, retry e riconciliazione"; test previsti includono nodo
      offline, retry/idempotenza.
    - *LAN e segmenti di rete diversi*: la connessione è sempre **outbound dal nodo** (mai un path
      amministrativo inbound, vedi anche `00-project-spec.md` "esplicitamente fuori scope" e D-001)
      — proprio per funzionare a prescindere da NAT/firewall tra segmenti diversi, non solo in LAN.
    - Il Control Plane non invia mai comandi arbitrari, solo objective/piani/capability tipizzate; il
      nodo ricontrolla firma, freshness, entitlement, policy e approval localmente (v1.4 nota 2) —
      execution, policy e verifica restano sempre sul nodo amministrato (coerente con S1/S5 e con
      l'intero modello "the runtime decides and executes", non il Control Plane).
  - Conflitti con principi/regole: nessuno — è coerente con D-001 (il contratto è già pensato per
    questo dal V0.1) e con l'intero threat model del progetto.
  - Fuori scope dichiarato per l'attuale repository: **sì, in gran parte.** Per `piano-bops-v0.9.1-v2.0.md`
    §2/§4 (open-core, due repository), il Control Plane (storage, identity, RBAC, entitlement
    remoto, knowledge service) e il Portal enterprise sono **proprietari**, costruiti nel
    repository privato `bOps.Commercial` — mai in questo repository pubblico `bOps`. Nel
    repository OSS restano solo "i contratti di protocollo strettamente necessari" e un "agent
    connector locale" (v1.4, Affected projects/files). Quindi anche una volta raggiunta v1.4, la
    maggior parte di questa funzionalità non sarà comunque implementata qui.
- **Versione candidata**: v1.4 (già assegnata dal piano stesso), con la parte più consistente nel
  repository privato `bOps.Commercial`, non in questo repository.
- **Stato**: Già coperta dal piano — nessuna azione da consolidare, tenuta come traccia della
  verifica fatta il 2026-09-15.

### SPEC-004 — Pagina UI dedicata a plugin/Skill installati e attivi

- **Data**: 2026-09-15
- **Richiesta originale**: "per il caricamento delle plugin/skill e per sapere quali sono
  presenti e/o attive credo sia necessario anche una pagina dedicata nella ui."
- **Area toccata**: `web/bops-ui` (nuova feature "Plugins"), `bOps.Api` (nuovi endpoint di
  lettura, eventualmente enable/disable)
- **Verifica di coerenza**:
  - Già implementata: **No.** `app.routes.ts` oggi ha solo `dashboard`, `approvals`, `settings` —
    nessuna route/pagina plugin esiste.
  - Già pianificata: **No, non ancora assegnata a nessuna versione con questo scope.** Il backlog
    di v0.10 (piano §7) elenca la UI Angular *esclusa* da "Affected projects/files" (compaiono solo
    `bOps.Abstractions`, `bOps.Runtime`, `bOps.Cli`, `bOps.Api`, il loader, il manifest, template/
    sample, `docs/plugins/`) — coerente col precedente v0.9/ADR-0018, dove API e UI sono state
    deliberatamente scaglionate in sessioni separate. ADR-0020 (appena scritta per V0.10) definisce
    `PluginManager` (Install/List/Enable/Disable/Remove) ma resta lato `bOps.PluginHost`/CLI — non
    espone ancora nulla su `bOps.Api`, quindi manca anche l'endpoint HTTP da cui la UI potrebbe
    leggere.
  - Conflitti con principi/regole: **Nessuno nuovo.** Non è come SPEC-001 (API key): qui non ci
    sono secret coinvolti, e "vedere/abilitare/disabilitare un plugin" è nella stessa classe di
    rischio operativo delle approvazioni che la UI già espone oggi (Approvals) — quindi il gap di
    autenticazione noto di `bOps.Api` (ADR-0018) non è un blocco nuovo o specifico per questa
    richiesta, è la stessa condizione già accettata per tutto `bOps.Api` in questa fase.
  - Fuori scope dichiarato: no.
- **Nota tecnica**: servono comunque due pezzi non ancora scritti prima della pagina UI: (1) che
  il lavoro V0.10 in corso (`bOps.PluginHost`, ADR-0020) sia committato; (2) endpoint su
  `bOps.Api` che espongano `PluginManager` (lista installati/attivi, e se richiesto anche enable/
  disable) — analogo a come `GET /api/providers` (ADR-0019) è stato aggiunto solo quando la
  Settings page ne ha avuto bisogno. Una versione minima "sola lettura" (cosa è installato/attivo)
  è più semplice e prioritaria delle azioni di enable/disable dalla UI.
- **Versione candidata**: da assegnare in consolidamento — naturale come chiusura/completamento di
  V0.10 (UI di corredo, sullo stesso modello v0.9/Settings) oppure primi passi di v0.11; non prima
  che il lavoro V0.10 in corso sia committato.
- **Stato**: Consolidata (2026-09-16) — inviata all'agent in `consolidamento-2026-09-16.md`, non più pendente.

### SPEC-003 — Aggiornare il README e valutare una versione bilingue

- **Data**: 2026-09-15
- **Richiesta originale**: "il file readme di progetto non mi sembra aggiornato. andrebbe
  aggiornato e se esiste un modo per avere il readme bilingue (inglese e italiano) sarebbe utile
  farlo verso la fine (la traduzione dopo la v2.0 nel frattempo aggiorna però il readme se
  necessario)"
- **Area toccata**: `README.md`
- **Verifica di coerenza**:
  - Già implementata: **Parzialmente disallineata.** `README.md` (ultimo commit `99dac6b`)
    descrive ancora "V0.9.1 onward is repository/licensing readiness, then the plugin loader" come
    se v0.9.1 fosse ancora in corso, mentre risulta chiusa (`HANDOFF.md`, D-013–D-015). Inoltre,
    al momento della verifica risultano non committati — quindi non ancora documentabili come
    "disponibili" — i deliverable di V0.10 (`bOps.PluginHost`, ADR-0020, sample plugin): il README
    va aggiornato **dopo** che quel lavoro è committato e verificato, non prima (vedi Definition of
    Done, `agentic/05-workflow.md`: build senza warning, test passati, ADR accettata).
  - Già pianificata: la manutenzione del README rientra nello spirito di v0.9.1 implementation
    note 2 ("Allineare README e documentazione alle capability realmente registrate") già
    applicata una volta lì; tenerlo allineato ad ogni milestone è buona pratica del progetto, non
    una voce di roadmap a parte. La versione bilingue non è menzionata in nessun documento
    esistente — nessun conflitto, è un'idea nuova.
  - Conflitti con principi/regole: nessuno. Il README non è vincolato alla convenzione
    inglese/normativa di `agentic/` (quella si applica solo a quella cartella, `piano-bops.md`
    resta italiano per scelta esplicita) — una versione bilingue del README non contraddice nulla
    di già deciso.
  - Fuori scope dichiarato: no.
- **Nota**: la scelta di Fabio di rimandare la traduzione a dopo v2.0 è già coerente di suo con lo
  scope discipline del progetto (`agentic/05-workflow.md`) — nessun'obiezione da fare qui.
- **Versione candidata**:
  - Aggiornamento contenuti README (stato v0.9.1/V0.10, tabella tool, roadmap): da fare quando
    l'agent attualmente al lavoro su V0.10 committa il proprio risultato — non prima, e non da
    questa chat mentre il lavoro è in corso nella stessa working tree.
  - README bilingue (EN/IT): post-v2.0, per scelta esplicita di Fabio.
- **Stato**: Consolidata (2026-09-16) — inviata all'agent in `consolidamento-2026-09-16.md`, non più pendente.

### SPEC-002 — Rinominare i package core in "Skill"

- **Data**: 2026-09-15
- **Richiesta originale**: "nel nuovo piano è stata introdotta una logica per la gestione degli
  skill nel repo commerciale. gli skill alla fine se non erro sostituiscono i package... pertanto
  i package core (quelli non commerciali) forse andrebbero rinominati (a partire dai path a finire
  ai nomi di progetto) con la terminologia skill e non package. se invece era già previsto
  qualcosa di differente dimmelo ora"
- **Area toccata**: nomenclatura/namespace di tutti i package core, `bOps.Abstractions`,
  documentazione
- **Verifica di coerenza**:
  - Già implementata: N/A (richiesta di rinomina, non di funzionalità).
  - Già pianificata: **Sì, in modo esplicitamente diverso.** `piano-bops-v0.9.1-v2.0.md` §7, v1.1,
    implementation note 1: *"ADR per distinguere package, Skill, capability, tool ed agente"* —
    il progetto ha già in programma di tenerli concettualmente separati, non di fonderli.
  - Conflitti con principi/regole:
    - Principio 7 (`agentic/00-project-spec.md`): "Everything beyond the minimal runtime is a
      package... tools, tool categories, operating systems and LLM providers all load through the
      same extension contract."
    - **ADR-0006** ("tools-os-providers-are-packages"): decisione fondante, ancora valida.
    - **ADR-0012**: "official Skills need to load as packages into a core they don't need to
      fork" — le Skill si caricano *come* package, non li sostituiscono.
    - `piano-bops-v0.9.1-v2.0.md` §4 (confine tra repository): elenca "Package generici e
      provider" e "Sample Skill" come due righe distinte della stessa tabella — non sinonimi.
  - Fuori scope dichiarato: non è vietata esplicitamente, ma contraddice decisioni già prese e
    anticiperebbe in modo scorretto il lavoro di v1.1.
- **Risposta data a Fabio**: "Package" resta il meccanismo tecnico generico di caricamento/
  estensione (gratuito, OSS, per sempre) usato da tool, OS e provider LLM. "Skill" è un livello
  semantico più alto introdotto a v1.1 (capability/Evidence/Finding/ExecutionPlan), per lo più
  commerciale, che si caricherà **come** package, non lo sostituisce. Nessuna rinomina dei
  package core è coerente con quanto già deciso.
- **Versione candidata**: N/A — nessuna implementazione prevista.
- **Stato**: Scartata — in conflitto con principio 7, ADR-0006, ADR-0012 e con l'ADR di
  distinzione già pianificata a v1.1. Non riaprire senza informazioni nuove (coerente con come
  `agentic/06-decisions.md` tratta le decisioni prese).

### SPEC-001 — Settings: provider LLM e API key modificabili e persistenti

- **Data**: 2026-09-15
- **Richiesta originale**: "la prima cosa che ho notato è che nel frontend la pagina setting non
  permette di modificare davvero le impostazioni. in particolare dovrei inserire l'api key di
  open router. bisogna anche assicurarsi che il salvataggio della api key sia sicuro e
  persistente. anche la selezione del provider deve essere persistente ovviamente e tutti i suoi
  parametri configurabili."
- **Area toccata**: `bOps.Api` (nuovi endpoint di scrittura), gestione secret/configurazione,
  `web/bops-ui` (Settings)
- **Verifica di coerenza**:
  - Già implementata: **No.** Confermato nel codice: `settings.html` mostra letteralmente
    *"switching the active one is not supported from here yet (ADR-0019)"* e non mostra mai il
    valore della key (solo il booleano `hasApiKey`). Settings è **volutamente** read-only in v0.9
    — non è un bug, è la scope decision di ADR-0019 e del piano (§3 stato di partenza: "Settings
    read-only con provider discovery").
  - Già pianificata: **Non con una versione assegnata.** ADR-0019 la cita esplicitamente come
    estensione futura non schedulata: *"Revisit if a future Settings page needs to let an
    operator switch the active provider at runtime rather than only view it."* I prerequisiti
    espliciti compaiono in `piano-bops-v0.9.1-v2.0.md` §7, v1.0, punti 2 e 3 (autenticazione/
    autorizzazione reale per `bOps.Api`; secret-provider contract).
  - Conflitti con principi/regole:
    - **S6** (`agentic/03-security-rules.md`): oggi una API key arriva solo da environment
      variable o .NET user-secrets; non esiste un meccanismo approvato per scriverla da un form
      UI/API. Serve prima un secret-provider reale, non un campo salvato in un file di config in
      chiaro o in un nuovo store improvvisato.
    - **ADR-0018**: `bOps.Api` non ha ancora autenticazione. Un endpoint che gestisce credenziali
      di provider su un'API raggiungibile senza auth è un rischio concreto — ADR-0019 nasconde già
      il valore della key nella sola lettura esistente proprio per questo motivo. L'auth reale è
      pianificata solo a v1.0 (§7 punto 2).
    - **Scope discipline** (`agentic/05-workflow.md`): "A task that appears to require work from a
      later version is a signal to stop and ask" — esattamente questo caso.
  - Fuori scope dichiarato: **No** (non compare in piano §10). È una feature legittima e già
    anticipata dal progetto stesso, solo prematura rispetto ai suoi gate di sicurezza.
- **Nota tecnica**: la parte "selezione del provider persistente" (senza gestione key) è
  architetturalmente più leggera di quanto sembri — `IChatModelRegistry.Create(ChatModelOptions)`
  già accetta options arbitrarie, quindi costruire un `IChatModel` diverso non richiede modifiche a
  `bOps.Abstractions`. Resta comunque da esporre un endpoint di scrittura su un'API oggi non
  autenticata.
- **Versione candidata**: v1.0 per la parte "API key" (dipende dal secret-provider contract e
  dall'auth reale). La parte "provider persistente" potrebbe in teoria staccarsi prima, ma anche lì
  serve comunque un endpoint di scrittura su `bOps.Api` non ancora protetto.
- **Chiarimenti di Fabio (2026-09-15)**:
  - Confermato: nessun problema a farla slittare "ben dopo v1.0" — non è urgente rispetto al
    secret-provider contract e all'autenticazione reale di `bOps.Api`; nessuna scorciatoia da
    valutare ora.
  - Requisito di visualizzazione: la key va mostrata mascherata, con i primi 6 e gli ultimi 4
    caratteri visibili e tre puntini (`...`) in mezzo — non il solo booleano `hasApiKey` attuale
    (ADR-0019), ma nemmeno il valore in chiaro.
  - Requisito fondamentale (non negoziabile secondo Fabio): la key deve essere gestibile
    (inserimento/modifica) dalla UI, non solo in lettura.
  - Vincolo di compatibilità: la configurazione via `appsettings.json`/user-secrets per chi usa
    solo la CLI (senza UI) deve restare valida — il nuovo meccanismo di gestione da UI si affianca
    al percorso CLI esistente (rule S6), non lo sostituisce. La precedenza/coordinamento tra le due
    fonti è un dettaglio di design da risolvere nell'ADR del secret-provider contract a v1.0, non
    da anticipare ora.
- **Versione candidata**: post-v1.0 (confermato da Fabio, nessuna urgenza). Resta comunque
  dipendente dal secret-provider contract e dall'autenticazione reale di `bOps.Api` previsti in
  `piano-bops-v0.9.1-v2.0.md` §7, v1.0, punti 2 e 3.
- **Stato**: Consolidata (2026-09-16) — inviata all'agent in `consolidamento-2026-09-16.md`, non più pendente.

<!-- Ogni nuova specifica va aggiunta qui sotto con questo template:

### SPEC-001 — <titolo breve>

- **Data**: AAAA-MM-GG
- **Richiesta originale**: <come l'ha detta Fabio>
- **Area toccata**: <componente/package/documentazione/processo>
- **Verifica di coerenza**:
  - Già implementata: <sì/no + riferimento>
  - Già pianificata: <sì/no + dove (versione, ADR, decision register)>
  - Conflitti con principi/regole: <nessuno / elenco regola + motivo>
  - Fuori scope dichiarato: <no / sì, riferimento>
- **Versione candidata**: <v0.1x / da definire in consolidamento — dipendenze: ...>
- **Stato**: Raccolta

-->
