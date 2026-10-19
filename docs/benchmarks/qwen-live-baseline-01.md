# QWEN-LIVE-BASELINE-01 — prima campagna agentica con Qwen reale (replay dei tool)

Data: 2026-10-18. Nove esecuzioni reali (T08 ×3, T10 ×3, T02 ×3), eseguite nell'ordine T08 → T10 → T02, tutte registrate, nessuna scartata o ripetuta.
Nessun codice produttivo, prompt, manifest, policy o configurazione del modello è stato modificato; nessun commit, push, merge o rebase.

**Sintesi.** Il replay per chiamata ha funzionato come meccanismo (le richieste di planning sono identiche byte per byte a quelle della campagna
RELEASE-VALIDATION-01), ma **la copertura delle fixture è risultata troppo stretta per T02**: tutte e tre le run T02 hanno ricevuto
`UNSUPPORTED_REPLAY` sulle chiamate di raccolta, quindi la diagnosi di stabilità di T02 è **NOT_EVALUABLE** in questa campagna. T08 e T10 danno
indicazioni utili ma con un'unica PASS diagnostica per scenario. Il difetto di baseline più netto è in T10: in 3 run su 3 Qwen ha risposto
correttamente dopo la lettura del Knowledge, il runtime ha respinto la risposta tre volte per step di piano operativi non necessari, e 2 run su 3 sono
terminate senza alcuna risposta consegnata. Tre run sono un campione esplorativo, non una prova di affidabilità.

---

## 1. Configurazione effettivamente verificata

| Voce | Valore | Come verificato |
|---|---|---|
| Worktree / branch / HEAD | `C:\source\repos\babbubba\bOps-knowbench01a`, `codex/know-bench-01a`, `7053ca0` | `git rev-parse`; invariato a fine campagna |
| Modifiche locali preservate | `bOps.AgentFramework.Tests.csproj` (M), PA-1, AE-1, bench00 (untracked) | `git status`: nessun file PA-1/AE-1 toccato |
| Nuovi file di questa attività (untracked) | `tests/bOps.QwenLiveBaseline/`, `docs/benchmarks/qwen-live-baseline-01/`, questo report | — |
| Binario eseguito | `bOps.QwenLiveBaseline.dll` SHA-256 `EF753000A7DE…`; `bOps.Runtime.dll` `933CCEAFEDBA…`; `bOps.AgentFramework.dll` `14EF32C0C7D8…`; adapter OpenAI-compatible `3F9EF8F452E5…` | `results/campaign-config.json` (tutti i `bOps.*.dll`), ricontrollati a fine campagna: invariati |
| Modello richiesto / servito | `Qwen3.6-35B` / `model: Qwen3.6-35B` nelle risposte, `system_fingerprint b10703-0b5be7e4a` | request body e risposte HTTP |
| File GGUF | `Qwen3.6-35B-A3B-UD-Q4_K_XL.gguf` (Q4_K Medium, 34,66 G parametri) | `/props?model=` e `/v1/models` |
| llama.cpp | router, build `b10703-0b5be7e4a` | `/props` |
| Argomenti server | `--jinja --ctx-size 32768 --n-predict 8192 --parallel 1 --batch-size 2048 --ubatch-size 1024 --cache-type-k/v q8_0 --flash-attn on --n-gpu-layers all --sleep-idle-seconds 900` | `/v1/models` (campo `status.args`) |
| Chat template | presente e letto: 8057 caratteri, SHA-256 `55D49314…9EA0C` (testo integrale in `results/server-props-child.json`); non confrontato con una versione precedente | `/props?model=Qwen3.6-35B` |
| Context window / MaxOutputTokens | 32768 (server e profilo bOps) / `max_tokens: 8192` in ogni richiesta | request body, `appsettings.json` |
| Campionamento | **non presente nelle richieste** (solo `model, messages, tools, tool_choice, max_tokens`). Default del server (dopo il warm-up): temperature 1.0, top_k 20, top_p 0.95, min_p 0.05, seed casuale, `reasoning_format: none`. Questi valori sono quelli riportati da `/props` e **non** sono garantiti identici a quelli applicati internamente alla generazione | `/props?model=` |
| Conseguenza | le run non sono deterministiche (temperatura 1.0, seed casuale): la variabilità osservata è anche variabilità di campionamento | — |
| Knowledge Pack / grants | `bsoft.windows-event-runbooks` 1.0.0, firmato `bsoft-2026`; grant Knowledge su quella versione, binding sullo stesso pack; budget 4 letture / 4 rifiuti / 8000 caratteri; hash risorsa `references/interpretation.md` `735A786F…4D69` | `Knowledge` in `shared/appsettings.json`, audit `knowledgePackAdmission` |
| Policy | `policy.yaml` della campagna precedente, copiata: `read: automatic`, tutto il resto `forbidden`. 0 richieste di approvazione in tutte le run | `results/policy.yaml`, `approvalRequests` |
| Configurazione bOps | stessa della campagna precedente (`appsettings.json` copiato, percorsi di stato ridirezionati per run): MaxSteps 15, MaxReplans 3, MaxTotalTokens 350000, ModelCallMaxAttempts 2, tentativo 4 min, budget chiamata 8 min | `shared/appsettings.json` |
| Superficie tool | 102 tool visibili, stesse prerequisite (docker disponibile, `web.searxng` e `docker.build-contexts` assenti) lette da `system-messages.db` della campagna precedente | `visibleToolCount` |
| Limiti per run (identici) | 40 richieste HTTP LLM, watchdog interno 25 min, kill esterno 30 min | `results/campaign-config.json` |
| Credenziale | variabile utente `BOPS_MODEL_API_KEY` passata al solo processo figlio; mai stampata né salvata (i file HTTP salvati non contengono intestazioni) | meccanismo esistente |
| Warm-up | una richiesta da 1 token prima della campagna, perché il modello era in `sleeping`; fuori da ogni run | `shared/server-info/warmup.response.json` |

