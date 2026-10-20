# T02-GROUNDING-01 — ricostruzione T02-r3 e limite del contratto

Data: 19 ottobre 2026. Worktree: `C:\source\repos\babbubba\bOps-knowbench01a`; branch verificato: `codex/know-bench-01a`.

**Esito: proposta motivata, nessun fix produttivo. Il difetto diagnostico resta aperto.** Il contratto esistente non contiene le affermazioni strutturate della finale né la relazione codice/significato. Riconoscere e verificare autonomamente questi errori nella prosa libera richiede un nuovo contratto, non una modifica locale al parser dei verdetti. Si applica quindi il punto 6 del brief: arresto alla proposta, con riproduzioni e test offline. Non sono stati modificati criteri di Completed, prompt, modello, matcher, snapshot o budget.

## Perimetro e fonti

Analizzata esclusivamente T02-r3, task `bf5be0a4-4ecb-4b2b-a7e1-a541e219da76`, negli artefatti già esistenti sotto:

`C:\source\repos\babbubba\bOps-backups\qwen-t02c-live-01-20261018T2114Z\runs\T02-r3`.

Fonti: `model-calls.json` (RequestJson/ResponseJson), `http/007–010.request.json` e `.response.json`, `state.json` (risultati, fatti, finale e marker persistiti), `audit.jsonl`. I JSON HTTP 007–010 e le corrispondenti RequestJson/ResponseJson risultano uguali dopo parsing e serializzazione normalizzata; `PayloadTruncated=false` per check e correction. Non sono stati aperti in scrittura i database della run.

SHA-256 dei tre export letti:

| File | SHA-256 |
|---|---|
| model-calls.json | `24029eb0d486146484bda05a618a2868c952a7b2503099af98fdfeee5669b13a` |
| state.json | `cf201f8fcfe74e3602146d6a90d8b424466935f4e2f30fdb9ca8341695b3d2a9` |
| audit.jsonl | `48eecef620dd25c90e6683874eac4f9f3c257dd6248ce66b88508bd4874dc214` |

Il riferimento Microsoft è stato consultato in sola lettura, senza fornirlo a Qwen: [0x193 VIDEO_DXGKRNL_LIVEDUMP](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/bug-check-0x193--video-dxgkrnl-livedump) e [0x24 NTFS_FILE_SYSTEM](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/bug-check-0x24--ntfs-file-system). Microsoft distingue espressamente il live dump 0x193 da un vero bugcheck. Questo verifica la nomenclatura, non la causa dei problemi della macchina.

## Errore esatto e percorso di finalizzazione

Tutti e quattro gli step pianificati erano risolti prima della finale: system.info fallito/Unavailable, crashes e stability Partial, events Complete. La lettura Knowledge precedente è contesto già esistente, non un nuovo controllo effettuato in questa attività.

| HTTP | Percorso osservato | Evidenza |
|---|---|---|
| 007 | Tentativo finale vuoto | finish_reason=stop, 2465 completion token, content vuoto, nessuna tool call; audit seq 14 classifica EmptyResponse |
| 008 | Recovery produttivo dal vuoto; prima finale | 4333 caratteri content; contiene 43 dump kernel, NTFS e attribuzione dei 7 bugcheck a 0x193; audit seq 15 |
| 009 | Grounding-check sulla finale dopo disclosure | 139 caratteri content, una sola citazione F1.7; audit seq 16 |
| 010 | Unica grounding-correction | 4886 caratteri content; corregge i 7 bugcheck in 0x1e ma mantiene altri errori; audit seq 17 |
| stato | Finale persistita step 4; Completed | descrizione `Final response; evidence limitations appended by runtime; evidence grounding corrected`; audit seq 18 termina alle 21:28:15.8187394 UTC |

Il check restituisce esattamente:

```json
{"contradictions":[{"fact":"F1.7","quote":"Il codice `0x193` (NTFS_FILE_SYSTEM) è predominante, apparendo in 26 dump live e 7 bugcheck."}]}
```

La correction riceve, nella sua istruzione conclusiva, soltanto F1.7: `kind=kernel-bugcheck;code=0x1e = Integer 7 (from partial evidence)`, oltre all'istruzione di riscrivere la finale e mantenere il resto. Il system prompt conserva il ledger completo mostrato sotto. Non viene fornita una mappatura ufficiale dei codici.

La finale persistita continua a chiamare 43 elementi «dump kernel»/«crash kernel» e associa ancora `0x193` a `NTFS_FILE_SYSTEM`. L'ipotesi storage usa questa premessa sbagliata anche se presentata come ipotesi. Il totale pertinente è **42 segnalazioni kernel osservate = 27 live dump + 15 bugcheck**, più un evento WER. Non sono 42 incidenti indipendenti dimostrati, 42 bugcheck, né 42 file verificati o letti.

