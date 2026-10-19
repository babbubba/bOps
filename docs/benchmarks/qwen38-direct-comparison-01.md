# QWEN38-DIRECT-COMPARISON-01 — Qwen3.8-Flash-Next-Smart contro Qwen3.6 sugli stessi tre scenari

Data: 2026-10-19. Nove run reali (T08 ×3, T10 ×3, T02 ×3), ordine T08 → T10 → T02, tutte registrate, nessuna scartata o ripetuta.
Worktree `C:\source\repos\babbubba\bOps-knowbench01a`, branch `codex/know-bench-01a`, HEAD `7053ca0`. Nessuna modifica a codice produttivo, harness,
prompt, policy, manifest, Knowledge, fixture o matcher; nessun commit, push, merge, rebase, reset o clean. `git status` identico prima e dopo
(PA-1, AE-1, bench00 e gli altri file locali preservati). Unica aggiunta al repository: questo report.

**Sintesi.** Qwen3.8 completa 9 run su 9 con una risposta consegnata, senza `finish_reason=length` e senza errori diagnostici gravi. Qwen3.6 sugli stessi
scenari ne completava 6 su 9 (3 `Failed`/`ReplanLimitReached`) e nella T02 sullo stesso snapshot dava 2 FAIL su 3. Il costo è il tempo: generazione
a ~22 token/s contro ~59 (circa 2,7× più lenta), durata aggregata +38 % (T02: ×1,95), e quattro chiamate hanno superato i 240 s che erano il timeout
di tentativo della campagna Qwen3.6. Il confronto **non** isola i pesi del modello (budget, contesto, timeout e campionamento differiscono, §9) e **la
copertura del replay è peggiore** che per Qwen3.6 (25 `UNSUPPORTED_REPLAY` in 8 run su 9, perché il modello chiede quasi sempre `maxOutputBytes: 65536`,
valore non catturato). Le tre ripetizioni per scenario sono un campione esplorativo.

---

## 1. Configurazione effettiva Qwen3.8

| Voce | Valore | Come verificato |
|---|---|---|
| Modello richiesto / servito | `Qwen3.8-Flash-Next-Smart` in tutte le 74 richieste e risposte | `request.json`, `response.json` (campo `model`) |
| GGUF | `Qwen3.8-Flash-Next-UD-IQ4_XS-00001-of-00003.gguf` (unsloth), IQ4_XS 4,25 bpw, 176,9 G parametri, 93,7 GB, `n_ctx_train` 262144 | `/v1/models` |
| llama.cpp | router, build `b10703-0b5be7e4a` (stesso fingerprint della campagna Qwen3.6) | `/props`, `system_fingerprint` |
| Argomenti server | `--jinja --ctx-size 65536 --n-predict 16384 --reasoning on --reasoning-budget 12288 --reasoning-format deepseek --temperature 1.0 --top-k 20 --top-p 0.95 --min-p 0.0 --presence-penalty 0 --repeat-penalty 1 --parallel 1 --batch-size 2048 --ubatch-size 512 --cache-type-k/v q8_0 --flash-attn on --n-gpu-layers all --sleep-idle-seconds 900` | `/v1/models` (`status.args`, `preset`); coincide con il preset indicato, non modificato |
| Chat template | presente, 9993 caratteri, SHA-256 `12827f24…aadce` | `/props?model=` |
| Campionamento | non inviato dal client (solo `model, messages, tools, tool_choice, max_tokens`); default server: T 1.0, top-k 20, top-p 0.95, **min-p 0.0**, seed casuale. Run non deterministiche | `/props?model=` |
| `max_tokens` client | **16384 in tutte le 74 richieste** (Qwen3.6: 8192) | request body |
| Contesto bOps | profilo `Qwen3.8-Flash-Next-Smart`: 65536 token, riserva output 16384 (Qwen3.6: 32768 / 8192) | `appsettings.json` della campagna |
| Timeout di chiamata | transport 12 min, tentativo 11 min, budget logico 22 min, max 2 tentativi (Qwen3.6: 5 / 4 / 8 min) — **unica deviazione di configurazione oltre modello e budget**, v. §9 | `appsettings.json` |
| Limiti per run | 40 chiamate LLM, watchdog interno 25 min, kill esterno 30 min (invariati); nessuno raggiunto | `harness-result.json`, log |
| Altro | `AgentRunner`, prompt, recovery, finalization, grounding, manifest, policy (read-only), Knowledge Pack firmato, fixture, matcher: invariati. Binario harness SHA-256 `b4a974ea…2a82`, identico a T02C-LIVE-01 | hash prima/dopo |
| Fixture T08/T10 | pool della campagna QWEN-LIVE-BASELINE-01 (30 fixture) | directory del repository, sola lettura |
| Fixture T02 | snapshot congelato `T02-C-20261018T211054Z-cdc8` (13 fixture FC-*); 18 hash di `SHA256SUMS.txt` verificati prima e dopo | `SHA256SUMS.txt` |

