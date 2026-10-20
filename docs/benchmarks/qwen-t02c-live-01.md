# QWEN-T02C-LIVE-01 — tre run agentiche reali

Data: 18 ottobre 2026, Europe/Rome. Worktree `C:\source\repos\babbubba\bOps-knowbench01a`, branch `codex/know-bench-01a`, HEAD `7053ca0c8380f258442903360c44ec0fe1789bae`.

**Risultato:** execution outcome **3 Completed**; diagnostic outcome **0 PASS, 1 PARTIAL, 2 FAIL, 0 NOT_EVALUABLE**. Una sola run interamente coperta dal replay; 5 `UNSUPPORTED_REPLAY` su 12 invocazioni diagnostiche. T02-C rende le diagnosi valutabili, ma **non è ancora una baseline sufficientemente coperta per attribuire a una modifica dei prompt differenze nelle prestazioni dell'agente libero**. È utilizzabile come baseline esplorativa congelata, dichiarando copertura e limiti temporali.

Esattamente tre run, tutte conteggiate, senza sostituzioni. Nessuna nuova cattura Windows, fallback a tool reali o simulazione del modello. Nessuna modifica a fixture, matcher, prompt, manifest, policy, codice produttivo, PA-1 o AE-1. Non avviato PROMPT-ROUTING-FINALIZATION; nessun commit, push, merge, rebase, reset o clean.

## 1. Preflight e metodo

Sono stati usati AgentRunner reale, ModelInvocationBroker, MafContextProjector, adapter OpenAI-compatible/llama.cpp e planning, routing, recovery, grounding e finalization produttivi. I manifest sono reali; solo le risposte dei tool diagnostici sono fornite da ReplayBroker. Le letture Knowledge usano il pack locale congelato della precedente campagna. Il wrapper non esegue il tool reale; senza fixture restituisce Failure/Environment/Unavailable. Policy read-only e provider che rifiuta approvazioni invariati; zero richieste di approvazione.

**Nessun adattamento del loader necessario.** FixtureStore.Load riconosce già FC-*.json tramite F*.json e legge lo stesso formato. Il parametro --docs punta a una nuova directory esterna input/: scenarios.json è copia byte-identica dell'originale; input/fixtures è una junction esclusiva alla directory fixtures dello snapshot approvato. Nessun pool T02-B o storico caricato.

Il probe esterno preflight/Probe.csproj referenzia l'harness e invoca via reflection **FixtureStore.Load e ReplayBroker.Answer del binario effettivo**. Non è il validatore di cattura a decidere il matching. I 12 argomenti storici sono estratti dalla lista del validatore, ma risposte e ledger sono prodotti dal broker dell'harness. La SHA dell'assembly del probe coincide con quella delle run. Verificati 13 ID FC-, output e Completeness invariati; disponibilità delle sorgenti e troncamenti restano negli output originali, senza trasformazioni o ricostruzioni.

**12/12 probe serviti; 43/43 test del replay PASS**, nessuno skipped. Build probe/harness riuscito. Tutti gli hash di SHA256SUMS.txt verificati in sola lettura, senza nuove catture né validatore che scriva nello snapshot.

| Probe | Tool e argomenti | Fixture | Regola |
| --- | --- | --- | --- |
| O1 | system.events `{"mode":"aggregate","windowMinutes":1440}` | FC-C1-system.events | default-equivalent |
| O2 | system.events `{"minSeverity":"warning","mode":"raw","windowMinutes":1440}` | FC-C2-system.events | default-equivalent |
| O3 | system.stability `{"limit":50,"windowDays":2}` | FC-Q07-system.stability | limit-not-binding |
| O4 | system.crashes `{"sinceMinutes":1440}` | FC-Q05-system.crashes | default-equivalent |
| O5 | system.events `{"minSeverity":"warning","mode":"aggregate","windowMinutes":1440}` | FC-Q01-system.events | limit-not-binding |
| O6 | system.stability `{"windowDays":1}` | FC-Q06-system.stability | limit-not-binding |
| R1 | system.events `{"limit":500,"minSeverity":"error","mode":"aggregate","windowMinutes":1440}` | FC-Q03-system.events | exact |
| R2 | system.stability `{"limit":100,"windowDays":30}` | FC-C3-system.stability | exact |
| R3 | system.events `{"limit":100,"minSeverity":"critical","mode":"raw","windowMinutes":1440}` | FC-Q04-system.events | limit-not-binding |
| R4 | system.stability `{"limit":100,"windowDays":3}` | FC-Q08-system.stability | limit-not-binding |
| S1 | system.events `{"limit":200,"minSeverity":"warning","mode":"aggregate","windowMinutes":1440}` | FC-Q01-system.events | limit-not-binding |
| S2 | system.crashes `{"limit":100,"mode":"aggregate","sinceMinutes":1440}` | FC-Q05-system.crashes | default-equivalent |

