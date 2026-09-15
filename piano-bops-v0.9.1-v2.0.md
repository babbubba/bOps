# bOps — Piano evolutivo da v0.9.1 a v2.0

Stato: approvato; v0.9 conclusa, prossima milestone v0.9.1  
Data: 2026-09-15  
Titolare iniziale del copyright: Fabio Cavallari  
Brand di progetto: bSoft (nome commerciale non registrato)

## 1. Autorità e uso di questo piano

Questo documento è il piano operativo per il lavoro successivo a v0.9. Non sostituisce le
regole vincolanti in `CLAUDE.md` e `agentic/`; in caso di conflitto vincono sempre quelle regole,
gli ADR accettati e il decision register.

`piano-bops.md` resta un documento storico e non viene modificato. Le versioni da v0.1 a v0.9
sono concluse e non devono essere reimplementate. Il lavoro futuro parte da v0.9.1 e tratta ogni
eventuale gap storico come una nuova attività versionata, mai come riapertura retroattiva di una
milestone conclusa.

Ogni modifica futura deve preservare gli invarianti esistenti:

- il modello propone, il runtime decide ed esegue;
- nessun tool di esecuzione generica e nessun SQL arbitrario prodotto dal modello;
- policy ed entitlement sono verificati nel punto in cui avviene l'esecuzione;
- ogni azione con effetti è verificata e ogni esito è sottoposto ad audit;
- i package in-process sono software fidato, non una sandbox;
- il core non nomina package, provider o capability concrete;
- i contratti che attraversano boundary sono serializzabili e versionati.

## 2. Decisioni approvate

1. **Open core.** Il repository pubblico `bOps` rimane Apache-2.0. Core, SDK, package generici,
   provider, UI locale e sample Skill sono open source.
2. **Due repository.** Tutto il codice proprietario nasce in un unico monorepo privato
   `bOps.Commercial`: Skills ufficiali, entitlement commerciale, Control Plane e Portal. Una
   futura separazione interna richiederà un'esigenza reale di deployment, ownership o release.
3. **Distribuzione ibrida.** Sul nodo restano executor tipizzati, policy enforcement e verifica;
   nel Control Plane restano entitlement e knowledge/playbook commercialmente sensibili.
4. **CLA.** I contributi esterni al repository OSS richiedono un CLA individuale; quando i diritti
   appartengono a un datore di lavoro serve anche il percorso aziendale applicabile. Il testo
   vincolante deve essere revisionato da un legale software/IP.
5. **Identità legale.** Il titolare indicato nei materiali iniziali è `Fabio Cavallari`. `bSoft`
   è un brand non registrato, non una persona giuridica, e non deve essere indicato come titolare
   del copyright o come marchio registrato.
6. **Versioni.** v0.9 non viene rinumerata né riaperta. Seguiranno v0.9.1, v0.10 e v1.0; il nuovo
   programma commerciale inizia da v1.1. v2.0 è il gate di disponibilità enterprise generale.

## 3. Stato di partenza

| Versione | Stato | Funzionalità da considerare acquisite |
|---|---|---|
| v0.1–v0.4 | Concluse | Runtime, planning/replanning, policy/approval, audit e verification |
| v0.5–v0.6 | Concluse | Parità Windows/Linux, Filesystem, Network, Docker, Aspire e CI multipiattaforma |
| v0.7 | Conclusa | Task persistenti e riprendibili su SQLite |
| v0.8 | Conclusa | Provider Anthropic, OpenAI e DeepSeek |
| v0.9 | **Conclusa** | API, UI Angular, Settings/provider discovery e avvio coordinato API+UI tramite Aspire |
| v0.9.1+ | Non iniziate | Oggetto di questo piano |

Il handoff finale di v0.9 dichiara build .NET senza warning, test .NET non-live superati, build
Angular e 2 test Angular superati, oltre a verifica manuale di API/UI/Settings avviate con Aspire.
v0.9.1 deve ripetere questa baseline prima di introdurre qualsiasi modifica.

## 4. Confine tra i repository

| Area | `bOps` pubblico | `bOps.Commercial` privato |
|---|---|---|
| Runtime, policy, audit, memory | Sì | Solo consumo tramite contratti pubblici |
| Plugin/Skill/Agent SDK | Sì, Apache-2.0 | Nessun fork privato dei contratti pubblici |
| Package generici e provider | Sì | No |
| Sample Skill senza knowledge sensibile | Sì | No |
| Executor DBA ufficiali | No | Sì |
| Prompt, euristiche, playbook e knowledge ufficiali | **Mai** | Sì, accesso minimo e tracciato |
| Entitlement commerciale | Solo interfacce neutre | Provider locale/remoto e regole commerciali |
| Control Plane | Contratti di interoperabilità strettamente necessari | Backend, tenancy, RBAC e coordinamento |
| Portal enterprise | No; resta la UI locale OSS | Sì, come client del Control Plane |

Il Control Plane è il backend autorevole per tenant, identità, ruoli, agenti, target, entitlement,
policy distribuite, approvazioni, audit aggregato e report. Il Portal è soltanto il client web di
queste API: non prende decisioni di sicurezza e non accede direttamente ai nodi amministrati.