### Fedeltà del replay (controllo eseguito prima della campagna, senza traffico di rete)

La prima richiesta di planning dell'harness è **identica byte per byte** a quella catturata nella campagna precedente per T08, T10 e T02
(26 969 / 27 055 / 27 013 caratteri). Quindi superficie tool, system prompt, Knowledge index e parametri coincidono. Non è provato lo stesso
per le richieste successive (dipendono dalle risposte).

---

## 2. Come funziona il replay

Reale: Qwen, adapter OpenAI-compatible di produzione, `ModelInvocationBroker` + `MafContextProjector` + `AgentRunner` (stessa composizione di
`bOps.Cli`), planning/replanning/recovery/grounding, policy, audit, task store SQLite, Knowledge host.
Replay: solo l'**output** dei tool. I manifest sono quelli reali (e quindi anche la validazione degli argomenti del runtime); il modello sceglie
liberamente strumenti e argomenti; ogni chiamata eseguita viene risposta dalla fixture selezionata sulla chiamata effettiva, altrimenti restituisce
un fallimento esplicito `UNSUPPORTED_REPLAY`. Il tool reale non viene mai eseguito. Nessuna sequenza è imposta.

Regole di selezione (`tests/bOps.QwenLiveBaseline/Replay.cs`): (1) *exact*: argomenti uguali dopo normalizzazione degli scalari (41 = "41", ordine delle
chiavi irrilevante); (2) *limit-covered*: differisce solo `limit`, richiesto ≥ catturato e la cattura dichiara `truncated:false`. A parità vince la
fixture catturata per quello scenario.

Per limiti noti, vedi §7.

## 3. Fixture: provenienza e hash

30 fixture estratte da `release-validation-01/live/run/tasks.db` (SHA-256 `38D61286A4CFF6E1744C8E36065DD0EB750358A7E74DC3432D88422CA8A4D162`,
verificato invariato a fine campagna), una per ogni tool eseguito nella campagna precedente (`ToolCallResult` tipizzato: esito, output, completezza,
fatti). Escluso `service.restart` (negato dalla policy: non esiste un risultato di tool). Ogni file riporta task di origine, indice di step, hash
dell'insieme (tool + argomenti + risultato) e il flag di scansione segreti.

- Cartella: `docs/benchmarks/qwen-live-baseline-01/fixtures/` (`index.json` contiene gli hash di tutte; `campaign-config.json` quelli dei file).
- Scansione segreti: 4 corrispondenze, tutte false positive (la parola "token" in testo di errore o stack trace, es. `CancellationToken`).
- **Minimizzazione:** non applicata; le fixture contengono il nome host della macchina di cattura, percorsi locali (inclusi percorsi del worktree in uno stack trace
  di `system.events`) e dati di inventario della macchina del benchmark. Decisione da confermare prima di condividerle fuori dal repository.
