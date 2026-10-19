# REPLAY-DEFAULT-EQUIVALENCE-01 — selezione delle fixture per default dei tool

Data: 2026-10-18. Modificato solo l'harness `tests/bOps.QwenLiveBaseline/`; aggiunto un progetto di test dell'harness. Nessun codice produttivo, prompt,
manifest, PA-1, AE-1 o altro worktree toccato. Nessun commit, push, merge o rebase. Il report storico `qwen-live-baseline-01.md` **non è stato modificato**:
le sue nove run restano com'erano (checksum di tutti i 252 artefatti originali verificati invariati).

**Sintesi.**
- La causa vera dei `UNSUPPORTED_REPLAY` di T02 era in parte una regola di matching troppo rigida (3 chiamate su 6) e in parte **fixture realmente assenti**
  (3 su 6). Le 3 recuperabili sono ora servite; le altre 3 restano `UNSUPPORTED_REPLAY` perché non esiste una fixture semanticamente equivalente.
  Il ripasso offline del report precedente ("4 su 6 differiscono per un parametro omesso") era ottimistico: `minSeverity` omesso **non** ha un default
  equivalente a `warning`.
- 43 test deterministici passano (positivi e negativi); due mutazioni volontarie del codice (un default sbagliato, una prova di limite allentata) li
  fanno fallire.
- Tre nuove run T02 con Qwen reale, stessa configurazione, directory separata: **3/3 Completed**, ma **4 chiamate su 7** hanno ancora ricevuto
  `UNSUPPORTED_REPLAY`, tutte per osservazioni mancanti nel pool (§6). T02 è ora **parzialmente valutabile** (evidenze di eventi e crash servite), non
  pienamente. La copertura resta insufficiente per un confronto prompt-su-prompt senza nuove fixture (§9).

---

## 1. HEAD e stato Git

| Voce | Valore |
|---|---|
| Worktree / branch / HEAD | `C:\source\repos\babbubba\bOps-knowbench01a`, `codex/know-bench-01a`, `7053ca0` (invariato) |
| Modificato (tracked) | `tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj` (PA-1, preesistente, non toccato) |
| Untracked preesistenti conservati | PA-1 (`Golden/pa1/`, `PromptAssembly/`, `pa-1-…md`), AE-1 (`ScriptedAgentic/`, `ae-1-…md`), `bench00-maf-compatibility-report.md` (mtime 2026-10-16, invariato), QWEN-LIVE-BASELINE-01 (`qwen-live-baseline-01.md`, `qwen-live-baseline-01/`, `tests/bOps.QwenLiveBaseline/` ad eccezione dei file qui sotto) |
| Nuovi / modificati da questa attività (untracked) | `tests/bOps.QwenLiveBaseline/Replay.cs` (modificato), `tests/bOps.QwenLiveBaseline/ReplayMatching.cs` (nuovo), `tests/bOps.QwenLiveBaseline.Tests/` (nuovo), `docs/benchmarks/replay-default-equivalence-01/` (driver, risultati), questo report |
| Binari | Dei 37 `bOps.*.dll` registrati da QWEN-LIVE-BASELINE-01 **solo `bOps.QwenLiveBaseline.dll` differisce** (`B4A974EA605A…` contro `EF753000A7DE…`); runtime, adapter, tool e policy sono byte per byte quelli della campagna originale |
| Input condivisi | fixture (30), `shared/` (appsettings, policy, prerequisite, pack, trust store), `scenarios.json`, `tasks.db` della campagna precedente (`38D61286A4CF…`): invariati |

Nota: i test dell'harness sono in un progetto nuovo perché l'harness è un eseguibile di misura fuori da `bOps.slnx`; il progetto compila **gli stessi
sorgenti** `Replay.cs` e `ReplayMatching.cs` (collegamento, non copia), con le stesse attenuazioni degli analyzer dell'harness.

---

## 2. Fase 1 — Verifica puntuale (cosa fa davvero il tool)