Resta anche `492` per la somma di 448 e 42 report non leggibili: il risultato aritmetico è **490**. I tempi di segnalazione sono inoltre usati per descrivere un cluster di guasti in un secondo; questi dati non dimostrano quella cronologia né una causa. La finale dichiara i dati parziali/minimi: **in r3 non è accertata un'omissione totale della disclosure o una dichiarazione generale di raccolta completa**. Il problema delle affermazioni di completezza è valutato separatamente nei test.

## Fatti disponibili e provenienza

`state.json`, `Steps[1].Result.Facts`: 11 fatti prodotti da SystemCrashFacts; step 2: 3 fatti StabilityFacts; step 3: 2 fatti SystemEventFacts. Totale 16, di cui 12 visibili nel ledger di HTTP 009 e 010, quattro omessi. I numeri sono osservazioni nel perimetro della chiamata, non totali reali degli incidenti. L'identità F è posizione del fatto nello step persistito.

| ID | Tipo e chiave | Valore | Nel ledger del check |
|---|---|---:|---|
| F1.1 | system.crashes.observed-count / all | 43 | sì, Partial |
| F1.2 | system.crashes.group-count / groups | 10 | sì, Partial |
| F1.3 | system.crashes.kind-count / kernel-live-dump | 27 | sì, Partial |
| F1.4 | system.crashes.kind-count / kernel-bugcheck | 15 | sì, Partial |
| F1.5 | system.crashes.kind-count / wer | 1 | sì, Partial |
| F1.6 | system.crashes.kernel-code-count / kind=kernel-live-dump;code=0x193 | 26 | sì, Partial |
| F1.7 | system.crashes.kernel-code-count / kind=kernel-bugcheck;code=0x1e | 7 | sì, Partial |
| F1.8 | system.crashes.kernel-code-count / kind=kernel-bugcheck;code=0x7f | 2 | no |
| F1.9 | system.crashes.kernel-code-count / kind=kernel-bugcheck;code=0xa | 2 | no |
| F1.10 | system.crashes.dump-reference-count / all | 42 | no |
| F1.11 | system.crashes.kernel-dump-path / newest | percorso, non riprodotto qui | no |
| F2.1 | system.stability.category-count / hardwareError | 8 | sì, Partial |
| F2.2 | system.stability.category-count / displayFault | 26 | sì, Partial |
| F2.3 | system.stability.window-days / window | 1 | sì, Partial |
| F3.1 | system.events.matched-count / source=.NET Runtime;eventId=1026 | 2 | sì |
| F3.2 | system.events.matched-count / source=.NET Runtime;eventId=1025 | 1 | sì |

La selezione è il round-robin esistente, con 12 slot: non una perdita casuale, un errore del matcher o un troncamento dei record di modello. Il blocco dichiara quattro fatti aggiuntivi omessi e gli step limitati 0, 1, 2. F1.10 conta riferimenti a dump, non l'accessibilità dei file. La sua omissione non elimina F1.3–F1.5: la partizione necessaria a distinguere 43 report da 42 segnalazioni kernel era già mostrata.

Non esistono tra i Facts una mappatura `0x193 → VIDEO_DXGKRNL_LIVEDUMP`, una classificazione causale, un conteggio di incidenti indipendenti o un totale tipizzato dei report illeggibili. `448` e `42` si trovano nei detail testuali delle fonti di `Steps[1].Result.Output`, non in EvidenceFact numerici. Il check è istruito a confrontare i fatti del ledger, non a convalidare tutta la conoscenza tecnica o a costruire somme da detail testuali.

Il JSON crashes dichiara `partial`, `complete=false`, `truncated=false`, 43 observedItems, 10 observedGroups e 10 returnedGroups. Stability è analogamente Partial e non troncato. Partial qui riguarda fonti non leggibili, non il taglio dei dieci gruppi. Il runtime distingue anche osservazioni accorciate; `ToolResultCompleteness` non ha un valore Truncated: il troncamento del payload appartiene al formato del tool, oppure è rilevato come accorciamento dell'osservazione dal runtime.

## Causa tecnica accertata e ipotesi

Percorso verificato nel codice: `AgentRunner.cs`, ramo finale e `GuardGroundingAsync`; `EvidenceGrounding.cs`, Build/ParseVerdict/CorrectionInstruction/RepeatsContradiction; `FinalResponse.cs` per i marker.

