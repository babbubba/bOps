# REPLAY-CAPTURE-T02C-01 — preparazione, nessuna cattura reale

Data: 2026-10-18. Worktree `C:\source\repos\babbubba\bOps-knowbench01a`, branch `codex/know-bench-01a`, HEAD `7053ca0`.

T02-C è pronto per una futura acquisizione indipendente di **13 query obbligatorie**. Sono stati eseguiti build, test offline e solamente il comando reale `verify`.
**Non è stato eseguito `capture` reale, nessun collector ha acquisito diagnostica Windows, Qwen non è stato interrogato.** Le chiamate alla CLI `capture` nei test impiegano esclusivamente collector sintetici e directory temporanee eliminate dai test; non sono risultati T02-C reali.

## Modifiche

| File | Motivazione |
|---|---|
| `tests/bOps.ReplayFixtureCapture/Plan.cs` | Selezione dei piani T02-B/v1 e T02-C/v2; riuso delle prime dieci query, aggiunta C1/C2/C3, hash che include la versione. Nessuna modifica alle query o attese originali B. |
| `tests/bOps.ReplayFixtureCapture/Cli.cs` | `--plan T02-C` per verify/capture, metadati JSON, comando suggerito con selettore e Limited. Il default rimane B. |
| `tests/bOps.ReplayFixtureCapture/Runner.cs` | Stesso flusso protetto per entrambi i piani; ID di sessione T02-C, versione e nome nel piano registrato; Limited obbligatorio per C anche senza flag. |
| `tests/bOps.ReplayFixtureCapture/Store.cs` | Fixture C con prefisso `FC-`, fixture B ancora `FB-`; nessuna sovrascrittura. |
| `tests/bOps.ReplayFixtureCapture/Validation.cs` | Riconoscimento della versione dalla sessione, 13 query per C/10 per B, esclusione query sconosciute e duplicate, coerenza di sessione e prefisso, piano registrato in ordine, argomenti richiesti/effettivi, manifest e non atomicità nella provenance; indice biunivoco e ordine query del ledger. Conservati controlli di hash, identità, tempo, risultati e scansione sensibili. Obiettivo O2 obbligatorio per C, copertura interrogata sul matcher invariato. |
| `tests/bOps.ReplayFixtureCapture.Tests/Support.cs` | Il collector simulato riconosce anche le tre nuove query. |
| `tests/bOps.ReplayFixtureCapture.Tests/T02CPreparationTests.cs` | 23 casi aggiuntivi strettamente relativi a piano/versione/hash, contratti, gate, autorizzazione, copertura, limiti, troncamenti, tempo e separazione delle sessioni. |
| Questo report | Esiti, vincoli, rischi e comandi successivi. |

Non sono stati modificati matcher, codice sotto `src/`, prompt, manifest produttivi, configurazione Qwen, PA-1 o AE-1. Snapshot esterni T02-B e precedenti: nessuna scrittura, nessuna ricattura e nessuna validazione che aggiunga report allo snapshot storico.

## Piano autorizzabile

**T02-C, versione 2, SHA-256 completo:**

`46758334aa173b15c5aaf800f99851749918c5963e37d6e6ab106b412ed15386`

T02-B predefinito conserva versione 1 e hash `a9bf0ddf37602050c9dd18bcd0ad4ae4f1ff563ecaf4ccd5414694992ca27508` (assert esplicito nei test). I piani B con opzionali restano supportati. In C `OPT1` è rappresentata una sola volta da C2, con limite 50 esplicito; `OPT1` e `OPT2` non sono selezionabili.

Ordine di esecuzione:

| ID | Tool | Argomenti richiesti esatti |
|---|---|---|
| Q01 | `system.events` | `{"windowMinutes":1440,"minSeverity":"warning","limit":500,"mode":"aggregate"}` |
| Q02 | `system.events` | `{"windowMinutes":1440,"limit":500,"mode":"aggregate"}` |
| Q03 | `system.events` | `{"windowMinutes":1440,"minSeverity":"error","limit":500,"mode":"aggregate"}` |
| Q04 | `system.events` | `{"windowMinutes":1440,"minSeverity":"critical","limit":500,"mode":"raw"}` |
| Q05 | `system.crashes` | `{"mode":"aggregate","sinceMinutes":1440}` |
| Q06 | `system.stability` | `{"windowDays":1,"limit":200}` |
| Q07 | `system.stability` | `{"windowDays":2,"limit":200}` |
| Q08 | `system.stability` | `{"windowDays":3,"limit":200}` |
| Q09 | `system.stability` | `{"windowDays":7,"limit":200}` |
| Q10 | `system.stability` | `{"windowDays":30,"limit":200}` |
| C1 | `system.events` | `{"windowMinutes":1440,"mode":"aggregate","limit":50}` |
| C2 | `system.events` | `{"windowMinutes":1440,"minSeverity":"warning","mode":"raw","limit":50}` |
| C3 | `system.stability` | `{"windowDays":30,"limit":100}` |