Questa copertura riguarda quelle 12 chiamate, non ogni possibile query. Critical/raw non autorizza critical/aggregate; error/aggregate non autorizza error/raw. Il matcher non filtra o converte i risultati per soddisfare query diverse.

Snapshot: `C:\source\repos\babbubba\bOps-backups\replay-capture-t02c\T02-C-20261018T211054Z-cdc8`.

| Oggetto | SHA-256 |
| --- | --- |
| Piano approvato | 46758334aa173b15c5aaf800f99851749918c5963e37d6e6ab106b412ed15386 |
| Manifest snapshot SHA256SUMS.txt | b59465c0cdd5919e2a73268c76c76090c9b1f9caf4f06315d2dbf27aadb55818 |
| index.json | 357dc59c225aa36ca85e6c4d1f3068a3e601bdfc6c89777060d6896b462e25f4 |
| bOps.QwenLiveBaseline.dll | b4a974ea605a033532f4a075bf96e010e3d327ab43de74acba9007b47cb22a82 |
| Replay.cs | 4e28b0696df7bbeaaf93418eba1a3794d16bb0ed6ed059239b20b4e1f42d1406 |
| ReplayMatching.cs | ba9a5a522022877c797c0f23af0d2989b7f64b9ae64d1806110f7cbb9afb41e8 |
| Configurazione applicativa | 2abbe5c9e6775be07a74702375fe0b8d6004ea140f20036085a0acde48d8f911 |

L'hash snapshot è quello del manifest di integrità, accompagnato dall'hash dell'indice, non di un archivio ZIP. Gli hash di tutti i 101 file della directory binaria sono in preflight/binaries.json, invariati prima/dopo. Rispetto alla campagna originale differisce solo il binario dell'harness, già cambiato nella rivalidazione; tutti gli altri binari bOps coincidono. Rispetto alla rivalidazione anche l'harness è identico. Nessun sorgente del benchmark modificato.

Lo snapshot conserva 13 fixture, 9 Partial o con fonti indisponibili, 4 Truncated, dispersione 3,164 s e il precedente verdetto PASS-WITH-WARNINGS. Tale verdetto non dimostra diagnosi corrette.

## 2. Configurazione effettiva

| Parametro | Campagne precedenti e T02-C |
| --- | --- |
| Modello | Qwen3.6-35B; Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf |
| llama.cpp / fingerprint | b10703-0b5be7e4a |
| Template chat SHA-256 | 55d4931433fe502b794226ee7f4d206a6bdd436ac9f80eb7d8ebb4c639f9ea0c |
| Contesto server / profilo bOps | 32768 / 32768 token |
| MaxOutputTokens / max_tokens HTTP / riserva | 8192 / 8192 / 8192 |
| Sampling server | temperature=1; top_k=20; top_p=0.949999988079071; min_p=0.05000000074505806 |
| Seed server | 4294967295; non fissato per riproduzione deterministica |
| Wire HTTP | model e max_tokens; nessun override sampling |
| Timeout modello | request 5 min; attempt 4 min; budget 8 min; max attempts 2 |
| Limiti per run | 40 chiamate LLM; watchdog interno 25 min; kill esterno 30 min |

Il confronto integrale dei props col modello dopo warmup della campagna originale rileva solo is_sleeping differente; stessa unica differenza fra inizio/fine di questa campagna. Tutte le 26 risposte confermano modello e fingerprint. Configurazione applicativa byte-identica alla rivalidazione; cambiano solo i percorsi di isolamento previsti dall'harness e il pool di fixture.