## 5. Catena di propedeuticità

```text
chiusura v0.9
  -> v0.9.1 integrità repository e licensing
  -> v0.10 package/plugin SDK dinamico
  -> v0.11 completamento capability operative
  -> v1.0 hardening e contratto pubblico stabile
  -> v1.1 Skill/Capability, Evidence e piano immutabile
  -> v1.2 multi-agent logico con privilegi isolati
  -> v1.3 entitlement e packaging commerciale
  -> v1.4 trasporto sicuro e Control Plane foundation
  -> v1.5 PostgreSQL read-only
  -> v1.6 PostgreSQL remediation governata
  -> v1.7 SQL Server read-only
  -> v1.8 SQL Server remediation governata
  -> v1.9 Portal enterprise
  -> v2.0 hardening e disponibilità generale
```

Non è consentito iniziare una milestone se il suo gate di ingresso non è soddisfatto. Una
capability read-only deve essere completa e validata su target reali prima di introdurre la sua
remediation. Il Control Plane non può diventare il punto di esecuzione: policy, entitlement e
approval materializzata devono essere ricontrollati dal nodo locale.

## 6. Regole di versionamento e rilascio

- La versione dell'applicazione, la versione di `bOps.Abstractions` e le versioni dei package sono
  coordinate ma indipendenti.
- `bOps.Abstractions` resta `0.x` fino al gate v1.0 previsto da D-012. Da `1.0.0`, SemVer è
  vincolante: aggiunte compatibili in minor, correzioni in patch, breaking change solo in major.
- Nessuna versione è conclusa senza tag, release notes, artefatti riproducibili, SBOM, test previsti
  e stato esplicito delle verifiche su target reali.
- Le API tra nodo e Control Plane hanno una versione di protocollo distinta dalla versione del
  prodotto e supportano negoziazione esplicita.
- Il codice privato non viene copiato nel repository OSS per facilitare test o build. I contratti
  condivisi sono progettati e pubblicati dal repository OSS prima del loro consumo privato.

## 7. Backlog per versione

### v0.9 — milestone conclusa

**Status.** Completata il 2026-09-15; nessuna ulteriore attività funzionale è assegnata a questa
versione.

**Goal.** Registrare la consegna completata senza riaprire lo scope della versione.

**Affected projects/files.** File modificati dalla sessione v0.9, `HANDOFF.md`, test API/UI.

**Dependencies.** Gate soddisfatto.

**Implementation notes.** Lo scope consegnato comprende dashboard, approvazioni, dettaglio task
inline, Settings read-only con provider discovery e orchestrazione API/UI tramite Aspire. La route
`task-detail` separata e il client OpenAPI generato sono decisioni esplicitamente non adottate, non
task incompleti. Il cambio runtime del provider resta una possibile feature futura, non un gap di
v0.9.

**Tests.** Build e test .NET seriali; build e test Angular; eventuali smoke test manuali dichiarati
nel handoff.

**Security impact.** Confermare che la UI non sia un percorso privilegiato rispetto a CLI/API.

**License impact.** Inventariare i nuovi file e le dipendenze introdotte da v0.9.

**Definition of Done.** Soddisfatta dal handoff finale e dalla consegna verificata. Gli elementi
residui sono instradati a v0.9.1, v0.10, v0.11 o v1.0.

### v0.9.1 — integrità repository e licensing readiness

**Goal.** Preparare una base pubblicabile senza rifare funzionalità di v0.1–v0.9.

**Affected projects/files.** `.github/workflows/ci.yml`, `Directory.Build.props`, tutti i `.csproj`
OSS, `LICENSE`, nuovi `NOTICE`, `CONTRIBUTING.md`, `SECURITY.md`, `docs/licensing.md`, documentazione
di release e inventario third-party.

**Dependencies.** Chiusura formale di v0.9.

**Implementation notes.**

1. Correggere la CI perché usi il file soluzione realmente presente (`bOps.slnx`) e aggiungere la
   build/test Angular alla pipeline appropriata.
2. Allineare README e documentazione alle capability realmente registrate. Le capability ancora
   desiderate vanno assegnate a v0.11; non devono essere dichiarate come già disponibili.
3. Aggiungere test unitari mirati per SignalStore e componenti Angular introdotti in v0.9, inclusi
   task stream, approval e Settings, senza cambiare lo scope funzionale della UI.
4. Risolvere il debito degli ADR storici richiesti da `agentic/05-workflow.md` come ricostruzione
   documentale fedele delle decisioni già prese, senza modificare retroattivamente il codice.
5. Verificare che `LICENSE` sia il testo Apache-2.0 ufficiale e aggiungere un `NOTICE` essenziale,
   senza inserirvi condizioni ulteriori.
6. Applicare ai sorgenti OSS `Copyright 2026 Fabio Cavallari` e
   `SPDX-License-Identifier: Apache-2.0` con una riscrittura meccanica verificabile.