1. Il checker è lo stesso modello. Produce una citazione di contraddizione, non una rappresentazione tipizzata di tutte le affermazioni della risposta. L'unica contraddizione restituita è F1.7. Il parser verifica forma chiusa, ID mostrato e presenza letterale della quote; non verifica che quella quote contraddica semanticamente quel fatto.
2. Il runtime tratta Type/Key dei fatti come identificatori opachi (contratto EvidenceFact/ADR-0050). Non somma categorie, non conosce il vocabolario Windows e non associa una frase italiana a un insieme di fatti.
3. La correction è accettata se contiene risposta utilizzabile (F1), non propone tool o artefatti di protocollo, non ha forma di verdetto, mantiene l'heading delle limitazioni e non ripete la quote contestata dopo normalizzazione degli spazi. Non c'è un secondo controllo semantico. Cambiare la parte «7 bugcheck» basta a rimuovere l'intera quote originale, pur lasciando NTFS e 43 kernel.
4. La disclosure deterministica aggiunge limitazioni senza cambiare la prosa originale. Preservare l'heading non dimostra che le affermazioni di completezza nella prosa siano corrette. Il ramo finale arriva quindi legittimamente secondo il contratto corrente a Completed con marker Corrected, senza certificare la diagnosi.

Non è accertabile da questi dati perché Qwen abbia internamente omesso la contestazione di 43 kernel o inventato NTFS. L'istruzione che esclude le ipotesi e quella che chiede di mantenere il resto possono contribuire, ma sono spiegazioni possibili, non cause dimostrate. Non attribuiamo l'errore a temperatura, quantizzazione, budget, compattazione o replay senza prova.

## Decisione e proposta minima, non implementata

Non c'è un intervento locale nel contratto attuale che offra la verifica richiesta senza riconoscere il significato della prosa. Aggiungere solo il numero 42 al ledger o una stringa col nome Microsoft migliorerebbe il contesto ma lascerebbe lo stesso LLM come unico giudice. Un secondo check aggiungerebbe chiamate senza indipendenza. Cercare `43`, `kernel`, `0x193` e `NTFS` nel testo confonderebbe negazioni, citazioni, esempi, periodi diversi e ipotesi. Respingerne la finale cambiando Completed maschererebbe il difetto. Nessuna di queste alternative è stata implementata.

Il più piccolo disegno strutturato credibile da valutare in un'attività distinta richiede:

1. **Affermazioni chiuse con provenienza**: le osservazioni numeriche della finale devono riferirsi a task/step, scope della query, tipo/chiave del fatto, valore e modalità (conteggio osservato/minimo, totale, ipotesi). Le somme richiedono operandi espliciti e una partizione disgiunta attestata dal produttore; non si sommano displayFault e live dump di fonti sovrapposte.
2. **Riferimento Windows verificato nel package**: tabella limitata, versionata e con fonte Microsoft per i codici ammessi, separata dalle osservazioni della macchina. Un codice sconosciuto resta sconosciuto. Il core non interpreta nuove stringhe package-specifiche. Nessuna mappatura dimostra una causa storage/video.
3. **Verifica deterministica e resa vincolata**: confrontare le tuple con i fatti originali validati, confrontare la completezza con i metadati dello stesso scope, calcolare solo somme consentite. Rendere le osservazioni dal risultato verificato. Un JSON di claim accanto a prosa libera non garantirebbe che la prosa dica la stessa cosa: estrarre claim con Qwen potrebbe omettere proprio l'errore. Questo legame tra claim e testo è la parte architetturale necessaria; non basta estendere ParseVerdict.
4. **Riutilizzo del grounding**: le contraddizioni verificate possono alimentare la correction già limitata a un tentativo, mantenendo ID e provenienza; anche la correction deve rispettare il medesimo contratto. Nessuna nuova capacità operativa, nessun cambio di budget o dello stato Completed come scorciatoia. Le ipotesi restano esplicite e fuori da una certificazione causale, senza rendere lecite premesse fattuali false.

Sono necessari schema, responsabilità core/package e regola di rendering, quindi una decisione architetturale preventiva. Questa attività non li realizza automaticamente. Il proposal non promette verifica semantica generale o completezza di tutte le possibili affermazioni diagnostiche.

## Test offline

Modificato solo `tests/bOps.Runtime.Tests/EvidenceGroundingLoopTests.cs`: 12 nuovi casi `T02_`, fatti minimizzati dagli export, nessun accesso Windows o provider reale. Percorso del dump sanitizzato nel caso di selezione. Tutti passano.

