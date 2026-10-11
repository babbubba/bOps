# BENCH-00 — Raccolta automatica completa dei test live

**Status:** IMPLEMENTATO su `feat/bench-00-capture`, basato sul branch `fix/af-05h-live-regression-hardening` (AF-05H H2+H4). **Repo:** bOps OSS. **No subagent.**

## Sequenza aggiornata

AF-05H H2+H4 è già presente nel commit `07e422a4c9c65bd617cafaf209d2352657f1c130`. BENCH-00 viene sviluppato su un branch dedicato partendo da quel commit; non si sviluppa sul branch AF-05G e non precede più il fix AF-05H.

Lo stesso harness seleziona il runtime da acquisire con `-TargetRepoRoot`, indipendentemente dal percorso dello script. I due riferimenti runtime sono:

- baseline AF-05G `97507aa7a914cdabdfafbd42e382c0f1dfd399da`;
- AF-05H H2+H4 `07e422a4c9c65bd617cafaf209d2352657f1c130`.

Il validatore accetta il riferimento diretto o un commit discendente senza differenze nella sorgente runtime rispetto al riferimento più recente. Questo consente di usare anche il branch BENCH-00 dopo i commit dello strumento, che non modificano il runtime.

Eseguire prima la baseline e poi AF-05H usando due worktree già disponibili:

```powershell
.\scripts\windows\run-live-benchmark.ps1 -TargetRepoRoot "C:\source\repos\babbubba\bOps-AF05G"
.\scripts\windows\run-live-benchmark.ps1 -TargetRepoRoot "C:\source\repos\babbubba\bOps"
```

Il primo percorso deve puntare al worktree AF-05G con l’HEAD indicato; il secondo al worktree AF-05H o al branch BENCH-00 discendente senza cambi runtime. La configurazione Development usata per entrambi deriva invece da `-ConfigSourceRoot` (default: repository che contiene lo script): viene acquisita una volta in uno snapshot privato isolato, distinto dal worktree runtime, e non viene modificata. Per confronti ripetibili passare esplicitamente lo stesso snapshot con `-ConfigSnapshotPath` a entrambe le esecuzioni; non viene riacquisito né sovrascritto silenziosamente. Gli hash e la descrizione priva di segreti della configurazione effettiva sono nel manifest; `vault.dat` e credenziali non entrano nello ZIP. Modifiche locali a codice runtime nel target fanno fallire la selezione.

## Finalità

Implementare un benchmark **esclusivamente di acquisizione**, senza valutazioni, classificazioni di qualità, statistiche, diagnosi, riassunti, redazioni o troncamenti delle evidenze. Il comando PowerShell richiede UAC, prepara un host bOps con le impostazioni effettive della configurazione Development, avvia i task richiesti (`-RunsPerScenario`, da 1 a 3; default 3), acquisisce integralmente i record persistiti dei task e i relativi audit, e produce **un unico ZIP privato e autosufficiente**, etichettato come materiale potenzialmente sensibile, da caricare successivamente nel progetto ChatGPT per l'analisi. Non includere i record di altri task né l'intero DB storico.

## Scenari identici ai tre test originali

**B01**, storico `34579793-f2b5-4733-918a-0974b6381bcd`:

> Controlla lo stato attuale di CPU e memoria di questo PC e dimmi se vedi anomalie. Fai solo i controlli necessari e dammi una sintesi.

**B02**, storico `3f5b57ac-95d4-45ae-9cea-ba5aee69890c`:

> Diagnostica questo PC per capire se ci sono evidenze di instabilità, freeze o riavvii anomali. Controlla le informazioni di sistema, la stabilità negli ultimi 180 giorni, i crash negli ultimi 180 giorni, gli eventi di sistema rilevanti e poi completa comunque l'analisi controllando CPU, memoria e salute dello storage. Se qualche fonte è parziale, troncata o non disponibile dichiaralo esplicitamente. Se serve approfondire evidenza già raccolta usa l'evidenza persistita disponibile invece di ripetere inutilmente gli stessi controlli. Alla fine distingui chiaramente fatti osservati, ipotesi e limitazioni dell'analisi.

**B03**, storico `4888fea6-85f2-4cd9-8364-ed36eeb81862`:

> trova la causa dei continui crash che ha questo computer

Eseguire `-RunsPerScenario` round seriali, ciascuno nell'ordine `B01, B02, B03`; il default 3 produce `B01-R1, B02-R1, B03-R1, ... B03-R3` con nove ID distinti, mentre 1 produce esattamente B01, B02, B03. Manifest e roster registrano il valore richiesto e il totale task. Non alterare il testo dei prompt, temperature, provider, modello, selezione fallback o policy senza registrare differenze effettive dalla configurazione di riferimento. N=3 consente confronti esplorativi della variabilità in analisi successive; non è una prova statistica di determinismo.

## UX/launcher

Comando predefinito dalla root del branch BENCH-00; per selezionare il runtime baseline passare `-TargetRepoRoot`:

```powershell
.\scripts\windows\run-live-benchmark.ps1
```

- Rilevare elevazione e riavviare una sola volta tramite UAC (`Start-Process -Verb RunAs`), preservando cwd e parametri con quoting sicuro. Non richiedere all'operatore di digitare il comando di elevazione.
- `-PreflightOnly` avvia l'API isolata e verifica autenticazione, provider effettivo, credenziali e provenienza della configurazione, poi termina senza creare task né chiamare il modello. Esempio: `.\scripts\windows\run-live-benchmark.ps1 -TargetRepoRoot "C:\source\repos\babbubba\bOps-AF05G" -PreflightOnly`.
- Python già installato è una scelta consentita, preferibile se rende più semplici HTTP, SQLite WAL, esportazione dati originali e ZIP. Usare prevalentemente standard library; lo script PowerShell può essere il launcher minimo. Non servono UI, Angular, Python packages non indispensabili o `sqlite3.exe`.
- Default `API-only`: avviare direttamente la vera `bOps.Api` in ambiente `Development`, con URL loopback isolato. Tre test Windows read-only non necessitano del frontend, SearXNG, container Linux o Docker.
- Il launcher avvia una **API benchmark dedicata** su porta loopback selezionata dinamicamente e isola i database task/audit/sessioni, messaggi di sistema, delegazioni e inventory delle probe. Riutilizza lo snapshot Development privato da `-ConfigSourceRoot` per settings, vault e provider; sovrascrive per quel solo processo la policy con `Read=Automatic` e tutti gli altri livelli `Forbidden`, così nessuna mutazione o approvazione può essere eseguita automaticamente. Path/hash della policy configurata e override sono nella provenance. Non contatta né spegne API già in esecuzione e non avvia Aspire/Docker perché gli scenari sono test Windows API-only.
- UAC non elimina il bisogno di setup iniziale del provider o del vault: credenziali non accessibili nella shell elevata producono un errore prima dei task. Non vengono esportate chiavi.
- Credenziali API, master key e provider key: mai in argomenti di processo, ZIP, log, codice o commit; riutilizzare fonti configurate in modo sicuro senza leggere/esportare vault in chiaro. Se il segreto non è accessibile nella shell elevata, rilevare blocker prima dei test; l'installazione iniziale può richiedere setup una tantum.
- Non sono permessi auto-approval, remediation, resume/reconciliation automatica o mutazioni di sistema. Per gli stessi prompt originali imporre una policy effettivamente read-only sul benchmark host; fallire closed se non è dimostrabile e registrare l'eventuale scostamento di ambiente dalla baseline storica. Non inserire 'read-only' nel goal (cambierebbe la prova).
- Progress in console, bounded wait/retry e cleanup solo dei child process creati dallo script. Conservare risultati parziali in caso di errore. Nessun caricamento automatico dello ZIP.

## Interazione API

POST `http://localhost:<port>/api/agents/tasks` con body `{"goal":"..."}` e chiave idempotenza **unica per tentativo**, mantenuta identica sui retry della stessa intenzione. Risposta 202 task ID. GET `/api/agents/tasks/{id}` fino a stato terminale; gestire 503 busy, 404 temporaneo dopo 202, network failure, timeout, task `Running` ma non più `executing`. Non iniziare il task successivo mentre uno precedente esegue. Solo nove task propri e nuovi.

## Raccolta integrale, senza trasformazioni