7. Centralizzare metadati NuGet: licenza, autori, repository URL, descrizione, simboli/source link
   e packaging dei file di licenza/notice.
8. Produrre inventario di licenze dirette e transitive, SBOM e `THIRD-PARTY-NOTICES` dagli artefatti
   effettivamente distribuiti, non soltanto dai manifest di sviluppo.
9. Documentare il processo CLA, ma non inventare il testo contrattuale: il CLA individuale e quello
   aziendale entrano in vigore solo dopo revisione legale.
10. Pubblicare una policy di sicurezza con canale privato di segnalazione e tempi di risposta, senza
   promettere SLA non ancora sostenibili.

**Tests.** CI su Windows/Linux; `dotnet pack` e ispezione dei `.nupkg`; verifica automatica SPDX;
license/SBOM scan; verifica GitHub Licensee; controllo link e packaging documentale.

**Security impact.** Nessuna variazione al runtime; la supply chain diventa osservabile e il flusso
di disclosure viene definito.

**License impact.** Rende effettiva e riconoscibile Apache-2.0 per tutto lo scope pubblico; il CLA
non cambia la licenza outbound del codice OSS.

**Definition of Done.** CI verde sui file corretti, GitHub riconosce Apache-2.0, package OSS con
metadati coerenti, NOTICE e inventario inclusi negli artefatti, processo contributivo documentato.

### v0.10 — package loader e Plugin SDK

**Goal.** Caricare package di terze parti tramite il contratto esistente e pubblicare un SDK OSS
utilizzabile senza accesso al repository principale.

**Affected projects/files.** `bOps.Abstractions`, `bOps.Runtime`, `bOps.Cli`, `bOps.Api`, nuovo
loader, schema `bops-plugin.json`, template/sample, `docs/plugins/`, test runtime/architetturali.

**Dependencies.** v0.9.1.

**Implementation notes.**

1. Scrivere un ADR sul loader dopo aver rivalutato lo stato delle librerie candidate; preferire
   `AssemblyLoadContext` posseduto dal progetto se la dipendenza prevista è archiviata.
2. Definire e versionare il manifest: identità, publisher, versione, compatibilità host/SDK,
   capability dichiarate, configurazione, dipendenze e rischio massimo informativo.
3. Condividere una sola copia di `bOps.Abstractions`; isolare le altre dipendenze e attivare i
   package con il container ristretto previsto dalla regola A10.
4. Lasciare ogni package scoperto disabilitato fino all'abilitazione esplicita. In v0.10
   l'installazione remota non verificata è esclusa: prima si supportano artefatti locali.
5. Implementare `plugin install/list/enable/disable/remove` con operazioni atomiche e recuperabili.
6. Pubblicare `bOps.Abstractions` ancora in `0.x`, template, analyzer/validator del manifest e una
   sample Skill puramente dimostrativa e Apache-2.0.

**Tests.** Round-trip manifest; compatibilità e rifiuto versioni; collisioni ID/tool; dipendenze con
versioni confliggenti; package incompleto; enable/disable; installazione interrotta; test di
composizione CLI/API; test che un non-Read senza verification venga rifiutato.

**Security impact.** Dichiarare esplicitamente che `AssemblyLoadContext` non è isolamento di
sicurezza. Nessun download arbitrario e nessuna auto-abilitazione.

**License impact.** L'SDK è Apache-2.0; ogni package dichiara la propria licenza. Il loader non
presume che un package sia OSS né concede diritti su di esso.

**Definition of Done.** Un package esterno sample viene creato, impacchettato, installato,
abilitato, eseguito, verificato, disabilitato e rimosso senza modificare il core.

### v0.11 — completamento delle capability operative

**Goal.** Portare in avanti le capability valide promesse dalla documentazione storica, usando il
Plugin SDK reale e senza riaprire v0.5 o copiare le implementazioni difettose del vecchio piano.

**Affected projects/files.** Package `System.Core/Windows/Linux`, Filesystem e Network; nuovi
package `Service.Core/Windows/Linux`; eventuali estensioni Process nella famiglia System; manifest
plugin, host composition, README, test di conformità e integrazione su target reali.

**Dependencies.** v0.10. Le capability con effetti dipendono inoltre dalla disponibilità delle
rispettive capability read-only di verifica.

**Implementation notes.**

1. Congelare un inventario “documentato vs registrato” all'inizio della milestone. Il README viene
   aggiornato incrementalmente solo dopo che una capability supera il proprio gate.
2. Prima tranche, esclusivamente read-only: `system.swap`, `system.io`, `process.inspect`,
   `fs.search`, `fs.hash`, `network.port_check`, `network.route`, `service.list` e
   `service.status`.
3. Seconda tranche, dopo i relativi reader/verifier: `fs.move`, operazioni controllate di arresto
   processo e `service.start`, `service.stop`, `service.restart`. Nome e semantica delle operazioni
   di processo devono essere risolti prima dei manifest, evitando alias ambigui tra graceful stop e
   kill forzato.
