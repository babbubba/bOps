# BENCH-00 — Raccolta automatica completa delle nove esecuzioni

**Status:** PLAN / NON IMPLEMENTATO. **Priorità:** primo task assoluto, prima di qualunque fix AF-05H e prima del merge PR #100. **Repo:** bOps OSS. **No subagent.**

## Finalità

Implementare un benchmark **esclusivamente di acquisizione**, senza valutazioni, classificazioni di qualità, statistiche, diagnosi, riassunti, redazioni o troncamenti delle evidenze. Un comando PowerShell eseguito sul PC Windows deve richiedere automaticamente UAC, preparare un host bOps con le stesse impostazioni effettive del debug Aspire, avviare nove task, acquisire integralmente i record persistiti dei task e i relativi audit, e produrre **un unico ZIP privato e autosufficiente** da caricare successivamente nel progetto ChatGPT per l'analisi. Non includere i record di altri task né l'intero DB storico.

## Scenari identici ai tre test originali

**B01**, storico `34579793-f2b5-4733-918a-0974b6381bcd`:

> Controlla lo stato attuale di CPU e memoria di questo PC e dimmi se vedi anomalie. Fai solo i controlli necessari e dammi una sintesi.

**B02**, storico `3f5b57ac-95d4-45ae-9cea-ba5aee69890c`:

> Diagnostica questo PC per capire se ci sono evidenze di instabilità, freeze o riavvii anomali. Controlla le informazioni di sistema, la stabilità negli ultimi 180 giorni, i crash negli ultimi 180 giorni, gli eventi di sistema rilevanti e poi completa comunque l'analisi controllando CPU, memoria e salute dello storage. Se qualche fonte è parziale, troncata o non disponibile dichiaralo esplicitamente. Se serve approfondire evidenza già raccolta usa l'evidenza persistita disponibile invece di ripetere inutilmente gli stessi controlli. Alla fine distingui chiaramente fatti osservati, ipotesi e limitazioni dell'analisi.

**B03**, storico `4888fea6-85f2-4cd9-8364-ed36eeb81862`:

> trova la causa dei continui crash che ha questo computer

Eseguire tre round seriali `B01-R1, B02-R1, B03-R1, B01-R2, ... B03-R3`, con nove ID nuovi distinti. Non alterare il testo dei prompt, temperature, provider, modello, selezione fallback o policy senza registrare differenze effettive dalla configurazione di riferimento. N=3 consente confronti esplorativi della variabilità in analisi successive; non è una prova statistica di determinismo.

## UX/launcher

Unico comando dalla root repo:

```powershell
.\scripts\windows\run-live-benchmark.ps1
```

- Rilevare elevazione e riavviare una sola volta tramite UAC (`Start-Process -Verb RunAs`), preservando cwd e parametri con quoting sicuro. Non richiedere all'operatore di digitare il comando di elevazione.
- Python già installato è una scelta consentita, preferibile se rende più semplici HTTP, SQLite WAL, esportazione dati originali e ZIP. Usare prevalentemente standard library; lo script PowerShell può essere il launcher minimo. Non servono UI, Angular, Python packages non indispensabili o `sqlite3.exe`.
- Default `API-only`: avviare direttamente la vera `bOps.Api` usando impostazioni equivalenti a quelle dell'AppHost Aspire (`src/bOps.AppHost/Program.cs`; API debug su `http://localhost:5080`). Tre test Windows read-only non necessitano del frontend, SearXNG, container Linux o Docker. Non avviare Docker per abitudine.
- Auto-detect di API già in esecuzione, con verifica positiva di identità, autorizzazioni, configurazione e percorso di DB/audit. Preferire una **API benchmark dedicata** su porta loopback propria, con file task/audit/sessions temporanei, ma mantenere identici sorgenti di settings, vault, provider e policy e documentare ogni override. Non avviare un secondo writer sullo stesso SQLite; non spegnere processi non creati dal benchmark.
- Se richiesto `-HostMode Aspire` o una dipendenza necessita Docker, avviare Docker Desktop/servizio e Aspire solo se effettivamente disponibili, con timeout/readiness e verifica degli errori. UAC non può eliminare il bisogno di login o setup iniziale a Docker, provider o vault: in questi casi fallire con motivo preciso anziché fingere automazione assoluta.
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

1. Implementare BENCH-00 in commit solo tool/tests/docs su branch AF-05G, **senza cambiare il runner**.
2. L'operatore lancia in locale e ottiene ZIP pre-fix con nove task e acquisizione integrale o gap dichiarati; non committare evidenza reale.
3. Caricare ZIP nel progetto ChatGPT per analisi esterna.
4. Solo successivamente AF-05H-small (early-final→replan e deterministic disclosure), secondo ZIP identico, confronto esterno, CI e merge PR #100.
5. Quindi ARCH-01/02/03 e KNOW-01/02/03, come definito nel piano di convergenza.