La differenza di configurazione è un file `appsettings.json` copiato da T02C-LIVE-01 (SHA-256 `2abbe5c9…`) con 8 righe cambiate (modello, tre limiti di
token/contesto, tre timeout); il diff è in `shared/appsettings.json` contro `shared/appsettings.qwen36-reference.json` nella root esterna.

## 2. Esito del preflight

Due richieste reali minime (tool a zero argomenti, poi risultato del tool), fuori da ogni run: `preflight/pf1|pf2.*` nella root esterna.

| Punto | Esito |
|---|---|
| 1. Modello servito | `Qwen3.8-Flash-Next-Smart` (router; era `sleeping`, caricamento ~80 s alla prima richiesta, fuori dai tempi delle run) |
| 2. Tool calling | OK: `finish_reason=tool_calls`, `tool_calls[0].function.name/arguments` validi |
| 3. Compatibilità OpenAI | OK: `/v1/chat/completions` accetta `tools`, `tool_choice`, `max_tokens`; l'adapter produttivo ha funzionato in tutte le 74 chiamate (0 errori HTTP, 0 risposte non parsabili) |
| 4. `reasoning_content` / `content` | separati dal server (`reasoning-format deepseek`); `content` vuoto quando c'è una tool call |
| 5. Finale utilizzabile | OK (`stop`, testo corretto); in campagna 9/9 finali consegnati |
| 6. `finish_reason` | osservati solo `tool_calls` (50) e `stop` (24); mai `length` |
| 7. Budget di output effettivo | 16384 token nel body di ogni richiesta, uguale a `n-predict` del server. Nessuna chiamata ha superato **7870** token di completamento: il tetto non è stato mai esercitato |
| 8. Versione server | `b10703-0b5be7e4a` |

Throughput misurato dal server nel preflight: ~28–30 token/s a contesto quasi vuoto. Nessuna incompatibilità: la campagna è proseguita senza interventi.

## 3. Matrice delle nove run

"UR" = chiamate `UNSUPPORTED_REPLAY`. Token = `usage` del server (il completamento include il ragionamento; il server **non** espone i token di ragionamento
separati, quindi sotto sono riportati i caratteri di `reasoning_content`). Esito runtime e giudizio diagnostico sono indipendenti (§4).

| Run | Runtime | Diagnostico | Durata | Chiamate LLM | Token prompt / compl. | Caratteri reasoning | `length` | Tool (serviti/UR/rifiutati) | Finali premature | Replan |
|---|---|---|---:|---:|---:|---:|---:|---|---:|---:|
| T08-r1 | Completed | PASS | 117 s | 4 | 13 312 / 2 559 | 7 287 | 0 | devices UR; info servito | 0 | 0 |
| T08-r2 | Completed | PASS | 397 s | 7 | 25 053 / 9 557 | 33 153 | 0 | info servito; devices UR; fs.grep 1 rifiutato + 2 UR | 0 | 0 |
| T08-r3 | Completed | PASS | 89 s | 4 | 13 192 / 2 164 | 6 226 | 0 | devices UR; 1 lettura Knowledge | 0 | 0 |
| T10-r1 | Completed | PASS | 176 s | 6 | 21 487 / 4 228 | 11 119 | 0 | Knowledge load+read; web.fetch 1 rifiutato + 1 UR | 0 | 0 |
| T10-r2 | Completed | PASS (riserve) | 594 s | 6 | 24 951 / 15 017 | 40 780 | 0 | Knowledge load+read; nessun tool operativo | 1 | 1 |
| T10-r3 | Completed | PASS | 569 s | 12 | 68 546 / 12 573 | 29 911 | 0 | Knowledge; web.fetch UR; events 4 UR + 1 rifiutato; stability servito | 0 | 0 |
| T02-r1 | Completed | PARTIAL | 458 s | 11 | 74 539 / 9 700 | 21 293 | 0 | events 2 UR; crashes agg servito + raw UR; stability servito; update_history UR; Knowledge | 0 | 0 |
| T02-r2 | Completed | PARTIAL | 562 s | 12 | 72 618 / 11 773 | 27 349 | 0 | info UR; time UR; events 3 UR + 1 rifiutato; crashes e stability serviti; Knowledge | 0 | 0 |
| T02-r3 | Completed | PARTIAL | 520 s | 12 | 90 469 / 10 249 | 22 900 | 0 | events 3 UR + 1 rifiutato; crashes e stability serviti; update_history e reboot_pending UR; Knowledge | 0 | 0 |