Fatti verificati nel codice di produzione (lettura, nessuna esecuzione):

| Tool | Parametro | Se omesso | Fonte |
|---|---|---|---|
| `system.events` | `mode` | `raw` | `SystemEventsArguments` + `EvidenceModes.TryReadHorizon` |
| | `windowMinutes` | 60, **solo se manca anche `windowDays`** | `SystemEventsLimits.DefaultWindowMinutes` |
| | `limit` | 50 (max 500); `Take(limit)` dopo il raggruppamento | `SystemEventsLimits.DefaultEvents`, `SystemEventFormatting` |
| | `maxOutputBytes` | 32 768 | `SystemEventsLimits.DefaultOutputBytes` |
| | **`minSeverity`** | **nessun default**: tutte le severità (il manifest dice "Omit for every severity") | `TrySeverity` |
| | `source`, `eventId`, `channel`, `text`, `excludeSources` | nessun filtro | `TryName`/`TryText`/`TryExcludeSources` |
| `system.stability` | `windowDays` / `limit` | 30 / 50 | `StabilityArguments`, `StabilityLimits` |
| `system.crashes` | `mode` | `aggregate` | `SystemCrashesArguments` |
| | `sinceMinutes` | 1440, solo se manca `sinceDays` | `SystemMaintenanceLimits` |
| | `limit` | 100 | `SystemMaintenanceLimits.DefaultCrashes` |

Il validatore del runtime (`ArgumentSchema`) non coerce nulla: nomi esatti, tipi JSON nativi (la stringa `"41"` per un Integer è rifiutata), enum
confrontati ordinalmente (`"Warning"` ≠ `"warning"`), e i valori `null` dei parametri dichiarati sono saltati. I tre lettori di argomenti trattano
`null` come omesso (`json[name] is null`). Una finestra `windowDays: N` equivale a `N × 1440` minuti nel codice, ma **non** ho introdotto questo alias (non
serve a T02, e non è un default).

### Le sei chiamate T02 non coperte (campagna originale)

| # | Run | Tool e argomenti forniti | Omessi (default effettivo) | Fixture candidata | Equivalenza | Impatto se si abbinasse |
|---|---|---|---|---|---|---|
| 1 | r1 | `system.events` `{mode:aggregate, windowMinutes:1440}` | `minSeverity` (nessun filtro), `limit` (50), `maxOutputBytes` | F02 `{1440, warning, limit 500, aggregate}` | **Diversa**: F02 ha solo `warning` o più gravi; senza filtro entrano anche information/verbose | Conteggi, gruppi e completezza cambierebbero; 700 eventi osservati con filtro vs. un insieme non noto più grande |
| 2 | r2 | `system.events` `{minSeverity:warning, mode:raw, windowMinutes:1440}` | `limit` (50), `maxOutputBytes` | nessuna: F10 è raw ma con finestra di default (60 min) e `limit 50`; F02 è aggregate | **Nessuna fixture** | raw su 24 h: 700 eventi osservati contro un tetto di 50 ⇒ risultato troncato, non derivabile dall'aggregato |
| 3 | r2 | `system.stability` `{windowDays:2, limit:50}` | — | F01 `{windowDays:1, limit 50}` (F22: 7 giorni) | **Diversa** (finestra) | finestra, bucket e conteggi differenti |
| 4 | r2 | `system.crashes` `{sinceMinutes:1440}` | `mode` (aggregate), `limit` (100) | F06 `{mode:aggregate, sinceMinutes:1440}` (anche lì `limit` omesso ⇒ 100) | **Equivalente** per default verificati | nessuno: stessa query effettiva |
| 5 | r3 | `system.events` `{minSeverity:warning, mode:aggregate, windowMinutes:1440}` | `limit` (50), `maxOutputBytes` | F02 `{…, limit 500}` | **Equivalente per prova**, non per default: la cattura dichiara `truncated:false` e `observedGroups = 36 ≤ 50`; `Take(50)` taglia ciò che `Take(500)` non tagliava ⇒ stesso insieme di gruppi | nessuno: stessa lista di 36 gruppi, stessa `complete:true`, stessi `observedEvents:700` |
| 6 | r3 | `system.stability` `{windowDays:1}` | `limit` (50) | F01 `{windowDays:1, limit:50}` | **Equivalente** per default verificato | nessuno |