Prima RequestJson SHA-256 `af6b3b5249d3a85ee16009d269e04cfb23a5821b93bfa845ecbae9199aecbcf0` in tutte e tre le run e nelle due campagne precedenti: stessi prompt iniziali, goal, manifest e indice Knowledge. Non sono state aggiunte istruzioni per indirizzare il piano. Le richieste successive dipendono dalle risposte campionate e non devono coincidere.

Build e percorso modello sono dichiarati dall'API remota: nessun hash indipendente del GGUF o dell'eseguibile remoto disponibile. Server e cache prompt sono condivisi; l'isolamento è del task, non un riavvio del server. Le durate non sono una misura controllata della GPU.

## 3. Riferimento temporale

Riferimento convenzionale **2026-10-18 21:10:54.890281 UTC**, cioè **23:10:54.890281 Europe/Rome**. Le finestre terminano fra tale istante e 21:10:58.054376 UTC; letture sequenziali non atomiche. Le 24 ore riguardano il 17–18 ottobre rispetto alla cattura; stability a 7 giorni comprende contesto più ampio.

Goal originale: «Analizza gli eventi Windows delle ultime 24 ore, identifica eventuali problemi di stabilità e distingui le evidenze dalle possibili cause.» L'harness usa **TimeProvider.System e non esplicita al modello una cornice di replay congelato**: la richiesta relativa può sembrare corrente, mentre gli output mantengono le date originali. È un limite metodologico, non corretto alterando prompt o osservazioni. La diagnosi è valutata rispetto alle finestre catturate e non attesta lo stato attuale della macchina.

| Run | Inizio UTC | Età snapshot a inizio (s) | Durata (s) |
| --- | --- | --- | --- |
| T02-r1 | 2026-10-18T21:15:04.7533205+00:00 | 249.863 | 273.2 |
| T02-r2 | 2026-10-18T21:19:38.2651283+00:00 | 523.375 | 252.4 |
| T02-r3 | 2026-10-18T21:23:50.9991078+00:00 | 776.109 | 264.8 |

I watchdog misurano tempo reale corrente. Lo scarto non viene sottratto dalla durata. Inoltre timestampKind=reported è il tempo di segnalazione, non l'istante del guasto: questa distinzione è centrale nella valutazione.

## 4. Matrice e fasi LLM

Execution outcome e diagnostic outcome sono indipendenti. Completed descrive il runtime. PASS richiede affermazioni centrali supportate e task soddisfatto; PARTIAL indica un'analisi utile con errori/omissioni circoscritti; FAIL indica errori sostanziali nella ricostruzione centrale; NOT_EVALUABLE indica evidenze o risposta insufficienti per giudicare. Le lacune del replay non sono automaticamente errori del modello.

| Run | Execution | Diagnostic | LLM | Token input | Token output | Token totali | Replay servite/totali | UR | Length | Replan | Premature |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| T02-r1 | Completed | PARTIAL | 6 | 45819 | 15344 | 61163 | 2/2 | 0 | 0 | 0 | 0 |
| T02-r2 | Completed | FAIL | 10 | 63519 | 13853 | 77372 | 2/6 | 4 | 0 | 0 | 0 |
| T02-r3 | Completed | FAIL | 10 | 72039 | 15069 | 87108 | 3/4 | 1 | 0 | 0 | 0 |

Tutti i processi exit 0: nessun timeout, kill, cap raggiunto, eccezione harness o errore HTTP. Execution: Completed 3, Failed 0, Timeout 0, Inconclusive 0.

| Run | planning | execution | finalization | empty-response-recovery | grounding-check | grounding-correction |
| --- | --- | --- | --- | --- | --- | --- |
| T02-r1 | 1 | 2 | 1 | 0 | 1 | 1 |
| T02-r2 | 1 | 7 | 1 | 0 | 1 | 0 |
| T02-r3 | 1 | 5 | 1 | 1 | 1 | 1 |