Totale: 74 chiamate LLM, 404 167 token di prompt e 77 820 di completamento, 3 481 s di durata di run. 9/9 con exit 0, nessun kill, nessun watchdog, nessun tetto di
chiamate, 0 richieste di approvazione. 5 chiamate sono state respinte dal runtime con "arguments did not match the current planned step" (T08-r2, T10-r1, T10-r3,
T02-r2, T02-r3). Il grounding-check è stato eseguito in T02 ×3 e T10-r3 e ha sempre risposto `{"contradictions":[]}`.

Cause delle 25 UR: 15 riguardano `system.events` (12) o `system.devices` (3) con `maxOutputBytes: 65536`, diverso dal default 32768 su cui il matcher costruisce
l'equivalenza; 2 `web.fetch` (nessuna fixture esiste per quel tool); 2 `fs.grep` su percorsi Linux; 2 `system.update_history`, 1 `system.reboot_pending`, 1 `system.info`,
1 `system.time` (lo snapshot T02-C contiene solo events/crashes/stability); 1 `system.crashes` raw. Non è provato che le chiamate con `maxOutputBytes` sarebbero state servite con 32768.

## 4. Giudizi diagnostici (revisione del solo autore del report, non di Qwen)

Metodo: controlli diretti dei numeri e dei campi dei finali contro le fixture servite e contro il Knowledge Pack letto (`references/interpretation.md`), più lettura
completa dei piani, dei tool e dei finali. Regola applicata a T02: una run in cui la raccolta principale (`system.events`) non è mai stata servita non può superare PARTIAL,
**anche se non contiene errori sulle evidenze servite**; è una scelta cautelativa, non un difetto attribuito al modello.

### T08 — temperatura GPU e ventole (dato non disponibile da nessun tool)

Attesa: dire che il dato non è determinabile, senza inventare valori né capability. In tutte e tre le run `system.devices` (unica chiamata sensata) ha ricevuto UR: la
capacità di *scoprire* che l'inventario espone solo un generico ventilatore ACPI è quindi **non misurabile**. La risposta finale non ne dipende.

- **r1 — PASS.** Segnala correttamente l'assenza di letture, distingue "sconosciuto" da zero/normale/assente, non riporta intervalli di normalità, non afferma falsità sulla
  propria dotazione. Propone un controllo diretto dei sensori senza presentarlo come disponibile. Riserva minore: attribuisce il fallimento a "tool non disponibile nell'ambiente".
- **r2 — PASS, percorso inefficiente.** Pianifica `fs.grep` su `/sys/class/hwmon` e `/proc/driver/nvidia` *prima* di sapere che il sistema è Windows (piano creato prima di `system.info`);
  3 chiamate inutili. Il finale riconosce che i percorsi sono Linux, rifiuta di dare intervalli "normali" come fatto, elenca `nvidia-smi`, WMI e HWiNFO come canali **non invocabili** con i tool disponibili. Costo: 397 s, 254 s solo per il planning (25 588 caratteri di ragionamento).
- **r3 — PASS.** Un solo tentativo (UR), lettura Knowledge irrilevante (`windows-event-triage`, innocua), finale corretto: sconosciuto, due ipotesi etichettate, una verifica proposta.

Nessun tool inesistente è stato **chiamato**; nessuna temperatura o RPM inventati. Piccole sciatterie: refusi ("qualsisasi", "ventoli"), flag `nvidia-smi` approssimativi in r3.

### T10 — Knowledge su Kernel-Power 41

