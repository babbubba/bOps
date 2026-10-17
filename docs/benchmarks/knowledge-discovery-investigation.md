# KNOW-DISC-INV01 — Perché la condizione B di FIX12 non ha letto la Knowledge

Base: worktree `bOps-knowbench01a`, branch `codex/know-bench-01a`, HEAD `a74e1b6`.
Artefatti: `%LOCALAPPDATA%\bOps\benchmarks\KNOW-BENCH-FIX12-20261014-105800\` (`execution-metadata.json`,
`results\comparison.json`, `results\{A,B}\result.json`, `results\{A,B}\audit.jsonl`).
Nessuna inferenza live, nessuna capture, nessuna modifica al Knowledge Pack, nessun commit.

## 1. Sintesi

1. **FIX12 non ha girato sul codice attuale.** `execution-metadata.json` e `B/result.json` riportano
   `bopsCommit = 44696aa`. Quel commit precede di nove commit l'HEAD `a74e1b6`, e fra quei nove ci sono KNOW-FIX-01
   (`d3da032`) e l'intera ADR-0057 M1 T1–T6 (`a7709d5` … `a74e1b6`). Il Knowledge Index **non esisteva** quando FIX12 è
   stato eseguito.
2. **Causa in FIX12 (dimostrata dal codice e dagli artefatti):** la funzione `runtime.knowledge_read` era offerta in ogni
   richiesta di step, ma il modello non aveva informazioni che lo spingessero a chiamarla. Il catalogo delle skill veniva
   restituito solo dall'operazione `discover`. Il system prompt dello step non nominava la funzione e la contraddiceva: diceva
   "*The single offered native tool*" mentre i tool offerti erano due, e nello step finale "*no operational tool is offered.
   Give your final answer now*". Esito: **funzione offerta ma non selezionata**, indice **non pubblicato**.
3. **Prova comportamentale:** in entrambi i bracci il modello ha usato 3 volte `runtime.evidence_read`, che il prompt
   descrive (3 eventi `ResultCode:0` a step 1 in A e in B), e 0 volte `runtime.knowledge_read`, che il prompt non descrive.
   Il modello quindi sceglie le funzioni di controllo runtime quando il prompt le spiega.
4. **A HEAD, l'accessibilità è dimostrata offline** con la stessa composizione FIX12 (pack firmato, goal dello scenario,
   replay governato, opzioni runner):
   - indice pubblicato;
   - indice presente nel 100% delle richieste di step e assente dal planning;
   - funzione offerta;
   - `windows-event-triage` elencata come `K1` con la chiamata `read_resource K1.R1`;
   - lettura riuscita **in un solo passo per ref**, senza conoscere il nome della skill, anche passando dal vero adapter
     LlamaCpp in modalità nativa e in fallback.
5. **Ipotesi aperta:** con il prompt attuale il modello potrebbe comunque non leggere da solo. La lettura è opzionale
   ("*only if useful*") e compete con due imperativi espliciti: "*Invoke the operational tool…*" nello step operativo e
   "*Give your final answer now from the evidence already collected*" nello step finale. Il goal stesso impone
   `system.events`, e il planner non vede la Knowledge (scelta deliberata ADR-0057 H3). Solo un live A/B/C può dirlo
   (§7).
6. **Strumentazione aggiunta** (solo harness, sottoinsieme di ADR-0057 T7): `result.json` ora contiene `knowledgeDiscovery`
   e `goalSha256`, che distinguono i sei casi richiesti. È stata aggiunta anche la condizione C. I test offline sono verdi.

## 2. Percorso di inizializzazione e distribuzione in B (FIX12, `44696aa`)

| # | Passo | Codice @ `44696aa` | Evidenza FIX12 |
|---|---|---|---|
| 1 | Pack firmato caricato da `docs/benchmarks/know-bench-01d/signed-pack` e trust store di test | `KnowledgeBenchmark.LoadAsync` | metadata: `knowledgeLimits 4/4/8000` |
| 2 | Boot B: source con il solo pack, grant esatto `tenant-a/acme.windows-event-runbooks@1.0.0:Knowledge`, binding | `KnowledgeBenchmark.Boot` | — |
| 3 | `ComposeCanary(withHost: true)` → `KnowledgeTaskHost` | `KnowledgeBenchmark.Compose` | — |
| 4 | Apertura della Knowledge prima del primo salvataggio: admission, snapshot, pin, bridge | `AgentRunner.cs:363-385`, `AttachKnowledgeAsync :399-428` | B `audit.jsonl` riga 1: `{"Code":0,…,"StepIndex":-1}` = `KnowledgePackAdmissionAuditEvent`, `Code 0 = Admitted` (assente in A) |
| 5 | Il bridge rende il catalogo MAF (`AgentSkillsProvider.InvokingAsync` → `Instructions`) e lo **conserva internamente** | `KnowledgeReadBridge.cs:659-679` | — |
| 6 | Planning: solo goal e catalogo dei tool operativi, `NoNativeTools` | `AgentRunner.cs:2970`, `BuildPlanningSystemPrompt :4904` | prompt di planning **identici**: A 1172 token, B 1172 token |
| 7 | Step: vista dello step (`system.events`), evidence read se c'è evidenza, **più il manifest `runtime.knowledge_read`** | `AgentRunner.cs:1065-1069` | prompt degli step B +205, +203, +203 token rispetto ad A sulle tre richieste confrontabili (1356→1561, 3535→3738, 5128→5331): è la dimensione del manifest |
| 8 | Il system prompt dello step non menziona la funzione: "single offered native tool" (step 0); `PlanCompleteInstructions` + evidence (step finale) | `BuildStepSystemPrompt :5020-5031` | — |
| 9 | Il catalogo raggiunge il modello solo se il modello chiama `discover` | `KnowledgeReadBridge.cs:500-503` | nessuna chiamata |
| 10 | Ogni chiamata gestita dal bridge, rifiuti compresi, scrive un `KnowledgeReadAuditEvent` | `KnowledgeReadBridge.cs:390-410` | `knowledgeReads: []` calcolato dagli eventi tipizzati (`KnowledgeBenchmarkExport.cs:143` @ `44696aa`), `auditedToolsRefused: 0` |

FIX12 aveva anche un difetto di export, già corretto in `d3da032`: `SafeAudit` cercava `$type` mentre il discriminatore
persistito è `eventType`. Per questo le righe di `audit.jsonl` di FIX12 non hanno tipo, e i tipi qui sopra sono stati
ricostruiti dai campi (`Code`, `ResultCode`) e dagli enum di `44696aa`.

## 3. Quando l'indice viene pubblicato (HEAD `a74e1b6`)

- `RefreshKnowledgeIndexAsync` (`AgentRunner.cs:5453`) viene chiamato prima della prima chiamata di ogni logical call di
  step (`:1119`) e dopo ogni risposta Knowledge gestita (`:3454`). Chiama `DescribeIndex(new KnowledgeIndexQuery(run.Goal))`
  (`:5465`), verifica il turno dati contro il binding persistito (`KnowledgeIndexPublication.Verify`) e scrive un
  `KnowledgeIndexAuditEvent` per ogni esito distinto.
- Il **turno D** (dati del publisher) è `ProtectedBeforeHistory[0]`, **prima del goal** (`:1096`). La **parte C** (fatti
  tipizzati) è in coda al system prompt.
- La funzione viene offerta finché restano letture disponibili (`:1073`). Il planning e il replan restano byte-identici
  (H3): niente indice, niente funzione.

**Verifica offline** (`KnowledgeDiscoveryInvestigationTests`, composizione FIX12):

- `planningRequestsWithKnowledge = 0`;
- `stepRequestsWithIndex = stepRequests = stepRequestsOfferingFunction`;
- un solo `KnowledgeIndexAuditEvent` `Published`, `Listed = [K1 windows-event-triage, 1 risorsa, score > 0]`;
- `indexHash` per richiesta uguale a quello dell'audit.

## 4. Planning, expected tool, step authority, routing, MAF

| Elemento | Effetto sulla lettura | Impedisce? | Scoraggia? |
|---|---|---|---|
| Goal dello scenario: "*usando system.events con gli argomenti concordati*" | istruzione esplicita dell'operatore verso il tool operativo | no | **sì** |
| Planner senza Knowledge (H3), piano `[system.events]`; `runtime.knowledge_read` non può essere `expectedTool` | nessuno step "consulta la procedura" | no | **sì** (il piano non ha un momento per leggere) |
| Step authority: vista = esattamente `system.events` (`StepToolViewFor :4936`); la funzione Knowledge è aggiunta **accanto** | lettura sempre proponibile negli step | no | — |
| Prompt dello step operativo: "*Invoke the operational tool at most once… Propose the concrete tool call for the next unfinished step*" (`:5094`, `:5123`) | imperativo verso `system.events` | no | **sì** |
| Prompt dello step finale: `PlanCompleteInstructions` "*Give your final answer now from the evidence already collected*" (`:173`) | imperativo a concludere proprio quando la procedura servirebbe | no | **sì** |
| `KnowledgeControlInstruction` "*may be used, only if useful*" (`:222`) e parte C "*may be called, only if useful*" | lettura opzionale per contratto (ADR-0057 A.2.6) | no | debolmente |
| First-call-wins (ADR-0038): una lettura non completa lo step; un'altra chiamata nella stessa risposta non viene eseguita | costa una chiamata modello in più | no | sì (costo) |
| Una chiamata Knowledge **dopo** un evidence read nella stessa risposta → `NotHandled`, non auditata (`KnowledgeReadBridge.cs:410-419`) | lettura persa senza traccia nell'audit | sì, in quel caso | — |
| MAF: `AgentSkillsProvider` usato come libreria; le sue funzioni non sono mai dichiarate; nessun advertising nativo (ADR-0054) | in FIX12 il catalogo MAF era calcolato ma mai inviato | in FIX12 sì (non presentato) | — |
| Proiezione MAF/broker (`MafContextProjector`, `ContextGate`) | a HEAD il turno D arriva al provider; verificato dopo la proiezione e sul filo | no | — |

Conclusione: un piano con `system.events` **non impedisce** la lettura, ma il prompt la mette in coda. A FIX12 la
scoraggiava in modo decisivo, perché la funzione non era descritta e il prompt negava che esistesse. A HEAD la scoraggia
ancora, con imperativi espliciti nei due momenti decisivi.

## 5. La lettura era selezionabile in FIX12?

| Caso | FIX12 (`44696aa`) | HEAD (offline) |
|---|---|---|
| Indice non pubblicato | **Sì**: il concetto non esisteva (ADR-0057 è successiva) | No: `Published` |
| Indice pubblicato ma non presentato | Il catalogo MAF era calcolato (passo 5) ma restituito solo da `discover`, quindi mai presentato | No: `Presented` in tutte le richieste di step |
| Funzione non offerta | No: offerta (passo 7, delta di token costante) | No: offerta |
| Funzione offerta ma non selezionata | **Sì: è l'esito** (0 `KnowledgeReadAuditEvent`) | Riprodotto da script (`OfferedNotSelected`) |
| Lettura richiesta ma rifiutata | No | Riprodotto (`ReadRefused`, ref `K9.R1`) |
| Lettura riuscita | No (`knowledgeUsed:false`) | Riprodotto (`ReadSucceeded`, ref `K1.R1`) |

**Caso residuo non escludibile in FIX12:** una chiamata Knowledge in seconda posizione dopo `runtime.evidence_read`
nella stessa risposta non sarebbe stata auditata. Nel 2026-10-14 non esisteva la telemetria di routing che avrebbe
permesso di escluderla. Ora la si distingue come `SelectedNotServed`.

## 6. Descrizione e rappresentazione della Knowledge

La voce che il modello riceve a HEAD nel turno D, nella forma di ADR-0057 §B. I test offline verificano nella richiesta
reale il nome della skill, la descrizione e la chiamata `read_resource K1.R1`.

```json
{"ref":"K1","pack":"acme.windows-event-runbooks@1.0.0","publisher":"acme","kind":"Knowledge","skill":"windows-event-triage","description":"Procedure di interpretazione delle evidenze diagnostiche nei log Windows","body":{"chars":205,"call":{"operation":"load","ref":"K1"}},"resources":[{"ref":"K1.R1","path":"references/interpretation.md","chars":3776,"call":{"operation":"read_resource","ref":"K1.R1"}}],"more":0}
```

- **Selezione senza hardcoding: possibile.** Il goal condivide con nome e descrizione i token `windows`, `evidenze` e
  `log` (score > 0). L'unica voce autorizzata è sempre elencata (il selettore non esclude mai). La chiamata pronta
  `read_resource K1.R1` porta alla procedura in **una** lettura, che rientra nel budget (3776 ≤ 8000 caratteri).
  Il test C verifica che né il goal né l'addendum nominino la skill, il pack, il ref o la funzione.
- **Limiti di rappresentazione** (da correggere solo in una futura versione del pack, mai in questa misura):
  1. La descrizione dice *di cosa* tratta il pack, non *perché* serve oltre alle conoscenze del modello. Un modello che
     crede di saper già leggere i log Windows non vede un guadagno.
  2. Il corpo `SKILL.md` (205 caratteri, `load K1`) è solo un rimando alla risorsa. Un modello che sceglie `load` spende
     una lettura per niente; l'indice mitiga il problema esponendo direttamente `K1.R1`.
  3. La descrizione è in italiano mentre i prompt runtime sono in inglese. È coerente con il goal e non è un difetto
     dimostrato.

## 7. Difetti architetturali

| # | Difetto | Stato |
|---|---|---|
| D1 | In FIX12 il prompt contraddiceva la lista dei tool ("single native tool" / "no native tool" con la Knowledge offerta) | corretto in `d3da032` (KNOW-FIX-01) |
| D2 | Catalogo raggiungibile solo con `discover`, tre round-trip per leggere | corretto da ADR-0057 M1 (indice + lettura per ref) |
| D3 | `audit.jsonl` senza tipo di evento | corretto in `d3da032` |
| D4 | Nessuna osservazione per richiesta di cosa fosse presentato/offerto/proposto | **corretto qui** (T7 parziale, solo harness) |
| D5 | Lettura mai sollecitata nel punto di bisogno: lo step finale ordina di rispondere subito | **aperto**, richiede una decisione ADR |
| D6 | Una chiamata Knowledge dopo un evidence read nella stessa risposta viene persa senza audit Knowledge | aperto: ora visibile (`SelectedNotServed` e `toolRouting.selected`), non corretto |
| D7 | La risposta finale è redatta nell'export, quindi la qualità diagnostica non è misurabile dagli artefatti | **aperto**, decisione dell'operatore (canale locale protetto) |
| D8 | FIX12 è n = 1, temperatura 1, seed casuale: nessuna inferenza causale possibile | metodologico: n ≥ 3 per braccio (ADR-0057 H.4) |
| D9 | Il planner non conosce la Knowledge (H3) | decisione deliberata; da riconsiderare solo in M4 con evidenza |

## 8. Modifiche minime consigliate

**Fatte in questo pacchetto (solo test/harness, nessun codice runtime):**

- `tests/bOps.AgentFramework.Tests/BenchmarkKnowledgeDiscovery.cs`: per ogni richiesta registra, dopo la proiezione
  MAF/broker:
  - fase (`planning`/`step`);
  - funzione offerta;
  - numero di turni indice e posizione prima del goal;
  - `indexHash`;
  - presenza della parte C;
  - proposte Knowledge (operazione chiusa, ref verificato nella forma, `byName`).

  Produce anche le classificazioni `indexStage` (`NotBound`, `NotPublished`, `Suppressed`, `PublishedNotPresented`,
  `PresentedPartially`, `Presented`) e `readStage` (`NotBound`, `FunctionNotOffered`, `OfferedNotSelected`,
  `SelectedNotServed`, `ReadRefused`, `ReadSucceeded`). Nessun prompt, descrizione, goal o argomento libero viene
  esportato; il nome della skill compare solo se supera U7, come prevede T7.
- `KnowledgeBenchmarkExport.cs`: aggiunti `knowledgeDiscovery` e `goalSha256` in `result.json`; ammessa la condizione C.
- Condizione C: `KnowledgeBenchmark.GoalFor("C")` = goal di B + `docs/benchmarks/know-bench-01a/scenario-c-addendum.txt`.
  L'addendum non nomina skill, pack, ref né la funzione. Nuovo entry point `KnowledgeBenchmarkLiveTests.WithKnowledgeAndExplicitReadRequest`
  (`BOPS_RUN_KNOW_BENCH_01D=C`). La capture integrata resta A/B (`TrustedEventsCapture` rifiuta C).
- `KnowledgeDiscoveryInvestigationTests.cs`: 8 casi offline, elencati in §10.

**Proposte, non implementate** (richiedono una decisione):

1. **D5**: prima del live, decidere se misurare una variante della parte C o di `PlanCompleteInstructions` che, solo con
   un indice `Published` e letture residue, aggiunga una frase neutra del runtime (per esempio: "*reference data listed in
   the knowledge index may be read before answering if it applies to this evidence*"). Va trattata come condizione
   separata e richiede un emendamento di ADR-0057 §C.5, perché la parte C è a varianti fisse. Non va fusa con B.
2. **D7**: un canale locale protetto per la risposta finale, fuori da `result.json`, per la revisione cieca con la
   rubrica di `know-bench-01a/README.md`. Senza questo canale la qualità delle conclusioni resta non misurata.
3. **D6**: valutare se il bridge debba servire una chiamata Knowledge anche quando non è la prima chiamata runtime della
   risposta. Tocca ADR-0038 (first-call-wins), quindi va deciso in un ADR.
4. Una futura versione `1.0.1` del pack con una descrizione orientata al valore. È un esperimento separato con un nuovo pin.

## 9. Piano di benchmark A/B/C

| | A | B | C |
|---|---|---|---|
| Knowledge | assente (nessun host, provider rimosso) | pack 1.0.0, grant esatto, indice M1 | come B |
| Goal | `scenario.txt` | `scenario.txt` (stesso hash di A) | `scenario.txt` + addendum (hash diverso, registrato in `goalSha256`) |
| Domanda | baseline | il modello sceglie da solo di leggere? | se richiesto, il modello legge e usa la procedura tramite il percorso governato? |
| Confronto | — | B vs A: effetto della disponibilità | C vs B: effetto della richiesta esplicita. **Esperimento separato, mai aggregato con B.** |

Costanti per tutti i bracci:

- Qwen3.6-35B, baseline `Qwen8192`, reasoning attivo, stesso metadata pinnato;
- stesso corpus congelato (un'unica capture 01D salvata su file e rigiocata in replay; FIX12 invece usava una capture in
  memoria, quindi non riutilizzabile);
- 20 HTTP, 1200 s per condizione, Knowledge 4/4/8000;
- commit pulito e uguale a `bopsCommit`.

- **Ripetizioni:** n ≥ 3 per braccio, ordine ruotato in quadrato latino (ABC, BCA, CAB) per distribuire gli effetti di
  warm cache e carico.
- **Prerequisiti** (ADR-0057 H.4):
  - **B2 verificato**: il server accetta due messaggi `user` consecutivi (D, goal);
  - in B e C `indexStage = Presented` e `planningRequestsWithKnowledge = 0`;
  - in A `NotBound`.

  Se un prerequisito fallisce, la coppia è invalida e va esclusa, non corretta.
- **Metriche per run:**
  - `status` e `terminalCause`;
  - `knowledgeDiscovery.readStage` e `indexStage`;
  - letture riuscite e ref letti rispetto ai ref elencati;
  - uso di `discover` e proposte Knowledge;
  - `llmCalls`, `httpRequestsSent` e `modelRetries`;
  - `reportedPromptTokens` e `reportedCompletionTokens` (Δ > 20% da giustificare);
  - `totalExecutionMs`;
  - qualità della diagnosi con la rubrica 0..2 di `know-bench-01a/README.md`, revisore cieco. Serve D7: senza il canale
    protetto la qualità viene dichiarata "non misurata".
- **Criterio:** la discovery è risolta se in B la lettura è selezionabile con informazione completa (prerequisiti
  verdi). Il tasso di lettura in B è un esito da riportare, non da forzare. C fornisce il limite superiore di
  accessibilità e l'effetto sulla qualità quando la procedura viene letta.

## 10. Verifica eseguita (offline)

- `dotnet build tests/bOps.AgentFramework.Tests --no-restore`: 0 avvisi, 0 errori.
- `KnowledgeDiscoveryInvestigationTests` (8 casi): tutti superati.
  - `B_EveryStepRequestPresentsTheIndex…OfferedNotSelected`
  - `B_TheListedRefAloneReadsTheProcedure…`
  - `B_AnUnlistedRefIsRefused…ReadRefused`
  - `A_HasNeitherIndexNorFunction…NotBound`
  - `C_DiffersFromBOnlyInTheOperatorGoal…`
  - `FIX12_ArtifactsMapTo…`
  - `OverTheRealAdapter…` (nativo e fallback)
- Suite `bOps.AgentFramework.Tests` completa: 363 superati, 6 saltati (entry point live), 0 falliti.
  `KnowledgeBenchmarkCaptureInputTests` è stato adattato alla nuova firma `ExecuteAsync(string condition, …)`.
- `bOps.Architecture.Tests`: 202 superati.
- Non eseguiti: inferenza live, capture Windows, verifica B2, WSL/Linux.

## 11. Comandi PowerShell per l'esecuzione live (NON eseguiti; richiedono autorizzazione dell'operatore)

> **Aggiornato dopo l'audit su `784ac9c`.** Il replay da file non può più essere `Captured`: il passo 3 ora produce un corpus
> minimizzato (provider e dettagli in allowlist), lo giudica con il quality gate e ne registra l'approvazione esplicita
> dell'operatore (`OperatorAttested`); i passi 5-6 richiedono anche `BOPS_EVENTS_CORPUS_ATTESTATION_SHA256`. Dettagli, limiti
> e modello di fiducia in [know-bench-corpus-provenance.md](know-bench-corpus-provenance.md). **D7 (diagnosi protetta e revisione cieca):**
> ogni run ora archivia la risposta dell'agente in un archivio locale cifrato e ne registra in `result.json` solo id opaco, hash e stato;
> il passo 5 richiede perciò l'archivio autorizzato e il passo 6 la ripetizione. Protezione dei dati, procedura di revisione cieca e limiti
> in [know-bench-diagnosis-review.md](know-bench-diagnosis-review.md). Restano aperti, fuori da questa modifica,
> l'attestazione dei parametri del server e la definizione di C sul planner.

Prima di tutto l'operatore fa il commit di queste modifiche: l'harness rifiuta un checkout tracciato sporco e un HEAD
diverso da `bopsCommit`. I comandi vanno lanciati da una sessione PowerShell 7 dedicata, dalla root del worktree.

```powershell
# 0. Contesto comune (percorso corto: limiti di path di Windows)
Set-Location C:\source\repos\babbubba\bOps-knowbench01a
if (git status --porcelain --untracked-files=no) { throw 'Checkout tracciato non pulito: fare prima il commit.' }
$Commit = git rev-parse HEAD
$Root   = 'C:\bops-bench\KNOW-DISC-INV01'
$Corpus = Join-Path $Root '.bops-event-corpus\know-disc-inv01.bops-events.snapshot.json'
New-Item -ItemType Directory -Force (Split-Path $Corpus) | Out-Null
if (Test-Path -LiteralPath $Corpus) { throw 'Corpus già presente: usare un nuovo nome file.' }
$Proj = 'tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj'
dotnet build $Proj --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build fallita.' }
```

```powershell
# 1. Gate offline sul commit da misurare
dotnet test $Proj --no-build --no-restore --filter 'FullyQualifiedName~KnowledgeDiscoveryInvestigationTests|FullyQualifiedName~KnowledgeIndexRunnerTests|FullyQualifiedName~KnowledgeBenchmarkIsolationTests'
if ($LASTEXITCODE -ne 0) { throw 'Gate offline fallito: non procedere.' }
```

```powershell
# 2. B2 (UNA inferenza live): il server/template accetta due messaggi user consecutivi?
#    $BaseUrl e $Headers vanno presi dalla stessa configurazione usata per FIX12 (BOPS_KNOW_BENCH_CONFIG/Vault); non stampare la chiave.
$body = @{ model = 'Qwen3.6-35B'; max_tokens = 256; messages = @(
    @{ role = 'system'; content = 'Answer with OK.' },
    @{ role = 'user';   content = '[knowledge-index v1 probe] reference data [/knowledge-index]' },
    @{ role = 'user';   content = 'Reply OK.' }) } | ConvertTo-Json -Depth 5