Fasi ricavate dalle RequestJson/ResponseJson: planning dai record del piano; grounding dai marker produttivi; execution dalle proposte tool; finalization dalle risposte senza tool. In r3 comprende il tentativo vuoto, seguito da recovery distinto. La classificazione precisa è in analysis/*-review.json; l'analizzatore storico usa etichette step meno specifiche. Le 26 chiamate includono ogni check/correction/retry produttivo. Nessuna chiamata separata a Qwen per giudicare la campagna. Token da usage del server, incluso reasoning nell'output; length 0/26, il vuoto r3 termina con stop.

## 5. Piani, tool e fixture

Una sola revisione di piano per run, revision 0; nessuna modifica formale successiva o replanning. Ordine e argomenti sono stati scelti spontaneamente dal modello, non imposti.

### T02-r1

Piano iniziale: riepilogo errori/avvisi a 24 ore, poi stabilità a 7 giorni prima di eventuali dettagli raw.

| Step runtime | Tool | Argomenti effettivi | Fixture/esito | Regola |
| --- | --- | --- | --- | --- |
| 0 | system.events | `{"windowMinutes":1440,"minSeverity":"warning","mode":"aggregate"}` | FC-Q01-system.events | limit-not-binding |
| 1 | system.stability | `{"windowDays":7}` | FC-Q09-system.stability | limit-not-binding |

Pianificati 2 step, entrambi invocati e serviti, nessuno irrisolto. Nessuna lettura Knowledge o Evidence aggiuntiva. Finale, grounding e correzione; nessuna finale respinta per piano incompleto o recovery da vuoto.

HTTP 005 grounding contesta 117 contro la fact di categoria hardwareError=119. FC-Q09 contiene però 117 WHEA-19 e 2 WHEA-2: entrambi i numeri sono corretti per scope diversi. La frase originaria cita EventID 19: la contestazione è almeno un **possibile falso positivo di scope**, non una verità accettata perché prodotta da Qwen. HTTP 006 cambia il testo verso 119. Questo limite del controllo produttivo è distinto dal replay e dal giudizio esterno.

### T02-r2

Piano iniziale: contesto OS/ora, stabilità, crash e viste critical/error a 24 ore.

| Step runtime | Tool | Argomenti effettivi | Fixture/esito | Regola |
| --- | --- | --- | --- | --- |
| 0 | system.info | `{}` | UNSUPPORTED_REPLAY | — |
| 1 | system.time | `{}` | UNSUPPORTED_REPLAY | — |
| 2 | system.stability | `{"windowDays":1}` | FC-Q06-system.stability | limit-not-binding |
| 3 | system.crashes | `{"mode":"aggregate","sinceMinutes":1440,"limit":200}` | FC-Q05-system.crashes | limit-covered |
| 4 | system.events | `{"minSeverity":"critical","mode":"aggregate","windowMinutes":1440,"limit":200}` | UNSUPPORTED_REPLAY | — |
| 5 | system.events | `{"minSeverity":"error","mode":"raw","windowMinutes":1440,"limit":200}` | UNSUPPORTED_REPLAY | — |

Pianificati 6 step, tutti invocati: 2 serviti, 4 senza completamento con evidenza per limiti del replay. Il modello prosegue dopo i fallimenti senza replanning.

HTTP 005 propone insieme crashes e `runtime_knowledge_read {"operation":"read_resource","ref":"K1.R1"}`. Il routing serve prima Knowledge e avverte che crashes non è stato eseguito; HTTP 006 ripropone crashes, eseguito una sola volta. Non è una lettura diagnostica ridondante o una finale prematura. Il Knowledge read (`windows-event-triage`, `references/interpretation.md`, 4069 caratteri) riesce. Nessuna EvidenceRead. Crashes limit=200 è diverso dal limite omesso nel piano, ma il matcher lo serve con limit-covered.

HTTP 009 finale, HTTP 010 grounding senza contraddizioni. Il check limitato ai fatti tipizzati non certifica le inferenze temporali/causali della risposta.

### T02-r3

Piano iniziale: informazioni di sistema, crash, stabilità a un giorno ed errori aggregati a 24 ore.

| Step runtime | Tool | Argomenti effettivi | Fixture/esito | Regola |
| --- | --- | --- | --- | --- |
| 0 | system.info | `{}` | UNSUPPORTED_REPLAY | — |
| 1 | system.crashes | `{"mode":"aggregate","sinceMinutes":1440}` | FC-Q05-system.crashes | exact |
| 2 | system.stability | `{"windowDays":1}` | FC-Q06-system.stability | limit-not-binding |
| 3 | system.events | `{"minSeverity":"error","windowMinutes":1440,"mode":"aggregate","limit":500}` | FC-Q03-system.events | exact |

Pianificati 4 step, tutti invocati: 3 serviti, 1 senza evidenza per il replay. Un Knowledge read a HTTP 004: `runtime_knowledge_read {"operation":"read_resource","ref":"K1.R1"}`, stessa risorsa della r2, 4069 caratteri. Nessuna EvidenceRead.

HTTP 007 ha finish_reason=stop, 2465 token di output, ma zero caratteri content e nessuna tool call. Il runtime manda il recovery produttivo; HTTP 008 dà il finale. Il vuoto è un comportamento modello/provider sul wire, non timeout, UR o nuova run. HTTP 009 grounding riconosce che i 7 bugcheck sono 0x1e, non 0x193; HTTP 010 corregge questa parte ma lascia altri errori. Nessun replan o finale prematuro per step incompleti.

### UNSUPPORTED_REPLAY

Cinque UR su quattro forme distinte: system.info {} due volte; system.time {} una volta; critical/aggregate e error/raw una volta ciascuno, con argomenti completi nelle tabelle. Sono **limiti del replay**, non errori di schema o scelte illegittime del modello. Cercare contesto OS/ora e quelle viste degli eventi è ragionevole per il task. Nessuna fixture di altra modalità, severità, finestra o campagna è stata usata in sostituzione. Zero chiamate Windows reali.

## 6. Giudizi diagnostici indipendenti

Giudizi del revisore di questa attività contro fixture originali e finali persistiti. Non è un secondo essere umano, una validazione cieca o un consenso fra giudici. Il grounding Qwen è parte del sistema misurato e non è il solo giudice. Le prove qui sono conteggi e campi strutturati, senza messaggi raw degli Event Log o identificativi della macchina.

### r1 — PARTIAL

**Supportato:** FC-Q01 contiene 100 record warning/error, 8 WHEA-19, 16 processor-power 37, 2 .NET 1026 e 1 .NET 1025. FC-Q09 distingue il contesto di 7 giorni: hardwareError=119 (117 WHEA-19 + 2 WHEA-2), displayFault=26, kernelCrash=3, unexpectedShutdown=14 segnalazioni. Il modello distingue le finestre nelle sezioni e propone varie cause come ipotesi. Analisi utile, senza UR.

**Errori circoscritti:** chiama costanti ogni giorno gli errori hardware, ma la timeline FC-Q09 ha un bucket giornaliero a zero e conteggi variabili. Presenta 14 segnalazioni unexpectedShutdown come 14 shutdown: i gruppi sono 7 EventLog 6008 e 7 Kernel-Power 41, senza deduplicazione che dimostri 14 incidenti distinti. Tratta i display fault reported come guasti avvenuti in quel momento e li colloca nella sintesi nel tardo pomeriggio, pur citando 20:30 UTC. La corruzione di memoria e la localizzazione processore/controller hanno più certezza del consentito; la tabella di ipotesi attenua ma non elimina queste affermazioni.

**Partial/Truncated:** il primo finale nomina minidump non leggibili e messaggi campione tagliati; la correzione perde questi dettagli. La disclosure stability Partial nel finale persistito è **aggiunta dal runtime**, non pieno riconoscimento spontaneo del modello. FC-Q09 è Partial e non troncata; i singoli campioni FC-Q01 possono essere tagliati anche con output completo.

**Verifiche omesse:** ricondurre il contesto di 7 giorni al giorno richiesto, separare segnalazioni duplicate da incidenti, verificare i tempi WER e le categorie stability notCollected. Crashes avrebbe dettagliato kernelFault non raccolto da stability, ma non chiamarlo non è da solo motivo di bocciatura: non si impone una sequenza canonica. Le ipotesi CPU/RAM/firmware/termiche richiedono ulteriori verifiche e non sono cause confermate.

### r2 — FAIL

**Supportato:** riconosce FC-Q06 e FC-Q05 Partial, minidump indisponibili, 448+42 WER non leggibili e le quattro query senza evidenza; cita la finestra congelata. I gruppi crashes contano 43 elementi in 10 gruppi: 27 live dump, 15 bugcheck, 1 altro record WER. Corretti 8 WHEA e 26 segnalazioni display; segnala la possibile sovrapposizione delle fonti display senza sommare i conteggi.

**Errore centrale:** presenta come fatto un evento critico a cascata con 42 crash kernel in circa 2 secondi. FC-Q05 marca i tempi reported: provano la segnalazione, non che 42 guasti o riavvii siano avvenuti simultaneamente. I 42 elementi kernel includono live dump, non solo crash. La finestra di segnalazione display FC-Q06 è diversa da crashes; l'identità degli incidenti non deriva dal solo conteggio 26. Su questa ricostruzione costruisce un episodio singolo e una cascata: alcune cause sono etichettate ipotesi, ma la premessa è nei fatti. Afferma inoltre che gli errori corrected non abbiano causato crash diretti, esclusione non dimostrata.

**Perché FAIL:** le quattro UR limitano legittimamente la raccolta; non giustificano una ricostruzione temporale non supportata dalle due fixture servite. Anche ignorando le UR, il nucleo diagnostico resta non sostenuto. Il runbook letto non impedisce l'errore.

**Verifiche omesse:** separare reported/occurred, verificare numero di incidenti e correlazione fra fonti, limitare le assenze alle sorgenti leggibili. Le categorie a zero di stability non provano un'assenza universale quando altre sorgenti non sono disponibili.

### r3 — FAIL

**Supportato:** recovery riuscito dopo un vuoto; identifica 8 WHEA, 26 display fault e 3 errori .NET; riconosce WER parziale/minidump indisponibili. Il grounding corregge 26 live dump 0x193 contro 7 bugcheck 0x1e. La disclosure finale dell'UR system.info è aggiunta dal runtime.

**Errori centrali rimasti dopo la correzione:** chiama 43 dump/crash kernel ciò che FC-Q05 descrive come 43 elementi: 27 live dump + 15 bugcheck = **42** kernel e 1 altro WER. Riduce i tempi a un secondo e li usa come accadimento dei guasti invece di segnalazione. Calcola **492** report illeggibili da **448+42**, cioè **490**.

Associa ancora **0x193 a NTFS_FILE_SYSTEM** e fonda su questa premessa l'ipotesi storage. Il codice è invece VIDEO_DXGKRNL_LIVEDUMP, non un bugcheck reale; NTFS_FILE_SYSTEM è 0x24. Verifica su fonti primarie Microsoft: [0x193](https://learn.microsoft.com/en-au/windows-hardware/drivers/debugger/bug-check-0x193--video-dxgkrnl-livedump), [0x24](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/bug-check-0x24--ntfs-file-system), consultate il 18 ottobre 2026. Le fixture classificano già 0x193 come live dump/displayFault. Il controllo web è del revisore e non è stato fornito al modello. Etichettare un'ipotesi non rende valida una premessa tecnica falsa.

**Verifiche omesse:** mappa dei codici, separazione crash/live dump/report, somme, semantica temporale, riscontro della tesi storage (FC-Q06 non osserva storageError nelle fonti leggibili). Non è penalizzata una sequenza diversa di tool. L'UR system.info non spiega questi errori centrali.

### Completezza e limiti del giudizio

Tutte le run ricevono almeno una fixture Partial. **Nessuna riceve una fixture top-level truncated=true**: le quattro presenti nel pool non sono selezionate dalle query spontanee. La capacità di gestire quel troncamento è quindi **non verificata empiricamente**, pur essendo preservata nel loader e nel preflight. Le troncature sampleMessage sono un livello diverso. status=complete dell'invocazione non equivale a completezza dell'evidenza.

I risultati non certificano la causa hardware reale: mancano dump leggibili e verifiche indipendenti. Si giudica il supporto delle affermazioni. Il grounding ha limiti propri: possibile falso positivo di scope r1, nessuna contraddizione r2, correzione solo parziale r3.

## 7. Aggregati e variabilità

| Metrica | Valore |
| --- | --- |
| Diagnostic PASS/PARTIAL/FAIL/NOT_EVALUABLE | 0/1/2/0 |
| Run interamente servite dal replay | 1/3, soltanto r1 |
| Invocazioni servite/totali | 7/12; 5 UR |
| Piani distinti | 3/3; lunghezze 2, 6, 4, ordini e filtri differenti |
| Finali premature per step irrisolti | 0 eventi, 0/3 run |
| Replanning | 0 eventi, 0/3 run |
| Recovery risposta vuota | 1/3 run, r3, riuscito |
| Grounding check/correction | 3/2 |
| EvidenceRead / ridondanti | 0/0, nessuna ambiguità da paginazione |
| Knowledge read | 2, una per r2/r3, Repeat=false |
| LLM mediana; intervallo | 10; 6–10 |
| Token input mediana; intervallo | 63519; 45819–72039 |
| Token output mediana; intervallo | 15069; 13853–15344 |
| Token totali mediana; intervallo | 77372; 61163–87108 |
| Durata s mediana; intervallo | 264.8; 252.4–273.2 |
| finish_reason=length | 0/26 |
| Totali LLM/input/output | 26 / 181377 / 44266 |

**Errori comuni:** tempi di segnalazione trattati come accadimento e inferenze su incidenti/causalità più forti delle osservazioni. Dichiarare limiti generici non neutralizza premesse errate. Confusione segnalazioni/incidenti anche negli shutdown r1, nei live dump r2/r3. **Non comuni:** scope grounding WHEA e generalizzazione giornaliera r1; cascata ed esclusione causale dei corrected r2; codice NTFS, 43 contro 42 kernel e somma 492 r3. Vuoto solo in r3.

| Categoria | Riscontro |
|---|---|
| Modello | Errori diagnostici sopra; risposta vuota r3 |
| Orchestrazione | Possibile falso positivo scope nel grounding r1; correzioni/disclosure non equivalgono a diagnosi validata |
| Replay | Cinque query prive di fixture; nessuna evidenza di matching errato nelle sette servite |
| Dati | Minidump indisponibili, WER parziale, sampleMessage tagliati; contatori minimi |
| Valutazione | Un revisore, tre run esplorative, causa reale non verificata, top-level Truncated non esposto, hash remoto GGUF assente |

## 8. Confronto con le campagne precedenti

| T02 r1/r2/r3 | QWEN-LIVE-BASELINE-01 | REPLAY-DEFAULT-EQUIVALENCE-01 | T02-C |
| --- | --- | --- | --- |
| Runtime | Completed / Failed / Completed | Completed / Completed / Completed | Completed / Completed / Completed |
| LLM | 5 / 10 / 9 | 4 / 9 / 9 | 6 / 10 / 10 |
| Token totali | 28055 / 51258 / 42277 | 37639 / 76419 / 108748 | 61163 / 77372 / 87108 |
| UR | 1 / 3 / 2 | 0 / 2 / 2 | 0 / 4 / 1 |
| Finali premature | 0 / 0 / 0 | 0 / 0 / 1 | 0 / 0 / 0 |
| Diagnostic storico/attuale | NOT_EVALUABLE per T02 originale | PARTIAL / PARTIAL / PARTIAL | PARTIAL / FAIL / FAIL |

Fonti storiche: report e artefatti esistenti delle due campagne, conservati invariati. Nessuna promozione retroattiva delle vecchie run usando T02-C. Goal e prima richiesta coincidono, runtime/configurazione sono equivalenti nei limiti verificati. **Le evidenze non sono equivalenti:** le precedenti fixture erano meno complete e riferite ad altre acquisizioni. Anche i piani campionati divergono. Non si dichiarano miglioramenti o peggioramenti di Qwen/runtime da token, UR, finali premature o giudizi diagnostici. Le chiamate diagnostiche totali sono rispettivamente 6, 7, 12.

T02-C rimuove le lacune delle 12 chiamate storiche, ma i nuovi piani aggiungono query diverse: la copertura dell'agente libero resta insufficiente. Tre run sono una valutazione esplorativa, **non una dimostrazione statistica di affidabilità**.

## 9. Artefatti e integrità

Root esterna dedicata, **non in Git**: `C:\source\repos\babbubba\bOps-backups\qwen-t02c-live-01-20261018T2114Z`.

| Percorso relativo alla root | Contenuto |
| --- | --- |
| runs/T02-r1, runs/T02-r2, runs/T02-r3 | Task/processo indipendente; tasks.db, system-messages.db, audit.jsonl, harness-result.json, snapshots separati |
| runs/T02-r*/http | 26 request, 26 response, 26 meta; wire JSON senza header/credenziali |
| runs/T02-r*/state.json e model-calls.json | Stato esportato e RequestJson/ResponseJson produttivi, incluso recovery/grounding |
| logs/T02-r*.launch.json, *.exit.json, *.out.txt, *.err.txt | PID, UTC, limiti, kill esterno e log separati |
| analysis/T02-r*.json, *-supplement.json, *-review.json | Metriche, finali, piani, ledger, età snapshot, fasi, letture |
| preflight/ | Probe/sorgente, 12 query e ledger, hash, props e confronti server/binari/richieste, integrità iniziale/finale |
| shared/ e input/ | Config/policy/prerequisiti/pack/trust copiati; scenario originale, junction esclusiva T02-C |
| Run-Three.ps1, analyze.py, export.py, phases.py, make-report.py | Driver e analisi riproducibili; nessuna simulazione |
| artifact-manifest.json | SHA-256 di tutti i file ordinari della campagna, esclusa junction fixture coperta dal manifest snapshot |