| Casi aggiunti | Risultato dimostrato |
|---|---|
| LedgerReproduction (1) | Stessa selezione dei 12 ID; F1.10 omesso e quattro omissioni dichiarate; partizione kernel/WER presente |
| CitedCountOrCategoryContradiction (3) | Se il checker cita 43 kernel, 28 live dump o la frase reale dei 7 bugcheck, il loop già esistente accetta una correction supportata in un solo tentativo, senza nuovi tool |
| NegativeControls (5) | Risposta corretta, negazione di 43 kernel, totale sconosciuto, esempio ipotetico di 43 bugcheck e ipotesi storage non causale vengono conservati quando il checker non cita contraddizioni |
| KnownDefect_PartialCorrection (1) | Rimangono NTFS e 43 kernel dopo la correzione dei 7 bugcheck; oggi Completed/Corrected |
| KnownDefect_Disclosure (1) | Un'affermazione di completezza su Partial può restare nella finale con disclosure se il checker non la cita |
| KnownDefect_ShortenedObservation (1) | Lo stesso limite per un'osservazione accorciata da 9000 caratteri, pur segnalata nel prompt |

I casi KnownDefect sono **caratterizzazione di difetti aperti**, non criteri di accettazione diagnostica. I controlli negativi esercitano il runtime con verdetti scriptati: non dimostrano che Qwen distingua correttamente ipotesi e osservazioni. Analogamente i casi positivi non sostituiscono la verifica indipendente di una citazione. Non è stato aggiunto un falso test che dichiari risolta l'associazione NTFS.

Eseguiti con SDK .NET 10.0.401, `--no-restore --logger 'console;verbosity=minimal'`:

| Progetto e filtro FullyQualifiedName | Pass / fail / skipped |
|---|---|
| bOps.Runtime.Tests: EvidenceGrounding, EvidenceDisclosure, EvidenceLimitations, FinalResponse, FinalAnswer, EarlyFinal, EmptyResponse, OutputLimit (unione OR) | **412 / 0 / 0** |
| bOps.AgentFramework.Tests: PromptAssembly oppure ScriptedAgentic | **155 / 0 / 0** |
| bOps.Packages.System.Core.Tests: HighDensityFacts, EvidenceCompleteness oppure SystemEventFacts | **64 / 0 / 0** |

Totale finale mirato **631 test**, senza contare due volte la prima esecuzione dei nuovi casi. Build con warnings-as-errors riuscita. La prima compilazione di sviluppo aveva usato un inesistente enum Truncated nel nuovo test: corretto modellando l'accorciamento reale dell'osservazione, senza modifiche al prodotto. Golden non aggiornati. PA-1/AE-1, LIVE-05, F1 e i relativi percorsi di recovery mantengono i controlli esistenti.

La modifica preesistente al csproj AgentFramework e tutti gli harness/documenti/golden già presenti sono stati preservati. Nessun commit, push, merge, rebase, reset o clean. Nessuna interrogazione Qwen, nuova cattura Windows o scrittura in altri worktree/snapshot.

## Limiti residui e rivalidazione minima

La produzione resta esposta al difetto osservato: non viene dichiarato risolto. Il ledger rimane limitato a 12 fatti e quattro codici per chiamata crashes; una citazione valida non è una prova di contraddizione; una correction accettata non certifica le altre affermazioni. I tempi di report, gli incidenti distinti, le somme da detail e le inferenze causali restano fuori da una verifica deterministica. Disclosure e Completed non equivalgono a correttezza diagnostica.

Piano futuro, **non eseguito**, dopo approvazione e realizzazione del nuovo contratto:

1. Tre task indipendenti sul medesimo T02-C congelato, stesso goal/configurazione Qwen, stesso replay/matcher e pack Knowledge della campagna. Nessuna nuova cattura né modifica dei budget. Registrare gli hash degli input e dei binari del candidato.
2. Conservare RequestJson/ResponseJson di ogni chiamata, audit, state e finale realmente persistita, inclusi recovery/check/correction. Contare il costo completo e verificare che il candidato mantenga LIVE-05, F1 e le invarianti di sicurezza.
3. In ciascuna run verificare con i dati strutturati e il riferimento Microsoft, senza Qwen come unico giudice: 0x193 live dump grafico; 43 elementi con 42 segnalazioni kernel e 1 WER; 10 gruppi distinti dagli incidenti; 7 bugcheck 0x1e; 490 solo se viene esplicitamente sommato 448+42; Partial/minimi e tempi di segnalazione preservati. Nessuna causa dedotta come dimostrata dalla correlazione.
4. Rivedere separatamente le formulazioni lecite e ipotetiche e l'eventuale assenza di claim errati nella prosa. Criterio diagnostico: zero contraddizioni verificabili residue in **tutte e tre** le finali consegnate; una run fallita o una finale errata non è successo, anche se il runtime completa. Tre run sono un canary circoscritto, non una stima di affidabilità generale.

Ripetere ora tre run senza modifica produttiva misurerebbe soltanto di nuovo il limite noto. Si conclude pertanto con proposta e test offline, come consentito dal brief.
