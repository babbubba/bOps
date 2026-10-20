# KNOW-BENCH-01E-OPS01 — input operativi

Base letta: `codex/know-bench-01a`, `33836ae38f99986802a1dc0ea121c6f261a2adef`.
Preparazione per il percorso integrato `CaptureThenAB`; qui non si esegue quel
percorso. Preflight locale Settings/Vault PASS fornito dall'operatore, non ripetuto.
Nessuna capture, richiesta LLM o benchmark eseguita. Nessun secret letto.

## Contratto ricavato dal codice

Fonti: `KnowledgeBenchmarkLiveTests.cs` (`ExecuteAsync`, `CaptureThenAB`),
`KnowledgeBenchmark.cs` (`RunnerOptions`, `SignDelivery`, `ValidateCondition`,
`Boot`, `LoadAsync`), `know-bench-01d/SPEC.md`,
`KnowledgeHostCanaryLiveTests.LocalQwenConfiguration`, `SettingsStore`,
`AgentRunnerOptions`, `KnowledgePackVerifier`, `KnowledgeBenchmarkExport`,
`BenchmarkHttpBudget` e `OpenAiCompatible/Dtos.cs`.

| Variabile | Valore / controllo |
|---|---|
| `BOPS_KNOW_BENCH_METADATA` | Percorso assoluto esterno al checkout di un nuovo JSON UTF-8 senza BOM; condiviso A/B. |
| `BOPS_KNOW_BENCH_METADATA_SHA256` | SHA-256 lowercase dei byte effettivi del file, non del JSON normalizzato. |
| `BOPS_KNOW_BENCH_COMMIT` | `git rev-parse HEAD`, 40 cifre lowercase, DOPO tutti i commit di script/documentazione/firma. Checkout tracked pulito. Il codice ricontrolla HEAD. |
| `BOPS_KNOW_BENCH_MANIFEST_SHA256` | SHA-256 lowercase dei byte di `bops-pack.json` verificato contro `test-trust.json`; non `contentSha256` del payload. |
| `BOPS_KNOW_BENCH_GRANT` | Autorizzazione esplicita dell'operatore: `tenant-a/acme.windows-event-runbooks@1.0.0:Knowledge`. |
| `BOPS_KNOW_BENCH_RESULTS` | Directory assoluta NUOVA, vuota, protetta, preesistente al futuro run; A e B devono essere assenti. Metadata e evidenze fuori da questa directory. |

Il codice richiede questi 14 campi non null: `baseline`, `llamaCppVersion`,
`model`, `modelFileSha256`, `quantization`, `ctxSize`, `nPredict`, `reasoning`,
`sampling`, `bopsCommit`, `corpusSha256`, `providerProfileSha256`, `runtimeLimits`,
`retryPolicy`. Non esiste un JSON Schema più restrittivo nel test: il codice
confronta stringhe/bool/int per baseline=`Qwen8192`, model=`Qwen3.6-35B`,
nPredict=8192, reasoning=true, commit e corpus, e il fingerprint del profilo.
Non verifica da remoto versione, hash GGUF, quantizzazione, sampling o residenza.
Lo script aggiunge controlli locali di forma; non certifica i fatti dichiarati.

Nel percorso integrato `corpusSha256` DEVE essere `single-run-in-process`:
il vero hash emerge dalla capture futura ed è esportato per entrambi i bracci.
Nessun corpus Windows è necessario per preparare questi input. Per il percorso
individuale replay servono invece corpus REALE già acquisito, percorso e hash
dell'envelope canonico verificato da `CanaryEventsCorpus`, non Get-FileHash;
lo script qui consegnato prepara esclusivamente il percorso integrato.

`providerProfileSha256` usa esattamente `JsonSerializer.SerializeToUtf8Bytes(new {
options.Provider, options.BaseUrl, options.Model, options.SupportsNativeToolCalling,
options.RequestTimeout })`, senza opzioni: nomi PascalCase, ordine indicato,
RequestTimeout nullable TimeSpan di 120s. Lo script compila un piccolo helper
temporaneo .NET 10, con le DLL esistenti del test project, e usa `SettingsStore`
per il profilo non segreto. Non chiama `LocalQwenConfiguration.Read`, che
risolverebbe secret e Vault. Nessun `ConvertTo-Json` per questo fingerprint.