4. `system.uptime` non viene creato come tool separato: il dato resta parte di `system.info`.
5. `system.environment` come dump generico è escluso perché può trasferire secret al modello. Una
   futura capability potrà esporre soltanto chiavi allowlisted e valori classificati/redatti, dopo
   threat-model specifico.
6. `process.start` generico è escluso: sarebbe un percorso equivalente a un execution tool. Ogni
   avvio necessario deve appartenere a una capability di dominio tipizzata e limitata.
7. Il codice condiviso e il formato output seguono A8: `.Core` contiene manifest, parsing e output;
   Windows/Linux contengono soltanto la raccolta o l'azione specifica di piattaforma.
8. Prima dei package Service scrivere un ADR sulla strategia Windows e Linux. Un eventuale uso di
   `systemctl` è interno a tool tipizzati, usa argomenti strutturati e non accetta comandi composti;
   va confrontato con D-Bus in termini di dipendenze, affidabilità e superficie di test.
9. Usare i nuovi package come dogfooding del loader v0.10. Il core non deve cambiare per conoscere
   i nuovi nomi di tool.
10. Ogni operazione non-Read dichiara verification specifica e implementa `IVerifiableTool` prima
    di poter essere registrata. `fs.move` verifica origine, destinazione e identità del contenuto;
    process/service verificano lo stato osservato dopo l'azione.

**Tests.** Conformance condivisa Windows/Linux; target Windows e Linux reali; filesystem reale con
symlink e collisioni; processi controllati creati dal test; servizi di test; timeout, permission
denied e target scomparso; verifica `Confirmed/Refuted/Inconclusive`; manifest e caricamento
dinamico; test architetturale che nessuna capability introduca shell/exec generico.

**Security impact.** Le capability read-only precedono sempre quelle con effetti. Argomenti da
modello non sono concatenati in comandi o path; process kill e service operations richiedono
policy/approval adeguate, blast radius esplicito e verifica post-azione. Nessun environment dump.

**License impact.** Package e test first-party sono Apache-2.0; nuove dipendenze native/NuGet sono
ammesse solo dopo inventario licenza e aggiornamento degli artefatti third-party/SBOM.

**Definition of Done.** README e registry coincidono; tutte le capability approvate sono disponibili
su ogni piattaforma dichiarata e testate su target reali; ogni azione è tipizzata, autorizzata,
verificata e auditata; le capability escluse non compaiono come disponibili.

### v1.0 — security hardening e contratto pubblico stabile

**Goal.** Rendere il runtime e l'SDK pubblicabili come base affidabile per estensioni commerciali.

**Affected projects/files.** Tutti i core project, host CLI/API, loader, UI locale, CI/release,
`docs/security/`, ADR e test di sicurezza.

**Dependencies.** v0.11.

**Implementation notes.**

1. Threat model completo: LLM, prompt injection, package in-process, supply chain, API, filesystem,
   audit, secrets, approvazioni e futuri boundary remoti.
2. Autenticazione e autorizzazione reali per `bOps.Api`; identità dell'approvatore derivata dal
   canale autenticato, mai da un campo libero del client.
3. Introdurre il secret-provider contract e provider locali sicuri; nessun secret in prompt,
   audit, log, telemetria o report.
4. Firma dei package, provenienza, trust store e policy di publisher. Un artefatto non verificato
   resta disabilitato e soggetto al livello di fiducia minimo.
5. Rate limiting, idempotenza, cancellation, timeout, concurrency, retry selettivi e recovery da
   arresto del processo; rendere persistenti le approvazioni se il threat model lo richiede.
6. Fuzzing di manifest/argomenti/protocolli, audit verifier esposto all'operatore, permessi file
   restrittivi, SBOM e release firmate/riproducibili.
7. Congelare la superficie `bOps.Abstractions` 1.0 solo dopo review di compatibilità e documentazione
   completa.

**Tests.** Test-first sul core; authn/authz e privilege escalation; signature/tampering; fuzz;
restart/recovery; concorrenza; test reali OS/Docker; scansioni dipendenze e pacchetti.

**Security impact.** È il gate che rende accettabile installare package e preparare connessioni a
sistemi commerciali. Nessuna dichiarazione di sandboxing in-process.

**License impact.** SDK 1.0 Apache-2.0; artefatti e documentazione includono NOTICE, SBOM e notice
third-party. Nessun codice commerciale entra nel rilascio.

**Definition of Done.** Threat model accettato, API autenticata, secret e package verificati,
recovery testata, SDK 1.0 documentato e release candidate riproducibile su Windows/Linux.

### v1.1 — Skill/Capability SDK, Evidence e piano immutabile

**Goal.** Aggiungere i contratti generici necessari alle Skills operative senza nominare database
o prodotti nel core.

**Affected projects/files.** `bOps.Abstractions`, `bOps.Runtime`, `bOps.Policy`, `bOps.Audit`,
sample Skill OSS, serialization context, test core e documentazione architetturale.

**Dependencies.** v1.0.

**Implementation notes.**

1. ADR per distinguere package, Skill, capability, tool ed agente. I contratti normativi restano in
   `bOps.Abstractions`; non introdurre un SDK parallelo che violi A7.
