# REPLAY-CAPTURE-T02B-01 — harness di sola cattura per lo snapshot T02-B (preparato, NON eseguito)

Data: 2026-10-18/19. È stato preparato e collaudato offline un harness che, quando verrà autorizzato, legge dalla macchina reale i collector
`system.events`, `system.stability` e `system.crashes` e produce lo snapshot indipendente **T02-B**, destinato a sostituire le fixture storiche
**solo** nello scenario T02. **Nessuna acquisizione è stata eseguita**: nessun collector è stato invocato (né dai test, che usano doppi, né dal comando
`verify`), Qwen non è stato interrogato, il matcher di replay non è stato modificato. Nessun commit, push, merge o rebase.

## 1. Stato Git e file

HEAD `7053ca0` (invariato), branch `codex/know-bench-01a`. Nessun file tracciato modificato oltre al `bOps.AgentFramework.Tests.csproj` di PA-1 già modificato prima.
PA-1, AE-1, bench00 (mtime 2026-10-16), le 30 fixture storiche e i 252 artefatti delle nove run originali: **verificati invariati** (hash).
Nessun file sotto `src/` toccato.

| File nuovo (tutti untracked, fuori da `bOps.slnx`) | Contenuto |
|---|---|
| `tests/bOps.ReplayFixtureCapture/Plan.cs` | piano chiuso: 10 query obbligatorie + 2 opzionali, attese per query, hash del piano, autorizzazione |
| `…/Environment.cs` | sonde identità/elevazione/hash dei binari, manifest reali, collector reali (costruiti solo da `capture`), policy read-only |
| `…/Checks.cs` | preflight, controlli di risultato, copertura del limite, report temporale |
| `…/Runner.cs` | l'unico percorso che interroga un collector; fail-fast; promozione atomica |
| `…/Store.cs` | ledger con catena di hash, guardia della directory di uscita, scrittura fixture |
| `…/SecretScan.cs`, `…/Minimizer.cs` | scansione segreti/identificativi, minimizzazione derivata e verificabile |
| `…/Cli.cs`, `Program.cs`, `.csproj` | comandi `verify`, `capture`, `scan`, `suggest-rules`, `minimize`, `check-snapshot` |
| `tests/bOps.ReplayFixtureCapture.Tests/` | 82 test offline |
| questo report | |

Compilazione: il `ArgumentSchema.cs` del runtime (validatore di produzione, `internal`) e `ReplayMatching.cs` (default verificati) sono **compilati in dal
sorgente** (link, nessuna copia), così gli argomenti sono validati con il codice dell'agente e gli "argomenti effettivi" sono calcolati con gli stessi default
del replay. Nessuna modifica a codice produttivo.

## 2. Come ciascun requisito è soddisfatto

