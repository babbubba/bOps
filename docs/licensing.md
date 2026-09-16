# Licenze e proprietà intellettuale di bOps

Stato: policy di progetto; non sostituisce una consulenza legale  
Data: 2026-09-15  
Titolare iniziale del copyright: Fabio Cavallari  
Brand usato dal progetto: bSoft (nome commerciale non registrato)

## In breve

bOps adotta un modello **open core**:

- il repository pubblico `bOps` è distribuito con licenza Apache License 2.0;
- il futuro repository privato `bOps.Commercial` conterrà le Skills ufficiali, il Control Plane,
  il Portal, l'entitlement commerciale e il knowledge proprietario;
- i package di terze parti possono avere una licenza propria, open source o commerciale;
- il codice Apache-2.0 può essere usato anche da aziende e anche per guadagno;
- la licenza commerciale proteggerà i componenti proprietari, non trasformerà il core pubblico in
  software non commerciale.

Il testo legalmente vincolante per il repository pubblico è il file [`LICENSE`](../LICENSE), non
questa spiegazione. Il testo della futura licenza commerciale deve essere redatto o revisionato da
un legale software/IP prima di qualsiasi distribuzione.

## 1. Che cosa copre Apache-2.0

Salvo un'indicazione esplicita diversa, il codice e la documentazione originali presenti nel
repository pubblico `bOps` sono rilasciati sotto Apache-2.0. Lo scope previsto comprende:

- `bOps.Abstractions`, Runtime, Policy, Memory e Audit;
- host CLI e API;
- UI locale OSS;
- package System, Filesystem, Network e Docker;
- provider LLM first-party;
- Plugin/Skill/Agent SDK generici;
- sample, template e documentazione pubblica privi di knowledge proprietario.

Le dipendenze esterne non diventano Apache-2.0 perché sono usate da bOps: ciascuna conserva la
propria licenza. Lo stesso vale per codice, immagini o dati importati da terzi e identificati
separatamente.