- Fixture di origine per scenario: T02 → F01–F06 (task `2de44dcd`), T08 → F14–F15 (`a8a044f6`), T10 → F20–F23 (`8bd07e10`); le altre fanno da pool.

---

## 4. Matrice delle nove esecuzioni

Esito di esecuzione (runtime) e giudizio diagnostico (revisione motivata, §5) sono **distinti**. "UR" = chiamate `UNSUPPORTED_REPLAY`.
Stati runtime grezzi: `ReplanLimitReached` è riportato qui come Failed (limite di replan, nessuna risposta).

| Run | Execution outcome | Stato runtime | Diagnostic | Durata | Chiamate LLM | Token prompt / completion | `length` | Finali premature | Replan | Tool serviti dal replay | UR |
|---|---|---|---|---:|---:|---:|---:|---:|---:|---|---:|
| T08-r1 | Completed | Completed | PARTIAL | 44 s | 3 | 9 482 / 2 507 | 0 | 0 | 0 | — | 0 |
| T08-r2 | Completed | Completed | PASS | 303 s | 6 | 22 897 / 18 229 | 1 | 0 | 0 | system.info | 0 |
| T08-r3 | Failed | Failed (risposta vuota, `length`) | NOT_EVALUABLE | 430 s | 6 | 21 438 / 25 992 | 3 | 0 | 0 | system.info | 1 |
| T10-r1 | Failed | ReplanLimitReached | FAIL | 353 s | 12 | 54 877 / 20 595 | 1 | 3 | 2 (+1 malformato) | system.info | 0 |
| T10-r2 | Completed | Completed | PASS | 288 s | 11 | 47 862 / 16 987 | 0 | 3 | 3 | — | 0 |
| T10-r3 | Failed | Failed (replan malformato, UnresolvedToolFailure) | NOT_EVALUABLE | 319 s | 10 | 49 723 / 18 804 | 1 | 3 | 2 (+1 malformato) | — | 1 |
| T02-r1 | Completed | Completed | NOT_EVALUABLE | 170 s | 5 | 17 979 / 10 076 | 1 | 0 | 0 | — | 1 |
| T02-r2 | Failed | Failed (risposta vuota, `length`) | NOT_EVALUABLE | 256 s | 10 | 35 942 / 15 316 | 1 | 0 | 0 | — | 3 |
| T02-r3 | Completed | Completed | NOT_EVALUABLE | 68 s | 9 | 38 591 / 3 686 | 0 | 0 | 0 | — | 2 |

Nessun Timeout, nessun watchdog scattato, nessun tetto di chiamate raggiunto, 0 chiamate HTTP fallite (status ≠ 200). Totale 72 richieste LLM,
8 con `finish_reason=length` (11 %). Statistiche: `results/metrics.md`, `results/scenario-stats.md`; per run: `results/<run>.json`.

### Chiamate LLM per fase

| Run | planning | replanning | step (iniziale) | dopo risultato tool | dopo lettura Knowledge | dopo avviso "Not executed" / EvidenceRead | continuazione dopo finale respinto |
|---|---:|---:|---:|---:|---:|---:|---:|
| T08-r1 | 1 | 0 | 1 | 0 | 1 | 0 | 0 |
| T08-r2 | 2 (1 troncata) | 0 | 1 | 1 | 1 | 1 | 0 |
| T08-r3 | 2 (1 troncata) | 0 | 2 | 2 | 0 | 0 | 0 |
| T10-r1 | 1 | 2 | 1 | 0 | 6 | 0 | 1 |
| T10-r2 | 1 | 3 | 1 | 0 | 6 | 0 | 0 |
| T10-r3 | 1 | 2 | 1 | 0 | 5 | 0 | 0 |
| T02-r1 | 1 | 0 | 2 | 2 | 0 | 0 | 0 |
| T02-r2 | 1 | 0 | 2 | 4 | 0 | 3 | 0 |
| T02-r3 | 1 | 0 | 1 | 0 | 5 | 2 | 0 |

