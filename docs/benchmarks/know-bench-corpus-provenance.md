# KNOW-BENCH — provenienza, minimizzazione e qualità del corpus congelato

Questa nota descrive le tre garanzie del corpus Windows unico usato dalle nove esecuzioni A/B/C del benchmark
KNOW-DISC-INV01 (piano dei comandi in [knowledge-discovery-investigation.md](knowledge-discovery-investigation.md) §11).
Riguarda solo l'harness (`tests/bOps.AgentFramework.Tests`); nessuna governance di produzione è stata modificata e le
guardie di provenienza esistenti (`RequireRealCorpus`, `KnowledgeBenchmark.Replay(requireCaptured: true)`) restano invariate.
Le garanzie sono fissate da test offline deterministici (§7).

## 1. Modello di fiducia

| Classificazione | Come si ottiene | Garanzia | Dove è accettata |
|---|---|---|---|
| `Captured` | Solo nel processo che esegue con successo la chiamata nativa a `WindowsSystemEventsTool` (capture integrata o `CanaryEventsCorpusTool` in modalità `capture`). | Osservazione locale diretta; nessuna attestazione crittografica. | `RequireRealCorpus` e `RequireBenchmarkProvenance`. |
| `OperatorAttested` | Un file salvato su disco, con integrità SHA-256 verificata, che supera il quality gate e per il quale l'operatore ha registrato un'approvazione esplicita (§3). | **Attestazione operativa**: i byte non sono cambiati dall'approvazione e la ricevuta di capture coincide con quella dell'approvazione. **Non** prova indipendente che i dati provengano da quella capture. | Solo `RequireBenchmarkProvenance`, cioè `KnowledgeBenchmark.ReplayFrozen` (il percorso A/B/C del benchmark). **Non** `RequireRealCorpus`. |
| `Unverified` | Qualunque altro file importato da disco con origine `windows-capture`. | Integrità dei byte (SHA-256 pinnato), nessuna provenienza. | Mai nel benchmark. |
| `Synthetic` | Fixture e sorgenti controllate. | — | Mai nel benchmark. |

Un file letto da disco non diventa mai `Captured`, nemmeno con un'attestazione. Lo SHA-256 da solo prova solo che i byte non
sono cambiati: non è prova di provenienza. Un attaccante che può scrivere sia il corpus sia il sidecar, e conosce i pin
dell'operatore, può falsificare l'intera catena: il confine di fiducia è l'operatore che custodisce i pin fuori banda.

### Capture attendibile vs replay attestato

- **Capture attendibile**: una sola chiamata governata al tool nativo, in un processo, con i parametri esatti dello scenario
  (`KnowledgeBenchmark.Arguments`). Il file è creato `CreateNew`, minimizzato e giudicato dal gate **prima** di poter essere approvato.
  Il processo che l'ha eseguita la classifica `Captured`.
- **Replay attestato**: nei processi successivi (le 9 esecuzioni) lo stesso file è riletto, ricontrollato in integrità, rigiudicato
  dal gate e legato al sidecar di approvazione. Classificazione `OperatorAttested`. Tutti i bracci leggono gli stessi byte e la
  stessa provenienza.

## 2. Sidecar di attestazione