2. Una capability dichiara identità/versione, rischio, permessi, precondizioni, schema input/output,
   timeout, dry-run, approval, verification e rollback. Non contiene comandi o SQL liberi.
3. Introdurre Evidence, Finding e classificazioni FACT/INFERENCE/RECOMMENDATION/EXECUTED_ACTION/
   VERIFICATION con riferimenti stabili e redazione al boundary.
4. Separare il piano descrittivo esistente dall'`ExecutionPlan` autorizzabile. Il piano eseguibile
   è canonico, versionato e hashato; una modifica materiale invalida l'approvazione.
5. Estendere la policy in modo generico per identità, Skill, capability, target, ambiente, rischio,
   blast radius e maintenance window, mantenendo Critical non autorizzabile.
6. Generare report strutturati da evidence e finding, senza permettere al modello di inventare prove.

**Tests.** Test-first; round-trip di ogni contratto; canonicalizzazione/hash; invalidazione approval;
finding senza evidence rifiutato; redazione; policy contestuale; sample Skill end-to-end.

**Security impact.** Crea il boundary deterministico che impedisce `LLM -> arbitrary SQL/action`.

**License impact.** Contratti, sample e documentazione sono Apache-2.0; nessun playbook ufficiale
commerciale viene pubblicato.

**Definition of Done.** Una sample Skill OSS seleziona capability, produce evidence/finding/piano,
passa policy/approval, esegue un tool tipizzato e verifica il risultato con audit completo.

### v1.2 — multi-agent logico nello stesso processo

**Goal.** Introdurre orchestrazione tra agenti specializzati senza microservizi e senza eredità
implicita dei privilegi.

**Affected projects/files.** Contratti agent in `bOps.Abstractions`, orchestrator in
`bOps.Runtime`, policy/audit, persistence, test deterministici e `docs/architecture/multi-agent.md`.

**Dependencies.** v1.1.

**Implementation notes.**

1. ADR su identità, delega, budget, timeout e responsabilità dell'orchestrator.
2. Ogni agente dichiara ruolo, capability/target/rischio consentiti e budget. Il contesto delegato
   è una riduzione esplicita dei privilegi del chiamante.
3. Prima implementazione: Discovery, Diagnostic, Remediation e Verification agent logici. Diagnostic
   e Verification sono normalmente read-only; Verification è indipendente dal Remediation agent.
4. Persistenza e audit rappresentano parent/child, motivazione della delega, budget e risultati.
5. Esecuzione sequenziale predefinita; il parallelismo richiede una successiva policy esplicita su
   conflitti, blast radius e cancellazione.

**Tests.** Fake model/agent; tentativi di privilege escalation; budget e timeout; cicli/deadlock;
cancellazione; evidence provenance; verification indipendente; resume di task delegati.

**Security impact.** Un sub-agent non può aumentare privilegi, target o rischio e non può approvare
le proprie azioni.

**License impact.** Agent contracts/runtime generico Apache-2.0; ruoli e playbook specialistici
ufficiali restano privati.

**Definition of Done.** Un obiettivo viene delegato, diagnosticato, pianificato, autorizzato,
eseguito e verificato da ruoli distinti con correlazione audit completa e privilege isolation.

### v1.3 — entitlement e packaging commerciale

**Goal.** Rendere installabili Skills proprietarie senza incorporare pagamenti o regole commerciali
nel core OSS.

**Affected projects/files.** Interfacce neutrali in `bOps.Abstractions`; enforcement generico in
runtime/audit; nuovo repository privato `bOps.Commercial` con packaging ed entitlement locale.

**Dependencies.** v1.2; repository privato disponibile; bozza di licenza commerciale revisionata.

**Implementation notes.**

1. ADR per entitlement fail-closed al punto di esecuzione: tenant/subject, Skill/versione,
   capability/feature, target, tier, validità e reason code.
2. Il core conosce soltanto `ISkillEntitlementService` e decisioni serializzabili. Nessun provider
   di pagamento, prezzo o nome di prodotto commerciale nel core.
3. Nel repository privato creare package firmati, manifest commerciali e un provider locale per
   licenze firmate con scadenza/grace esplicite; clock rollback e manomissioni sono errori chiusi.
4. Auditare valutazione, fonte e decisione senza registrare token/licenza completa.
5. Aggiungere un controllo CI che impedisca la pubblicazione accidentale nel repository OSS di
   `LicenseRef-bOps-Commercial`, prompt, playbook, fixture o package privati.

**Tests.** Licenza valida/scaduta/non ancora valida/manomessa; Skill/versione/target errati; cache
offline; revoca; clock skew; enforcement dopo approval; assenza provider; redazione audit.

**Security impact.** L'entitlement è un controllo di accesso commerciale, non una protezione
assoluta contro copia o decompilazione. Policy e verification restano indipendenti.

**License impact.** L'interfaccia è Apache-2.0. Provider e Skills private usano
`LicenseRef-bOps-Commercial` insieme a un vero testo contrattuale revisionato; l'identificatore SPDX
da solo non concede diritti.

