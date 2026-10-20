# KNOW-BENCH-01A: preparazione bloccata prima del live

Branch: `codex/know-bench-01a`, base `daa77c8`. Nessun benchmark, contatto al provider, lettura dei log, test live o suite eseguiti. Questa consegna definisce il confronto e il contenuto del pack; non dichiara un host A/B eseguibile o un pack gia firmato. Nessun codice del runtime o della composizione e stato modificato.

## Tool e autorizzazione verificati nel sorgente

`WindowsSystemToolProvider.GetTools()` include `WindowsSystemEventsTool`. `FirstPartyToolComposition.Create()` seleziona quel provider su Windows e assegna `bops.packages.system.windows`; l'API registra la sequenza nel suo `ToolRegistry`. Il manifest `SystemToolManifests.Events("windows")` dichiara `RiskLevel.Read`, piattaforma Windows e `Requires = []`. Il registry applica comunque disponibilita della piattaforma e abilitazione del package. I diritti Event Log dell'identita corrente determinano quali canali siano leggibili: nessuna elevazione e nessun cambio di gruppi. Nessun requisito di approvazione intrinseco del tool Read; restano autorevoli le verifiche del runtime.

**Nel registry del canary il tool NON e disponibile:** `KnowledgeProcess.ComposeCanary()` registra solo `test.read` e `test.other`. Non e stato interrogato un registry di un processo API in esecuzione; disponibilita effettiva dei canali su questa macchina non verificata.

Schema: tutti gli argomenti opzionali; `mode` raw/aggregate; `windowMinutes` intero 1..10080 (default 60); `windowDays` intero 1..180 solo aggregate e incompatibile con windowMinutes; `minSeverity` critical/error/warning/information/verbose; `source`, `eventId`, `channel`, `text`, `excludeSources` stringhe vincolate; `limit` 1..500; `maxOutputBytes` 4096..65536. Su Windows eventId e un numero testuale 0..65535. Default dei canali: System e Application. I vincoli intercampo sono applicati da `SystemEventsArguments`, quelli Windows dal tool. Nessun parametro fromUtc/toUtc o percorso EVTX.

## Scenario unico e prove identiche

Prompt comune: byte UTF-8 di `scenario.txt`. Argomenti comuni:

```json
{"mode":"raw","windowMinutes":1440,"minSeverity":"warning","limit":500,"maxOutputBytes":65536}
```

Una futura raccolta unica tramite il vero `WindowsSystemEventsTool`, con l'identita del canary, deve congelare l'intero `ToolCallResult` (output, completeness, facts, eventuale errore) e gli argomenti. Nessun evento sintetico e nessuna risposta attesa nel prompt. Salvare hash SHA-256 dei byte dell'output e del prompt, finestra UTC effettiva, data di raccolta, revisione, versione dello schema e stato delle sorgenti. Nessuna evidenza operativa e stata raccolta in questa preparazione; non si assume che siano presenti crash, riavvii o WHEA. Una raccolta indisponibile non e uno scenario diagnostico utile; una raccolta parziale va dichiarata e non puo sostenere conclusioni di assenza.

Le due condizioni devono ricevere lo stesso risultato congelato per gli stessi argomenti. Ogni proposta di argomenti diversa deve essere registrata e rifiutata, senza una nuova lettura variabile. Non iniettare il risultato nel prompt iniziale: deve arrivare come normale risposta tool sotto il runtime. Contare separatamente la raccolta nativa e le invocazioni che servono il risultato congelato: il confronto sarebbe **diagnosi su evidenza reale catturata**, non due raccolte native live. Non normalizzare via i timestamp per fingere identita.

## Isolamento previsto

