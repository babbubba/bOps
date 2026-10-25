# DGX Spark — preset `Qwen3.8-Flash-Next-Smart` a 128K con KV F16 e 32K di generazione

Task di riferimento: campagna MAF+Knowledge A/B/C/D (estensione di `BOPS-0125`). Questo documento è la procedura **dell'operatore**: nessuna modifica
al DGX è stata fatta da bOps e il servizio di inferenza non è stato riavviato. Cambia solo il preset Smart; checkpoint, campionamento, parallelismo,
sezione globale `[*]` e gli altri preset (`Qwen3.8-Flash-Next`, `Qwen3.8-Flash-Next-64k`) restano come sono.

## 1. Diff esatto di `models.ini`

Sezione `[Qwen3.8-Flash-Next-Smart]`, tre chiavi (quattro righe di valore):

```diff
 [Qwen3.8-Flash-Next-Smart]
 ...
-ctx-size = 65536
+ctx-size = 131072

-cache-type-k = q8_0
-cache-type-v = q8_0
+cache-type-k = f16
+cache-type-v = f16

-n-predict = 16384
+n-predict = 32768
```

Invariati (da non toccare): `n-gpu-layers = all`, `parallel = 1`, `flash-attn = on`, `jinja = true`, `no-mmap = true`, `fit = off`, `batch-size = 2048`,
`ubatch-size = 512`, `reasoning = on`, `reasoning-budget = 12288`, `reasoning-format = deepseek`, `temp = 1.0`, `top-k = 20`, `top-p = 0.95`, `min-p = 0.0`,
`presence-penalty = 0.0`, `repeat-penalty = 1.0` e il percorso del GGUF
`.../Qwen3.8-Flash-Next-GGUF/snapshots/c8b5954a88c2775c546b92593eda40ea041d3176/UD-IQ4_XS/Qwen3.8-Flash-Next-UD-IQ4_XS-00001-of-00003.gguf`.

Procedura (sul DGX, dall'operatore):

```bash
cp models.ini models.ini.bak-$(date +%Y%m%d-%H%M%S)
# modificare soltanto le tre chiavi della sezione [Qwen3.8-Flash-Next-Smart] come nel diff
diff -u models.ini.bak-* models.ini        # deve mostrare solo quelle variazioni
```

Il riavvio o il ricaricamento del servizio llama.cpp è una decisione dell'operatore (con `fit = off` un preset che non entra in memoria fallisce al
caricamento invece di degradare in silenzio).

## 2. Verifica che il server usi davvero 128K / F16 / 32K

Un benchmark dichiarato 128K/F16 non è valido se il server gira ancora a 64K/Q8. Dopo il riavvio, **letture soltanto** (nessuna inferenza):

```bash
BASE=http://<dgx>:8080
curl -s "$BASE/props?model=Qwen3.8-Flash-Next-Smart" | python3 -c "import sys,json; p=json.load(sys.stdin); print(p.get('default_generation_settings',{}).get('n_ctx'), p.get('total_slots'), p.get('build_info'))"
curl -s "$BASE/v1/models" | python3 -c "import sys,json; [print(m['id'], m.get('status',{}).get('value'), m.get('status',{}).get('args')) for m in json.load(sys.stdin)['data']]"
# memoria dopo il caricamento (la KV F16 occupa piu della Q8_0): confronta con il carico precedente
nvidia-smi --query-gpu=memory.used,memory.total --format=csv
```

Atteso: `n_ctx` 131072 per slot, 1 slot, gli argomenti del preset con `--ctx-size 131072 --cache-type-k f16 --cache-type-v f16 --n-predict 32768`. Il preflight
del benchmark (`-PreflightOnly`, profilo con `expectedServer`) fa le stesse letture e **fallisce** se il server riporta valori diversi o non li riporta
(a meno di `-AllowUnverifiedServer`, che resta scritto nel report).

La misura reale dell'effetto non è presunta: la KV F16 aumenta il consumo di memoria e può rallentare la generazione rispetto a Q8_0. La campagna
registra durata, token e prompt tokens per chiamata; velocità e accuratezza si leggono dal report, non dal cambio di configurazione. In passato la
generazione di Qwen3.8 è stata misurata a circa 22–30 token/s (`docs/benchmarks/qwen38-direct-comparison-01.md`).

## 3. Profilo bOps (CLI) e relazioni fra i limiti

File: `scripts/windows/maf-cli-benchmark-profile.qwen38-smart-128k.example.json` (copia con l'URL reale fuori dal repository).

| Chiave | Valore | Perché |
|---|---|---|
| `ModelProvider:Provider` / `Model` | `LlamaCpp` / `Qwen3.8-Flash-Next-Smart` | il provider id del `ChatModelDescriptor` è il valore di `Provider` (pacchetto `LlamaCpp`); il modello è l'alias del preset |
| `Agent:Context:Profiles:0` | `MaxContextTokens` 131072, `ReservedOutputTokens` 32768 | coincidono con `ctx-size` e `n-predict` del preset |
| `Agent:Context:SafetyReserveTokens` | 2048 | budget di input = 131072 − 32768 − 2048 = **96256** |
| `Agent:MaxOutputTokens` | 32768 | diventa `max_tokens` nella richiesta (verificato da un test: ogni `ModelRequest` e ogni evento di audit lo riportano) |
| `ModelProvider:RequestTimeout` | 00:50:00 | trasporto HTTP |
| `Agent:ModelCallAttemptTimeout` | 00:45:00 | < trasporto (validato dal CLI); 32768 token a 20 token/s sono ~27 min più il prefill di un prompt fino a 96K |
| `Agent:ModelCallMaxAttempts` / `ModelCallBudget` | 2 / 01:35:00 | il budget copre due tentativi interi più l'attesa |
| `Agent:MaxAttemptDuration` | 02:15:00 | il runtime si ferma da solo (`BudgetExceeded`, evidenza intatta) prima del timeout esterno dell'harness (9000 s) |
| `Agent:MaxTotalTokens` | 600000 | cap per run; il ragionamento è contato nei token di completamento |

Il reasoning budget (12288) è interno al limite di generazione (32768): la riserva di output non è sottratta due volte e il pensiero non può consumare
la risposta finale oltre il limite complessivo. Il parser OpenAI-compatibile legge già `content`, `reasoning_content`/`reasoning`, `tool_calls`,
`finish_reason` e `usage` (i token di ragionamento solo se il server li espone; altrimenti restano i caratteri); un reply di solo ragionamento senza
risposta e una chiamata a tool valida sono già distinti, quindi non è stata modificata nessuna logica del parser.

Altri modelli: nessuna regola è specifica di Qwen3.8. Con un profilo da 8K, 32K, 64K o senza profilo (capacità ignota) lo stesso codice calcola un budget
Knowledge proporzionale alla finestra oppure, se la finestra è ignota, applica i soli limiti fissi.