- **r1 — PASS.** `load` + `read_resource references/interpretation.md` (0 letture ridondanti). Piano a un solo step: `web.fetch` di un URL Microsoft **inventato** (non presente nel pack). Il primo tentativo ha usato l'URL
  corretto del pack ed è stato respinto dal runtime perché diverso dal piano; il secondo ha ripetuto l'URL inventato (UR). Il finale usa solo il pack, dichiara esplicitamente di non aver potuto consultare Microsoft, non attribuisce al pack ciò che non dice (dichiara che 1074/6006 non vi compaiono).
  Riserva: nomi di campo d'esempio (`PowerButtonHostname`, `ShutdownEventCode`) non nel pack, marcati "tipicamente".
- **r2 — PASS (riserve).** Piano iniziale di **12 step operativi** per un obiettivo risolvibile con il solo Knowledge (planning 276 s, 23 143 caratteri di ragionamento); letto il pack; il primo finale (corretto) è stato **respinto** per step non risolti;
  replan a piano vuoto; finale accettato. Il finale è fedele al pack nei punti chiave, ma mescola conoscenza generale non nel pack (sezioni A–F: 1074/6006, avvio rapido, UPS) presentata senza distinzione; cita come "riferimenti del pacchetto" solo link che il pack contiene.
- **r3 — PASS.** Piano con `web.fetch` (altro URL inventato) e cinque raccolte; 5 UR e 1 rifiuto, solo `system.stability` servita. Il finale è il più utile: quadro dal pack, tabella evidenza→significato, poi i dati serviti. Verificati contro la fixture: 19 boot / 11 arresti puliti, 16/3/126/26 per categoria,
  41 con 0x0 (5) e 0x1e (3), 6008 ×8, WER 1001 0x1e ×3, WHEA 19 ×122 e 2 ×4, 0x193 ×26 in ~1,4 s, bucket giornalieri, copertura Application/System: **tutti corretti**. Dichiara conteggi minimi (`partial`), timestamp *reported* contro *occurred*, 1074/6006 non letti. Riserva: "41 + 6008 + nessun 1074/6006 = arresto non pulito *confermato*" è una regola pratica non nel pack, formulata prima di aver letto 1074/6006.

Nessuna risposta corretta è stata rifiutata dal runtime *dopo il replan*; una sola (r2) è stata respinta prima.

### T02 — stabilità Windows (snapshot congelato)

Evidenze effettivamente servite in tutte e tre le run: la **stessa coppia** `system.crashes` aggregate (43 item, 10 gruppi, partial) e `system.stability` 1 giorno (partial). `system.events` non è mai stato servito. Truncated top-level: non servito in nessuna run (come in T02C-LIVE-01), quindi **la gestione di `Truncated` non è verificata**; `Partial` è stato riconosciuto spontaneamente in 3/3.

Verificati contro le fixture FC-Q05/FC-Q06 e riportati correttamente in tutte e tre: 43 item/10 gruppi; 26 live dump 0x193 + 15 bugcheck (0x1e ×7, 0xa ×2, 0x7f ×2, 0xd1/0x50/0x3b/0x101 ×1) + 1 live dump 0x1a1 + 1 WER crashpad; 42 item in ~2,2 s; **490 report illeggibili (448+42)**; WHEA 19 ×8 *corrected*, *occurred*, 1+1+4+1+1 per ora; displayFault 26 *reported*; `boots=0`, categorie `kernelFault`/`memoryExhaustion` non raccolte, minidump non leggibile.

Elementi qualitativamente nuovi rispetto a Qwen3.6 (stessa evidenza):
- **0x193 trattato come LiveKernelEvent/displayFault** secondo la classificazione dello strumento; nessun nome di bugcheck inventato (in T02-C r3 Qwen3.6 lo associava a NTFS_FILE_SYSTEM).
- **Nessuna "cascata" presentata come fatto**: tutte e tre le run dichiarano che i 42 item in ~2 s con codici eterogenei sono *registrazioni*, non incidenti distinti ("ogni bugcheck implica un riavvio"), e propongono la scrittura in blocco dei report come ipotesi con pro/contro. (T02-C r2: FAIL proprio per la cascata presentata come fatto.)
- **Scoperta dell'offset di 20 h 00 m 55 s** tra i timestamp dei 26 item 0x193 in crashes (00:29Z) e stability (20:30Z), stessa durata 1,505 s (r2, r3; r1 lo nota come "non confrontare/sommare"): verificato esatto. Conclusione prudente: "molto probabilmente le stesse 26 registrazioni, non sommare".
- **`kernelCrash=0` contro 15 bugcheck** nello stesso intervallo segnalata come divergenza da risolvere (r1, r3).
- Somma corretta 448+42=490 (Qwen3.6 r3: 492) e 42 contro 43 kernel (Qwen3.6 r3: 43).

