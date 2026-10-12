# BENCH-MATRIX-02

Gli snapshot espliciti sono indipendenti dalla UI corrente e dalla disponibilità
di ConfigSourceRoot. Restano obbligatori gli hash dei file, il fingerprint, la
selezione salvata e il preflight del provider/modello/endpoint effettivo con
credenziali disponibili e fallback vuoto. Gli snapshot schema 1 precedenti senza
metadati derivati restano compatibili: i metadati vengono ricostruiti dai file
verificati, come nel collector precedente. Nessun provider o modello viene scelto
automaticamente.

startup.json conserva gitCommit (HEAD eseguito), runtimeReferenceCommit
(baseline), runtimeProvenance, configurationProvenance e effectiveConfiguration;
le ultime tre sezioni sono anche in manifest.json. Il nome ZIP termina con il
commit eseguito e include un run ID univoco. effectivePolicySha256 viene calcolato
sui byte del file che finisce nello ZIP, inclusi i CRLF su Windows.

Prima dell'avvio API il collector esegue dotnet restore --locked-mode per entrambi
i helper Windows invocati dai target di bOps.Api.csproj. Un restore fallito blocca
l'avvio; i comandi riusciti sono registrati in startup.json. L'output NuGet non
viene esportato perché potrebbe contenere credenziali di feed.

## Preparazione degli snapshot (una volta)

Se non sono già disponibili, salvare nella UI la configurazione Qwen/LlamaCpp
desiderata, con fallback vuoto, ed eseguire il launcher con -PreflightOnly e senza
-ConfigSnapshotPath. Conservare il percorso privato stampato come Config snapshot.
Ripetere una volta per Nemotron/OpenRouter. Questo preflight avvia l'API e verifica
le credenziali, ma non crea task e non chiama il modello. Non è stato eseguito
durante questa modifica. I nuovi snapshot continuano a leggere la configurazione
persistita dalla UI.

Lo snapshot contiene il vault cifrato ed è protetto con ACL private. Lasciarlo
fuori dal repository e dagli ZIP; mantenere disponibili BOPS_API_KEY e
BOPS_VAULT_MASTER_KEY nel contesto Windows previsto dal launcher, senza inserire
valori nei comandi.

## Due benchmark 1×3 sulla stessa codebase

Sostituire i due percorsi con snapshot già acquisiti. Ciascun comando esegue
B01, B02 e B03 una volta. La UI può rimanere nella sua configurazione corrente.

```powershell
$repo = 'C:\source\repos\babbubba\bOps'
$expectedCommit = (git -C $repo rev-parse HEAD).Trim()
$qwenSnapshot = 'C:\percorso\snapshot-qwen'
$nemotronSnapshot = 'C:\percorso\snapshot-nemotron'
$bench = Join-Path $repo 'scripts\windows\run-live-benchmark.ps1'

& $bench -TargetRepoRoot $repo -ConfigSnapshotPath $qwenSnapshot `
    -ExpectedTargetCommit $expectedCommit -RunsPerScenario 1
if ($LASTEXITCODE -ne 0) { throw 'Benchmark Qwen fallito' }

& $bench -TargetRepoRoot $repo -ConfigSnapshotPath $nemotronSnapshot `
    -ExpectedTargetCommit $expectedCommit -RunsPerScenario 1
if ($LASTEXITCODE -ne 0) { throw 'Benchmark Nemotron fallito' }
```

## Verifica offline eseguita

- 11 test unittest mirati su Windows: riuso dopo cambio UI/sorgente rimossa,
  file/vault/metadati/fingerprint alterati e aggiunta di un file opzionale,
  schema legacy, acquisizione dai percorsi configurati, provider/modello/endpoint
  errati, fallback, credenziali mancanti, provenance e SHA runtime atteso,
  hash policy contro i byte ZIP, bootstrap helper e passaggio UAC simulato.
- Worktree detached nuovo da b37bdd4, inizialmente senza project.assets.json:
  restore dei due helper tramite il collector; restore API e build --no-restore
  con NuGet.Config che espone soltanto un feed locale vuoto, usando la cache
  locale. Build riuscita con zero avvisi/errori; worktree di test rimosso.
- git diff --check superato. Nessuna suite .NET completa o verifica Linux;
  nessuna API reale avviata, chiamata LLM, esecuzione live, push o merge.

Scenari, policy read-only, verifica runtime atteso, raccolta forense, audit e
accounting restano invariati. Nessuna modifica a runtime, UI o progetti .csproj.