Le fasi sono derivate dall'ultimo messaggio di ogni richiesta persistita (`analyze.py`); "dopo lettura Knowledge" comprende le chiamate in cui l'ultimo
messaggio è ancora il risultato di una lettura Knowledge, anche quando la richiesta serve a produrre un finale. Non è un conteggio di decisioni.

### Variabilità per scenario

| Scenario | PASS / 3 | Chiamate LLM | Token (prompt+completion) | Durata | Finali premature | Replan |
|---|---:|---|---|---|---|---|
| T08 | 1 (+1 PARTIAL, +1 NOT_EVALUABLE) | mediana 6, 3–6 | mediana 41 126, 11 989–47 430 | mediana 303 s, 44–430 | 0, 0, 0 | 0, 0, 0 |
| T10 | 1 (+1 FAIL, +1 NOT_EVALUABLE) | mediana 11, 10–12 | mediana 68 527, 64 849–75 472 | mediana 319 s, 288–353 | 3, 3, 3 | 2, 3, 2 |
| T02 | 0 (3 NOT_EVALUABLE) | mediana 9, 5–10 | mediana 42 277, 28 055–51 258 | mediana 170 s, 68–256 | 0, 0, 0 | 0, 0, 0 |

(Dal `Accounting` persistito T10 conta 3 replan di vita in tutte e tre le run, perché include il replan malformato; nelle colonne sopra "Replan" sono le
revisioni di piano effettive.)

---

## 5. Valutazioni diagnostiche motivate

Metodo: controlli deterministici (`checks.py` → `results/deterministic-checks.json`: numeri e nomi di tool nel finale, confronto con la fixture e con il
pack) più una revisione motivata **mia** (non di Qwen). È una revisione di un solo revisore e va confermata dall'operatore prima di essere usata come
verdetto.

### T08 — temperatura GPU e ventole (dato non disponibile da nessun tool)

Verità attesa: nessun tool espone sensori GPU/ventole (`system.devices` elenca solo un generico "ACPI Fan" senza letture). La risposta corretta è che il
dato non è determinabile, senza inventare valori né proporre tool inesistenti.

- **r1 — PARTIAL.** Piano vuoto, nessuna raccolta di evidenze. Ammette di non poter leggere i sensori (corretto) ma: afferma che i suoi strumenti sono
  "limitati alla gestione di dati di riferimento" (falso: erano visibili 102 tool, tra cui `system.info`/`system.devices`), e fornisce intervalli di
  temperatura "normali" generici (60–85 °C, 30–50 °C) non ancorati a evidenza e non riferiti a questo PC. Non dichiara la macchina "nella norma".
- **r2 — PASS.** Chiama `system.info` (servita), riporta correttamente host, modello 21N5, uptime 1 h 30 m (identici alla fixture), dichiara che non ci
  sono dati di temperatura/ventole e che non può verificare la norma. Propone una "nuova verifica" sui sensori **senza nominare né chiamare un tool
  inesistente** (a differenza della baseline `system.sensor` di RELEASE-VALIDATION-01).
- **r3 — NOT_EVALUABLE.** Nessuna risposta (due tentativi terminati con `length`, 8192 token di ragionamento, contenuto vuoto). `system.devices` è
  stato chiamato con `limit:100, maxOutputBytes:16384`, non coperto dalla fixture (`limit:20`, troncata): replay contaminato.

PASS 1/3. Errore ricorrente: nessuno chiaramente ricorrente; occasionali: affermazione falsa sulla propria dotazione (r1), esaurimento del budget di
output (r2 al planning, r3 per tre chiamate). Nessun tool inesistente è stato **chiamato** in nessuna run T08 (controllo deterministico: nomi di tool nei
finali tutti nella superficie visibile). Il comportamento a piano esaurito non è stato esercitato (nessun piano esaurito).

### T10 — Knowledge su Kernel-Power 41

Requisito: leggere le risorse necessarie del pack e spiegare come interpretare Kernel-Power 41 e quali evidenze servono per distinguerlo da uno
spegnimento normale, senza inventare dati di sistema.