Errori residui, minori e verificabili: r1 "9 codici bugcheck distinti" (sono 7 bugcheck + 2 live dump), un carattere CJK ("分布") e un frammento inglese nel testo; r2 "4 chiamate non eseguite" (sono 5 UR + 1 rifiuto); r3 "`dumpReferenceCount` > 0 per 41 item" (sono 42). Nessuno incide sulle conclusioni; il grounding-check, limitato ai fatti tipizzati, non li può rilevare.

- **r1 — PARTIAL.** Corretto e prudente; copertura limitata (nessun `system.events`, nessun crashes raw, nessuna `update_history`).
- **r2 — PARTIAL.** Idem; in più `system.info`/`system.time` non servibili dallo snapshot, quindi l'offset di 20 h resta "non spiegabile" (dichiarato).
- **r3 — PARTIAL.** Idem, con 1 conteggio sbagliato di 1.

## 5. Confronto con le baseline Qwen3.6

Baseline: T08 e T10 da QWEN-LIVE-BASELINE-01; T02 dalla campagna T02C-LIVE-01 sullo stesso snapshot. "Non confrontabile" indica che le evidenze servite differiscono e non si attribuisce la differenza al modello.

### Esiti per run

| Scenario | Qwen3.6 (runtime / diagnostico / UR) | Qwen3.8 (runtime / diagnostico / UR) |
|---|---|---|
| T08 | Completed PARTIAL 0 · Completed PASS 0 · Failed NOT_EVALUABLE 1 | Completed PASS 1 · Completed PASS 3 · Completed PASS 1 |
| T10 | ReplanLimit FAIL 0 · Completed PASS 0 · Failed NOT_EVALUABLE 1 | Completed PASS 1 · Completed PASS (riserve) 0 · Completed PASS 5 |
| T02 | Completed PARTIAL 0 · Completed FAIL 4 · Completed FAIL 1 | Completed PARTIAL 4 · Completed PARTIAL 5 · Completed PARTIAL 5 |

Runtime Completed: Qwen3.6 6/9 (T08 2/3, T10 1/3, T02 3/3), Qwen3.8 9/9. Diagnostico: Qwen3.6 2 PASS, 2 PARTIAL, 3 FAIL, 2 NOT_EVALUABLE
(T08: PASS, PARTIAL, NE; T10: PASS, FAIL, NE; T02: PARTIAL, FAIL, FAIL); Qwen3.8 6 PASS, 3 PARTIAL, 0 FAIL, 0 NOT_EVALUABLE (con la cautela di rubrica su T02, §4).

### Per dimensione

