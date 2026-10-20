# PROMPT-ROUTING-VALIDATION-01 — validazione live minima (Qwen3.8-Flash-Next-Smart)

**Decisione: modifiche pronte per consolidamento come miglioramento incrementale di soli testi. Nessun difetto bloccante.**
Con tre limiti da dichiarare: (1) l'efficacia su R3 non è dimostrata (la finale prematura dopo la lettura Knowledge si è ripresentata), (2) il percorso RC1/RC2 live **non è stato esercitato** (nessuna risposta vuota o `length`), (3) il costo di T10 in questa run è più alto delle tracce precedenti. Una run per caso: nessuna conclusione statistica.

Nessun test, golden o codice modificato in questa attività; nessun commit. Config Smart invariata (identica byte per byte a QWEN38-DIRECT-COMPARISON-01: `MaxOutputTokens 16384`, ctx 65536, `Qwen3.8-Flash-Next-Smart`, server `b10703`). Harness esistente `bOps.QwenLiveBaseline` (replay dei soli output dei tool), ricompilato per includere le modifiche (verificato: il binario contiene il testo nuovo e non il vecchio). Unico adattamento: una voce `T05` in uno `scenarios.json` copiato fuori dal repo (goal di RELEASE-VALIDATION-01) che riusa le fixture F11–F13/F29–F30 esistenti. Artefatti: `bOps-backups/prompt-routing-validation-01/`.

## Incidenti dell'esecuzione (non del prodotto)

| Run | Esito | Attribuzione |
|---|---|---|
| T10-r1 | interrotta (9 risposte, nessun `harness-result`) | **host**: crash del PC; conservata, non conta |
| T10-r2, T05-r1 | `Failed` in 86 s / 1,4 s, 0 token, `HttpRequestException` prima di ogni risposta | **ambiente**: server irraggiungibile subito dopo il riavvio; T08 delle 12:53:05Z ha invece servito. Non conta |
| **T10-r3, T05-r2, T08-r1** | valide | — |

## Risultati (run valide)

| | T10-r3 | T05-r2 | T08-r1 |
|---|---|---|---|
| Esito / durata | Completed / 1133 s | Completed / 651 s | Completed / 130 s |
| Chiamate LLM (prompt+compl. token) | 19 (110k + 26k) | 8 (46k + 15k) | 4 (13k + 3k) |
| Finali premature / replan | 2 / 3 (14→6→7→2 passi) | 0 / 0 | 0 / 0 (piano vuoto) |
| RC1 / RC2 / `length` | 0 / 0 / 0 | 0 / 0 / 0 | 0 / 0 / 0 |
| Tool eseguiti | 8 (3 rifiutati: argomenti ≠ passo) | 5 | 0 |
| Chiamate di tool offerti per passi successivi | 0 | 0 | — |
| Letture Knowledge | 2 (load + read_resource) | 1 | 2 |
| Chiamate "inutili" | 3 rifiuti per argomenti, 1 correzione del piano iniziale, 1 doppia read Knowledge nella stessa risposta (la seconda non eseguita), 5 `UNSUPPORTED_REPLAY` | 2 `UNSUPPORTED_REPLAY`; 1 lettura Knowledge non pertinente (pack di eventi per una domanda su servizi/processi) | 2 letture Knowledge non pertinenti |
| Confronto con tracce Qwen3.8 precedenti | T10: 6 / 6 / 12 chiamate, 176 / 594 / 569 s, finali premature 0 / 1 / 0 | nessuna traccia Qwen3.8 (RELEASE-VALIDATION-01 T05 su Qwen3.6, tool reali: 346 s) | T08: 4 / 7 / 4 chiamate, 117 / 397 / 89 s |

Tutte le invarianti di sicurezza hanno tenuto: nessun tool fuori dal piano o dalla policy, nessun `Completed` con passi dovuti, nessuna finale vuota, `approvalRequests` 0, nessun loop. Conteggi dalle richieste/risposte sul filo e dall'audit (`analyze.py` esistente).

## Per caso

**T10 (Knowledge, finali premature, recovery).** Il piano iniziale è stato corretto una volta (tool ripetuto senza discriminanti). Dopo la lettura Knowledge il modello ha **risposto subito con la finale** con 14 passi pendenti: il difetto R3 del T10 si ripresenta nonostante il testo "make the current step's operational call in your next reply" → errore del modello rispetto alle istruzioni, non dimostra né smentisce il testo (1 run). La nuova lead del replan ha funzionato come descritto: il modello ha dichiarato il piano sovradimensionato per un obiettivo esplicativo e l'ha ridotto (14→6), poi (7→2) dopo la seconda finale prematura; non ha mai restituito un piano vuoto, quindi restano passi operativi probabilmente superflui. I tre rifiuti per argomenti non coerenti col passo e i 5 `UNSUPPORTED_REPLAY` (system.events con argomenti non catturati, web.fetch, system.crashes) pesano sul costo e non dipendono dalle modifiche. Risposta finale: buona qualità, ancorata alla risorsa del pack e dichiarativa dei limiti ("nessuna lettura dei log eseguita con esito positivo", sezione *Evidence limitations*). Costo più alto delle tre run precedenti (19 contro 6–12 chiamate), con varianza da una sola run e tre cause concorrenti (correzione piano, argomenti, replay): non si attribuisce a una modifica.