$resp = Invoke-WebRequest -Method Post -Uri "$BaseUrl/chat/completions" -Headers $Headers -ContentType 'application/json' -Body $body -SkipHttpErrorCheck
"B2 HTTP $($resp.StatusCode)"   # 200 senza errore di template = B2 superato; altrimenti fermarsi (ADR-0057 §L.1)
```

```powershell
# 3. Capture UNICA del corpus congelato (profilo 01D, argomenti dello scenario), quality gate, approvazione dell'operatore, replay di verifica.
#    Modello di fiducia, minimizzazione e quality gate: docs/benchmarks/know-bench-corpus-provenance.md
#    Un file salvato non è mai "Captured": dopo la revisione dell'operatore diventa OperatorAttested (attestazione operativa, non prova crittografica).
$env:BOPS_EVENTS_CORPUS_MODE = 'capture'; $env:BOPS_EVENTS_CORPUS_PROFILE = '01D'
$env:BOPS_EVENTS_CORPUS_PATH = $Corpus;   $env:BOPS_EVENTS_CORPUS_ACCEPT_MINIMIZATION = '1'
$Sidecar = $Corpus -replace '\.bops-events\.snapshot\.json$', '.bops-events.attestation.json'
$Test = 'FullyQualifiedName=bOps.AgentFramework.Tests.KnowledgeEventsCorpusTests.SyntheticCorpus_TwoGovernedReplaysAreIdentical'
try {
    # 3a. Capture + quality gate (corpus vuoto, troncato, incompleto o senza copertura System/Application => fallisce con i codici).
    dotnet test $Proj --no-build --no-restore --filter $Test
    if ($LASTEXITCODE -ne 0) { throw 'Capture o quality gate falliti: ispezionare e usare un nuovo nome file.' }
    $CorpusSha = (Get-Content -LiteralPath $Corpus -Raw | ConvertFrom-Json).sha256
    # 3b. Dopo aver ISPEZIONATO il file minimizzato, approvazione esplicita: l'operatore ripete lo stesso hash.
    $env:BOPS_EVENTS_CORPUS_MODE = 'approve'; $env:BOPS_EVENTS_CORPUS_SHA256 = $CorpusSha; $env:BOPS_EVENTS_CORPUS_APPROVE_SHA256 = $CorpusSha
    dotnet test $Proj --no-build --no-restore --filter $Test
    if ($LASTEXITCODE -ne 0) { throw 'Approvazione rifiutata.' }
    $AttestationSha = (Get-Content -LiteralPath $Sidecar -Raw | ConvertFrom-Json).sha256
    # 3c. Replay attestato di verifica (nessun modello).
    $env:BOPS_EVENTS_CORPUS_MODE = 'replay'; $env:BOPS_EVENTS_CORPUS_ATTESTATION_SHA256 = $AttestationSha
    dotnet test $Proj --no-build --no-restore --filter $Test
    if ($LASTEXITCODE -ne 0) { throw 'Replay di verifica fallito.' }
} finally { Remove-Item Env:BOPS_EVENTS_CORPUS_MODE, Env:BOPS_EVENTS_CORPUS_PROFILE, Env:BOPS_EVENTS_CORPUS_APPROVE_SHA256 -ErrorAction SilentlyContinue }
attrib +R $Corpus; attrib +R $Sidecar
"Corpus SHA-256: $CorpusSha"            # conservare ENTRAMBI gli hash fuori dai file
"Attestation SHA-256: $AttestationSha"
```

```powershell
# 4. Metadata condiviso: copia di FIX12 con commit e corpus aggiornati, DOPO aver riverificato il server
$meta = Get-Content "$env:LOCALAPPDATA\bOps\benchmarks\KNOW-BENCH-FIX12-20261014-105800\execution-metadata.json" -Raw | ConvertFrom-Json
$meta.bopsCommit = $Commit; $meta.corpusSha256 = $CorpusSha
$meta.evidence = "Riverifica server $(Get-Date -Format yyyy-MM-dd) per KNOW-DISC-INV01"
$MetaPath = Join-Path $Root 'execution-metadata.json'
$meta | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $MetaPath -Encoding utf8NoBOM
attrib +R $MetaPath
$MetaSha = (Get-FileHash -LiteralPath $MetaPath -Algorithm SHA256).Hash.ToLowerInvariant()
```

```powershell
# 5. Ambiente comune ai tre bracci
$env:BOPS_EVENTS_CORPUS_PATH = $Corpus; $env:BOPS_EVENTS_CORPUS_SHA256 = $CorpusSha
$env:BOPS_EVENTS_CORPUS_ATTESTATION_SHA256 = $AttestationSha   # senza attestazione verificata l'harness si ferma prima di contattare il server
$env:BOPS_EVENTS_CORPUS_ACCEPT_MINIMIZATION = '1'
$env:BOPS_KNOW_BENCH_METADATA = $MetaPath; $env:BOPS_KNOW_BENCH_METADATA_SHA256 = $MetaSha
$env:BOPS_KNOW_BENCH_COMMIT = $Commit
# D7: archivio locale protetto delle diagnosi. Percorso NUOVO, fuori dal checkout e fuori da $Root\results; l'ACCEPT è l'autorizzazione
# esplicita della scrittura locale. Senza, i run live si fermano prima del primo ottetto di rete (la qualità non sarebbe misurabile).
$Archive = 'C:\bops-bench\KNOW-DISC-INV01-private\diagnoses'
$env:BOPS_KNOW_BENCH_DIAGNOSIS_ARCHIVE = $Archive; $env:BOPS_KNOW_BENCH_DIAGNOSIS_ARCHIVE_ACCEPT = '1'
$env:BOPS_KNOW_BENCH_SETTINGS_VERIFIED = '1'     # solo dopo la verifica manuale del server
# $env:BOPS_KNOW_BENCH_CONFIG = '<stesso valore usato per FIX12>'
$ManifestSha = (Get-FileHash 'docs/benchmarks/know-bench-01d/signed-pack/acme.windows-event-runbooks/1.0.0/bops-pack.json' -Algorithm SHA256).Hash.ToLowerInvariant()
$Grant = 'tenant-a/acme.windows-event-runbooks@1.0.0:Knowledge'