- Aprire SQLite in lettura `sqlite3` con gestione WAL corretta e snapshot online consistente (API `backup`, se appropriata), senza checkpoint, `VACUUM`, migrazione o scrittura. Identificare DB verificando presenza del task ID effettivamente emesso dall'API, non scegliendo il primo `tasks.db` trovato.
- Esportare **l'intera riga `tasks` originale** di ogni ID e `state_json` come testo originale non troncato, inclusi `RequestJson` e `ResponseJson` di **tutte** le ModelCall, plans, steps, tool input/output, Observations, EvidenceRead, grounding, limitations, replan, disclosure, final e token. Conservare hash SHA-256 degli esatti byte esportati e metadati di colonna/tipo necessari a ricostruire il record. La response API UI è parziale e non sostituisce il TaskState persistito.
- Dal file audit JSONL importare **tutte e soltanto** le righe complete, originali, degli eventi legati ai nove TaskId, mantenendo il wrapper `Seq`, `PrevHash`, `Hash`, `EventJson`, sequenza e byte delle linee. Gli spazi della catena globale dovuti a eventi di altri task devono essere indicati: la selezione non è una catena contigua verificabile da sola.
- Includere la risposta API terminale originale e metadati di startup/host (Git SHA, branch, dirty status, API host/config, provider/model richiesto ed effettivo per call come già persistito, varianti/override, timestamp, versioni harness, timeout). Solo provenance non sensibile aggiunta dallo script; i payload TaskState/audit dei nove task restano **integrali**, anche qualora contengano dati sensibili.
- Se bOps ha **già** applicato un proprio limite di persistenza, il collector non può recuperare i byte mai salvati: riportare limitazione della sorgente, non ricostruire dati artificiali.
- Nessun limite artificiale alla dimensione del ZIP, nessuna allowlist di campi, nessuna redazione dei payload selezionati, nessun riepilogo quantitativo interpretativo, classificazione di errori, anomaly detection o score. Lo strumento conserva esclusivamente fatti di acquisizione: presenti/mancanti/HTTP/status/errore di esportazione. Non correggere o riscrivere la risposta finale.
- ZIP auto-contenuto con `README-SENSITIVE`, `manifest.json`, `scenarios.json`, `run-roster.json`, `startup.json`, original per-task `state_json`, original per-task audit JSONL, original API terminal JSON, `capture-status.json`, SHA-256 file. Path solo relativi all'archivio. Nome stabile `bops-live-benchmark-<UTC>-<sha>.zip`.
- **Avviso esplicito:** lo ZIP potrebbe contenere informazioni riservate del PC, nomi utenti/host/IP, eventi e log grezzi, perfino segreti presenti nei contenuti registrati dal runtime. Il collector non li anonimizza. Applicare ACL locali restrittivi, niente commit/upload automatico, revisione volontaria prima di caricarlo in ChatGPT.

## Test automatici per il collector (non per il modello)

Fixture offline/sintetiche: avvio self-UAC una volta; path con spazi; API già attiva e collisioni; API-only e Aspire opzionale; autenticazione mancante; nove task seriali e idempotenti; 202/404/503/timeouts; partial progress; snapshot WAL live; esatta conservazione `state_json`, Unicode, output enormi e nested requests/responses; audit globale con altri task e preservazione sole linee selezionate; hash roundtrip; nessuna perdita non dichiarata di eventi; nessuna analisi/grading; nessuna chiave aggiunta dal collector; nessuna mutation/approval automatica. Il test offline non chiama provider reali.

## Acceptance gate

1. Implementare BENCH-00 in commit solo tool/tests/docs sul nuovo branch dedicato basato sul branch AF-05H H2+H4, **senza cambiare il runner**.
2. L'operatore esegue il collector con `-TargetRepoRoot` sul worktree AF-05G e ottiene ZIP baseline con nove task e acquisizione integrale o gap dichiarati; non committare evidenza reale.
3. Caricare ZIP nel progetto ChatGPT per analisi esterna.
4. Eseguire quindi lo stesso collector sul worktree AF-05H H2+H4, ottenendo secondo ZIP comparabile; il confronto esterno, CI e merge PR #100 seguono la raccolta.
5. Quindi ARCH-01/02/03 e KNOW-01/02/03, come definito nel piano di convergenza.