| Dimensione | T08 | T10 | T02 |
|---|---|---|---|
| Qualità diagnostica | **Equivalente-migliore** (stessa conclusione corretta; Qwen3.8 non ripete il PARTIAL di Qwen3.6-r1, nessuna affermazione falsa sulla dotazione). Cautela: devices UR in 3/3 | **Migliore** (3 PASS contro 1 PASS; le due run Qwen3.6 non-PASS erano per rifiuto del runtime e per UR) | **Migliore sulla coppia comune di fixture**: Qwen3.8 senza errori centrali, Qwen3.6-R2 (stesse due fixture servite) FAIL. **Non confrontabile** con Qwen3.6-R1, l'unica run senza UR e con eventi serviti |
| Pianificazione autonoma | **Equivalente** (r2 Qwen3.8 sonda percorsi Linux; Qwen3.6-r2 più sobrio) | **Peggiore/equivalente**: URL `web.fetch` inventati (2/3), piano a 12 step per obiettivo Knowledge-only (1/3); anche Qwen3.6-r2 aveva scelto `web.fetch` | **Equivalente-peggiore**: 6–7 raccolte per piano, molte non servibili; Qwen3.6 2/6/4 |
| Scelta/uso dei tool | Migliore (0 tool inesistenti, 0 capability inventate in entrambi, Qwen3.8 senza affermazioni errate) | Migliore sul Knowledge (0 letture ridondanti in entrambi) | Non confrontabile (UR diversi) |
| Hallucination / errori fattuali | **Migliore**: 0 gravi contro 1 (r1 Qwen3.6) | **Migliore**: 0 gravi; 1 URL inventato per piano, nomi di campo d'esempio | **Migliore**: 0 gravi contro cascata (R2) e NTFS 0x193 (R3); 3 slip minori |
| Finalizzazione | **Migliore**: 3/3 consegnate contro 2/3 (r3 Qwen3.6 vuota per `length`) | **Migliore**: 3/3 consegnate, 1 respinta in r2 contro 9 respinte e 2 run senza risposta | Equivalente (3/3 Completed in entrambi) |
| Finali premature | — (0/0) | **Migliore** (1 contro 9) | Equivalente (0/0) |
| Gestione Partial | — | Migliore/equivalente (dichiarato sempre) | **Migliore**: spontaneo 3/3; in Qwen3.6-R1 la disclosure Partial era aggiunta dal runtime |
| Gestione Truncated | — | — | **Non confrontabile** (non servito in nessuna campagna) |
| Durata | Migliore (602 s contro 777 s; mediana 117 s contro 303 s) | **Peggiore** (1 339 s contro 960 s) | **Peggiore** (1 540 s contro 790 s) |
| Chiamate LLM | Equivalente (15 contro 15) | Migliore (24 contro 33) | Peggiore (35 contro 26) |
| Token totali | Migliore (65 837 contro 100 545) | Migliore (146 802 contro 208 848) | Peggiore (269 348 contro 225 643) |
| Run bloccate da output esaurito | Migliore (0 `length` contro 4; 1 run Failed per risposta vuota) | Migliore (0 `length` contro 2) | Equivalente (0 contro 0) |

### Come non leggere il confronto
- Nessun miglioramento diagnostico è attribuito a Qwen3.8 su evidenze che Qwen3.6 non ha ricevuto. In T08 Qwen3.8 non ha *mai* avuto l'inventario dispositivi servito; Qwen3.6 ha chiamato `system.devices` in una sola run (anch'essa UR).
- In T10 la riduzione dei finali premature (1 contro 9) dipende dal comportamento del modello nel piano e nell'esecuzione degli step pianificati (anche con UR), non da un cambiamento del runtime: la regola di blocco è identica.
- In T02 Qwen3.8 ha avuto 14 UR su 3 run (Qwen3.6 5 su 3), ma i dati che entrambi hanno ricevuto sono la stessa coppia di fixture nel solo caso Qwen3.6-R2.

## 6. Variabilità fra le tre ripetizioni

| Scenario | Diagnostico | Chiamate | Durata | Strategia |
|---|---|---|---|---|
| T08 | 3 PASS | 4 / 7 / 4 | 117 / 397 / 89 s | tre strategie distinte (devices+info; info+devices+grep Linux; devices+Knowledge); r2 è l'outlier per un planning da 254 s |
| T10 | 3 PASS | 6 / 6 / 12 | 176 / 594 / 569 s | molto diversa: solo `web.fetch`; piano a 12 step poi replan vuoto; cinque raccolte operative con UR |
| T02 | 3 PARTIAL | 11 / 12 / 12 | 458 / 562 / 520 s | simile; stessa interpretazione dei dati in tutte e tre (burst di reporting, offset, WHEA corrected come contesto, nessuna causa dimostrata) |

La variabilità del *giudizio* è bassa (e minore di Qwen3.6: PASS/PARTIAL/NE, PASS/FAIL/NE, PARTIAL/FAIL/FAIL), quella del *percorso* e del costo è alta in T08 e T10. Temperatura 1.0 e seed casuale: con n = 3 non si stima una probabilità di errore.

## 7. Latenza e utilizzo dei token

| | Qwen3.8 (9 run) | Qwen3.6 (9 run: T08, T10 da LIVE-BASELINE, T02 da T02C) |
|---|---:|---:|
| Durata aggregata | 3 481 s (58 min) | 2 527 s (42 min) |
| Chiamate LLM | 74 | 74 (T08 15, T10 33, T02 26) |
| Token prompt + completamento | 481 987 | 535 036 |
| Throughput di generazione | 19,8–25,3 token/s (mediana ≈ 22) | 57,8–60,5 token/s (T08/T10; T02C non ricalcolato) |
| `finish_reason=length` | 0 / 74 | 6 / 74 (T08 4, T10 2, T02C 0) |
| Chiamata più lunga | 334 s (finale T02-r2, 7 870 token, 15 822 car. di ragionamento) | 8 192 token (limite) |
| Prompt massimo | 12 173 token | — |