**Definition of Done.** Una Skill privata firmata viene caricata ma può descriversi/eseguirsi solo
con entitlement valido; denial, expiry e tampering sono fail-closed e auditati.

### v1.4 — trasporto sicuro e Control Plane foundation

**Goal.** Abilitare il modello ibrido mantenendo esecuzione, policy e verifica sul nodo amministrato.

**Affected projects/files.** Contratti di protocollo OSS strettamente necessari; agent connector
locale; nel repository privato Control Plane, storage, identity, RBAC, entitlement remoto e
knowledge service.

**Dependencies.** v1.3; threat model remoto; gestione certificati/segreti pronta.

**Implementation notes.**

1. ADR che supera soltanto per il lavoro post-v1.0 la topologia local-only di D-001: connessione
   outbound dal nodo, autenticazione mutua, registrazione esplicita, rotazione credenziali,
   protocol negotiation e revoca.
2. Il Control Plane non invia comandi arbitrari: invia objective/piani/capability tipizzate. Il
   nodo ricontrolla firma, freshness, entitlement, policy, approval hash e target.
3. Implementare tenant, user, role, agent, target, Skill, entitlement, policy, task, approval,
   execution, audit event e report con isolamento tenant by construction.
4. RBAC minimo: Administrator, Operator, Approver, Viewer, sostenuto da permission granulari.
5. Il knowledge service restituisce regole/versioni firmate o risultati strutturati; non invia
   secret né testo capace di cambiare policy/runtime state.
6. Audit scritto prima localmente e poi spedito con idempotenza, ordering, retry e riconciliazione.

**Tests.** mTLS/identity, tenant isolation, replay/downgrade, revoca, nodo offline, ordering audit,
retry/idempotenza, policy locale più restrittiva, approval alterata, compatibility matrix protocollo.

**Security impact.** Nuovo trust boundary di rete; nessun inbound amministrativo obbligatorio e
nessuna fiducia implicita nel controller.

**License impact.** Solo i contratti interoperabili indispensabili sono Apache-2.0; Control Plane,
knowledge ed entitlement remoto restano proprietari.

**Definition of Done.** Un nodo registrato riceve un objective tipizzato, rifiuta istruzioni non
valide, applica localmente i gate, esegue/verifica e sincronizza audit in modo tenant-safe.

### v1.5 — PostgreSQL DBA read-only

**Goal.** Consegnare la prima Skill commerciale capace di spiegare con evidence perché un ambiente
PostgreSQL è degradato, senza remediation.

**Affected projects/files.** Solo `bOps.Commercial`: package PostgreSQL, executor/adapters per
versione, query parametrizzate, knowledge remoto, report e integration test infrastructure.

**Dependencies.** v1.4; versioni PostgreSQL supportate dichiarate; ambienti di test reali; modello
di permessi e secret provider disponibili.

**Implementation notes.**

1. Nessun SQL dal modello. Ogni capability usa query allowlisted, parametrizzate, version-aware e
   testate; niente `SELECT *` sui cataloghi.
2. Coprire discovery/health, performance e query, lock/transaction, vacuum/statistics, index,
   capacity, backup/PITR, WAL/replication, configuration e security.
3. Rilevare versione, estensioni e permessi prima della raccolta. Un dato non accessibile diventa
   `UNKNOWN_DUE_TO_PERMISSION`, mai `OK`.
4. `pg_stat_statements` è opzionale e non viene abilitato automaticamente. `EXPLAIN ANALYZE` è una
   capability distinta perché esegue la query e richiede policy adeguata anche se produce output
   diagnostico.
5. Backup e restore usano stati distinti: configured, exists, fresh, consistent, WAL chain,
   theoretically restorable, restore verified e restore tested.
6. Il report contiene executive summary, finding/evidence, root cause, raccomandazioni ordinate,
   rischio e azioni che richiederebbero approval.

**Tests.** PostgreSQL reale per ogni versione supportata: healthy, restricted user, blocking, long
transaction, dead tuples, index scenario, slow query, estensione presente/assente, backup e
replication metadata; test di compatibilità delle query.

**Security impact.** Account least-privilege, secret fuori da prompt/audit, statement/lock timeout,
limiti di cardinalità/output e redazione di SQL/PII secondo policy.

**License impact.** Package, query, knowledge e report template ufficiali sono proprietari; driver
e dipendenze mantengono le rispettive licenze e notice.

**Definition of Done.** La richiesta “analizza PostgreSQL PROD e dimmi perché è lento” produce
discovery, evidence, diagnosi, root cause, raccomandazioni prioritarie, rischio e report, senza
alcuna modifica al database.

### v1.6 — PostgreSQL remediation governata

**Goal.** Aggiungere azioni PostgreSQL controllate soltanto dopo la completezza read-only.

**Affected projects/files.** `bOps.Commercial` PostgreSQL executor, policy pack, verification,
rollback, approval UX/API e test di failure injection.

**Dependencies.** v1.5; immutable plan/approval v1.1; entitlement/control plane disponibili.