`<nome>.bops-events.attestation.json`, nella stessa directory del corpus, creato `CreateNew` (mai sovrascritto) dalla modalità
`approve`. Formato `bops-events-corpus-attestation-v1`, busta `{ attestation, sha256 }` canonica come il corpus. Contiene:
hash del corpus, `captureId`, `capturedUtc`, argomenti canonici, versione di minimizzazione, tool invocato, hash di manifest e
schema, ambiente, cifre del gate (eventi, eventi utilizzabili, canali richiesti, verdetto), `approvedUtc`, il tipo di approvazione
(`operator-explicit-sha256-confirmation`) e la stringa di garanzia ("operator-attested; SHA-256 pinned; acquisition not
independently provable; unsigned").

Approvazione esplicita: l'operatore deve ripetere in `BOPS_EVENTS_CORPUS_APPROVE_SHA256` lo stesso hash che ha pinnato in
`BOPS_EVENTS_CORPUS_SHA256`. Una conferma assente o diversa non approva nulla. Il replay richiede in più
`BOPS_EVENTS_CORPUS_ATTESTATION_SHA256` (hash del sidecar, conservato separatamente dai file).

Il replay rifiuta: file o sidecar modificati (anche con hash incorporato ricalcolato), hash errati o assenti, sidecar mancante,
sidecar che descrive un altro corpus (captureId, argomenti, minimizzazione, ricevuta, cifre del gate), approvazione precedente
alla capture, corpus non più idoneo, capture che porti un'attestazione. Il controllo di legame e gate è ripetuto a ogni chiamata del tool.

Il valore di provenienza che viaggia come evidence fact è limitato a 1024 caratteri dal runtime: per questo la stringa di garanzia è
breve e `auditReference` (che punta all'audit di un altro processo) è omesso nella provenienza attestata.
Il modello vede solo `benchmarkCorpus.origin = "operator-attested"`, uguale in A, B e C.

## 3. Procedura dell'operatore (sostituisce i passi 3 e 5 della sezione 11 per la parte corpus)

```powershell
# 3a. Capture UNICA (profilo 01D) + quality gate. Se il gate fallisce il test fallisce con i codici di rifiuto; usare un nuovo nome file.
$env:BOPS_EVENTS_CORPUS_MODE = 'capture'; $env:BOPS_EVENTS_CORPUS_PROFILE = '01D'
$env:BOPS_EVENTS_CORPUS_PATH = $Corpus;   $env:BOPS_EVENTS_CORPUS_ACCEPT_MINIMIZATION = '1'
dotnet test $Proj --no-build --no-restore --filter 'FullyQualifiedName=bOps.AgentFramework.Tests.KnowledgeEventsCorpusTests.SyntheticCorpus_TwoGovernedReplaysAreIdentical'
$CorpusSha = (Get-Content -LiteralPath $Corpus -Raw | ConvertFrom-Json).sha256   # conservarlo FUORI dai file

# 3b. Revisione dell'operatore (ispezionare il file minimizzato) e approvazione esplicita: ripetere lo stesso hash.
$env:BOPS_EVENTS_CORPUS_MODE = 'approve'; $env:BOPS_EVENTS_CORPUS_SHA256 = $CorpusSha; $env:BOPS_EVENTS_CORPUS_APPROVE_SHA256 = $CorpusSha
dotnet test $Proj --no-build --no-restore --filter 'FullyQualifiedName=bOps.AgentFramework.Tests.KnowledgeEventsCorpusTests.SyntheticCorpus_TwoGovernedReplaysAreIdentical'
$Sidecar = $Corpus -replace '\.bops-events\.snapshot\.json$', '.bops-events.attestation.json'
$AttestationSha = (Get-Content -LiteralPath $Sidecar -Raw | ConvertFrom-Json).sha256   # conservarlo FUORI dai file

# 3c. Verifica di replay attestato (senza modello), poi proteggere i due file.
$env:BOPS_EVENTS_CORPUS_MODE = 'replay'; $env:BOPS_EVENTS_CORPUS_ATTESTATION_SHA256 = $AttestationSha
dotnet test $Proj --no-build --no-restore --filter 'FullyQualifiedName=bOps.AgentFramework.Tests.KnowledgeEventsCorpusTests.SyntheticCorpus_TwoGovernedReplaysAreIdentical'
Remove-Item Env:BOPS_EVENTS_CORPUS_MODE, Env:BOPS_EVENTS_CORPUS_PROFILE, Env:BOPS_EVENTS_CORPUS_APPROVE_SHA256 -ErrorAction SilentlyContinue
attrib +R $Corpus; attrib +R $Sidecar
```

Per le nove esecuzioni A/B/C, oltre alle variabili già previste al passo 5:

```powershell
$env:BOPS_EVENTS_CORPUS_PATH = $Corpus; $env:BOPS_EVENTS_CORPUS_SHA256 = $CorpusSha
$env:BOPS_EVENTS_CORPUS_ATTESTATION_SHA256 = $AttestationSha
```

Senza `BOPS_EVENTS_CORPUS_ATTESTATION_SHA256` (o con un sidecar assente o non coerente) `KnowledgeBenchmarkLiveTests` si ferma prima di
leggere la configurazione del provider, il metadata o di contattare il server.

## 4. Minimizzazione (`allowlist-provider-and-structured-details-v2`)

Deterministica e idempotente (un corpus salvato viene ricostruito dal proprio output all'atto della lettura), applicata una sola volta
prima del congelamento e quindi identica per i tre bracci. La versione precedente (`…-v1`, che azzerava provider e dettagli) non
viene più prodotta né accettata.

**Conservato sempre:** timestamp UTC, canale (solo `System`/`Application`), Event ID numerico (≤ 5 cifre), gravità, conteggi,
copertura, completezza e troncamento.

**Provider:** conservato solo se appartiene alla allowlist esplicita `CorpusMinimizer.ProviderAllowlist` (confronto senza distinzione
di maiuscole, ortografia canonica in uscita): `Microsoft-Windows-Kernel-Power`, `Microsoft-Windows-WHEA-Logger`, `Application Error`,
`Windows Error Reporting`, `EventLog`, `.NET Runtime`, `Microsoft-Windows-WER-SystemErrorReporting`, `Service Control Manager`,
`Disk`, `Ntfs`. Ogni altro provider diventa `[redacted-provider-N]`, numerato per prima comparsa: lo stesso provider ignoto ha sempre lo
stesso numero, quindi la correlazione resta possibile senza nominarlo.

**Dettagli strutturati** (campo `details`, solo se l'etichetta compare nel messaggio e il valore ha la forma ammessa; altrimenti il
campo è assente, mai dedotto):

| Provider / Event ID | Campi |
|---|---|
| Kernel-Power / 41 | `bugcheckCode` |
| WHEA-Logger / qualunque | `errorClass` (corrected, fatal, recoverable), `component`, `errorSource` |
| Application Error / 1000 | `applicationName`, `moduleName` (solo nome file `.exe/.dll/.sys`, senza percorso), `exceptionCode`, `faultOffset` |
| Windows Error Reporting / 1001 | `eventName`, `faultBucketType` |
| .NET Runtime / 1026 | `applicationName`, `frameworkVersion`, `exceptionType` (nome del tipo, senza stack) |
| WER-SystemErrorReporting / 1001 | `bugcheckCode` |
| EventLog (6008), Service Control Manager, Disk, Ntfs | nessuno (solo provider + ID) |

**Mai conservato:** testo del messaggio, percorsi, nomi utente e di macchina, indirizzi IP, e-mail, righe di comando, stack, id e nomi di
processo, `unit`, dettagli delle sorgenti. I valori dei dettagli sono vincolati dalla forma stessa (nome file, esadecimale, versione,
nome tipo, parola chiusa): un valore fuori forma viene scartato, non troncato.

Limiti: l'estrazione riconosce le etichette inglesi dei messaggi Windows; un messaggio localizzato o riformulato non produce dettagli (il
provider e l'ID restano). `exceptionType` e `applicationName` possono rivelare il nome di software proprietario; è una scelta dichiarata,
necessaria alla rubrica ("il modulo segnalato, da solo, non dimostra il difetto"). La allowlist è un elenco chiuso nel codice di
test: ampliarla è una modifica da rivedere.

## 5. Quality gate (`CorpusQualityGate`)

Eseguito (a) alla capture, sugli stessi byte che vengono scritti, (b) all'approvazione, (c) a ogni replay attestato. Non sintetizza,
non ripara e non sostituisce eventi o corpus; riporta solo codici chiusi e conteggi.

| Codice | Fallisce quando |
|---|---|
| `IntegrityMismatch` | l'hash ricalcolato non è quello pinnato |
| `UnsupportedSchema` | formato, tool o `schemaVersion` non supportati, oppure il corpus non ha la forma attesa |
| `MinimizationMismatch` | la versione di minimizzazione non è quella corrente |
| `NotNativeCapture` | origine diversa da `windows-capture` o ricevuta di capture assente |
| `ParametersMismatch` | argomenti diversi da quelli dello scenario, o modalità non `raw` |
| `CaptureIncomplete` | `complete`, `status`, `completeness` non indicano una capture completa |
| `Truncated` | il risultato è troncato |
| `CoverageIncomplete` | la copertura globale non è `complete` o manca/non è completo lo store di `System` o `Application` |
| `SourcesInconsistent` | una sorgente elencata non è `available` o manca `System`/`Application` |
| `NoUsableEvents` | nessun evento ha provider in allowlist, Event ID numerico, gravità critical/error/warning, canale richiesto e tempo nella finestra |
| `MinimizationViolation` | un record conserva testo libero, dati di processo o un provider né in allowlist né redatto |

Un corpus vuoto o senza eventi utili è **non idoneo**: non viene approvato e non è dichiarato valido. In capture il file minimizzato resta
su disco per ispezione ma il test fallisce con i codici; non c'è mai un corpus sostitutivo.

La copertura di System e Application è la copertura **delle sorgenti e degli store** richiesta dal tool, non la presenza di eventi in
entrambi i canali: una macchina può non avere avvisi in uno dei due nella finestra.

## 6. Limiti residui

- L'approvazione è un'attestazione operativa, non una prova crittografica (§1). Nessuna firma, PKI o registro di fiducia.
- La capture nativa su Windows non è stata eseguita in questa fase: percorso e gate sono verificati con fixture offline che
  riproducono la forma dell'output del tool, non con un Event Log reale. Il primo test su una macchina reale può rivelare messaggi in
  formato diverso (dettagli assenti, non errati) o un corpus non idoneo.
- Un corpus per tutte le nove esecuzioni significa che le tre ripetizioni condividono la stessa evidenza: la varianza misurata è del modello,
  non dell'evidenza.
- `eventId`/provider ignoti ma diagnostici (per esempio un servizio proprietario) sono redatti: dichiarato, non un difetto.
- Il gate non ha modo di sapere se l'insieme di eventi sia "interessante": garantisce solo che ce ne sia almeno uno utilizzabile.

## 7. Test offline

| Classe | Cosa fissa |
|---|---|
| `KnowledgeCorpusMinimizationTests` | provider e dettagli ammessi conservati; segreti, percorsi, utenti, IP, e-mail, comandi e provider ignoti redatti; nessun dettaglio inventato o fuori forma; determinismo e idempotenza; gruppi aggregati; allowlist |
| `KnowledgeCorpusQualityGateTests` | capture idonea; corpus vuoto, troncato, incompleto, senza copertura di Application, con storia che inizia nella finestra, con parametri diversi, alterato, non supportato, sintetico o con testo libero conservato: tutti non idonei con il codice atteso |
| `KnowledgeCorpusAttestationTests` | capture → approvazione → replay come `OperatorAttested` (e mai `Captured`/`RequireRealCorpus`); rifiuto del file non attestato; hash errati, file e sidecar alterati; sidecar falsificato ma coerente; approvazione di corpus non idoneo, di hash diverso o sintetico; tre replay consecutivi con byte identici in A, B e C; rifiuto al dispatch se corpus o attestazione cambiano; modalità `approve`/`replay` dell'operatore |
| Regressione | `KnowledgeCorpusProvenanceTests`, `TrustedEventsCaptureTests`, `KnowledgeEventsCorpusTests`, `KnowledgeBenchmark*Tests`, `KnowledgeDiscoveryInvestigationTests` invariati e verdi |

## 8. Per il canale D7 (non implementato qui)

- La risposta finale va scritta in un sink protetto prima del `Dispose` del mondo e riferita da un ID opaco; lo schema di `result.json`
  può aggiungere solo hash e lunghezza.
- Per la revisione cieca il revisore ha bisogno del corpus pinnato e del suo sidecar: un pacchetto per run deve includere gli hash
  (`corpusSha256`, `attestationSha256`, già presenti in `corpusProvenance`) e **non** il braccio.
- `result.json.corpusProvenance` contiene ora `classification: OperatorAttested`, `attestationSha256` e `approvedUtc` per i run da file:
  il verdetto di validità del run (§9 del documento di indagine) dovrebbe rifiutare ogni run con classificazione diversa.
- Le rubriche "citazioni verificabili" possono ora usare provider e dettagli del corpus allowlisted; i provider redatti sono
  citabili solo come `[redacted-provider-N]`.