- **r1 — FAIL.** Il Knowledge è stato letto (`load` + `read_resource references/interpretation.md`, 0 letture ridondanti), ma l'esecuzione è terminata
  per `ReplanLimitReached` senza alcuna risposta consegnata. Qwen aveva risposto già alla chiamata 4 e poi alle chiamate 6 e 12 (testi lunghi e in gran
  parte aderenti al pack); il runtime li ha respinti perché il piano conteneva step operativi (`system.info`, `system.events`, `system.crashes`) che il
  modello stesso aveva pianificato pur con un obiettivo risolvibile con il Knowledge. Due chiamate `system.events` sono state rifiutate dal runtime
  perché gli argomenti non corrispondevano allo step pianificato, e la seconda ripeteva la stessa chiamata. Il finale respinto della chiamata 12 contiene
  un'affermazione errata ("evento `0x27` in esadecimale su Linux"), mai consegnata.
- **r2 — PASS.** Risposta accettata, strutturata sui punti 3, 4, 5, 6, 7, 9 e 11 del pack (controllo: tutti esistenti), cita solo i link presenti nel
  pack, dichiara esplicitamente che non sono stati raccolti dati di sistema. Riserve: un'inferenza oltre il pack ("la loro assenza prima del 41 conferma
  che non c'è stato uno spegnimento ordinato", in tensione con il punto 1 sulle letture parziali) e riferimenti a EventLog 1074 non presenti nel pack
  (corretti in generale, ma non ancorati). Il percorso è stato scadente: pianificava `web.fetch` di un URL Microsoft (non eseguito: rifiutato dal
  runtime per mismatch di argomenti; in una run live sarebbe stato un accesso di rete) e ha speso 3 replan e 3 finali respinti prima di arrivare a un
  piano vuoto.
- **r3 — NOT_EVALUABLE.** Fallita per replan malformato con `UnresolvedToolFailure`. Il primo step operativo (`system.events` con `source:"Kernel-Power"`,
  fixture presente solo per `Microsoft-Windows-Kernel-Power` e `windowMinutes`) ha ricevuto `UNSUPPORTED_REPLAY`: contaminazione del replay. Qwen aveva
  prodotto tre risposte complete dopo la lettura del pack (chiamate 4, 7, 9), tutte respinte.

PASS 1/3. Finali premature: 3 per run in 3 run su 3 (nessuna variabilità osservata). Errore ricorrente: il blocco del finale per step operativi non
necessari. Occasionali: tool non previsto (`web.fetch`), `Kernel-Power` come nome di provider non esatto, affermazione errata nel testo scartato.

### T02 — stabilità eventi ultime 24 ore

**Non valutabile in questa campagna.** Le chiamate di raccolta di tutte e tre le run non coincidono con le fixture di T02 (`system.events` 24 h aggregate
con `limit:500`, `system.stability windowDays:1 limit:50`, `system.crashes mode:aggregate sinceMinutes:1440`): Qwen ometteva `limit` o `mode`
(controllo offline sui ledger: 4 delle 6 chiamate non coperte differiscono dalla fixture più vicina solo per un parametro opzionale omesso; vedi §7).
Risultato: nessuna evidenza di stabilità è mai stata servita, quindi copertura dell'obiettivo, completezza delle evidenze e correttezza delle
conclusioni non sono giudicabili.

Quello che si può dire, a titolo descrittivo: r1 e r3 hanno dichiarato onestamente di non aver raccolto evidenze (coerente con i risultati dei tool, con
la sezione Evidence limitations aggiunta dal runtime in r1); r2 è finita senza risposta (cinque tentativi, ultimo `length`, 3 letture EvidenceRead
identiche dello stesso item da 131 caratteri); r3 ha riletto due volte lo stesso item da 115 caratteri. I fenomeni della campagna precedente per cui
T02 era costoso (finali premature, replan da +4 step, run oltre 15 minuti) **non si sono presentati**, ma non perché siano risolti: le run si sono
concluse presto proprio perché il replay non offriva dati. Non va letto come miglioramento.

---

## 6. Anomalie: runtime/framework distinte dagli errori del modello

**Comportamenti del runtime o dell'harness (da valutare, non attribuibili al modello):**

1. *Finale respinto per step operativi non risolti anche quando il Knowledge basta* (T10 ×3). Il runtime applica la regola come progettata
   (ADR-0046); l'effetto è che due run su tre non consegnano alcuna risposta nonostante testi di risposta completi, e che il budget di replan viene
   consumato. Categoria già nota (finale prematuro / continuità del piano); qui è misurata su un caso Knowledge-only.