Conclusione: 3 chiamate su 6 sono recuperabili senza inventare nulla (4, 5, 6); le chiamate 1, 2, 3 restano senza evidenza. In particolare il vecchio
report contava la 1 tra le quasi-equivalenti: non lo è.

---

## 3. Fase 2 — Modifiche all'harness

`tests/bOps.QwenLiveBaseline/ReplayMatching.cs` (nuovo): per tre tool, una **specifica verificata** (default, "null = omesso", campo che conta le voci su
cui agisce `limit`, fonte citata). Gli altri tool non hanno specifica: nessun default applicato.

`Replay.cs` (`ReplayBroker`): regole, nell'ordine:

1. **`exact`** — argomenti letteralmente uguali (tipi preservati, ordine delle proprietà irrilevante, `null` ≠ proprietà assente). *Cambiato rispetto a prima:* il
   vecchio confronto normalizzava gli scalari (`41` = `"41"`) e scartava ogni `null`; entrambe erano equivalenze non verificate.
2. **`default-equivalent`** — uguali dopo l'applicazione dei soli default verificati ai parametri omessi da *entrambe* le chiamate; `null` letto come omesso solo per
   i tre tool verificati. Un valore esplicito diverso dal default non combacia; `minSeverity` non ha default e non combacia mai con un valore esplicito.
3. **`limit-covered`** (già esistente, ora sui valori effettivi) — solo `limit` differisce, richiesto ≥ catturato e la cattura dichiara `truncated:false`.
4. **`limit-not-binding`** (nuova) — solo `limit` differisce, richiesto < catturato, la cattura dichiara `truncated:false` **e** il suo conteggio osservato
   (`observedGroups`/`observedEvents`/`observedItems`, secondo il modo) è ≤ del limite richiesto. Un limite più piccolo che non taglia nulla restituisce gli
   stessi record.

Ambiguità: più fixture `exact` letterali (la stessa chiamata catturata più volte) mantengono l'ordine storico (fixture dello scenario, poi id più basso);
più candidati derivati (regole 2-4), o di tipo misto, **non vengono scelti**: la chiamata è `UNSUPPORTED_REPLAY` con `Rule: "ambiguous"` e gli id in
`OtherCandidates`. Nessun fuzzy matching, nessuna fallback sui tool reali, e il risultato servito è l'oggetto catturato non modificato (compresi
`complete`, `truncated`, `observedEvents`, `returnedEvents`). Il vocabolario del ledger (`SERVED` / `UNSUPPORTED_REPLAY`, `Rule`, `OtherCandidates`) è invariato.

**Limite residuo noto (preesistente, non toccato):** `system.info` esiste in 4 catture letteralmente identiche (F04, F07, F14, F24) con risultati diversi
(uptime); resta la scelta storica per scenario/id. È una scelta deterministica ma non ha alcuna prova di equivalenza; la lascio perché cambiarla altera T08/T10
già misurate. Da decidere (§9).

---

## 4. Fase 3 — Test

Progetto `tests/bOps.QwenLiveBaseline.Tests` (xunit 2.9.3, nessun modello, rete o dato di macchina): **43 test, 43 superati**. Riassunto:

| Requisito | Test (positivo → negativo) |
|---|---|
| Omesso con default verificato | stability senza `limit` → F01; crashes senza `mode` → F06; tutto omesso → capture con tutti i default |
| Esplicito uguale al default | `limit:50` esplicito = omesso (entrambe le direzioni) |
| Esplicito diverso dal default | `mode:raw`, `limit:51` (cattura troncata), `mode:aggregate` contro default raw, `maxOutputBytes:16384` → nessun abbinamento |
| `null` vs assente | `null` = omesso per i tre tool verificati; per un tool senza specifica (`fs.list`) `null` ≠ assente in entrambe le direzioni; `null` ≠ `warning`, `null` ≠ valore non default |
| Parametro omesso senza default | `minSeverity` omesso ≠ `warning`, ≠ `verbose`; `source`, `excludeSources` omessi ≠ valorizzati |
| Limiti differenti | più grande + cattura completa ⇒ `limit-covered`; troncata ⇒ nessuno; più piccolo ⇒ `limit-not-binding` solo se osservati ≤ limite (36 ok, 35 no, 10 no), cattura troncata ⇒ nessuno; raw conta `observedEvents`, aggregate `observedGroups`; limite diverso non nasconde un altro argomento diverso |
| Finestre differenti | 1 vs 2 giorni, 1 vs 30 (default), 1439 vs 1440, 60 (default) vs 1440, `sinceDays` vs `sinceMinutes`, `windowDays` vs `windowMinutes` (nessun alias); default di finestra non applicato accanto a `windowDays` |
| Enum | tutti i `minSeverity` validi combaciano solo con se stessi; `Aggregate`, `AGGREGATE`, `agg`, `""`, `bogus` non combaciano con alcuna cattura e non ricevono il default; `Warning` ≠ `warning` |
| Tipi | `"1"` ≠ `1`, `41` ≠ `"41"`, `true` ≠ `1` |
| Fixture multiple | due derivate ⇒ `ambiguous`, nessuna servita; derivate di regole diverse ⇒ `ambiguous`; una `exact` batte le derivate e le elenca; duplicati letterali ⇒ ordine storico |
| Tool non coperto | nessuna fixture ⇒ `UNSUPPORTED_REPLAY` (fallimento `Environment`, `Unavailable`); fixture di altro tool, nome con maiuscole diverse ⇒ nessuna |
| Risultato intatto | il `ToolCallResult` servito è lo stesso oggetto della cattura |
| Sui dati reali | le sei chiamate T02 originali: 4, 5, 6 servite (`F06 default-equivalent`, `F02 limit-not-binding` con `observedGroups=36`, `F01 default-equivalent`); 1, 2, 3 restano `UNSUPPORTED_REPLAY`; ogni fixture risponde ai propri argomenti; i 2 casi `SERVED` della campagna originale (`system.info`) sono serviti dalla stessa fixture, stessa regola, stessi candidati |

Verifica che i test discriminino: ho alterato volontariamente (a) il default di `windowDays` di stability da 30 a 31 e (b) la prova di `limit-not-binding`
(`count <= wanted + 100`); in entrambi i casi un test è fallito. Il codice è stato poi ripristinato e i 43 test sono tornati verdi.
Build dell'harness: 0 avvisi, 0 errori.

### Tabella prima/dopo (chiamate T02 originali)

| # | Chiamata | Prima | Dopo |
|---|---|---|---|
| 1 | events aggregate 1440, senza `minSeverity` | UNSUPPORTED_REPLAY | UNSUPPORTED_REPLAY (nessuna fixture equivalente) |
| 2 | events raw warning 1440 | UNSUPPORTED_REPLAY | UNSUPPORTED_REPLAY (nessuna fixture) |
| 3 | stability `windowDays:2` | UNSUPPORTED_REPLAY | UNSUPPORTED_REPLAY (nessuna fixture) |
| 4 | crashes `sinceMinutes:1440` | UNSUPPORTED_REPLAY | SERVED F06 `default-equivalent` |
| 5 | events aggregate warning 1440, `limit` omesso | UNSUPPORTED_REPLAY | SERVED F02 `limit-not-binding` |
| 6 | stability `windowDays:1` | UNSUPPORTED_REPLAY | SERVED F01 `default-equivalent` |