- Il throughput effettivo è circa 2,7 volte inferiore; il confronto a parità di token è favorevole a Qwen3.8 solo perché ragiona meno (T08-r2: 33 mila caratteri contro 70 mila di Qwen3.6-r2) e non si ferma per `length`.
- Il peso temporale è nel **finale**: in T02 la chiamata finale vale 225–334 s su 458–562 s (circa la metà); in T10-r3 226 s su 569 s. I tre planning iniziali più lunghi (254, 276 s) coincidono con i casi di piano sovradimensionato.
- Quattro chiamate (254, 276, 334, 245 s) hanno superato i 240 s del timeout di tentativo della campagna Qwen3.6; con quel timeout sarebbero state interrotte e ritentate. La deviazione dei timeout (§1) ha quindi avuto effetto sulle run, in senso favorevole al modello.
- Nessuna chiamata ha superato 8192 token di completamento e nessun prompt ha superato 12 173 token: i limiti più ampi (16384 / 65536) **non sono stati sfruttati**; non spiegano da soli i risultati.
- Il tempo per run (1,5–10 minuti) non è controllato per carico o cache del server (`parallel=1`, prompt cache condivisa).

## 8. Problemi attribuibili a modello, runtime e replay

**Modello (Qwen3.8):** URL di documentazione inventati nel piano (T10-r1, T10-r3, entrambi di Microsoft Learn con path plausibili ma non presenti nel pack); preferenza per `web.fetch` esterno invece del Knowledge per un obiettivo "usa la conoscenza disponibile" (2/3);
piano a 12 step operativi per un obiettivo Knowledge-only (T10-r2); sonde su percorsi Linux in un sistema Windows (T08-r2, piano creato prima di `system.info`); uso sistematico di `maxOutputBytes: 65536`; piani T02 con 6–7 raccolte quando solo 2 sono servibili dallo snapshot; conteggi sbagliati di 1–2 unità, un carattere CJK, riferimenti a UR nel testo ("benchmark").

**Runtime:** il controllo "arguments did not match the current planned step" ha respinto 5 chiamate; in T10-r1 ha respinto l'URL **corretto** del pack in favore dell'URL inventato del piano, penalizzando un'autocorrezione. Messaggio non informativo su cosa differisca (problema già noto). Finale corretto respinto per step non risolti in T10-r2 (1 su 3, contro 3 su 3 in Qwen3.6): il difetto "Knowledge-only con piano operativo" persiste ma più raramente. Il grounding-check non rileva errori su fatti non tipizzati (es. "9 codici bugcheck"). Nessun `length` da gestire: il recupero LIVE-05 dopo `length` non è stato esercitato.

**Replay/harness:** 25 UR in 8 run su 9 (Qwen3.6, insieme di confronto: 7 UR in 4 run su 9 — 2 in LIVE-BASELINE per T08/T10, 5 in T02C). 15 hanno il solo `maxOutputBytes: 65536` come differenza evidente rispetto al default catturato; T02 non ha mai ricevuto eventi; lo snapshot non contiene `info/time/update_history/reboot_pending`. Il matcher non è stato modificato come da incarico. T08 non ha mai ricevuto `system.devices`, quindi non si può dire se Qwen3.8 capisca *quali sensori sono accessibili*.

## 9. Limiti del confronto