function Invoke-Arm([string]$Condition, [string]$Results) {
    $test = @{ A = 'WithoutKnowledge'; B = 'WithKnowledge'; C = 'WithKnowledgeAndExplicitReadRequest' }[$Condition]
    $env:BOPS_RUN_KNOW_BENCH_01D = $Condition; $env:BOPS_KNOW_BENCH_RESULTS = $Results
    if ($Condition -eq 'A') { Remove-Item Env:BOPS_KNOW_BENCH_MANIFEST_SHA256, Env:BOPS_KNOW_BENCH_GRANT -ErrorAction SilentlyContinue }
    else { $env:BOPS_KNOW_BENCH_MANIFEST_SHA256 = $ManifestSha; $env:BOPS_KNOW_BENCH_GRANT = $Grant }
    try {
        dotnet test $Proj --no-build --no-restore --filter "FullyQualifiedName=bOps.AgentFramework.Tests.KnowledgeBenchmarkLiveTests.$test"
        if ($LASTEXITCODE -ne 0) { Write-Warning "$Condition in $Results terminata con exit $LASTEXITCODE: controllare result.json" }
    } finally { Remove-Item Env:BOPS_RUN_KNOW_BENCH_01D, Env:BOPS_KNOW_BENCH_MANIFEST_SHA256, Env:BOPS_KNOW_BENCH_GRANT -ErrorAction SilentlyContinue }
}
```

```powershell
# 6. Tre ripetizioni, ordine a quadrato latino (9 run; ognuno <= 20 HTTP e <= 1200 s)
$orders = @(@('A','B','C'), @('B','C','A'), @('C','A','B'))
for ($rep = 1; $rep -le 3; $rep++) {
    $dir = Join-Path $Root "results\rep$rep"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $env:BOPS_KNOW_BENCH_REPETITION = "$rep"      # D7: finisce solo nella mappa privata dell'archivio e in result.json
    foreach ($c in $orders[$rep - 1]) { Invoke-Arm $c $dir }
}
```

```powershell
# 7. Riepilogo (prerequisiti + metriche); nessun testo libero negli artefatti
Get-ChildItem (Join-Path $Root 'results') -Recurse -Filter result.json | ForEach-Object {
    $r = Get-Content $_.FullName -Raw | ConvertFrom-Json
    $d = $r.knowledgeDiscovery
    [pscustomobject]@{
        Rep = $_.Directory.Parent.Name; Arm = $r.condition; Status = $r.status; Cause = $r.terminalCause
        Index = $d.indexStage; Read = $d.readStage; PlanningWithK = $d.planningRequestsWithKnowledge
        Proposals = $d.knowledgeProposals; ReadsOk = @($d.reads | Where-Object resultCode -eq 'Success').Count
        RefsRead = (@($d.reads | Where-Object resultCode -eq 'Success' | ForEach-Object ref) -join ',')
        Http = $r.httpRequestsSent; Retries = $r.modelRetries
        PromptTok = $r.reportedPromptTokens; ComplTok = $r.reportedCompletionTokens; Ms = $r.totalExecutionMs
        Goal = if ($r.goalSha256) { $r.goalSha256.Substring(0, 12) } else { $null }
        Diagnosis = $r.diagnosis.availability; Archive = $r.diagnosis.archiveStatus; Reviewable = $r.diagnosis.qualityMeasurable
    }
} | Sort-Object Arm, Rep | Format-Table -AutoSize
# Validità: A -> Index/Read = NotBound; B e C -> Index = Presented e PlanningWithK = 0; Goal uguale fra A e B, diverso in C.
# Qualità: un run con Reviewable = False NON ha una qualità misurata (non è zero): vedi know-bench-diagnosis-review.md §7.
```

```powershell
# 7b. Revisione cieca (dopo i 9 run): pacchetto per il revisore -> compilazione -> ricongiunzione. Procedura completa in
#     know-bench-diagnosis-review.md §7; il revisore riceve SOLO la directory del pacchetto.
.\scripts\Invoke-KnowBenchReview.ps1 -Action ExportPackage -ArchiveRoot $Archive -AcceptLocalArchiveAccess `
    -PackageDirectory 'C:\bops-bench\KNOW-DISC-INV01-review\package' -CorpusPath $Corpus -CorpusSha256 $CorpusSha -AttestationSha256 $AttestationSha
```

```powershell
# 8. Pulizia dell'ambiente
Remove-Item Env:BOPS_EVENTS_CORPUS_*, Env:BOPS_KNOW_BENCH_* -ErrorAction SilentlyContinue
```