La Apache License 2.0 concede, nel rispetto delle sue condizioni, una licenza mondiale,
perpetua, non esclusiva e gratuita per usare, riprodurre, modificare, distribuire e sublicenziare
il software, anche in forma binaria. Include inoltre una concessione esplicita di brevetto nei
limiti definiti dalla licenza. La [licenza ufficiale](https://www.apache.org/licenses/LICENSE-2.0.html)
e la [FAQ della Apache Software Foundation](https://www.apache.org/foundation/license-faq.html)
confermano che non viene fatta distinzione tra uso personale, interno o commerciale.

Di conseguenza sono consentiti, tra gli altri:

- uso privato o aziendale senza acquistare una licenza commerciale bOps;
- modifica e creazione di fork;
- distribuzione di sorgenti o binari;
- inclusione in un prodotto proprietario;
- vendita di servizi o prodotti che usano il core OSS;
- sviluppo di package closed-source sopra il contratto pubblico.

Questi utilizzi non sono “furto” o uso non autorizzato quando rispettano Apache-2.0. Il vantaggio
commerciale di bOps deve quindi risiedere in Skills ufficiali, knowledge/playbook, Control Plane,
Portal, entitlement, governance, marchio, supporto e know-how che non vengono pubblicati sotto
Apache-2.0.

## 2. Obblighi principali di chi redistribuisce bOps

La sezione 4 di Apache-2.0 contiene le condizioni complete. In termini operativi, chi distribuisce
bOps o un'opera derivata deve almeno:

1. fornire ai destinatari una copia della licenza Apache-2.0;
2. indicare in modo evidente i file modificati;
3. conservare gli avvisi pertinenti di copyright, brevetto, marchio e attribuzione presenti nel
   sorgente;
4. se la distribuzione originale contiene un file `NOTICE`, conservarne gli avvisi pertinenti in
   una delle forme ammesse dalla licenza;
5. rispettare le licenze e gli obblighi delle dipendenze di terze parti.

Apache-2.0 non impone di pubblicare le modifiche, non impone di contribuire il codice a monte e non
richiede che un prodotto più ampio sia interamente open source. Consente di applicare condizioni
ulteriori alle proprie modifiche o all'opera più ampia, purché gli obblighi Apache relativi al
codice ricevuto continuino a essere rispettati.

La licenza fornisce il software “AS IS”, senza garanzie o responsabilità ulteriori rispetto a
quanto stabilito dal testo applicabile.

## 3. Cosa Apache-2.0 non concede

Apache-2.0 non concede diritti sui nomi commerciali, marchi, service mark o nomi di prodotto,
tranne l'uso descrittivo necessario a indicare l'origine del software e riprodurre gli avvisi.

`bOps` e `bSoft` non risultano attualmente registrati come marchi sulla base della dichiarazione
del titolare. Pertanto:

- non deve essere usato il simbolo ®;
- questa documentazione non afferma l'esistenza di un marchio registrato;
- prima del lancio commerciale devono essere eseguite clearance e valutazione di registrazione nei
  territori pertinenti;
- una futura trademark policy dovrà distinguere il diritto di usare il codice dal diritto di
  presentare un prodotto modificato come prodotto ufficiale bOps/bSoft.

Il titolare indicato nei notice iniziali è **Fabio Cavallari**. `bSoft` è indicato come brand e non
come persona o entità giuridica titolare dei diritti.

## 4. Repository e licenza commerciale

La separazione approvata è:

```text
bOps                 pubblico  Apache-2.0
bOps.Commercial      privato   licenza commerciale proprietaria
  ├─ Skills ufficiali
  ├─ Entitlement
  ├─ Control Plane
  └─ Portal
```

Control Plane e Portal sono componenti diversi ma possono stare nello stesso monorepo privato:

- il **Control Plane** è il backend autorevole per tenant, utenti, ruoli, agenti, target, policy,
  entitlement, approvazioni, audit aggregato e report;
- il **Portal** è il frontend che usa le API del Control Plane e non prende autonomamente decisioni
  di sicurezza.

Nel repository commerciale ogni file originale proprietario dovrà riportare un identificatore
come `SPDX-License-Identifier: LicenseRef-bOps-Commercial`, insieme al copyright corretto. Questo
identificatore classifica la licenza per gli strumenti SPDX, ma **non è un testo di licenza e non
concede da solo alcun diritto**. La distribuzione dovrà includere un vero contratto commerciale
revisionato legalmente.

Il repository privato può dipendere dai package Apache-2.0 pubblicati da `bOps`. Non deve copiarne
o forkarne internamente i contratti: una modifica necessaria al boundary viene prima progettata e
pubblicata nel repository OSS in forma generica.

## 5. Come funzionerà il modello ibrido

Il modello approvato divide responsabilità e proprietà intellettuale:

- sul nodo del cliente: runtime OSS, executor tipizzati firmati, enforcement locale di policy ed
  entitlement, esecuzione e verification;
- nel Control Plane privato: servizio entitlement, tenant/RBAC, coordinamento, knowledge e playbook
  commercialmente sensibili;
- nel Portal privato: esperienza utente enterprise sopra le API del Control Plane.

Il Control Plane non deve inviare shell o SQL arbitrario al nodo. Può inviare solo objective,
piani e capability tipizzate che il nodo ricontrolla localmente. Una decisione remota non sostituisce
policy, approval, entitlement o verification locali.

La protezione ha livelli distinti:

| Livello | Cosa protegge | Limite |
|---|---|---|
| Licenza commerciale | Diritto contrattuale di usare e distribuire i componenti privati | Richiede termini validi e possibilità concreta di enforcement |
| Repository privato e least privilege | Sorgenti, playbook e knowledge | Non protegge ciò che viene consegnato al cliente |
| Knowledge server-side | Know-how che non lascia il Control Plane | Richiede disponibilità del servizio e gestione della modalità offline |
| Firma dei package | Provenienza e integrità dell'artefatto | Non impedisce di leggere o copiare il binario |
| Entitlement | Abilita tenant, Skill, versione, feature, target e periodo autorizzati | Non è DRM inviolabile e non sostituisce il contratto |
| Offuscamento eventuale | Aumenta il costo di analisi di un assembly .NET | Non garantisce segretezza e non è un security boundary |

Nessun controllo tecnico deve essere descritto come protezione assoluta da copia, decompilazione o
uso illecito.

## 6. Entitlement non significa licenza

La licenza commerciale è l'accordo giuridico che stabilisce i diritti del cliente. L'entitlement
è il meccanismo tecnico con cui il prodotto applica una parte di quei diritti.

Il modello previsto può valutare:

- tenant e soggetto;
- Skill e versione;
- capability o feature;
- target/server autorizzato;
- tier;
- periodo di validità e modalità offline concordata.

Il core OSS conoscerà soltanto un contratto neutro di valutazione. Prezzi, SKU, provider di
pagamento e logica commerciale non entreranno nel core. Una mancata risposta o una licenza
commerciale non valida deve fallire in modo chiuso per le capability commerciali, ma non deve
disabilitare le normali funzionalità Apache-2.0.

## 7. Package di terze parti

Un package esterno può essere Apache-2.0, usare un'altra licenza compatibile con la sua modalità di
distribuzione oppure essere proprietario. Deve dichiarare nel manifest la propria licenza e
includerne i testi necessari.

L'installazione di un package non trasferisce a bOps la sua proprietà intellettuale e la licenza
Apache di bOps non concede automaticamente diritti su quel package. Allo stesso modo, un package
proprietario non può rimuovere o restringere i diritti Apache sul runtime OSS già ricevuto.

Dal punto di vista della sicurezza, un package .NET caricato in-process ha i privilegi del processo
host. Trust level, firma ed entitlement non costituiscono sandboxing. Installare un package equivale
a installare software con i privilegi del servizio bOps.

## 8. Dipendenze e notice di terze parti

NuGet, npm, immagini container, driver database e altri componenti conservano le rispettive
licenze. La root `LICENSE` descrive bOps, non sostituisce le licenze delle dipendenze.

Prima di ogni rilascio devono essere generati dagli artefatti effettivi:

- inventario diretto e transitivo;
- SBOM machine-readable;
- elenco delle licenze e delle attribuzioni richieste;
- `THIRD-PARTY-NOTICES` pertinente all'artefatto distribuito;
- verifica di incompatibilità, copyleft inatteso o dipendenza senza licenza identificabile.

Gli SBOM generati non vengono versionati nel repository: descrivono un restore e un insieme di
artefatti precisi e diventerebbero obsoleti senza che il diff sorgente lo renda evidente. Script,
configurazione e `THIRD-PARTY-NOTICES` restano invece versionati. Le directory `sbom/` e
`artifacts/` sono ignorate da Git.

La CI genera su Linux due documenti CycloneDX tramite `./scripts/Generate-Sbom.ps1`: un inventario
della soluzione .NET e l'inventario runtime della UI ricavato dall'albero delle dipendenze
installato da `npm ci`. Per la UI lo script genera prima l'inventario npm completo e conserva poi
soltanto i componenti installati che il lockfile non classifica come `dev`: questa verifica evita
la perdita di dipendenze runtime Angular che npm 10 tratta anche come peer dependency quando viene
usato direttamente `npm sbom --omit dev`. La CI pubblica i due documenti come artifact della
workflow con conservazione di 30 giorni. Questi documenti dimostrano lo stato del build del
repository, ma non sostituiscono gli SBOM di release.

Quando esisterà una workflow di rilascio, essa dovrà generare uno SBOM distinto da ogni artefatto
finale distribuito — package NuGet, CLI/API pubblicate e bundle UI — dopo il restore e il packaging,
e allegarlo alla release insieme al suo digest. Gli SBOM di release devono avere la stessa durata
degli artefatti cui si riferiscono e non la retention temporanea delle normali workflow CI.

Lo snapshot human-readable v0.9.1 si rigenera dopo restore .NET e `npm ci` con:

```powershell
./scripts/Generate-Sbom.ps1
./scripts/Generate-ThirdPartyNotices.ps1
```

Il secondo comando usa `artifacts/sbom/bops-dotnet-solution.cdx.json` e
`web/bops-ui/package-lock.json`, fallendo se trova una licenza non risolta. Lo snapshot distingue
inoltre le dipendenze npm runtime da quelle di sviluppo.

La verifica v0.9.1 ha identificato `Json.More.Net`, `JsonPatch.Net` e `JsonPointer.Net` nel solo
grafo dell'AppHost Aspire di sviluppo. I relativi binari NuGet includono un Open Source
Maintenance Fee Agreement oltre alla licenza MIT dei sorgenti. Non devono essere descritti o
ridistribuiti come semplici binari MIT senza verificare l'accordo incluso e lo scenario d'uso
commerciale; il dettaglio e le versioni esatte sono registrati in
[`THIRD-PARTY-NOTICES`](../THIRD-PARTY-NOTICES).

La rilevazione preliminare delle dipendenze dirette correnti mostra principalmente MIT,
Apache-2.0 e 0BSD. Non è una verifica legale completa e non sostituisce l'inventario transitivo o
l'ispezione dei binari realmente distribuiti.

## 9. Contributi: CLA e licenza outbound

Il progetto ha scelto un Contributor License Agreement anziché il solo Developer Certificate of
Origin.

Il CLA previsto deve:

- confermare che il contributore ha il diritto di inviare il contributo;
- concedere in modo non esclusivo i diritti copyright e brevetto necessari;
- lasciare al contributore la proprietà del proprio lavoro, salvo un accordo diverso esplicito;
- consentire in modo trasparente l'uso e la sublicenza dei contributi nelle offerte bOps, comprese
  quelle commerciali;
- prevedere un percorso aziendale quando i diritti appartengono al datore di lavoro;
- essere firmato e registrato prima del merge.

Il CLA disciplina il rapporto tra contributore e maintainer. La licenza **outbound** del codice
unito nel repository pubblico rimane Apache-2.0: tutti, non soltanto Fabio Cavallari o bSoft,
continuano a poterlo usare commercialmente.

La Apache Software Foundation spiega che i CLA servono a chiarire i termini con cui la proprietà
intellettuale viene contribuita e distingue accordi individuali e aziendali. Il suo materiale è un
riferimento, non un modulo da copiare senza adattamento e revisione legale:
[ASF Contributor Agreements](https://www.apache.org/licenses/contributor-agreements.html) e
[ASF CLA FAQ](https://www.apache.org/licenses/cla-faq.html).

Chi contribuisce al repository privato necessita inoltre degli accordi appropriati per
confidenzialità, accesso e proprietà intellettuale. Il CLA del progetto OSS non sostituisce NDA,
contratto di lavoro o accordo con un collaboratore commerciale.

## 10. Scenari comuni

| Scenario | Consentito? | Condizione principale |
|---|---|---|
| Un'azienda usa internamente il core bOps | Sì | Rispetto di Apache-2.0; nessun entitlement commerciale per il solo core |
| Un'azienda vende un servizio basato sul core OSS | Sì | Apache-2.0 consente uso commerciale |
| Qualcuno modifica e redistribuisce bOps | Sì | Licenza inclusa, modifiche indicate, notice conservati |
| Un vendor crea un package closed-source | Sì | Licenza propria del package e rispetto degli obblighi sul codice OSS usato |
| Un cliente usa una Skill ufficiale commerciale | Solo con licenza | Contratto commerciale ed entitlement applicabile |
| Un fork usa nome/logo come se fosse ufficiale | Non automaticamente | Apache-2.0 non concede diritti di marchio |
| Un contributore invia codice al repository pubblico | Sì, dopo accettazione | CLA firmato e contributo pubblicato sotto Apache-2.0 |
| Un collaboratore accede a playbook privati | Solo se autorizzato | Accordi IP/confidenzialità e least privilege |

## 11. File e metadati attesi

Il [piano consolidato](../agentic/_plans/2026-09-16-consolidated-roadmap.md) mantiene questa
separazione e aggiunge un repository privato di coordinamento:

```text
bOps pubblico
├─ LICENSE                    testo integrale Apache-2.0
├─ NOTICE                     attribuzioni pertinenti, non nuove restrizioni
├─ CONTRIBUTING.md            processo CLA e contributi
├─ SECURITY.md                disclosure privata e versioni supportate
├─ THIRD-PARTY-NOTICES        notice dell'artefatto distribuito
├─ SBOM                       formato machine-readable per release
└─ sorgenti                   copyright + SPDX-License-Identifier: Apache-2.0

bOps.Commercial privato
├─ LICENSE-COMMERCIAL.txt     testo revisionato legalmente
├─ COMMERCIAL.md              guida operativa non sostitutiva del contratto
├─ THIRD-PARTY-NOTICES
├─ SBOM
└─ sorgenti proprietari       copyright + SPDX-License-Identifier: LicenseRef-bOps-Commercial

`bOps.Workspace` privato
├─ submodule bOps             commit pubblico verificato
├─ submodule bOps.Commercial  commit privato autorizzato
└─ bootstrap/agentic          coordinamento, nessuna copia dei sorgenti prodotto
```

Il root di coordinamento non unifica le licenze e non modifica la proprietà dei due repository:
contiene soltanto riferimenti immutabili ai commit e automazione di bootstrap/validazione.

Il file `NOTICE` non deve essere usato per aggiungere divieti che modifichino Apache-2.0. La guida
ASF sull'applicazione della licenza raccomanda `LICENSE`, `NOTICE` e intestazioni coerenti:
[Applying the Apache License 2.0](https://www.apache.org/legal/apply-license).

## 12. Controlli prima di ogni release

1. Confermare lo scope pubblico/privato di ogni file aggiunto.
2. Verificare SPDX, copyright e metadati NuGet/npm.
3. Generare SBOM e third-party notices dagli artefatti finali.
4. Verificare che nessun file proprietario o secret sia entrato nel repository pubblico.
5. Verificare che gli artefatti commerciali includano anche gli obblighi OSS applicabili.
6. Verificare che GitHub identifichi Apache-2.0; GitHub usa Licensee sul contenuto del file
   `LICENSE`, non sulle licenze delle dipendenze
   ([GitHub license detection](https://docs.github.com/en/rest/licenses/licenses)).
7. Verificare che tutti i contributi esterni accettati abbiano il CLA richiesto.
8. Ottenere approvazione legale per licenza commerciale, CLA e trademark policy prima del lancio.

## 13. Fonti primarie

- [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0.html)
- [Apache Licensing and Distribution FAQ](https://www.apache.org/foundation/license-faq.html)
- [Applying the Apache License 2.0](https://www.apache.org/legal/apply-license)
- [ASF Contributor Agreements](https://www.apache.org/licenses/contributor-agreements.html)
- [ASF CLA FAQ](https://www.apache.org/licenses/cla-faq.html)
- [GitHub: adding a license](https://docs.github.com/en/communities/setting-up-your-project-for-healthy-contributions/adding-a-license-to-a-repository)

In caso di divergenza tra questa guida e un testo di licenza o contratto applicabile, prevale il
testo legale applicabile.