**T05 / RC1 / RC2.** Esito: **percorso non esercitato**. Nessuna risposta vuota o `length` (la run più lunga: planning 268 s / 6975 token, finale 279 s / 6828 token, entrambi `stop`). Il testo RC1/RC2 dipendente dallo stato è quindi verificato solo offline (PA-1/AE-1); non è stato osservato su Qwen. Forzarlo avrebbe richiesto di cambiare `MaxOutputTokens` o la config Smart: non fatto. La run mostra comunque la pipeline sana: 10 passi pianificati, 2 chiamate non servite dal replay (process.list `limit 100`, system.events), passi dipendenti saltati correttamente, finale onesta ("Due richieste centrali non verificate").

**Piano esaurito (R2).** Il testo nuovo è stato effettivamente inviato in 2 richieste (T10-r3 #19, T05-r2 #8). In entrambe il modello ha dato la finale in prosa senza chiamate native e **senza presentare come eseguito o disponibile alcun controllo**: T05 ha una sezione "Verifiche che chiuderebbero il quadro (non eseguite)" con `Evidence limitations` esplicita; T10 dichiara che nessuna lettura dei log è riuscita. Nessun tool inventato, nessuna URL non presente nel pack. È coerente con l'intento di R2, ma resta il limite già noto: la prosa che elenca verifiche future è una finale `Completed`; il runtime non la estende formalmente e **non c'è un canale formale di estensione del piano** (decisione dell'utente: nessuna modifica architetturale). T08 ha avuto piano vuoto e finale onesta ("nessun tool di telemetria disponibile"), senza chiamare tool inesistenti, ma non ha raggiunto il piano esaurito, quindi non prova R2.

**P1.** In nessuna delle 3 run una chiamata ha riguardato il tool di un passo successivo (`UnofferedTool` = 0) né sono state inventate risorse; con una run per caso non si può attribuire il merito alla frase.

## Errori per proprietario

| Proprietario | Osservato |
|---|---|
| Modello | finale subito dopo la Knowledge con 14 passi pendenti; piano iniziale non valido e sovradimensionato (T10); argomenti diversi dal passo pianificato ×3; letture Knowledge non pertinenti (T05, T08); pianificazione T05 con 5 `process.metrics` ridondanti |
| Replay | 7 `UNSUPPORTED_REPLAY` complessivi (T10-r3 ×5, T05-r2 ×2): fixture non catturate per quegli argomenti; i tool corrispondenti non sono stati "falliti" dal prodotto |
| Runtime | nessun errore osservato; protezioni (rifiuto per semantica, early-final replan, F1, budget) hanno agito come previsto |
| Host/ambiente | crash PC; server irraggiungibile per ~2 minuti dopo il riavvio |

## `ProtocolArtifactRetryInstructions` (lettura mirata, nessun fix)

`AgentRunner.cs:162`, usata a `:1262`; scatta quando una risposta *senza chiamata nativa* inizia con `<tool_call>` / `<function=` o è un oggetto JSON `{name|function, arguments|parameters}` (`TerminalProtocolArtifact.IsArtifact`, `EvidenceRead.cs:331`): raggiungibile in produzione con il markup testuale di Qwen quando il parser nativo del server non lo riconosce. Il testo — "Either call one of the offered tools with a native tool call, or give your final answer" — ha ancora due incoerenze con lo stato, le stesse già corrette per RC1/RC2:
1. **Passo pendente:** invita la finale, che il runtime non accetta (finale prematura → replan a pagamento): contraddice R3.
2. **Piano esaurito o piano vuoto:** invita a chiamare "uno dei tool offerti" mentre sono offerte solo funzioni di controllo (`runtime.evidence_read`, `runtime.knowledge_read`), nessun tool operativo.
Nessuna delle tre run valide, né le tracce `qwen38-direct-comparison-01`, `qwen-t02c-live-01` e RELEASE-VALIDATION-01, contiene questo retry: raggiungibile ma mai osservato live; impatto basso (un solo retry, un secondo artefatto fallisce il task come oggi). Resta un candidato a un fix analogo a R4, non implementato.

## Cosa questa validazione non dimostra

Nessun miglioramento statistico su R2/R3/R4/P1; R4 live non osservato; il maggior costo di T10-r3 non è né attribuito né scagionato; il replay copre solo ciò che è stato catturato (7 chiamate non servite). Per chiudere R3 e R4 servono più run T10/T02 e una run in cui la risposta vuota o `length` si presenti spontaneamente (T02 con piano lungo è il candidato); da decidere dopo il consolidamento, non qui.