Argomenti effettivi: agli eventi si applica il default verificato `maxOutputBytes=32768`; a Q05 `limit=100`; gli altri sono invariati. Q01–Q10 mantengono i controlli B originali. C1/C2: schema 2, status `complete`, completeness `Complete|Partial`, fonti tutte disponibili, troncamento ammesso e dichiarato, budget 32768 byte. C3: schema 1, status `complete|partial`, completeness `Complete|Partial`, fonti non disponibili registrate, troncamento ammesso e dichiarato, budget 32768 byte. Le aggiunte rispettano i controlli già esistenti per queste forme di query e non allentano quelli delle prime dieci.

## Verifiche effettivamente eseguite

| Verifica | Esito |
|---|---|
| Build progetto di cattura, `--no-restore` | PASS, 0 avvisi / 0 errori |
| Tutti i test `bOps.ReplayFixtureCapture.Tests` | **138/138 PASS**, 0 skipped; inclusi i 115 casi preesistenti e 23 nuovi |
| Tutti i test `bOps.QwenLiveBaseline.Tests` | **43/43 PASS**, 0 skipped; matcher invariato |
| Contratti offline | PASS nei test: tutte le 13 query tramite `ArgumentSchema` produttivo, manifest reali statici e policy read-only; nuovi parametri inclusi |
| `verify --plan T02-C --require-elevation-type Limited ... --json` | Exit 0, `ok=true`, nessun problema, versione 2, 13 query, tutte validation `ok`, policy `Automatic` |
| Identità del processo di verify | Medium 0x2000, Limited, `elevated=false`, `administratorRole=false`; account/SID verificati dal gate |
| Confronto hash dei file preesistenti nel worktree | 1831/1837 invariati; i soli 6 file preesistenti cambiati sono quelli dell'harness/supporto elencati sopra |

Il progetto test segnala un warning preesistente `CS8631` in `CaptureFlowTests.cs:259`; quel file è invariato. Nessuna incompatibilità rilevata dai test B, comprese le opzionali e la validazione. Nessun avvio della solution o del benchmark live.

Comandi di build/test eseguiti dalla radice del worktree:

```powershell
dotnet build tests/bOps.ReplayFixtureCapture/bOps.ReplayFixtureCapture.csproj --no-restore
dotnet test tests/bOps.ReplayFixtureCapture.Tests/bOps.ReplayFixtureCapture.Tests.csproj --no-restore --verbosity minimal
dotnet test tests/bOps.QwenLiveBaseline.Tests/bOps.QwenLiveBaseline.Tests.csproj --no-restore --verbosity minimal
```

I test provano che le fixture troncate ai limiti 50, 100, 200 e 500 non servono limiti diversi e non dichiarano un limite minimo equivalente. Un caso sintetico riproduce Q02/Q10 troncate e verifica 12/12 servite grazie a C1/C2/C3 esatte, senza nascondere i troncamenti. Un altro caso, con limite vincolante sulle warning aggregate, dimostra che **13 fixture non garantiscono 12/12**. Il validator conserva i warning di copertura e di qualità. I casi mancanti/duplicati/non autorizzati, sessioni miste, piano alterato e tempo incoerente sono rifiutati. Il test di dispersione usa 26 secondi per chiamata: 312 secondi tra le finestre ⇒ exit 6, nessuna promozione.

## Stato Git iniziale e finale

Iniziale e finale: branch `codex/know-bench-01a`, HEAD `7053ca0`. Unico file tracciato modificato: `tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj`, già modificato all'inizio e verificato byte-identico alla ricognizione. Tutte le modifiche di questa attività sono in percorsi già untracked oppure nuovi; nessun commit, push, merge, rebase, reset o clean.

Stato iniziale (`git status --short`):

```text
 M tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj
?? docs/benchmarks/ae-1-scripted-runtime-baseline.md
?? docs/benchmarks/bench00-maf-compatibility-report.md
?? docs/benchmarks/pa-1-request-assembly-baseline.md
?? docs/benchmarks/qwen-live-baseline-01.md
?? docs/benchmarks/qwen-live-baseline-01/
?? docs/benchmarks/replay-capture-t02b-01.md
?? docs/benchmarks/replay-default-equivalence-01.md
?? docs/benchmarks/replay-default-equivalence-01/
?? tests/bOps.AgentFramework.Tests/Golden/pa1/
?? tests/bOps.AgentFramework.Tests/PromptAssembly/
?? tests/bOps.AgentFramework.Tests/ScriptedAgentic/
?? tests/bOps.QwenLiveBaseline.Tests/
?? tests/bOps.QwenLiveBaseline/
?? tests/bOps.ReplayFixtureCapture.Tests/
?? tests/bOps.ReplayFixtureCapture/
```