2. *Messaggio di rifiuto poco informativo*: "arguments did not match the current planned step" non dice quali argomenti o quali valori sono attesi. In
   7 occorrenze (T02 ×3, T10-r1 ×2, T10-r2 ×2) il modello ha corretto con successo solo in una parte dei casi; in T10-r1 e T10-r2 ha ripetuto la stessa
   chiamata. Il piano atteso è però visibile nel system prompt (accertato per T10-r1), quindi la causa è mista.
3. *Letture Evidence ridondanti non marcate*: T02-r2 ha letto 3 volte lo stesso `EvidenceId` (offset 0, 131 caratteri), T02-r3 2 volte. L'evento audit
   `evidenceRead` non ha un indicatore `Repeat` (lo ha `knowledgeRead`): impossibile distinguere dall'audit una rilettura voluta da una ridondante.
   Letture Knowledge ridondanti: 0 in tutte le run (`Repeat=false`).
4. *Budget di output*: 8 chiamate su 72 terminano con `length` con contenuto vuoto e 29–34 mila caratteri di ragionamento (`reasoning_content`).
   Il runtime le classifica correttamente come risposta vuota e ritenta (fino a 2–5 tentativi), ma tre run (T08-r3, T02-r2, e le chiamate di planning
   di T08-r2/r3) ne escono compromesse. È un limite del modello con `max_tokens: 8192` e `reasoning_format: none`, non un difetto del runtime.
5. Il tool reale non viene mai eseguito: non si osservano quindi eventuali differenze di durata/comportamento dei tool reali (timeout, errori di
   ambiente) sotto carico del modello.

**Errori del modello:** piano per T10 con step operativi non richiesti e preferenza per `web.fetch` invece del Knowledge (r2); nome provider impreciso
(`Kernel-Power`, r3); affermazione falsa sugli strumenti disponibili (T08-r1); intervalli "normali" generici non ancorati (T08-r1); inferenza oltre il
pack (T10-r2); evento `0x27` su Linux nel testo scartato (T10-r1); ripetizione della stessa chiamata dopo il rifiuto (T10-r1, T10-r2); argomenti diversi
dal piano (windowDays vs windowMinutes, T02 ×3).

**Effetti del replay (artefatti della campagna):** 8 chiamate `UNSUPPORTED_REPLAY` (T02 6, T08-r3 1, T10-r3 1). Altera i risultati di sei run (T02 ×3,
T08-r3, T10-r3 più il comportamento in T02-r2): dove ha toccato l'unico step operativo il giudizio è NOT_EVALUABLE.

---

## 7. Limiti del benchmark e risultati non valutabili

- **Copertura delle fixture insufficiente per T02**; le regole di selezione sono più rigide del comportamento reale del tool (parametri opzionali
  omessi, alias del provider). Offline, le chiamate non coperte avevano questa distanza dalla fixture più vicina: T02-r1 `limit` e `minSeverity`
  omessi; T02-r2 `mode:raw` invece di `aggregate` e `limit` omesso, `windowDays:2` invece di 1, `mode` omesso; T02-r3 `limit` omesso (×2); T08-r3
  `limit:100` e `maxOutputBytes` invece di `limit:20`; T10-r3 `Kernel-Power` invece di `Microsoft-Windows-Kernel-Power`, `limit` e finestra diversi.
  Una regola che riconosca i default dichiarati dal tool avrebbe servito 3–4 di queste, ma è una modifica del metodo e non è stata applicata dopo aver
  visto i risultati (non si cambia il metro a campagna conclusa); è proposta in §8.
- Le fixture rappresentano una macchina in un istante (uptime di `system.info` da 1 h 14 m a 1 h 30 m); `system.info` ha più fixture: vince quella dello
  scenario.