Nella campagna originale tutte le altre chiamate servite (2 `system.info`) restano invariate; le chiamate non coperte di T08-r3 (`system.devices`
`{limit:100, maxOutputBytes:16384}`) e T10-r3 (`system.events` `{source:Kernel-Power, eventId:"41", limit:10}`) **restano** `UNSUPPORTED_REPLAY`, perché le
catture più vicine hanno finestra o filtro diversi: non sono nell'ambito di questa attività.

---

## 5. Provenienza e completezza delle fixture coinvolte

Tutte le 30 fixture invariate (hash verificati), estratte da `release-validation-01/live/run/tasks.db` (SHA-256 `38D61286A4CF…`).

| Fixture | Chiamata catturata | Origine | Stato della cattura |
|---|---|---|---|
| F01 | `system.stability {windowDays:1, limit:50}` | task `2de44dcd` (T02) | `status:partial`, `complete:false`, `truncated:false`, 7 gruppi |
| F02 | `system.events {1440, warning, limit 500, aggregate}` | T02 | `status:complete`, `complete:true`, `truncated:false`, `observedEvents 700`, `observedGroups 36`, `returnedGroups 36` |
| F06 | `system.crashes {aggregate, sinceMinutes:1440}` | T02 | `partial`, `complete:false`, `truncated:true`, 54 elementi, 16 gruppi, tetto di record raggiunto, 490 report WER illeggibili |
| F10 | `system.events {limit 50, warning}` (raw, 60 min) | altro task | `complete:false`, `truncated:true`, `observedEvents 46`, `returnedEvents 22` |
| F20 | events `{windowDays 7, WHEA-Logger, aggregate}` | altro task | complete, 0 eventi |
| F21 | events `{Kernel-Power, 41, 10080, limit 20}` | T10 | complete, 8 eventi |
| F22 | `system.stability {windowDays:7, limit:50}` | T10 | partial |

Per le chiamate servite per derivazione il campo `complete`/`truncated` restituito al modello è quello della cattura: F02 `complete:true`, F06
`truncated:true`, F01 `complete:false`. Le finestre sono quelle del giorno di cattura (2026-10-17): il replay non "sposta" l'orario.

---

## 6. Fase 4 — Rivalidazione T02 (3 run reali)