L'operatore deve dare il percorso del settings.json effettivamente usato dal
preflight: `Settings:FilePath` della configurazione esistente, con override
`Settings__FilePath` (o chiave environment equivalente del configuration provider),
risolto rispetto alla directory della configurazione. Default `settings.json`.
`BOPS_KNOW_BENCH_CONFIG` identifica quella configurazione esistente; percorso
relativo di Settings non è relativo a questo worktree. Il profilo selezionato
è `LlamaCpp`, anche se ActiveProviderId è diverso. Non normalizzare BaseUrl.
La validazione del fingerprint nel futuro run rileverà una selezione diversa.

Runtime: default letti da `AgentRunnerOptions`, con override esistenti
attempt=90s, budget chiamata=300s, durata condizione=600s; trasporto=120s;
maxSteps=15, lifetimeSteps=60, tokens=350000, replans=3/lifetime=12;
maxAttempts=3, backoff=1s..30s con jitter; limite HTTP=5 incluse planning/retry;
Knowledge 4/4/8000. Nessun incremento dei budget. Lo script conserva anche
la serializzazione completa delle opzioni runtime in `runtimeLimits`.

## Windows: preparazione riproducibile

Usare PowerShell 7 e SDK .NET 10. Le DLL devono corrispondere al checkout finale.
Non occorre build generale. Se vanno aggiornate, unico comando consentito qui
come procedura futura: `dotnet build tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj --no-restore`.
Non usare `--no-build` con DLL di freschezza sconosciuta. Il helper non ricompila
progetti repository; compila solo il proprio progetto temporaneo senza pacchetti.

1. Completare/committare eventuali modifiche, incluse delivery/trust se rifirmati.
   Non preparare metadata prima del commit finale. I comandi di seguito non
   eseguono test live e non abilitano capture.

```powershell
Set-Location 'C:\source\repos\babbubba\bOps-knowbench01a'
git branch --show-current
git status --short
git rev-parse HEAD
# Il pin viene calcolato solo dopo Verify=Admitted; nessun grant creato.
.\scripts\Prepare-KnowBenchmarkInputs.ps1 -VerifyPackOnly
Remove-Item Env:BOPS_RUN_KNOW_BENCH_CAPTURE_AB,Env:BOPS_RUN_KNOW_BENCH_01D,Env:BOPS_EVENTS_CORPUS_MODE,Env:BOPS_EVENTS_CORPUS_PATH,Env:BOPS_EVENTS_CORPUS_SHA256,Env:BOPS_KNOW_BENCH_SIGN,Env:BOPS_RUN_LIVE_QWEN_KNOWLEDGE_CANARY,Env:BOPS_KNOW_BENCH_SETTINGS_VERIFIED,Env:BOPS_KNOW_BENCH_GRANT -ErrorAction SilentlyContinue
```

2. Richiedere i percorsi all'operatore e creare una cartella locale protetta
   esterna al checkout con parent già esistente. Per esempio:

```powershell
$env:BOPS_KNOW_BENCH_CONFIG = Read-Host 'Percorso assoluto appsettings.json ESISTENTE del preflight PASS'
$settings = Read-Host 'Percorso assoluto settings.json EFFETTIVO dello stesso preflight'
$ops = Read-Host 'Nuova cartella assoluta esterna al checkout per metadata/evidenze'
if (Test-Path -LiteralPath $ops) { throw 'Scegliere una cartella nuova' }
New-Item -ItemType Directory -Path $ops -ErrorAction Stop | Out-Null
$facts = Join-Path $ops 'dgx-facts.json'
```

3. Compilare `dgx-facts.json` con evidenze reali dei controlli DGX sotto.
   Questa sagoma con null NON è un input pronto: lo script la rifiuta.
   Non copiare secret nei fatti o nelle evidenze.

```json
{
  "llamaCppVersion": null,
  "modelFileSha256": null,
  "quantization": null,
  "ctxSize": null,
  "nPredict": null,
  "reasoning": null,
  "sampling": null,
  "backendBuildFlags": null,
  "chatTemplate": null,
  "concurrency": null,
  "loadingState": null,
  "residentModel": null,
  "requestOverrides": null,
  "evidence": null
}
```

