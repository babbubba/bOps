# KNOW-BENCH D7 — diagnosi protetta e revisione cieca

Riguarda solo l'harness (`tests/bOps.AgentFramework.Tests`) e `scripts/Invoke-KnowBenchReview.ps1`. Nessun codice di produzione, prompt,
scenario A/B/C, Knowledge Pack, configurazione DGX o credenziale è stato modificato. Provenienza, minimizzazione e quality gate del
corpus (D1–D3, [know-bench-corpus-provenance.md](know-bench-corpus-provenance.md)) sono invariati e coperti dagli stessi test.

## 1. Il problema

`KnowledgeBenchmarkExport` oscurava `finalObservation` e il mondo di esecuzione (cartella temporanea con store e audit) veniva eliminato:
dopo un run non restava nulla che permettesse di valutare la **qualità** della diagnosi, che la rubrica del benchmark
([know-bench-01a/README.md](know-bench-01a/README.md), «Metriche e giudizio») affida a un revisore umano cieco. D7 conserva la risposta
dell'agente in un archivio locale protetto, separato da `result.json` e dagli audit condivisibili, e prepara la revisione cieca.

## 2. Cattura della diagnosi

`DiagnosisCapture` usa solo ciò che il runtime già offre; non cambia prompt, runner o provider.

| Fonte | Quando | Disponibilità |
|---|---|---|
| **Passo finale persistito** (`FinalResponse.IsFinalStep` sullo `TaskState`, ricaricato dallo store se il task è fallito/scaduto) | Il runtime ha persistito una risposta finale. | `Complete` se il task è `Completed`; `Partial` (motivo `TaskNotCompleted`) altrimenti. |
| **Risposta del modello vista al confine** (`BenchmarkModel`, dopo la risposta del provider e prima del giudizio del runtime) | Nessun passo finale persistito. Si conserva l'ultima risposta senza tool call, di una richiesta non di planning, non vuota, né artefatto di protocollo, né risposta di controllo del guard. | `Partial` (`NotPersisted`). |
| **Corpo grezzo di una risposta troncata** (`finish_reason = length`) | L'adapter toglie il testo troncato da `TextResponse` e lo lascia solo nei dettagli grezzi; si legge `choices[0].message.content`. Il `reasoning_content` non è mai una risposta. | `Partial` (`TruncatedAtOutputLimit`). |
| Nulla | Il modello non ha prodotto testo utilizzabile. | `None`: non si inventa nulla. |

L'osservazione di un passo-tool non è mai una diagnosi (anche se la sua descrizione coincide col marcatore: serve l'assenza di `ToolCall`).
Il salvataggio avviene in `KnowledgeBenchmarkExport.RunAsync` **prima** di `World.Dispose()`, anche se il task è fallito, scaduto o
l'esecuzione ha lanciato un'eccezione (che viene comunque rilanciata invariata).

## 3. Archivio locale protetto

Garanzie distinte, ciascuna con il proprio meccanismo (non sono intercambiabili):

| Garanzia | Meccanismo | Cosa **non** garantisce |
|---|---|---|
| **Riservatezza a riposo** | Ogni record è AES-256-GCM con `VaultCipher` (il cifrario del vault, ADR-0029), chiave dati casuale a 256 bit, AAD = tipo + id (un record non si può rietichettare o scambiare). La chiave è avvolta da **DPAPI scope CurrentUser** (Windows; `crypt32` via P/Invoke isolato in `WindowsDpapiKeyProtector`, nessuna dipendenza NuGet) oppure da un **segreto dell'operatore** (HKDF-SHA256 + AES-GCM; portabile, obbligatorio fuori da Windows). | Non protegge da un processo in esecuzione come lo stesso utente (DPAPI) o da chi conosce il segreto. |
| **Restrizione d'accesso** | Windows: DACL **protetta** (senza ereditarietà) che concede solo all'utente corrente, creata con la directory; i file la ereditano. Unix: directory `0700`, file `0600`. Verificata a ogni apertura/lettura: un permesso allargato fa fallire con `PermissionsTooBroad`, non viene «riparato». | Non cifra: chi supera l'ACL (amministratore, backup) vede i file, ma sono comunque sigillati. |
| **Integrità** | SHA-256 del testo (byte UTF-8 esatti) e del record sigillato; il tag GCM autentica il contenuto. Rileggere e verificare è parte del salvataggio (`Stored` solo dopo la verifica). | Non prova la provenienza: un hash dice solo che i byte non sono cambiati. |
| **Nessuna sovrascrittura** | File temporaneo `.partial`, flush su disco, `File.Move` senza overwrite. Un record esistente non viene toccato. | — |