- A: processo e task nuovi, store/audit/snapshot separati, nessun pack aggiunto alla source, nessun grant o binding, `knowledgeHost: null` tramite l'opzione `withHost: false` esistente. Nessun catalogo, resource, SKILL.md o `runtime.knowledge_read` nel contesto; non usare `Unauthorized(...)`, che configura comunque un pack e produce un fallimento di admission.
- B: stessa identita `ActorA`/tenant, modello, prompt, manifest operativo e risultato tool. Source privata B contenente solo il pack pertinente, provider trusted per tenant-a, grant esatto tenant/pack/version/kind e binding. Admission, snapshot verificato, bridge e audit esistenti; ogni read deve essere autorizzata. Il catalogo B puo descrivere la risorsa; il suo corpo arriva solo attraverso una lettura auditata. Una B senza letture riuscite va marcata come intervento Knowledge non utilizzato, anche se il task termina Completed.
- Riutilizzare AgentRunner, ModelInvocationBroker, MafGovernedTurnBroker, SqliteTaskStore e JsonLinesAuditSink. Riutilizzare il percorso `LocalQwenConfiguration.Read()` e ProviderCredentialResolver/VaultStore del canary, senza modificare Vault o configurazione. Quel helper oggi e privato: renderlo condivisibile SOLO nel progetto test fa parte dell'adattamento minimo. Bloccare il run se modello diverso da Qwen3.6-35B: l'attuale controllo accetta qualunque nome che inizi per Qwen. Registrare stesso profilo/configurazione non segreta e stesso limite di chiamate per A e B; il nome esatto del modello locale non e stato letto ne presunto.
- Non inserire contenuto diagnostico del pack nel goal, nel SystemPrompt, negli argomenti del tool o negli scripted replies. Il manifest gia contiene criteri interpretativi: lasciarlo identico in A/B e registrarlo come conoscenza comune preesistente, evitando di attribuirla al pack B.
- Prima del live verificare offline la serializzazione completa delle ModelRequest: in A nessuna provenienza Knowledge/Skill/Experience, binding, catalogo o testo del pack; in B nessun corpo prima del read riuscito e attribuito. I testi del pack e la rubrica del revisore non entrano nei prompt. L'isolamento qui e progettato, non ancora dimostrato da esecuzione.

## Pack B previsto

Payload redatto sotto `pack-source/windows-event-triage/`; pack id previsto `acme.windows-event-runbooks`, versione `1.0.0`, kind `Knowledge`. Usare il formato implementato `bops-pack.json` + `bops-pack.sig.json`, non il vecchio formato ZIP descritto in `docs/knowledge/knowledge-pack-format.md`. Creare manifest con file/hash/dimensioni e range engine compatibile; firmare esatti byte del manifest RSA-PSS/SHA-256 mediante TestPack/TestPublisher gia usati dal canary, con chiave effimera test-only e trust store pubblico. Non creare un nuovo signer produttivo. Ammettere attraverso KnowledgePackVerifier e KnowledgeAdmissionService; registrare hash e pin. Il payload e disponibile ma **la firma e l'admission non sono state prodotte**: richiedono la composizione B mancante. Nessun log, risposta precalcolata, comando eseguibile o dato della macchina nel pack.

## Metriche e giudizio

| Metrica | Fonte / modalita prevista |
| --- | --- |
| Completed/Failed e causa terminale | TaskState.Status e TerminalReason, confrontati con lo store; distinti dalla diagnosi |
| Correttezza diagnostica | Revisore offline cieco rispetto alla condizione, rubriche sotto; nessun LLM giudice |
| Evidenze e citazioni | Risposta finale confrontata con risultato congelato; lista di claim e tuple timestamp/canale/provider/id, validita del dettaglio e hash evidence |
| Conclusioni non supportate | Lista separata di claim, ragione del mancato supporto e severita; non ricavabile dal solo audit |
| Chiamate LLM e token | Wrapper recording/capped del canary e ModelCallAuditEvent (incluse planning/retry/failure), prompt/completion, TaskState.Accounting; uso assente = null, non zero |
| Tool proposti/eseguiti/riusciti/rifiutati | ModelResponse.ToolCalls, ToolCallAuditEvent.Outcome/Authorization/FailureKind/Completeness; distinguere proposte non dispatchate e Knowledge dalla superficie operativa |
| Tempo complessivo e invocazioni | Stopwatch del run da aggiungere al test host; ModelCallAuditEvent.DurationMs e ToolCallAuditEvent.Duration; raccolta nativa separata dal replay |
| Knowledge autorizzata | KnowledgePackAdmissionAuditEvent e KnowledgeReadAuditEvent, result/operation/resource/hash/tenant/provider; A deve avere zero read riuscite e zero admission |
| Risposta finale | ModelResponse finale confrontata con PlanStep.Observation persistita; salvata in artifact locale separato dall'audit |

Rubrica umana, 0..2 per ciascuna voce: classificazione dei fenomeni osservabili; distinzione causa/indizio; correlazione temporale senza duplicare episodi; citazioni verificabili; rispetto di copertura e incertezza. 0 = errato/assente, 1 = parziale, 2 = corretto rispetto al corpus. Per categorie assenti valutare se evita di inventarle; nessuna causa precostituita. Elencare claim non supportati anche quando il totale e alto. Il completamento tecnico non implica un punteggio diagnostico positivo.