Finale: medesimo elenco, con in aggiunta `?? docs/benchmarks/replay-capture-t02c-01.md`. Il nuovo test è incluso nella directory test già untracked. `git diff --check` PASS (per i file tracciati); il confronto SHA-256 comprende anche gli untracked preesistenti, inclusi bench00, PA-1, AE-1 e gli artefatti storici della campagna. La manifestazione temporanea usata per il confronto è rimossa alla fine. Il worktree `C:\source\repos\babbubba\bOps` non è stato modificato.

## Comandi PowerShell esatti

Impostazioni comuni:

```powershell
$dll = 'C:\source\repos\babbubba\bOps-knowbench01a\tests\bOps.ReplayFixtureCapture\bin\Debug\net10.0\bOps.ReplayFixtureCapture.dll'
$outRoot = 'C:\source\repos\babbubba\bOps-backups\replay-capture-t02c'
$planHash = '46758334aa173b15c5aaf800f99851749918c5963e37d6e6ab106b412ed15386'
```

Verify, eseguito con successo in questa attività (non costruisce né interroga collector, non crea output):

```powershell
dotnet $dll verify --plan T02-C --require-elevation-type Limited --out-root $outRoot --json
```

**Solo per una successiva acquisizione esplicitamente approvata; NON eseguito:**

```powershell
dotnet $dll capture --plan T02-C --enable-capture --plan-sha256 $planHash --require-elevation-type Limited --out-root $outRoot --max-window-end-spread-seconds 300
```

Dopo quella futura acquisizione, usare l'ID effettivamente restituito, senza unire cartelle né scegliere fixture da sessioni diverse. **NON eseguito in questa attività:**

```powershell
$snapshotId = Read-Host 'ID T02-C restituito dal comando capture'
$snapshot = Join-Path $outRoot $snapshotId
dotnet $dll validate-snapshot --snapshot $snapshot --max-window-end-spread-seconds 300 --json
```

Il validatore seleziona il piano dalla sessione e controlla directory, versione, indice, ambiente e provenance coerenti. Scrive solo il proprio report in `reports/validation-*.json`; non va lanciato sugli snapshot storici durante questa preparazione.

Directory future: `$outRoot\T02-C-<UTC>-<suffix>\`, con `plan.json`, `environment.json`, ledger, indice, `temporal-report.json`, `SHA256SUMS.txt`, `fixtures\FC-*.json` solo dopo promozione e report successivi in `reports\`. Output diagnostici fuori da Git; nessuna directory reale T02-C è stata creata da verify. `pending/` o `aborted/` non sono pool utilizzabili.

## Rischi residui e condizioni per il benchmark Qwen

- Una sessione sequenziale **non è uno snapshot atomico**. Restano registrati timestamp effettivi, finestre e dispersione. La soglia 300 secondi è invariata: il T02-B riportava 3,5 secondi per dieci query; le tre aggiunte richiedono una verifica reale del tempo alla futura cattura, non un aumento preventivo della soglia. Il test oltre soglia conferma l'aborto. Non è stato misurato alcun tempo reale C.
- C2 espone messaggi raw potenzialmente contenenti account, percorsi e identificativi. Scansione euristica con conteggi e digest, senza riportare i valori riconosciuti; necessaria revisione umana dei report. I falsi negativi restano possibili. Nessuna minimizzazione automatica delle fixture di misura.
- Fonti non disponibili, risultati Partial, troncamenti, limiti vincolanti e ambiguità fra candidati possono impedire 12/12. Il matcher è invariato, senza fallback a collector reali; nessun filtro o trasformazione è introdotto per ottenere il punteggio.
- Identità Medium/Limited e non elevata deve essere nuovamente verificata dal processo che effettuerà la cattura. Nessun recupero con privilegi amministrativi.
- Prima di approvare un benchmark: autorizzare espressamente questa cattura e il suo hash; acquisire tutte le 13 query nella stessa nuova sessione; validare integrità/provenance/identità/tempo e qualità, rivedere i dati sensibili e la copertura effettiva delle 12 chiamate; decidere se Partial/Truncated sono accettabili, mantenendoli visibili. Se 12/12 è un requisito, deve risultare dal report reale e non dalla sola presenza delle query.
- Servirà inoltre un'integrazione replay esplicita e verificata per scenario: T02 deve puntare soltanto al nuovo pool congelato, senza mescolare T02-B o fixture storiche; preservare gli scenari restanti. Questa integrazione e le esecuzioni Qwen sono rinviate, come richiesto.

Nessun problema implementativo aperto o regressione rilevata dai test. Restano da autorizzare ed effettuare la cattura reale, la revisione del dataset e l'eventuale successiva integrazione/approvazione del benchmark.