- **Non è un confronto isolato dei pesi.** Cambiano insieme: modello, `max_tokens` (16384 contro 8192), contesto (65536 contro 32768), timeout di chiamata (11 min contro 4), `min_p` (0.0 contro 0.05), `reasoning_format` (deepseek contro none), `ubatch` (512 contro 1024). Ex post, nessuna chiamata Qwen3.8 ha superato 8192 token né 12 173 token di prompt, ma il tetto più basso avrebbe comunque cambiato il comportamento di Qwen3.6 a campioni diversi; il timeout ha invece contato (4 chiamate > 240 s).
- Tre run per scenario, T = 1.0, seed casuale; un solo revisore, nessuna validazione cieca; nessuna stima statistica. Differenze di 1–2 run non sono significative.
- Fixture diverse e UR molto diverse tra campagne; la tabella §5 distingue i casi non confrontabili. T08 e T10 non hanno una verità di sistema da verificare, solo il rispetto dei vincoli.
- Il tool reale non è mai eseguito: non si misurano durata o errori dei tool. `web.fetch` non è stato eseguito (nessun accesso alla rete).
- La cap PARTIAL su T02 è una scelta di rubrica mia (§4): senza di essa le tre run potrebbero meritare PASS, al netto degli slip minori.
- Reasoning budget 12288 del server: non verificabile da fuori; nessuna chiamata ha raggiunto livelli vicini (massimo ≈ 15 800 caratteri di ragionamento per chiamata).
- Il quadro temporale congelato non è dichiarato al modello (come in T02C-LIVE-01): le finestre "ultime 24 ore" fanno riferimento al 2026-10-18.

## 10. Raccomandazione

**Decisione: preferire Qwen3.8 come modello principale per i compiti diagnostici di bOps, in modo provvisorio e condizionato; il costo in tempo è il prezzo da accettare.**

Motivi, solo da quanto osservato:
1. 9/9 finali consegnati contro 6/9, 0 `length`, 0 FAIL e 0 NOT_EVALUABLE contro 3 FAIL e 2 NOT_EVALUABLE; finali premature 1 contro 9.
2. Sulla stessa evidenza (T02: coppia crashes+stability) nessun errore centrale, mentre Qwen3.6 ne commetteva (cascata come fatto, 0x193 come NTFS, somma 492). Le correzioni sono interpretative: reported contro occurred, non sommare, divergenza kernelCrash/bugcheck, offset tra i tool.
3. Token totali −10 %, grazie a meno ragionamento e nessun tetto di output.

Controindicazioni che restano:
1. **Latenza**: generazione ≈ 2,7× più lenta, T02 ≈ 2× più lenta (≈ 8–9 minuti per run); quattro chiamate oltre 240 s. Qualsiasi adozione richiede timeout di tentativo e budget di chiamata ben oltre i 4/8 minuti attuali (qui 11/22).
2. Pianificazione eccessiva e URL inventati: la protezione contro `web.fetch` verso documentazione non presente nel pack e il blocco "arguments did not match" che penalizza l'autocorrezione restano punti deboli del sistema, non risolti dal cambio di modello.
3. Evidenza limitata: n = 3, giudizio di un solo revisore, copertura T08/T02 contaminata da UR, confronto non a parità di budget.

Quello che servirebbe per trasformare "provvisorio" in "definitivo" (nessuna di queste cose è stata eseguita): ripetere con ≥ 6 run per scenario; rieseguire una riga Qwen3.6 con gli stessi budget e timeout come controllo; ampliare le fixture (`maxOutputBytes` e `system.events`); misurare T02 con un task che serva anche eventi e almeno una fixture top-level Truncated.

## 11. Artefatti e integrità

Radice esterna (non in Git): `C:\source\repos\babbubba\bOps-backups\qwen38-direct-comparison-01`

| Percorso | Contenuto |
|---|---|
| `runs/<scenario>-r<n>/` | per run: `http/NNN.request.json` e `NNN.response.json` (byte per byte, senza intestazioni), `NNN.meta.json`, `audit.jsonl`, `tasks.db`, `system-messages.db`, `harness-result.json` (ledger del replay) |
| `analysis/` | per run metriche con `analyze.py` esistente (letto dal repository, non modificato), viste testuali dei piani e dei finali, stato git dopo |
| `preflight/` | `/v1/models`, `/props`, richieste e risposte del preflight, 3 dry-run senza rete (non conteggiate), stato git prima |
| `shared/`, `input/` | configurazione, policy, pack firmato, junction alla directory fixture dello snapshot T02-C |
| `Run-Campaign.ps1`, `logs/` | driver (una process-per-run, kill esterno 30 min), log, `launch.json`/`exit.json` per run |
| `artifact-manifest.json` | SHA-256 di 410 file ordinari; nessun file contiene la credenziale |

Integrità finale: `git status` identico, binario harness invariato, 18 hash dello snapshot verificati, nessuna richiesta di approvazione, nessun altro worktree toccato.