`llamaCppVersion`: release, commit e build identificabili; `modelFileSha256`:
64 hex lowercase del GGUF reale (per modello sharded, hash e inventario di ogni
shard nelle evidenze e identificazione esplicita del file associato al campo);
`quantization`: tipo effettivo e metadati GGUF; `ctxSize`: intero effettivo PER
SLOT; `nPredict`: intero 8192 verificato; `reasoning`: boolean true verificato
con template/configurazione effettiva. `sampling`: oggetto con TUTTI i sampler,
ordine, seed, penalty e default effettivi; nessun default dedotto da memoria.
`backendBuildFlags`, `chatTemplate`, `concurrency`, `loadingState`:
descrizioni/oggetti di evidenze (backend, template/hash, slot/concorrenza,
cold/warm); `residentModel`: identificazione effettiva; `requestOverrides`:
override/proxy reali, oppure oggetto vuoto SOLO se accertata l'assenza;
`evidence`: stringa non vuota con percorsi/riferimenti e timestamp dei controlli.
Tutti sono non verificati localmente; il JSON conserva questa distinzione.

L'adapter DTO invia model/messages e, quando applicabili, tools/tool_choice:
non invia n_predict, sampler o flag reasoning. Verificare quindi i default
effettivi del server e ogni eventuale proxy. Un alias /models da solo non
dimostra GGUF, ctx per slot, thinking o nPredict.

4. Dopo revisione delle evidenze, preparare file e variabili nello stesso processo:

```powershell
$metadata = Join-Path $ops 'execution-metadata.json'
$results = Join-Path $ops 'results' # deve essere ASSENTE
.\scripts\Prepare-KnowBenchmarkInputs.ps1 -SettingsPath $settings -ServerFactsPath $facts -MetadataPath $metadata -ResultsPath $results
# Verifica indipendente dei byte, del commit e del risultato vuoto.
if ((Get-FileHash -LiteralPath $metadata -Algorithm SHA256).Hash.ToLowerInvariant() -cne $env:BOPS_KNOW_BENCH_METADATA_SHA256) { throw 'Metadata pin mismatch' }
if ((git rev-parse HEAD) -cne $env:BOPS_KNOW_BENCH_COMMIT) { throw 'Commit changed' }
if (git status --porcelain --untracked-files=no) { throw 'Tracked checkout dirty' }
if (Get-ChildItem -LiteralPath $results -Force) { throw 'Result root not empty' }
```

Conservare SHA metadata, commit e pin manifest indipendentemente, senza riscrivere
il file. Non spostare metadata dentro results. Directory A/B assenti; dopo ogni
fallimento usare una nuova directory risultati e ispezionare l'eventuale staging.
Lo script non prova l'immutabilità del server e lascia attestazione/grant assenti.

5. SOLO l'operatore, dopo i controlli reali e l'autorizzazione del pack, può eseguire
   esplicitamente queste assegnazioni (non eseguite da OPS01):

```powershell
$env:BOPS_KNOW_BENCH_GRANT = 'tenant-a/acme.windows-event-runbooks@1.0.0:Knowledge'
$env:BOPS_KNOW_BENCH_SETTINGS_VERIFIED = '1'
```

Questa è attestazione umana delle impostazioni reali, da riverificare prima
di ogni braccio; revocarla se configurazione/modello cambia. L'ack di minimizzazione
e l'opt-in capture sono separati e restano assenti in questa preparazione.
Nessun comando di esecuzione del benchmark è incluso qui.

## Pack, firma, trust, pin e grant

La delivery già materializzata è
`docs/benchmarks/know-bench-01d/signed-pack/acme.windows-event-runbooks/1.0.0`.
La verifica OPS01 tramite il verifier reale con engine test 1.3.0 è PASS;
pin manifest attuale:
`d2f76cd7b80cbcb759c089d8b52d47feb8cfa0b8222530f2c3772ab2334650ca`.
Riutilizzarla con il suo `test-trust.json`: è il pack reale di runbook previsto
dal benchmark, firmato dalla chiave test pubblica acme; non è una certificazione
di publisher produttivo. Non sostituire signature, trust o grant con fixture.