**Implementation notes.** Aggiungere incrementalmente cancel/terminate session, VACUUM, ANALYZE,
REINDEX, create/drop index e configuration change come capability distinte. Ogni azione dichiara
precondizioni, lock/blast radius, timeout, maintenance window, expected effects, rollback possibile
e post-metriche. `VACUUM FULL`, drop index, terminate e cambi restart-required richiedono policy e
approval; DROP DATABASE/TABLE, TRUNCATE e DELETE non limitato non sono capability autonome.

**Tests.** Target reali isolati; approval hash invalidation; lock timeout; partial completion;
cancellation; rollback; verification refuted/inconclusive; regression post-metrics; safety test che
le operazioni vietate non possano essere espresse o bypassare policy.

**Security impact.** Nessuna remediation autonoma Critical; verifica indipendente e audit completo.

**License impact.** Tutta la logica di remediation e i playbook restano privati.

**Definition of Done.** Ogni capability modifica un target di test reale soltanto con piano ed
entitlement validi, produce verifica specifica/post-metriche e gestisce fallimento parziale senza
dichiarare un falso successo.

### v1.7 — SQL Server DBA read-only

**Goal.** Portare lo stesso standard di evidence e diagnosi a SQL Server senza forzare
un'astrazione comune con PostgreSQL.

**Affected projects/files.** Solo `bOps.Commercial`: package SQL Server, adapter/version matrix,
query parametrizzate, knowledge, report e target di integrazione.

**Dependencies.** v1.6 come validazione completa del modello Skill; SQL Server support matrix e
licenze degli ambienti di test definite.

**Implementation notes.** Coprire instance/database discovery, performance, Query Store, waits,
blocking/deadlock, I/O/memory, TempDB, index/statistics, capacity, backup/restore, integrity,
security e configuration. Rilevare versione/edition e permission granulari senza richiedere
`sysadmin`. Usare delta e workload context; nessuna regola cieca di rebuild per percentuale.
Distinguere `RESTORE VERIFYONLY`, restore isolato e restore testato con CHECKDB.

**Tests.** SQL Server reale per ogni versione/edition supportata: healthy, restricted user,
blocking, Query Store, waits, stale statistics, index scenario, backup history, TempDB; permission
matrix e query compatibility.

**Security impact.** Query read-only allowlisted, least privilege, timeout, output limit e
redazione; assenza di check recente è `INTEGRITY_NOT_VERIFIED`, non corruzione.

**License impact.** Package, knowledge e report template sono proprietari; dipendenze e immagini di
test sono usate secondo le rispettive licenze.

**Definition of Done.** Un'analisi SQL Server produce evidence correlata, root cause e report
azionabile su performance, availability, backup, integrity e security senza modifiche al target.

### v1.8 — SQL Server remediation governata

**Goal.** Aggiungere remediation SQL Server con lo stesso livello di controllo di PostgreSQL.

**Affected projects/files.** `bOps.Commercial` SQL Server executor, policy pack, verification,
rollback, approval e test.

**Dependencies.** v1.7.

**Implementation notes.** Introdurre separatamente cancel/kill, statistics update, reorganize/
rebuild, force/unforce plan, integrity check, backup e configuration change. Considerare edition,
HA, log growth, workload e maintenance window. KILL in PROD, force plan, drop index, rebuild ampio,
configurazione server/security e restore richiedono approval/policy; DBCC repair e restore sopra
PROD non sono autonomi.

**Tests.** Target reali, failure injection, plan force/unforce, verification e rollback, impatto su
log/HA, approval immutabile e test negativi delle hard rule.

**Security impact.** Nessuna scorciatoia sysadmin, query dinamica libera o operazione Critical
autorizzata dal modello.

**License impact.** Logica e playbook restano privati.

**Definition of Done.** Ogni remediation supportata attraversa evidence, piano, policy,
entitlement, approval, execution, independent verification, post-metrics e audit.

### v1.9 — Portal enterprise

**Goal.** Fornire l'interfaccia multi-tenant del Control Plane senza duplicarne la logica.

**Affected projects/files.** `bOps.Commercial` Portal, API/BFF del Control Plane, design system,
test E2E/accessibilità/sicurezza. La UI locale OSS rimane separata.

**Dependencies.** v1.4 Control Plane stabile; v1.5–v1.8 API e report delle Skills stabili.

**Implementation notes.** Dashboard, Agents, Tasks, Targets, Skills, Policies, Approvals, Audit,
Reports, Licenses e Settings. Autorizzazione sempre server-side; tenant e target visibili nel
contesto; approval mostra piano/hash, effetti, rollback e verification. Il Portal non conserva
secret o token di licenza in storage browser persistente.

**Tests.** E2E per ogni ruolo, tenant isolation, session expiry/CSRF/XSS, approval stale,
accessibilità, responsive layout, grandi audit stream, disconnessione e retry idempotente.

**Security impact.** Il frontend non è un security boundary e non può ampliare permessi restituiti
dal backend.