Gli artefatti grezzi possono contenere Event Log raw e identificativi: restano nella root esterna. Nel repository viene aggiunto **solo questo report**, con conteggi e riferimenti generici.

Test eseguito dal worktree:

```powershell
dotnet test tests/bOps.QwenLiveBaseline.Tests/bOps.QwenLiveBaseline.Tests.csproj --no-restore --verbosity minimal
```

Il driver ha eseguito per ciascuna run:

```text
dotnet <harness.dll> --scenario T02 --run <1|2|3>
  --root <root nuova> --docs <root>/input
  --config <root>/shared/appsettings.json --max-calls 40 --max-minutes 25
```

Kill esterno a 30 minuti; rifiuto di una root con run preesistenti. È documentazione della campagna conclusa: nessun rilancio sostitutivo.

Integrità finale: **1839 file sorgente/documenti preesistenti invariati**, inclusi untracked PA-1, AE-1, harness e report storici; **20 file snapshot invariati**; **101 file directory binaria invariati** rispetto al preflight. Prove in preflight/final-integrity.json e manifest prima/dopo. Nessun altro worktree modificato. Unica aggiunta sorgente: docs/benchmarks/qwen-t02c-live-01.md.

## 10. Raccomandazioni e decisione

1. Conservare questa campagna come **baseline esplorativa valutabile**, con 1 PARTIAL e 2 FAIL: le affermazioni principali sono controllabili contro evidenze adeguate anche quando sono errate. Un basso punteggio non equivale a NOT_EVALUABLE.
2. Prima di attribuire effetti ai prompt, decidere come coprire le quattro nuove forme di query e altre scelte possibili. Un ampliamento controllato delle fixture o un task più circoscritto costituirebbero una nuova baseline, non una correzione retroattiva. Nessuna cattura o modifica è stata effettuata qui.
3. Esplicitare nel protocollo futuro il riferimento congelato. Cambiare goal/prompt/orologio logico richiede nuova baseline; qui resta dichiarato il disallineamento temporale.
4. Valutare separatamente reported/occurred, conteggi sovrapposti, live dump/bugcheck, codici e mantenimento delle limitazioni dopo grounding. Completed o meno UR non sostituiscono il giudizio diagnostico.
5. Mantenere stesso snapshot, matcher/binario e configurazione nei confronti, stratificare gli UR e ampliare il campione prima di affermare affidabilità. La gestione top-level Truncated non è stata misurata da queste tre run spontanee.

**Decisione:** T02-C è utilizzabile per un prossimo confronto esplorativo trasparente sullo stesso pool, ma **non è ancora sufficiente come baseline riproducibile e pienamente coperta per un confronto generale dei prompt agentici**: soltanto 1/3 run interamente servita e tempo relativo ambiguo. Attività terminata dopo tre run e il report, senza avviare interventi su prompt/runtime.