Per verifica completa offline di admission/snapshot/read/grant, procedura
esistente, facoltativa, DOPO DLL aggiornate e con switch live/signing rimossi:

```powershell
dotnet test tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName=bOps.AgentFramework.Tests.KnowledgeBenchmarkIsolationTests.SameReplay_OnlyAuthorizedBReceivesKnowledgeThroughBroker'
if ($LASTEXITCODE -ne 0) { throw 'Offline admission/grant check failed' }
```

Usa probe in memoria e corpus sintetico solo per verifica offline; nessuna
capture o chiamata LLM. Non produce input corpus per il benchmark reale.

Se la delivery fosse mancante o una rifirma fosse deliberatamente necessaria:
archiviare manualmente la directory pubblica e trust abbinato FUORI dal repository,
revisionare `pack-source`, quindi usare SOLTANTO il signer esistente:

```powershell
# SOLO dopo archiviazione deliberata: SignDelivery rifiuta directory esistenti.
$env:BOPS_KNOW_BENCH_SIGN = '1'
try {
    dotnet test tests/bOps.AgentFramework.Tests/bOps.AgentFramework.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName=bOps.AgentFramework.Tests.KnowledgeBenchmarkIsolationTests.SameReplay_OnlyAuthorizedBReceivesKnowledgeThroughBroker'
    if ($LASTEXITCODE -ne 0) { throw 'Signing/admission failed' }
} finally { Remove-Item Env:BOPS_KNOW_BENCH_SIGN -ErrorAction SilentlyContinue }
.\scripts\Prepare-KnowBenchmarkInputs.ps1 -VerifyPackOnly
```

`SignDelivery` legge i due payload da `know-bench-01a/pack-source`, usa
TestPack/TestPublisher e RSA-PSS/SHA-256, crea manifest/payload/firma e trust
PUBBLICO. La chiave privata effimera resta in memoria; nessun secret reale.
Nuova chiave implica nuova firma/trust: revisionare e committare insieme i nuovi
artefatti, poi ricalcolare HEAD e metadata. La signature da sola non è un pin.

Il grant non è un file o una firma da generare: `ValidateCondition` confronta
pin e autorizzazione esatta, poi `Boot` passa `PackGrant(tenant-a, reference,
Knowledge)` al servizio `KnowledgeGrants` esistente e registra fonte/binding
solo per tenant-a. L'integrato azzera gli input Knowledge per A e li usa per B.
Nel percorso individuale A richiede pin/grant assenti; B li richiede presenti.

## DGX: comandi di sola lettura, eseguiti dall'operatore

Nessun warm-up/generation/POST. Nessun comando eseguito sul DGX da OPS01.
Le ispezioni sono da ripetere prima delle misure; errori 401/404 o campi mancanti
sono dati mancanti, non prova di default. Richiedere evidenze all'amministratore
se i permessi o la configurazione non rendono osservabili i fatti.

```bash
date --iso-8601=seconds
pgrep -x llama-server
read -r -p 'PID effettivo llama-server: ' PID
[[ "$PID" =~ ^[0-9]+$ ]] || exit 1
readlink -f "/proc/$PID/exe"
readlink -f "/proc/$PID/cwd"
# Identità build del binario EFFETTIVAMENTE in esecuzione, non di un clone qualsiasi.
"/proc/$PID/exe" --version
sha256sum "/proc/$PID/exe"
# Stampa soltanto flag pubblici utili; non scaricare cmdline/environment completi.
python3 - "$PID" <<'PY'
import pathlib, sys
args = pathlib.Path('/proc', sys.argv[1], 'cmdline').read_bytes().decode().split('\0')
allowed = {'-m','--model','--model-alias','--alias','-c','--ctx-size','-n','--n-predict',
           '-np','--parallel','--n-gpu-layers','-ngl','--device','--split-mode','--tensor-split',
           '--temp','--top-k','--top-p','--min-p','--typical','--seed','--samplers',
           '--repeat-penalty','--repeat-last-n','--presence-penalty','--frequency-penalty',
           '--mirostat','--mirostat-lr','--mirostat-ent','--reasoning-budget','--reasoning-format',
           '--chat-template','--chat-template-file','--chat-template-kwargs','--jinja',
           '--mmap','--no-mmap','--mlock','--flash-attn','--cache-type-k','--cache-type-v'}
for i, arg in enumerate(args):
    flag = arg.split('=', 1)[0]
    if flag in allowed:
        print(arg if '=' in arg else flag + ' ' + (args[i+1] if i+1 < len(args) and not args[i+1].startswith('-') else ''))
PY
read -r -p 'URL base server diretto verificato, senza credential nella URL: ' SERVER_URL
curl --fail --silent --show-error "$SERVER_URL/health"
curl --fail --silent --show-error "$SERVER_URL/models"
curl --fail --silent --show-error "$SERVER_URL/props"
curl --fail --silent --show-error "$SERVER_URL/slots"
read -r -p 'Percorso GGUF effettivo ricavato da processo/configurazione: ' GGUF
stat -- "$GGUF"
sha256sum -- "$GGUF"
# Mappature del processo: corroborazione modello/residenza, non garanzia GPU.
awk '/\.gguf/ {print}' "/proc/$PID/maps"
nvidia-smi --query-compute-apps=pid,process_name,used_gpu_memory --format=csv
nvidia-smi --query-gpu=index,name,uuid,memory.used,memory.total --format=csv
```