Stessa configurazione di QWEN-LIVE-BASELINE-01: modello `Qwen3.6-35B` su `<llm-host>:8080`, `appsettings.json` copiato (hash identico), policy
read-only, 40 chiamate LLM, watchdog 25 min, kill 30 min, temperatura 1.0 e seed casuale (non deterministico). Run in `…\bOps-backups\replay-default-equivalence-01\runs\`
(directory separata; gli artefatti originali non sono toccati). Driver: `docs/benchmarks/replay-default-equivalence-01/Run-T02.ps1`; sintesi in `results/`.

| Run | Stato | Durata | Chiamate LLM | Token prompt / completion | `length` | Finali premature | Replan | Tool eseguiti → ledger | UR |
|---|---|---:|---:|---|---:|---:|---:|---|---:|
| r1 | Completed | 111 s | 4 | 31 862 / 5 777 | 0 | 0 | 0 | `system.events {warning, aggregate, 1440}` → F02 `limit-not-binding` | 0 |
| r2 | Completed | 285 s | 9 | 60 015 / 16 404 | 1 | 0 | 0 | events `{error, aggregate, 500, 1440}` → **UR**; stability `{30, limit 100}` → **UR**; crashes `{aggregate, 1440, limit 100}` → F06 `default-equivalent` | 2 |
| r3 | Completed | 313 s | 9 | 92 132 / 16 616 | 0 | 1 | 1 | events `{warning, aggregate, limit 200, 1440}` → F02 `limit-not-binding`; events raw `{critical, limit 100, 1440}` → **UR**; stability `{windowDays:3, limit 100}` → **UR** | 2 |

Totale 22 chiamate LLM (originale T02: 24), 0 chiamate HTTP fallite, nessun watchdog, nessun tetto raggiunto. Dei 7 tool call arrivati al replay: **3 serviti
(tutti per regola derivata, nessuno `exact`)** e **4 `UNSUPPORTED_REPLAY`**. La prima richiesta di planning di tutte e tre le run è **identica byte per byte** (SHA-256 `AF6B3B5249D3…`) a quella delle run T02 originali: stessa superficie tool,
stesso prompt, stesso Knowledge index. Le richieste successive dipendono dalle risposte campionate e non sono confrontabili.

Le 4 chiamate ancora non coperte e le osservazioni che mancherebbero (nessuna di queste regole è stata allentata per ottenere un PASS):

| Chiamata | Perché non c'è equivalente | Osservazione mancante nel pool |
|---|---|---|
| events aggregate 1440 `minSeverity:error`, `limit 500` (r2) | F02 è `warning` (insieme più ampio); un filtro diverso è un'altra query | aggregate 24 h con `minSeverity:error` (e `critical`, e senza filtro) |
| stability `windowDays:30, limit:100` (r2) | F01 è 1 giorno/50, F22 7 giorni/50: finestra e limite diversi | stability a 30 (default), 3 e 2 giorni, limite 100 e 50 |
| events raw `critical`, 1440, `limit 100` (r3) | nessun raw a 24 h | raw 24 h con `critical` / `warning` |
| stability `windowDays:3, limit:100` (r3) | idem | stability 3 giorni |

---

## 7. Metriche del modello reale e separazione dei fallimenti

Confronto descrittivo, non causale (stessa configurazione ma campionamento a temperatura 1.0 e piani diversi per run).

| | T02 originale (r1 / r2 / r3) | T02 rivalidato (r1 / r2 / r3) |
|---|---|---|
| Stato runtime | Completed / Failed (risposta vuota) / Completed | Completed / Completed / Completed |
| Durata | 170 / 256 / 68 s | 111 / 285 / 313 s |
| Chiamate LLM | 5 / 10 / 9 | 4 / 9 / 9 |
| Token prompt+completion | 28 055 / 51 258 / 42 277 | 37 639 / 76 419 / 108 748 |
| `finish_reason=length` | 1 / 1 / 0 | 0 / 1 / 0 |
| Finali premature | 0 / 0 / 0 | 0 / 0 / 1 |
| Evidenze servite | 0 / 0 / 0 | events / crashes / events |
| UR (chiamate al replay) | 1 / 3 / 2 | 0 / 2 / 2 |

**Fallimenti del replay (lacune di copertura, non colpa dell'agente):** i 4 UR della tabella §6. Per ciascuno il modello ha ricevuto un fallimento
`Environment`/`Unavailable` che dichiara l'assenza di evidenza.

**Comportamenti dell'agente / runtime in queste run (valutazione mia, un solo revisore, da confermare):**
- r1: ha chiamato un solo tool (events, 1 step di piano) e non ha pianificato né stability né crashes. Numeri spot-controllati contro F02 corretti
  (Kernel-Power 41 ×5, WHEA 19 ×41, .NET Runtime 1000 207+119, 1026 ×12, Application Error 1000 ×9, Netwtw14 ×5, PnP 219 ×15, Time-Service 24 ×14). Tuttavia
  attribuisce cause con linguaggio forte ("confermata", "bug del microcodice") e un'inferenza non supportata sull'ibernazione dopo il crash. Distingue evidenze e ipotesi
  nelle intestazioni.
- r2: ha scelto `minSeverity:error` e stability a 30 giorni (il goal chiede 24 h); i due UR hanno prodotto la sezione *Evidence limitations* corretta (aggiunta dal
  runtime) e il finale dichiara che eventi e stabilità non sono stati recuperati. Numeri crash corretti contro F06 (0x193 ×26, 0x1e ×7, 54 elementi in 16 gruppi).
  Usa 2 `EvidenceRead` con offset 0 e 4000 (non ripetuti; `analyze.py` conta comunque 1 "chiave duplicata" perché la sua chiave ignora l'offset).
- r3: un finale respinto (`system.crashes` non risolto), un replan vuoto, poi finale che **non** ha eseguito `system.crashes`; nella sezione limiti etichetta
  l'events raw come "3 giorni" (era 24 h) — errore del modello, non del replay. Dichiara correttamente che i due step non sono stati eseguiti.
- Nessun caso di dato inventato da un tool non servito rilevato nei controlli a campione; non ho verificato ogni numero del finale.

Valutazione diagnostica motivata (la mia, non del modello): r1 PARTIAL, r2 PARTIAL (limitata dal replay: mancano eventi e stabilità), r3 PARTIAL
(limitata dal replay e dal flusso agente). Nessuna PASS: nessuna run ha raccolto tutte le categorie di evidenza (eventi + stabilità + crash).

---

## 8. Validità della nuova baseline

- **Matching:** valido e conservativo. Ogni abbinamento derivato è dimostrabile dal codice dei tool o dalla cattura. Le run nuove devono essere confrontate
  **solo** con run fatte con lo stesso matcher (binario `B4A974EA605A…`).
- **T02 originale:** resta NOT_EVALUABLE e non è stata riletta come valida.
- **T02 nuova:** evidenza reale di eventi e crash viene servita, quindi la qualità dell'analisi è ora in parte giudicabile; ma **4 chiamate su 7** cadono ancora
  in lacune di fixture, e dipendono dalle scelte di finestra/severità del modello (che varia da run a run). Una differenza tra due prompt potrebbe quindi
  riflettere quale finestra il modello sceglie, non la qualità del ragionamento. **Non basta per un confronto prompt-su-prompt affidabile su T02.**
- Campione: 3 run, temperatura 1.0, un solo revisore, un modello, una macchina, una data.

## 9. Raccomandazioni (nessuna implementata)

1. **Aggiungere fixture dalla macchina reale** (decisione dell'operatore, con tool reali e policy read-only, non da me) per le osservazioni mancanti:
   stability a 30/3/2 giorni (limit 100 e 50), events aggregate 24 h con `error`, `critical` e senza `minSeverity`, events raw 24 h con `warning` e `critical`.
   Poi ripetere T02 prima di PROMPT-ROUTING-FINALIZATION.
2. In alternativa, ridurre la dipendenza dalle scelte di finestra rendendo T02 più specifico (modifica del goal, quindi nuova baseline) — decisione dell'operatore.
3. Decidere come trattare i duplicati `exact` con risultati diversi (`system.info` ×4): fixture unica, o `ambiguous` anche tra duplicati.
4. Se si vuole un alias `windowDays: N` ≡ `windowMinutes: N×1440` per l'aggregate, aggiungerlo con test; qui non è stato necessario.
5. Il testo "arguments did not match the current planned step" e le letture ridondanti restano come in §8 del report storico (fuori ambito).

## 10. Artefatti

| Cosa | Dove |
|---|---|
| Matching | `tests/bOps.QwenLiveBaseline/Replay.cs`, `ReplayMatching.cs` |
| Test (43) | `tests/bOps.QwenLiveBaseline.Tests/` (`ReplayMatchingTests.cs`, `CampaignFixtureTests.cs`, `legacy-served-calls.json`); `dotnet test tests/bOps.QwenLiveBaseline.Tests` |
| Driver e sintesi delle nuove run | `docs/benchmarks/replay-default-equivalence-01/` (`Run-T02.ps1`, `results/`: metriche, analisi per run, ledger, finali, manifest SHA-256) |
| Artefatti grezzi | `C:\source\repos\babbubba\bOps-backups\replay-default-equivalence-01\runs\T02-r{1,2,3}\` (HTTP byte per byte, `audit.jsonl`, `tasks.db`, `harness-result.json`) |
| Campagna originale | invariata in `…\qwen-live-baseline-01\` e `docs/benchmarks/qwen-live-baseline-01*` |

Non avviati: T10-COMPLETION-TRACE-01, PROMPT-ROUTING-FINALIZATION.