**Autorizzazione.** Si scrive solo se l'operatore indica un percorso assoluto *e* accetta la scrittura locale:

| Variabile | Significato |
|---|---|
| `BOPS_KNOW_BENCH_DIAGNOSIS_ARCHIVE` | Radice dell'archivio (nuova, assoluta, fuori dal checkout). |
| `BOPS_KNOW_BENCH_DIAGNOSIS_ARCHIVE_ACCEPT=1` | Autorizzazione esplicita. Con il solo percorso: `NotAuthorized`. |
| `BOPS_KNOW_BENCH_ARCHIVE_PROTECTION` | `dpapi` (default su Windows) o `operator-secret` (default altrove). |
| `BOPS_KNOW_BENCH_ARCHIVE_SECRET` | Segreto (≥ 16 caratteri) per `operator-secret`; mai scritto dall'harness. |
| `BOPS_KNOW_BENCH_REPETITION` | Ripetizione 1..99 del quadrato latino (default 1). |

I tre entry point live (`WithoutKnowledge`, `WithKnowledge`, `WithKnowledgeAndExplicitReadRequest`, più `CaptureThenAB`) **rifiutano di partire**
senza archivio autorizzato e apribile (creazione, permessi e chiave sono provati prima della prima chiamata al modello). `Validate` fallisce se lo
stato dell'archivio non è `Stored`.

**Layout** (i nomi sono id casuali, mai condizioni o nomi di skill):

```
<radice>/records/<dx-id>.sealed          diagnosi sigillata
<radice>/private/key.json                chiave dati avvolta (DPAPI o segreto)
<radice>/private/map/<dx-id>.sealed      mappa privata, sigillata: id -> condition, repetition, runId, hash corpus/attestazione
<radice>/private/exports/<pk-id>.sealed  registro degli export (inclusi/esclusi, rischio di svelamento)
<radice>/private/quality/…               risultati della ricongiunzione
```

**Errori e scritture parziali.** Ogni errore diventa un codice chiuso (`AlreadyExists`, `WriteFailed`, `PermissionsTooBroad`, `KeyUnavailable`,
`VerificationFailed`, …): messaggi, percorsi e testo non escono mai dall'archivio, né in log, né in eccezioni, né in `result.json`. In caso di
errore si rimuovono solo i file creati dalla chiamata (`.partial`, riga di mappa); un record preesistente non viene mai toccato.

**Cosa entra in `result.json`** (blocco `diagnosis`): `diagnosisId`, `repetition`, `availability`, `source`, `reason`, `textSha256`, `textBytes`,
`sealedSha256`, `archiveStatus` (`Stored`/`NotConfigured`/`NotAuthorized`/`Failed` + `archiveFailureCode`), `qualityMeasurable`,
`qualityStatus` (`PendingBlindReview`/`NotMeasurable`) e `qualityNotMeasurableReason`. Mai testo, mai percorsi. `finalObservation` resta
`[redacted…]`. `comparison.json` riporta per braccio solo disponibilità, stato dell'archivio e `qualityMeasurable`.

**Regola di misurabilità.** `qualityMeasurable` è vero solo con archivio `Stored` **e** diagnosi presente. Un run senza diagnosi o con archivio
fallito/non autorizzato è «non misurabile»: non vale zero e non entra nelle medie (`meanTotal = null`).

## 4. Identità anonime e mappa privata