| # | Requisito | Realizzazione | Verifica |
|---|---|---|---|
| 1 | 10 obbligatorie + 2 opzionali, queste disabilitate | `CapturePlan.All`; le opzionali partecipano solo con `--include-optional OPT1,OPT2` **e** con l'hash del piano che le include | test del piano; hash diverso per ogni combinazione |
| 2 | Collector e manifest reali; validazione argomenti; policy read-only; niente LLM né tool mutanti | collector `WindowsSystemEventsTool/StabilityTool/CrashEvidenceTool`; manifest `SystemToolManifests`; `ArgumentSchema.Validate` di produzione; `PolicyEngine` reale con la policy read-only delle campagne (`read: automatic`, il resto `forbidden`); lista di tre tool; prima di ogni chiamata si ricontrolla autorizzazione, rischio `Read`, manifest del tool reale = manifest rivisto (impronta), validazione e policy `Automatic` | test: tool mutanti/esecuzione rifiutati, manifest alterato o rischio `Medium` rifiutati prima dell'esecuzione, policy che vieta la lettura rifiuta |
| 3 | Nessuna elevazione; identità, SID e livello di integrità verificati e registrati | **Identity gate** (vedi §2bis): `capture` e `verify` leggono il token del proprio processo (account, SID, `TokenIntegrityLevel`, `TokenElevationType`, `IsPrivilegedProcess`, ruolo Administrators) e pretendono integrità **Medium** (0x2000–0x2FFF), non elevato, nessun ruolo Administrators, SID leggibile; altrimenti rifiuto (exit 2) **prima** di creare un collector o una directory. Nessun privilegio richiesto, nessuna ACL toccata. I dati sono registrati in `environment.json`, in `index.json`, nel `provenance` di ogni fixture e come evento `identityVerified` (primo evento del ledger) | 19 test in `IdentityGateTests`; `verify` reale: `<domain>\<operator>`, SID `S-1-5-21-<redacted>`, integrità Medium (0x2000), tipo di elevazione Limited, gate PASS |
| 4 | Per ogni acquisizione: query JSON, argomenti effettivi, `fromUtc`/`toUtc`, timestamp, completezza, troncamento, versione binario, SHA-256 | `provenance` di ogni fixture: `queryJson`, `effectiveArguments`, `windowFromUtc/ToUtc`, `startedAtUtc/endedAtUtc/durationMs`, `status`, `complete`, `truncated`, `toolCompleteness`, `limitCoverage`, `binaries` (SHA-256 e versione di Abstractions, Policy, System.Core, System.Windows e dell'harness), `manifestSha256`; `sha256` del contenuto, `outputSha256`, `fileSha256` nell'indice | test sui campi e sulle impronte |
| 5 | Un `snapshotId`; non dichiarare atomicità; misurare gli scostamenti | `snapshotId = T02-B-<UTC>-<4 hex>`; ogni file dichiara `atomicSnapshot: false`; `temporal-report.json` riporta inizio/fine di ogni query, fine finestra di ognuna e lo scarto massimo; oltre `--max-window-end-spread-seconds` (300) **il batch non viene promosso** (exit 6) | test con orologio simulato (scarto 18 s, e 60 s per chiamata ⇒ rifiuto) |
| 6 | Fuori da Git, directory dedicata, nessuna sovrascrittura o miscela | la radice di uscita non può stare in alcun working tree Git (cerca `.git` come cartella o file), non può essere già un pool di fixture, la directory dello snapshot deve essere nuova; scrittura prima in `pending/`, poi rinominata in `fixtures/` solo a batch pulito; file in sola lettura | test; il percorso previsto non esiste ancora |
| 7 | Scansione segreti; minimizzazione verificabile; payload originali intatti | scansione a ogni cattura (e comando `scan`): 9 forme di credenziali + 5 categorie di identificativi + l'identità del processo; i risultati contengono pattern, conteggi e digest da 8 hex, **mai il testo**. Qualunque credenziale ⇒ le fixture sono promosse invariate ma l'exit code è 3 ("da rivedere"). `minimize` produce una copia **derivata** in `minimized/<hash regole>/` con manifest (impronte di originali e minimizzati, conteggi per regola, struttura JSON preservata), rifiuta di sovrascrivere, e `--verify` la ricostruisce in memoria e confronta | test: originali byte-identici e read-only, copia riproducibile, alterazione rilevata, regola che spezza il JSON non scrive nulla, sostituzioni non sicure rifiutate |
| 8 | Fallire esplicitamente; niente invenzioni né fallback | primo risultato inatteso ⇒ stop, niente ritentativi, niente query sostitutive; `pending/` diventa `aborted/` con `ABORTED.json`; mai promosso. Attese per query: esito `Success`, `schemaVersion`, stato ammesso, completezza ammessa, troncamento (vietato o ammesso e registrato), tutte le fonti disponibili (eventi), modo, finestra uguale a quella richiesta, fine finestra entro l'intervallo di chiamata, uscita entro il budget, conteggi presenti, JSON valido; timeout 30 s; eccezione del collector ⇒ exit 5 | 10 test parametrici + test su esito `Failure`, troncamento, eccezione, timeout, orologio incoerente |
| 9 | Nessuna assunzione sul limite maggiore | l'harness **non decide** equivalenze: registra per ogni fixture `limitCoverage` (limite effettivo, osservati, restituiti, troncato, `messageDistribution`, "limite minimo che restituisce gli stessi record" solo se non troncato). Verificato nel codice che `limit` agisce solo dopo raggruppamento e ordinamento (`groups.Take(limit)`, `observed.Take(limit)`) e non influenza la raccolta; l'equivalenza resta del matcher, che richiede cattura non troncata **e** osservati ≤ limite richiesto | test: cattura troncata ⇒ nessun limite minore dichiarato; con le fixture prodotte il matcher serve limite 50/7 e rifiuta 6 e finestre diverse |
| 10 | Verifica senza interrogare i collector; cattura separata con abilitazione esplicita | `verify` (predefinito, `--json`) non crea né riceve collector: test con una fabbrica che solleva se invocata. `capture` richiede **tutti e tre**: `--enable-capture`, `--plan-sha256 <hash del piano rivisto>`, `--out-root` | test: senza abilitazione, con hash diverso, senza argomenti ⇒ exit 2, nessun collector creato, nessuna directory creata |

### 2bis. Identity gate (aggiunto dopo la prima consegna)

| Controllo | Esito richiesto | Dove |
|---|---|---|
| Account e SID dell'utente corrente | SID leggibile e `S-1-…` | `WindowsIdentity.GetCurrent()` |
| Livello di integrità del token (`TokenIntegrityLevel`, ultimo sub-authority del SID obbligatorio) | **Medium** (0x2000; accettato anche Medium-plus 0x2100); Untrusted, Low, High, System o illeggibile ⇒ rifiuto | `TokenReader` (advapi32, sola lettura del proprio token) |
| Elevazione (`Environment.IsPrivilegedProcess`) e ruolo Administrators abilitato | entrambi falsi | `IdentityGate` |
| Tipo di elevazione UAC (`TokenElevationType`: Default / Full / Limited) | registrato; `Full` implica elevato ⇒ rifiuto | provenance |

Il gate gira sia in `verify` (segnala NOT OK) sia all'inizio di `capture` (rifiuto con exit 2, prima di creare collector o directory). Nessuna API di
`AdjustTokenPrivileges`, impersonazione, `runas`, ACL o avvio di processi è presente nei sorgenti dell'harness (un test lo verifica). I dati che un token standard non
può leggere restano `unavailable`/`partial` come dichiarano i contratti dei tool (es. la cartella minidump in `system.stability`, accettata come `partial`);
per `system.events` una fonte non leggibile ferma il batch (le attese richiedono tutte le fonti disponibili), senza tentativi di recupero.

Codici di uscita: 0 ok · 2 rifiutato/configurazione · 3 completato ma con forme di credenziali da rivedere · 4 risultato inatteso · 5 collector non risponde/eccezione · 6 scarto temporale oltre il limite.

## 3. Elenco definitivo delle query

Ordine di esecuzione; effettivi = argomenti con i default verificati (tool: `events` limit 50 / maxOutputBytes 32768 / window 60; `stability` limit 50 / windowDays 30; `crashes` limit 100 / sinceMinutes 1440).
Tutti `Risk = Read`, policy `Automatic`, validazione argomenti `ok` (verificato nel `verify` reale).

| Id | Tool | Argomenti (esatti) | Effettivi | Attese di risultato |
|---|---|---|---|---|
| Q01 | `system.events` | `{"windowMinutes":1440,"minSeverity":"warning","limit":500,"mode":"aggregate"}` | + `maxOutputBytes:32768` | schema 2, status `complete`, completezza `Complete`, **non troncato**, fonti tutte disponibili |
| Q02 | `system.events` | `{"windowMinutes":1440,"limit":500,"mode":"aggregate"}` | + `maxOutputBytes:32768` | come Q01, ma troncamento ammesso (budget/ceiling) e completezza `Complete\|Partial` |
| Q03 | `system.events` | `{"windowMinutes":1440,"minSeverity":"error","limit":500,"mode":"aggregate"}` | + `maxOutputBytes:32768` | come Q01 |
| Q04 | `system.events` | `{"windowMinutes":1440,"minSeverity":"critical","limit":500,"mode":"raw"}` | + `maxOutputBytes:32768` | come Q01 (raw, circa 5 eventi) |
| Q05 | `system.crashes` | `{"mode":"aggregate","sinceMinutes":1440}` | `limit:100` | schema 2, status `complete\|partial`, completezza `Complete\|Partial`, troncamento ammesso (record ceiling), budget 65 536 B |
| Q06 | `system.stability` | `{"windowDays":1,"limit":200}` | invariati | schema 1, status `complete\|partial`, **non troncato** |
| Q07 | `system.stability` | `{"windowDays":2,"limit":200}` | invariati | idem |
| Q08 | `system.stability` | `{"windowDays":3,"limit":200}` | invariati | idem |
| Q09 | `system.stability` | `{"windowDays":7,"limit":200}` | invariati | idem |
| Q10 | `system.stability` | `{"windowDays":30,"limit":200}` | invariati | idem, troncamento ammesso (tetto di record per fonte) |
| OPT1 *(disabilitata)* | `system.events` | `{"windowMinutes":1440,"minSeverity":"warning","mode":"raw"}` | `limit:50`, `maxOutputBytes:32768` | troncamento ammesso; la più voluminosa e sensibile (righe con messaggi integrali) |
| OPT2 *(disabilitata)* | `system.events` | `{"windowMinutes":1440,"minSeverity":"critical","limit":500,"mode":"aggregate"}` | + `maxOutputBytes:32768` | come Q01 |

Q01, Q05, Q06, Q09 sono le *ricatture di ancoraggio* di F02, F06, F01, F22 (stesso istante degli altri). Le attese sono state provate sui risultati **reali** già
catturati (F02, F06, F01, F22): tutte accettate senza violazioni; un risultato reale troncato (F10) è rifiutato dove il piano vieta il troncamento.

Hash del piano (da passare a `capture`; cambia con qualunque modifica di query, argomenti o attese):

| Piano | `planSha256` |
|---|---|
| 10 obbligatorie | `a9bf0ddf37602050c9dd18bcd0ad4ae4f1ff563ecaf4ccd5414694992ca27508` |
| + OPT1 | `4ae9af8744724830489eff823faaccc74eaa42fd511b7100f95584cd4355055d` |
| + OPT2 | `79c4cd0892b1ab16856c01dd4e79429a58944b37990123fac2ecb4bca8fcc940` |
| + OPT1 + OPT2 | `1e950cd5a598b14778f4d855aa45306358b33743768fc737cf60c34c512bb978` |

## 4. Test eseguiti

| Progetto | Esito |
|---|---|
| `tests/bOps.ReplayFixtureCapture.Tests` | **115/115** (piano e verify 14, gate e flusso di cattura 39, scansione e minimizzazione 23, forma dei risultati reali 6, identity gate 19, `validate-snapshot` e tipo di elevazione 14; i casi parametrici sono contati singolarmente) |
| `tests/bOps.QwenLiveBaseline.Tests` (matcher, invariato) | 43/43 |
| Build `bOps.ReplayFixtureCapture` | 0 avvisi, 0 errori |

Tutti i test usano collector simulati con risultati sintetici nella forma dei JSON reali. L'unico comando eseguito sulla macchina reale è
`verify` (legge il proprio token, gli hash dei propri binari, i manifest statici; non costruisce collector): configurazione OK, identità `<domain>\<operator>`,
non elevata, 10 query validate, policy `Automatic` per tutte.
Mutazioni volontarie (controllo di elevazione, controllo dell'hash del piano, divieto di troncamento, e disattivazione dell'intero identity gate: 11 test falliscono) hanno fatto fallire i test corrispondenti; ripristinate.

Un test costruisce i tre collector reali solo per confrontarne il manifest con quello rivisto (il costruttore memorizza dipendenze; la lettura avviene in `ExecuteAsync`, mai chiamato).

## 5. Rischi residui

1. **Non atomicità.** Le query sono letture sequenziali di una macchina viva; le finestre sono relative all'orologio di ciascuna chiamata. Lo scarto è misurato e limitato
   (default 300 s), non eliminato. Q10 (30 giorni) può impiegare fino al budget di 20 s per fonte.
2. **Rotazione dei log.** Il log Application ha un tetto di 20 MB; se la copertura non raggiunge l'inizio delle 24 h, i risultati degli eventi non sono `complete` e il batch si ferma (comportamento voluto). Nessun dato parziale è promosso.
3. **Identità ed elevazione.** Le fixture storiche sono state catturate da `<operator>`, ma l'elevazione di allora **non è stata registrata** e non è certificabile
   (indizi non conclusivi: 490 report WER illeggibili e `minidumps: unavailable` in F01). T02-B parte solo con un token Medium, non elevato (verificato e registrato), quindi
   il nuovo snapshot è almeno ben definito; resta possibile che l'evidenza storica non sia confrontabile con esso. Il livello di integrità controllato è quello del **processo che lancia l'harness**
   (la tua console), non quello di questa sessione di lavoro: il gate si riesegue al momento della cattura.
4. **Stato della macchina e autocontaminazione.** Il PC è anche la macchina di sviluppo: gli eventi `.NET Runtime` provengono in gran parte dai test di bOps. Un nuovo snapshot conterrà anche le attività di oggi (campagne e questi test). Non è un difetto dell'harness, ma cambia l'evidenza rispetto a F02.
5. **Dati sensibili.** `environment.json` contiene account e nome macchina; le fixture contengono nomi host, controller di dominio, percorsi del profilo, ID dispositivo, GUID. La scansione segnala, non rimuove. La minimizzazione è una copia **derivata e non valida come pool di misura** (cambia dimensioni e raggruppamenti).
   Le regole vanno scritte dopo aver letto il report di scansione (`suggest-rules` dà solo il punto di partenza).
6. **Copertura finita.** Finestre diverse da 1, 2, 3, 7 e 30 giorni, o severità/combinazioni non elencate, resteranno `UNSUPPORTED_REPLAY`.
7. **Lo snapshot non è ancora usabile dal replay.** Servono: un pool per scenario (T02 → solo T02-B), `scenarios.json` accanto alle fixture, e una scelta esplicita in `Run-T02.ps1` — non fatto, e il matcher non è stato toccato. Il loader attuale legge ogni `F*.json` di una cartella, quindi T02-B va puntato come cartella separata, mai mescolata.
8. **Le espressioni regolari della scansione sono euristiche.** Possono dare falsi negativi su formati non previsti; non sostituiscono la revisione umana.
9. **Binario Debug.** Il comando usa il binario Debug; i suoi hash sono registrati nello snapshot. Una ricompilazione cambia gli hash ma non il piano.

## 6. Comando esatto per avviare successivamente la cattura

Prima, a titolo di controllo (non legge la macchina):

```powershell
dotnet "C:\source\repos\babbubba\bOps-knowbench01a\tests\bOps.ReplayFixtureCapture\bin\Debug\net10.0\bOps.ReplayFixtureCapture.dll" verify --out-root "C:\source\repos\babbubba\bOps-backups\replay-capture-t02b"
```

La cattura (legge Event Log, archivio WER ed elenco `C:\Windows\Minidump` con l'identità corrente, non elevata; **va eseguita solo dopo la tua revisione e approvazione esplicita**):

```powershell
dotnet "C:\source\repos\babbubba\bOps-knowbench01a\tests\bOps.ReplayFixtureCapture\bin\Debug\net10.0\bOps.ReplayFixtureCapture.dll" capture --enable-capture --plan-sha256 a9bf0ddf37602050c9dd18bcd0ad4ae4f1ff563ecaf4ccd5414694992ca27508 --out-root "C:\source\repos\babbubba\bOps-backups\replay-capture-t02b"
```

Con le opzionali (hash diverso, vedi tabella): aggiungere `--include-optional OPT1,OPT2` e usare `1e950cd5…bb978`.

Dopo la cattura, in ordine (tutti offline, nessuno modifica le fixture):

```powershell
$snap = "C:\source\repos\babbubba\bOps-backups\replay-capture-t02b\<snapshotId>"
dotnet "<dll>" check-snapshot --snapshot $snap      # SHA256SUMS, catena del ledger, file in sola lettura
dotnet "<dll>" scan --snapshot $snap                # report in $snap\reports\
dotnet "<dll>" suggest-rules --snapshot $snap > rules.json   # poi rivedere/estendere
dotnet "<dll>" minimize --snapshot $snap --rules rules.json
dotnet "<dll>" minimize --snapshot $snap --rules rules.json --verify
```

Uscite attese: 0 pulito; 3 con forme di credenziali da rivedere (le fixture sono comunque promosse e invariate); 4/5/6 batch non promosso (`aborted/` + `ABORTED.json`, nessun pool utilizzabile).


## 7. Aggiunte prima della cattura: `--require-elevation-type` e `validate-snapshot`

- `verify` e `capture` accettano `--require-elevation-type Limited`: un token con altro tipo di elevazione (es. `Default`, `Full`) viene rifiutato prima di creare collector o directory.
- `validate-snapshot --snapshot <dir> [--max-window-end-spread-seconds 300] [--json]` (solo lettura, scrive `reports/validation-<UTC>.json`) controlla: snapshot promosso e fuori da Git,
  pool separato dalle fixture storiche; le 10 acquisizioni obbligatorie presenti una volta, argomenti e hash del piano; SHA256SUMS, catena del ledger, SHA-256 di contenuto/output/file ricalcolati;
  identità (Medium, Limited, non elevato, SID) uguale in tutte le fixture e in `environment.json`, stessi binari; finestre di lunghezza esatta, dentro l'intervallo della chiamata, chiamate sequenziali,
  scarto massimo, accordo con `temporal-report.json`; tabella per query di stato/completezza/troncamento/fonti non disponibili/osservati/restituiti e verifica delle attese del piano;
  dati sensibili (conteggi per pattern, mai il testo); copertura teorica di 12 chiamate T02 già viste (le 6 non supportate originali, le 4 non supportate della rivalidazione, 2 servite) chiesta al
  matcher **non modificato**. Verdetto meccanico (`PASS`, `PASS-WITH-WARNINGS`, `REVIEW-CREDENTIAL-SHAPES`, `FAIL`); l'idoneità come dataset congelato resta una decisione del revisore.
  Exit: 0 / 2 (almeno un FAIL) / 3 (forme di credenziali da rivedere).
- La fabbrica dei collector è ora pigra: un rifiuto del gate non crea nemmeno la sorgente dei collector.
- `verify` reale con i parametri della cattura: gate PASS (`<domain>\<operator>`, SID `S-1-5-21-<redacted>`, Medium 0x2000, Limited), piano `a9bf0ddf…7508` (invariato).