L'attuale `KnowledgeCanaryDiagnostics` consente molte metriche tecniche ma allowlista solo test.read/test.other/runtime.knowledge_read e omette risposta finale e durata totale. Non basta per questo confronto. Conservare audit senza messaggi dei log o contenuto del pack, come oggi; esportare corpus e risposta solo in artifact locali deliberati, non tramite ampliamento indiscriminato dei log. Il TempDirectory del canary viene cancellato in Dispose: l'export degli artifact deve precederlo anche in caso di fallimento.

## Blocker e soluzione minima proposta (non implementata)

1. Aggiungere riferimento al package System.Windows nel SOLO progetto test e comporre il vero tool sotto `bops.packages.system.windows`, invece dei tool dummy. Verificare `GetAvailableManifests()` del registry prima del modello; non cambiare policy/planning/step authority.
2. Le finestre sono calcolate da `clock.GetUtcNow()` e i log sono mutabili. Neppure il costruttore internal con TimeProvider fisso congela i record. Senza una nuova modalita di cattura/replay, non e garantibile identita A/B. Soluzione minima da valutare: adapter test-only, manifest identico, capture una volta dal tool reale e replay dell'intero risultato per argomenti identici attraverso lo stesso dispatch governato. Nessuna nuova API nel tool produttivo, nessun executor alternativo. Questo aggiunge un percorso strutturale di evidenza al canary: lasciato NON IMPLEMENTATO come richiesto dal vincolo sui cambi strutturali. Se si richiede che entrambe le condizioni interroghino nativamente EventLogReader, questa proposta non soddisfa quel requisito e occorre un corpus EVTX immutabile supportato dal tool, oggi assente.
3. Aggiungere due entrypoint opt-in e export locale dei dati sopra, riusando configurazione/Vault e broker, verificare leakage e firma con massimo due test offline mirati. Nessun altro tool di sola lettura risolve automaticamente la mutabilita dei dati; non scegliere una misura variabile alternativa per fingere ripetibilita.

## Due comandi PowerShell per le future esecuzioni manuali

**Contratto futuro, NON operativo in questo commit.** I nomi dei test seguenti sono proposti; non esistono. Il guard evita una falsa riuscita di `dotnet test` con filtro senza test. Dopo l'implementazione approvata, eseguire dalla root del worktree, in sessioni separate. Ogni entrypoint deve riconoscere il proprio switch, utilizzare lo stesso corpus e rifiutare differenze di hash/profilo; nessun comando avvia entrambi. Non usare il vecchio switch BOPS_RUN_LIVE_QWEN_KNOWLEDGE_CANARY, che esegue ancora test.read.

A:
```powershell
if (!(Test-Path -LiteralPath './tests/bOps.AgentFramework.Tests/KnowledgeDiagnosticABLiveTests.cs')) { throw 'KNOW-BENCH-01A bloccato: host diagnostico e capture/replay non implementati.' }; $env:BOPS_RUN_KNOW_BENCH_01A = 'A'; try { dotnet test ./tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj --filter 'FullyQualifiedName=bOps.AgentFramework.Tests.KnowledgeDiagnosticABLiveTests.WithoutKnowledge' --logger 'trx;LogFileName=know-bench-01a-A.trx' --results-directory ./artifacts/know-bench-01a/A; if ($LASTEXITCODE -ne 0) { throw 'Condizione A fallita.' } } finally { Remove-Item Env:BOPS_RUN_KNOW_BENCH_01A -ErrorAction SilentlyContinue }
```

B:
```powershell
if (!(Test-Path -LiteralPath './tests/bOps.AgentFramework.Tests/KnowledgeDiagnosticABLiveTests.cs')) { throw 'KNOW-BENCH-01A bloccato: host diagnostico e capture/replay non implementati.' }; $env:BOPS_RUN_KNOW_BENCH_01A = 'B'; try { dotnet test ./tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj --filter 'FullyQualifiedName=bOps.AgentFramework.Tests.KnowledgeDiagnosticABLiveTests.WithKnowledge' --logger 'trx;LogFileName=know-bench-01a-B.trx' --results-directory ./artifacts/know-bench-01a/B; if ($LASTEXITCODE -ne 0) { throw 'Condizione B fallita.' } } finally { Remove-Item Env:BOPS_RUN_KNOW_BENCH_01A -ErrorAction SilentlyContinue }
```

Verifica di questa consegna: sola revisione statica e `git diff --check`; zero test offline (nessun codice introdotto), zero test live. I due test offline futuri devono coprire (1) identita corpus/argomenti e reale registrazione; (2) firma/admission/provenienza B e assenza totale di contenuti Knowledge in A.