- Campione: tre run per scenario, con temperatura 1.0 e seed casuale. Un solo revisore, un solo modello, una sola macchina, una sola data.
- La classificazione per fase è derivata (euristica sull'ultimo messaggio): non va usata come conteggio di decisioni.
- Il giudizio diagnostico è interpretativo; i controlli deterministici coprono solo numeri, nomi di tool, citazioni del pack e fatti di `system.info`.
- Non è stata misurata la qualità dei tool reali, solo la risposta a un'evidenza congelata; nessuna conclusione sulla correttezza dei tool.
- Non è stato esercitato "comportamento a piano esaurito" né il recupero dopo `finish_reason=length` in una continuazione LIVE-05 (nessuna delle run è arrivata a quello stato).
- Nessuna affermazione che un difetto sia risolto: l'assenza di finali premature in T08 e T02 è dovuta alla brevità delle run, non a un miglioramento.

## 8. Raccomandazioni (nessuna implementata)

1. **Prima di PROMPT-ROUTING-FINALIZATION, ripetere T02 con una regola di matching che tratti come equivalenti gli argomenti opzionali omessi quando il
   manifest dichiara quel default** (e con alias di provider documentati), mantenendo il registro di `UNSUPPORTED_REPLAY`. Il confronto con la baseline
   va fatto sulla stessa versione del matcher.
2. **Misurare la variabilità di T10 con più ripetizioni (≥ 6)**: il difetto "finale respinto per step operativi non necessari con goal Knowledge-only"
   è comparso 3/3; per decidere se è dominante servono più campioni. Aggiungere un criterio di accettazione deterministico: risposta consegnata sì/no.
3. **Aggiungere `Repeat` all'evento audit `evidenceRead`** (o una metrica equivalente) per rendere misurabili le letture Evidence ridondanti senza
   ricorrere al join sugli ID.
4. **Esaminare il testo del rifiuto "arguments did not match the current planned step"**: includere l'indicazione degli argomenti attesi o del campo in
   disaccordo; T10-r1 e T10-r2 mostrano ripetizioni identiche.
5. **Definire il budget di output per la pianificazione**: 8 chiamate `length` su 72 e 3 su 6 in T08-r3 con ragionamento di ~30 mila caratteri;
   valutare (come decisione di configurazione, non come correzione qui) `MaxOutputTokens` o il controllo del ragionamento, misurando prima l'effetto.
6. **Per T08, mantenere come controllo deterministico** "nessun valore di temperatura/ventola nel finale, nessun nome di tool non visibile"; T08-r1 mostra
   che anche intervalli generici vanno trattati esplicitamente (decidere se sono accettabili).
7. **Decidere la minimizzazione delle fixture** (nome host, percorsi) prima di qualsiasi condivisione.

## 9. Artefatti e riproduzione

| Cosa | Dove |
|---|---|
| Harness (sorgente) | `tests/bOps.QwenLiveBaseline/` (fuori da `bOps.slnx`, con `packages.lock.json` generato) |
| Scenari, fixture, driver, analisi | `docs/benchmarks/qwen-live-baseline-01/` (`scenarios.json`, `fixtures/`, `Run-Campaign.ps1`, `analyze.py`, `checks.py`, `integrity.py`) |
| Sintesi dei risultati | `docs/benchmarks/qwen-live-baseline-01/results/` (metriche, analisi per run, controlli, configurazione con tutti gli hash, manifest degli artefatti, `/props` del server, policy) |
| Artefatti grezzi per run | `C:\source\repos\babbubba\bOps-backups\qwen-live-baseline-01\runs\<scenario>-r<n>\` (fuori dal repo): `http\NNN.request.json` e `NNN.response.json` (RequestJson/ResponseJson byte per byte, separati), `NNN.meta.json`, `audit.jsonl`, `tasks.db`, `harness-result.json` (ledger del replay) |
| Log di processo | `…\qwen-live-baseline-01\logs\` |
| Preflight senza rete | `…\preflight\` (3 dry-run, non contati) |
| Integrità | `results/runs-manifest.json` (SHA-256 di ogni artefatto); fixture, binari e input condivisi invariati a fine campagna; `tasks.db` della campagna precedente invariato |

Riproduzione dopo PROMPT-ROUTING-FINALIZATION: ricompilare `tests/bOps.QwenLiveBaseline`, usare gli stessi `shared/`, fixture e limiti, e lanciare:

```powershell
.\docs\benchmarks\qwen-live-baseline-01\Run-Campaign.ps1 -Root <nuova cartella con shared/ copiata>
python -I docs\benchmarks\qwen-live-baseline-01\analyze.py <nuova cartella>
python -I docs\benchmarks\qwen-live-baseline-01\checks.py <nuova cartella>
```

Ogni run usa una directory nuova (l'harness rifiuta di riutilizzarne una non vuota) e non tocca `tasks.db`, audit o artefatti di campagne precedenti.