- Ogni run riceve un `dx-<26 caratteri>` casuale dal CSPRNG (≈125 bit), non derivato da condizione, run o tempo. L'alfabeto esclude le lettere
  `a`, `b`, `c` (e `i`, `l`, `o`): nessun id o nome file può contenere le etichette delle condizioni.
- La mappa `diagnosisId → condition, repetition, runId` è un file sigillato per diagnosi in `private/map`, **mai** nel pacchetto del revisore.
  `result.json` contiene l'id (e la condizione, perché sta nella cartella `A|B|C`): serve all'operatore per ricongiungere, non al revisore, che
  non riceve né i risultati né l'archivio.

## 5. Pacchetto di revisione cieca

`BlindReviewPackage.Export` (via `Invoke-KnowBenchReview.ps1 -Action ExportPackage`) scrive in una directory **nuova**, fuori dall'archivio:

| File | Contenuto |
|---|---|
| `cards/<dx-id>.txt` | La diagnosi, byte per byte come prodotta (UTF-8 senza BOM). Mai modificata per nascondere la Knowledge. |
| `evidence/events-corpus.json`, `evidence/corpus-attestation.json` | Il corpus minimizzato verificato (come nel replay) e la sua attestazione: identici per tutti i bracci. |
| `rubric.json`, `README-reviewer.md` | Rubrica, categorie di annotazione e istruzioni neutre. |
| `evaluations/<dx-id>.evaluation.json` | Modulo vuoto per diagnosi (punteggi `null`). |
| `package-manifest.json` | `packageId`, hash del corpus/attestazione, id delle carte, SHA-256 di ogni file. Nessun timestamp. |

Misure di cieco: nomi = id casuali; elenco in ordine di id (casuale rispetto a braccio e ordine di esecuzione); tutti i file e le directory
hanno **lo stesso timestamp neutro** (2000-01-01T00:00:00Z) invece di quelli reali; nessuna condizione, ripetizione, runId, metrica di accesso
alla Knowledge, mappa o percorso dell'archivio; stesso corpus per tutti. Le diagnosi `None` non hanno carta (nessuna qualità da giudicare);
quelle `Partial` hanno solo l'indicatore neutro `Incomplete` nel modulo. Le diagnosi illeggibili o alterate sono **escluse con motivo** nel
registro privato dell'export, non scartate in silenzio. Un export senza diagnosi revisionabili non crea nulla.