**License impact.** Portal e asset proprietari usano la licenza commerciale; eventuali componenti
OSS mantengono attribuzioni e notice.

**Definition of Done.** I quattro ruoli completano i workflow consentiti e vengono bloccati negli
altri; nessuna operazione sensibile dipende da un controllo solo client-side.

### v2.0 — enterprise GA

**Goal.** Portare l'intero sistema ibrido a un rilascio commerciale supportabile.

**Affected projects/files.** Entrambi i repository, pipeline release, migrazioni, deployment,
runbook, observability, backup/restore, licensing e support documentation.

**Dependencies.** Tutte le milestone precedenti e revisione legale/commerciale completata.

**Implementation notes.** Threat-model delta e penetration test, capacity/concurrency, retention e
data residency, disaster recovery, migrazioni backward/forward, canary/rollback, SLO misurabili,
telemetria privacy-aware, compatibility matrix, upgrade path, revoca entitlement, support bundle
redatto, release signing e provenance. Pricing e billing restano fuori dal core.

**Tests.** Endurance/soak, chaos/recovery, restore drill, upgrade/downgrade supportato, tenant
isolation, revocation offline/online, performance budget, security test indipendente e pilot su
ambienti rappresentativi autorizzati.

**Security impact.** Nessun livello “Autonomous” aggira policy; l'autonomia resta una modalità
vincolata da capability, target, rischio, blast radius e maintenance window.

**License impact.** Testi Apache, commerciali, CLA, trademark policy e third-party notices sono
revisionati da legale; gli artefatti contengono esclusivamente i termini pertinenti al proprio
scope.

**Definition of Done.** Release firmata e riproducibile, recovery provata, security review chiusa,
licenze approvate, documentazione operativa completa e criteri di supporto pubblicati.

## 8. ADR richiesti prima del codice

I numeri vanno assegnati al momento della creazione per evitare collisioni con il lavoro v0.9.

| Decisione | Versione minima |
|---|---|
| Open-core, due repository e confine pubblico/privato | v0.9.1 |
| Loader, manifest, activation boundary e package trust | v0.10 |
| Freeze della superficie `bOps.Abstractions` 1.0 | v1.0 |
| Skill/capability/evidence/execution plan immutabile | v1.1 |
| Multi-agent e delega con riduzione dei privilegi | v1.2 |
| Entitlement locale e remoto al punto di esecuzione | v1.3 |
| Trasporto nodo–Control Plane e modello multi-tenant | v1.4 |

Un ADR accettato non viene riscritto: ogni cambiamento successivo lo supera con un nuovo ADR.

## 9. Regole per contributori e proprietà intellettuale

- Nessun contributo esterno viene unito prima che il processo CLA sia operativo e verificabile.
- Il CLA deve essere trasparente sulla facoltà di usare i contributi anche in offerte commerciali;
  non deve presentarsi come cessione del copyright se non lo è.
- I contributi uniti nel repository pubblico restano disponibili a tutti sotto Apache-2.0,
  indipendentemente dal loro eventuale riuso nei prodotti commerciali.
- Un CLA OSS non sostituisce NDA, accordo di collaborazione, employee/contractor IP assignment o
  least privilege per chi accede al repository privato.
- Prompt, knowledge, benchmark proprietari, dati cliente e fixture reali non attraversano mai il
  repository pubblico o issue tracker pubblici.
- Prima dell'uso pubblico di `bOps`/`bSoft`: clearance del nome, domini/handle e valutazione della
  registrazione del marchio. Non usare il simbolo ® senza registrazione.

## 10. Fuori scope o esplicitamente vietato

- Vietare l'uso commerciale del core Apache-2.0: sarebbe incompatibile con la decisione open-core.
- Considerare entitlement, offuscamento o firma come protezione assoluta dalla copia.
- Memorizzare pricing, Stripe/PayPal o SKU commerciali nel core.
- Pubblicare nel repository OSS il testo della licenza commerciale prima della revisione legale.
- Microservizi per gli agenti logici prima che misure reali ne dimostrino il bisogno.
- Accesso SQL/shell generico, anche dietro approval o rischio Critical.
- `process.start` generico o dump non filtrato di tutte le variabili d'ambiente.
- Un tool `system.uptime` separato mentre `system.info` fornisce già lo stesso dato.
- Dichiarare backup, integrità o remediation riusciti senza evidence e verification specifiche.
- Iniziare SQL Server prima che il modello completo PostgreSQL abbia superato i gate reali.

## 11. Checklist di avvio della prossima sessione

1. Leggere `CLAUDE.md` e tutti i file richiesti in `agentic/`.
2. Leggere il handoff finale v0.9 e confrontarlo con questo piano.
3. Identificare senza ambiguità ogni modifica locale non appartenente alla nuova milestone; non
   includere configurazioni o credenziali locali nei commit.
4. Eseguire build/test baseline seriali senza target concorrenti.
5. Iniziare esclusivamente v0.9.1, con diff documentali, CI e test separati dalle feature.
6. Fermarsi al primo gate non soddisfatto; non anticipare v0.10, v0.11 o versioni successive.
