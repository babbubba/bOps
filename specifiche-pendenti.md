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
  `Pronta per consolidamento` (verificata, in attesa del batch) · `Scartata` (in conflitto con
  decisioni/regole già prese, motivo registrato, non si riapre senza informazioni nuove) ·
  `Già coperta dal piano` (verificata: non serve consolidarla, è già prevista così com'è —
  tenuta solo come traccia della verifica fatta)
- **Verifica di coerenza**: già implementata? già prevista nel piano? in conflitto con principi,
  decisioni o ADR esistenti? fuori scope dichiarato?
- **Versione candidata**: ipotesi, da confermare rispettando §5 del piano

---

## Indice

- [SPEC-001 — Settings: provider LLM e API key modificabili e persistenti](#spec-001--settings-provider-llm-e-api-key-modificabili-e-persistenti) — Pronta per consolidamento
- [SPEC-002 — Rinominare i package core in "Skill"](#spec-002--rinominare-i-package-core-in-skill) — Scartata
- [SPEC-003 — Aggiornare il README e valutare una versione bilingue](#spec-003--aggiornare-il-readme-e-valutare-una-versione-bilingue) — Pronta per consolidamento
- [SPEC-004 — Pagina UI dedicata a plugin/Skill installati e attivi](#spec-004--pagina-ui-dedicata-a-pluginskill-installati-e-attivi) — Pronta per consolidamento
- [SPEC-005 — Coordinamento agent remoti/multi-nodo](#spec-005--coordinamento-agent-remotimulti-nodo) — Già coperta dal piano

---

## Voci

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
- **Stato**: Pronta per consolidamento.

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
- **Stato**: Pronta per consolidamento.

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
- **Stato**: Pronta per consolidamento.

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