**Limite residuo del cieco (dichiarato, non nascosto).** Il testo non viene alterato: una diagnosi che cita la Knowledge («secondo il runbook…»),
i nomi di risorse (`windows-event-triage`, `references/…`), la procedura del pack o lo stile del pack può rendere riconoscibile il braccio B/C.
`BlindingRisk` misura (euristicamente, solo conteggi, nel registro privato dell'export — mai al revisore) quante carte contengono identificatori o
lessico di Knowledge, così il rischio si può riportare nei risultati. Altri segnali non eliminabili: lunghezza e struttura del testo, qualità
stessa, assenza di carte per le diagnosi `None`, e il fatto che C chiede una lettura esplicita. Se operatore e revisore coincidono, il cieco
dipende dalla disciplina di non aprire la mappa prima della fine; un secondo revisore indipendente è preferibile.

## 6. Rubrica e formato di valutazione

I **cinque criteri e le ancore 0/1/2 sono quelli del README** (nessun criterio modificato); `ReviewRubric` dà loro solo chiavi stabili e un test
verifica che titoli e scala compaiano nel README:

`phenomenaClassification` · `causeVersusClue` · `temporalCorrelation` · `verifiableCitations` · `coverageAndUncertainty`.

Il revisore compila `evaluations/<id>.evaluation.json`: `status` (`Draft`/`Final`), `reviewer`, i cinque `scores` (0, 1, 2; `null` = non
assegnato, non zero), `claims` e `notes`. Ogni claim ha una `category`, e facoltativamente `quote`, `corpusEvidence`, `severity`
(`Low|Medium|High`) e `comment`. Categorie:

| Categoria | Distinzione richiesta |
|---|---|
| `CorrectFact` | Fatto correttamente identificato |
| `JustifiedCorrelation` | Correlazione giustificata dal corpus |
| `UnsupportedCausalInference` | Inferenza causale non supportata |
| `HallucinationOrNonexistentReference` | Allucinazione / riferimento inesistente nel corpus |
| `UncertaintyHandling` | Gestione dell'incertezza (adeguata o carente, nel commento) |
| `RelevantAdditionalCheck` | Verifica aggiuntiva pertinente |
| `IncorrectFact` | **Aggiunta motivata:** fatto presente nel corpus ma letto male; non è né «corretto» né «inesistente» e il revisore deve poterlo registrare. È solo un'annotazione: i criteri non cambiano. |

Le annotazioni implementano «Elencare claim non supportati anche quando il totale è alto» del README. Le valutazioni non usano un LLM e restano
separate dalle metriche di accesso alla Knowledge (`knowledgeUsed`, `knowledgeDiscovery`): la ricongiunzione non le copia. Un file malformato,
una bozza, o riferito a un altro pacchetto/testo (`diagnosisTextSha256`) è `EvaluationRejected`; i problemi sono codici chiusi che non ripetono il
testo del revisore.

## 7. Procedura PowerShell

Presupposti: i passi 0–6 di [knowledge-discovery-investigation.md](knowledge-discovery-investigation.md) §11 (con `BOPS_KNOW_BENCH_DIAGNOSIS_ARCHIVE`
e `…_ACCEPT` impostati come nel passo 5). Tutto sotto è offline: nessun modello, nessun Event Log.

```powershell
# A. Dopo i 9 run: controllo di misurabilità (nessun testo, solo id/stato)
Get-ChildItem C:\bops-bench\KNOW-DISC-INV01\results -Recurse -Filter result.json | ForEach-Object {
    $r = Get-Content $_.FullName -Raw | ConvertFrom-Json
    [pscustomobject]@{ Arm = $r.condition; Dx = $r.diagnosis.availability; Archive = $r.diagnosis.archiveStatus
        Reviewable = $r.diagnosis.qualityMeasurable; Why = $r.diagnosis.qualityNotMeasurableReason }
} | Sort-Object Arm | Format-Table -AutoSize     # Reviewable = False => qualità NON misurata per quel run
```

```powershell
# B. Pacchetto per il revisore (directory NUOVA, fuori da archivio e checkout). Serve $CorpusSha/$AttestationSha pinnati fuori dai file.
.\scripts\Invoke-KnowBenchReview.ps1 -Action ExportPackage -ArchiveRoot $Archive -AcceptLocalArchiveAccess `
    -PackageDirectory 'C:\bops-bench\KNOW-DISC-INV01-review\package' -CorpusPath $Corpus -CorpusSha256 $CorpusSha -AttestationSha256 $AttestationSha
$PackageId = (Get-Content 'C:\bops-bench\KNOW-DISC-INV01-review\package\package-manifest.json' -Raw | ConvertFrom-Json).packageId
```

```powershell
# C. Consegnare al revisore SOLO la directory del pacchetto (non $Archive, non results\, non quality-results). Il revisore apre
#    README-reviewer.md, legge cards\*.txt contro evidence\events-corpus.json e compila evaluations\*.evaluation.json (status = Final).
#    Per rendere il pacchetto non modificabile durante la consegna si può copiare e proteggere:
Copy-Item 'C:\bops-bench\KNOW-DISC-INV01-review\package' 'C:\bops-bench\KNOW-DISC-INV01-review\to-reviewer' -Recurse
```

```powershell
# D. Ricevute le valutazioni (cartella evaluations compilata), ricongiungimento con la mappa privata e controllo di coerenza con i result.json.
.\scripts\Invoke-KnowBenchReview.ps1 -Action Join -ArchiveRoot $Archive -AcceptLocalArchiveAccess -PackageId $PackageId `
    -EvaluationsDirectory 'C:\bops-bench\KNOW-DISC-INV01-review\returned\evaluations' `
    -ResultsRoots @('C:\bops-bench\KNOW-DISC-INV01\results\rep1','C:\bops-bench\KNOW-DISC-INV01\results\rep2','C:\bops-bench\KNOW-DISC-INV01\results\rep3')
# Scrive <Archive>\private\quality\quality-results-<utc>-<rnd>.json (create-new, solo utente corrente).
$Quality = Get-ChildItem (Join-Path $Archive 'private\quality') -Filter 'quality-results-*.json' | Sort-Object Name | Select-Object -Last 1
$q = Get-Content $Quality.FullName -Raw | ConvertFrom-Json
$q.diagnoses | Select-Object condition, repetition, availability, qualityStatus, problem, resultJson, total | Sort-Object condition, repetition | Format-Table -AutoSize
$q.byCondition | ConvertTo-Json -Depth 6        # medie solo sui valutati; NotMeasurable/NotEvaluated/EvaluationRejected contati a parte
```

Stati della ricongiunzione: `Evaluated` (punteggio) · `NotMeasurable` (`NoDiagnosis`, `NotInPackage`, `Archive:<codice>`) · `NotEvaluated` (carta senza
modulo) · `EvaluationRejected` (`NotFinal`, `DescribesAnotherDiagnosis`, `InvalidJson`, `ScoreOutOfRange:<chiave>`, …). `resultJson` è `Consistent`,
`Inconsistent`, `NotFound` o `Unreadable`: confronta runId, id, hash e disponibilità con `result.json` senza copiarne alcuna metrica.
L'incrocio qualità × accesso alla Knowledge (`knowledgeDiscovery`, `knowledgeUsed`) si fa solo dopo, a mano, per `runId`.

Lo script rifiuta: archivio dentro il checkout, pacchetto già esistente o dentro l'archivio/checkout, hash non pinnati, `-AcceptLocalArchiveAccess`
assente; non deduce mai il successo dal solo exit code (richiede l'esito `Passed` nel TRX, perché un test saltato esce con 0).
Smoke test eseguito su Windows con DPAPI reale: export (3 carte, timestamp neutri) → moduli compilati → join.

## 8. Test offline (`tests/bOps.AgentFramework.Tests`)

| Classe | Copre |
|---|---|
| `KnowledgeDiagnosisArchiveTests` (13) | diagnosi completa e parziale, assenza di diagnosi, no-overwrite, scrittura fallita senza residui né riga di mappa orfana, record alterati/scambiati, segreto sbagliato/protezione diversa, permessi (ACL Windows / `0700`-`0600` Unix) e rifiuto di permessi allargati, id e nomi opachi, mappa separata e sigillata, assenza di testo/percorsi in proiezione ed errori, autorizzazione esplicita, DPAPI reale (Windows). |
| `KnowledgeDiagnosisCaptureTests` (10) | risposta archiviata prima della cancellazione del world; task fallito/scaduto; eccezione dopo il task; nessuna risposta (e un'osservazione-tool mai promossa); archivio non configurato/non autorizzato/guasto; fonti della cattura; due percorsi attraverso il vero runner (completa con corpus attestato; troncata e vuota). |
| `KnowledgeBlindReviewTests` (12) | contenuto del pacchetto dal lato revisore, carte identiche al testo, timestamp neutri e ordine, rubrica = README, corpus/attestazione diversi, record alterati ed esclusioni motivate, no-overwrite e permessi del pacchetto, rischio di svelamento privato, formato di valutazione rigoroso, ricongiunzione (separazione dalle metriche Knowledge, non misurabile ≠ zero), valutazioni di un altro pacchetto/testo. |

Regressione D1–D3: la suite completa del progetto (attestazione, minimizzazione, quality gate, provenienza, export sicuro, comparison, isolamento) passa
invariata; un test D7 verifica inoltre che con l'archivio attivo `corpusProvenance` resti `OperatorAttested` e che `finalObservation` resti oscurato.

## 9. Limiti residui

1. **Cieco imperfetto per costruzione** (vedi §5): contenuto, stile e riferimenti alla Knowledge non si alterano. Il rischio è misurato, non eliminato.
2. **Riservatezza ≠ difesa da un processo ostile dello stesso utente**: con DPAPI CurrentUser chiunque esegua codice come l'operatore decifra; con
   `operator-secret` conta la custodia del segreto. I pacchetti per il revisore e le sue valutazioni sono **in chiaro** (devono essere leggibili) e protetti
   solo dall'ACL della directory: la consegna fuori macchina è responsabilità dell'operatore.
3. **Cattura di ripiego (`ObservedModelReply`)** è un'osservazione al confine del modello: non è stata accettata dal runtime e non è validata. Si distingue
   sempre da `PersistedFinalStep` e dal motivo nel blocco `diagnosis`. Con troncamento solo il `content` grezzo è disponibile; un modello con ragionamento
   che esaurisce `n_predict` nel solo `reasoning_content` produce `None`.
4. **Elaborazione del testo**: l'hash è calcolato sui byte UTF-8 del testo; sequenze UTF-16 non valide diventano U+FFFD una sola volta, prima dell'hash.
5. **Hash del testo in `result.json`**: consente di verificare un testo noto ma non rivela un testo ad alta entropia; non sono hash del corpus.
6. **Un solo revisore per pacchetto** nel formato attuale; più revisori richiedono pacchetti o cartelle separate (nessun calcolo di accordo inter-revisore).
7. **Linux**: ACL/modi Unix e `operator-secret` sono testati in WSL; DPAPI e i test che attraversano il canary Windows sono solo Windows.

## 10. Problemi ancora aperti prima di A/B/C live

Fuori dall'ambito di D7 (restano da fare, non anticipati qui): orchestrazione dei run, parametri del provider e reportistica (P1/P2); attestazione dei
parametri del server DGX (`BOPS_KNOW_BENCH_SETTINGS_VERIFIED` resta una dichiarazione dell'operatore); definizione di C sul planner.

Specifici di D7, da risolvere o accettare esplicitamente prima del live:

1. **Nessuna esecuzione live di D7**: la cattura da risposta reale di Qwen (in particolare il percorso `length` con corpo grezzo reale e il comportamento
   del reasoning) è provata solo con risposte scritte a mano; la prima esecuzione live è anche la prima verifica sul campo.
2. **`CaptureThenAB` (capture integrata)** non persiste il corpus: il pacchetto cieco richiede il file attestato (`BOPS_EVENTS_CORPUS_PATH`), quindi per
   la revisione si usa il percorso a nove run con corpus congelato. Cambiare il capture è escluso da questa fase.
3. **Il ripiego di cattura deve essere visto sul primo run reale**: confrontare, per ogni run, `source` e `availability` con lo stato del task prima di
   fidarsi dei casi `Partial`.
4. **Procedura di consegna al revisore**: decidere chi è il revisore (indipendente dall'operatore?), il canale di consegna (il pacchetto è in chiaro) e
   se serve un secondo revisore; senza, il cieco dipende dalla disciplina dell'operatore.
5. **Custodia della chiave**: con DPAPI l'archivio è inutilizzabile se il profilo Windows viene perso o reimpostato; decidere se esportare le diagnosi in
   chiaro per backup (non previsto) o accettare il rischio. Con `operator-secret`, conservare il segreto fuori dai file.
6. **Percorsi**: l'archivio e il pacchetto devono stare fuori dal checkout e su un volume locale non di rete; nessun controllo sui backup/sync che
   possano copiare il pacchetto in chiaro (OneDrive, ecc.).
7. **Effetto di `RequireAuthorized`**: i quattro entry point live si fermano senza archivio autorizzato. È voluto (un run senza diagnosi non misura la
   qualità), ma cambia il contratto dei comandi live precedenti: i passi 5–6 del documento di indagine sono stati aggiornati di conseguenza.