Usare l'URL delle route effettivamente esposte (un proxy OpenAI può usare `/v1/models`,
mentre /health,/props,/slots richiedono il server diretto). /props e /slots, quando
abilitate nella build, forniscono default generation settings e contesto/slot;
salvare tutti i sampler e ordine, non solo temperature/topP. Argomenti processi
non provano eventuali override da environment/config/proxy. Non leggere
`/proc/PID/environ`, token, chiavi o dump completi di unit/container.

Per build da checkout sorgente noto all'operatore (solo se corrisponde al binario):

```bash
read -r -p 'Checkout llama.cpp corrispondente al binario: ' LLAMA_SRC
git -C "$LLAMA_SRC" rev-parse HEAD
git -C "$LLAMA_SRC" describe --tags --always --dirty
read -r -p 'CMakeCache.txt della build effettiva: ' CACHE
grep -E '^(GGML_|LLAMA_|CMAKE_BUILD_TYPE:|CMAKE_CUDA_ARCHITECTURES:)' "$CACHE"
```

Quantizzazione e template vanno ricavati dai metadati GGUF e dai log di caricamento
effettivo già disponibili, con lo strumento di ispezione GGUF di quella build
se installato; non inventare nome/CLI di uno strumento non individuato. Richiedere
all'operatore il dump pubblico dei metadati (`general.file_type`, tipi tensori,
template) e le sole righe di startup pertinenti a modello, slot ctx, GPU/offload,
sampler e thinking. Se sharded, verificare tutti i file. Memory GPU, /health e alias
non bastano a dimostrare quale modello sia residente: corroborare PID, mapping,
props, log di caricamento e modello dichiarato. Se mmap è disabilitato, la mancanza
di mapping GGUF non prova l'assenza del modello. Conservare evidenze e timestamp
per entrambi i bracci, residenza warm/cold, assenza di richieste concorrenti e
stabilità di processo/configurazione. Non avviare/caricare un modello con questi comandi.

## Prerequisiti ancora mancanti alla consegna

- Percorsi operatori della configurazione e del settings.json effettivi del
  preflight PASS; DLL corrispondenti al commit finale se si userà il futuro test.
- Evidenze DGX e `dgx-facts.json` compilato: release/commit/build llama.cpp,
  GGUF/hash/quantizzazione/modello residente, ctx per slot, nPredict=8192,
  reasoning, tutti i sampler/ordine/seed/default, backend/template/concorrenza,
  eventuali override e warm/cold. Nessuno di questi dati è stato verificato localmente.
- Destinazione locale protetta scelta dall'operatore; da questi dati lo script
  produrrà metadata/hash, pin commit finale e nuova root risultati vuota.
- Autorizzazione esplicita del grant e attestazione dell'operatore
  `BOPS_KNOW_BENCH_SETTINGS_VERIFIED=1` dopo i controlli reali. Per l'esecuzione
  integrata futura restano separati ack minimizzazione e opt-in capture.

Pack/firma/trust/pin non sono mancanti. Nessun corpus preacquisito richiesto
per l'integrato. Nessun input server è stato inventato per produrre un file pronto.
