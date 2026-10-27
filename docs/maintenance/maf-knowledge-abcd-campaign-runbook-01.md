# Campagna MAF + Knowledge A/B/C/D — runbook dell'operatore

Estende [`maf-cli-ab-benchmark-runbook-01.md`](maf-cli-ab-benchmark-runbook-01.md) (A/B storico, invariato) a quattro condizioni. Misura quanto la Knowledge
Windows di bOps renda **Qwen3.8-Flash-Next-Smart** (128K, KV F16, 32K di generazione) più competente come sistemista, con e senza il turno MAF governato.

| | MAF governed turn | Knowledge |
|---|---|---|
| **A** | off | off (significato storico) |
| **B** | on | off (significato storico) |
| **C** | off | on |
| **D** | on | on |

C e D usano **lo stesso pack firmato**, lo stesso trust store, lo stesso grant, lo stesso binding e lo stesso budget. Le sole variabili sperimentali fra le
quattro condizioni sono i due interruttori. Il confronto principale e quanto la Knowledge migliora la qualità diagnostica e quanto e ripetibile.

## 0. Prerequisiti (una volta)

1. **DGX**: applicare il diff di [`dgx-qwen38-smart-128k-f16-01.md`](dgx-qwen38-smart-128k-f16-01.md) e ricaricare il preset *a decisione dell'operatore*. Il benchmark non
   parte (il preflight fallisce) se il server non riporta 131072 / f16 / 32768.
2. **Profilo** (non segreto, fuori dal repository): copia di `scripts/windows/maf-cli-benchmark-profile.qwen38-smart-128k.example.json` con l'URL reale
   e, come nei profili storici, `"apiKeyEnv": "BOPS_MODEL_API_KEY"` (solo il *nome* della variabile). Prima di preflight e campagna la variabile deve esistere nella
   sessione PowerShell **con il valore che il server accetta**: un llama.cpp avviato con `--api-key` risponde 401 a qualsiasi altro valore (il preflight lo segnala
   come `endpoint-reachable`). Il valore non viene mai stampato né salvato. Se la chiave è più corta di 16 caratteri (tipico di una chiave locale), il controllo
   anti-fuga non può cercarla come semplice sottostringa (comparirebbe in parole comuni): la cerca solo in contesto di credenziale (`Bearer`, `apiKey=`, `"password":`
   ...), registra `secretLeakScanMode` e i nomi dei file in `run.json`, e non redige l'output; gli altri segreti dell'ambiente restano a ricerca completa.
3. **Pack firmato** (chiavi private fuori dal repository, versione immutabile):
   `scripts\windows\prepare-knowledge-benchmark-pack.ps1` (richiede PowerShell 7 e i sorgenti delle skill committati).
4. Il commit misurato e il `HEAD` del worktree: l'harness rifiuta modifiche non committate ai sorgenti runtime.

## 1. Preflight completo (nessuna inferenza)

Verifica commit e pulizia, build del CLI, endpoint e modello, configurazione reale del server (contesto per slot, tipi di KV, `n-predict`, slot),
le quattro modalità (MAF dal processo, Knowledge dall'ammissione del pack da parte del **CLI reale** contro uno stub loopback), firma/trust/grant, policy di
sola lettura, isolamento, ambiente. Esito `PASS`/`FAIL` per ogni controllo in `preflight.json`.

```powershell
$repo = 'C:\source\repos\babbubba\bOps-diagfix'
$bench = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'bOps-MafBenchmarks'
$commit = (git -C $repo rev-parse HEAD).Trim()
& "$repo\scripts\windows\run-maf-cli-benchmark.ps1" -PreflightOnly -AllowRemoteEndpoint `
    -TargetRepoRoot $repo -ExpectedTargetCommit $commit `
    -ModelProfile "$bench\profiles\qwen38-smart-128k.json" -Variants 'A,B,C,D' `
    -KnowledgePacksDirectory "$bench\knowledge-pack\packs" -KnowledgeTrustStore "$bench\knowledge-pack\publisher-trust.json" `
    -KnowledgePackId 'bsoft.windows-event-runbooks' -KnowledgePackVersion '2.0.0' `
    -OutputRoot "$bench\preflight-abcd-$(Get-Date -Format yyyyMMdd-HHmmss)"
```

Non modifica i sistemi diagnosticati: esegue letture HTTP (elenco modelli, `/props`, `/slots`) e il CLI contro un loopback che risponde 400.

## 2. Pilot B02 (4 run)

`B02 / A`, `B02 / B`, `B02 / C`, `B02 / D`, un run ciascuno, autorizzazione live esplicita, budget limitati.

```powershell
& "$repo\scripts\windows\run-maf-cli-benchmark.ps1" -AllowLive -AllowRemoteEndpoint `
    -TargetRepoRoot $repo -ExpectedTargetCommit $commit `
    -ModelProfile "$bench\profiles\qwen38-smart-128k.json" -Scenarios 'B02' -Variants 'A,B,C,D' -Rounds 1 -Order rotate `
    -KnowledgePacksDirectory "$bench\knowledge-pack\packs" -KnowledgeTrustStore "$bench\knowledge-pack\publisher-trust.json" `
    -KnowledgePackId 'bsoft.windows-event-runbooks' -KnowledgePackVersion '2.0.0' `
    -TaskTimeoutSeconds 9000 -CampaignTimeoutSeconds 43200 -PerRunTokenCap 600000 -TokenBudget 2400000 -ContinueOnTimeout `
    -OutputRoot "$bench\pilot-B02-abcd-$(Get-Date -Format yyyyMMdd-HHmmss)"
```

Il pilot serve a stimare tempi e token reali (e a verificare in vivo che il template di chat del server accetti i due messaggi `user` consecutivi che
l'indice Knowledge richiede: ADR-0057 B2, non verificabile senza inferenza).

## 3. Campagna completa: 36 run

`3 scenari × 4 condizioni × 3 ripetizioni`: `B01,B02,B03` × `A,B,C,D` × 3 round, ordine ruotato (nessuna condizione sempre prima o ultima).
**Un solo `-OutputRoot` = una sola identità di campagna.** Se il processo viene interrotto (riavvio del PC, timeout, rete), si rilancia **lo stesso comando
con `-Resume`**: l'harness rifiuta di continuare se commit, profilo, harness, pack o piano sono cambiati, non rifà i run già registrati e conta i token già spesi.

```powershell
$campaign = "$bench\campaign-abcd-36x-qwen38-smart"
& "$repo\scripts\windows\run-maf-cli-benchmark.ps1" -AllowLive -AllowRemoteEndpoint `
    -TargetRepoRoot $repo -ExpectedTargetCommit $commit `
    -ModelProfile "$bench\profiles\qwen38-smart-128k.json" -Scenarios 'B01,B02,B03' -Variants 'A,B,C,D' -Rounds 3 -Order rotate `
    -KnowledgePacksDirectory "$bench\knowledge-pack\packs" -KnowledgeTrustStore "$bench\knowledge-pack\publisher-trust.json" `
    -KnowledgePackId 'bsoft.windows-event-runbooks' -KnowledgePackVersion '2.0.0' `
    -TaskTimeoutSeconds 9000 -CampaignTimeoutSeconds 345600 -PerRunTokenCap 600000 -TokenBudget 21600000 -ContinueOnTimeout `
    -OutputRoot $campaign
# per riprendere dopo un'interruzione: stesso comando, con -Resume
```

Dimensionamento (profilo Qwen3.8 Smart): generazione misurata ~22–30 token/s; il ragionamento è limitato a 12288 token dal server, quindi una chiamata pesante
dura 10–15 minuti e il tetto di 32768 token (~25 minuti) è un caso limite. Timeout per tentativo 45 min, per chiamata logica 95 min, per attività 2 h 15 min
(il runtime si ferma da solo), per run 2 h 30 min (kill esterno), campagna 96 h come tetto finito (attesa realistica: 10–20 h). Budget: 600 000 token per run,
21,6 M per campagna (il tetto peggiore, 36 × 600 000; il costo in denaro è zero).

## 4. Verifica e analisi

Verifica hash e integrità degli artefatti, rigenera i report (`report/ab-report.*` storico e `report/abcd-report.md`), evidenzia run falliti e mancanti, mostra il
confronto A/B/C/D con media, dispersione e singoli esiti, le risorse Knowledge **realmente lette**, e separa i problemi (diagnostici, timeout, errori di protocollo,
saturazione del contesto). Esce con 0 solo se l'evidenza è integra, tutti i run previsti sono stati eseguiti e completati e i controlli di sicurezza sono verdi.

```powershell
& "$repo\scripts\windows\run-maf-cli-benchmark.ps1" -Review $campaign
```

## 5. Come leggere il risultato

- `Completed` e un esito del runtime, non un giudizio sulla diagnosi. La correttezza si giudica sui **fogli di valutazione ciechi** in `evaluation/` (senza condizione),
  inclusa la lista dei controlli per le stabilità: live dump (0x193) distinto da BSOD, categorie non sommate senza correlazione, WHEA ID 19 e altri ID non confusi,
  una raffica non trattata come guasti indipendenti, nessuna causa hardware confermata senza prove. I flag euristici (classe H) indicano solo le frasi da leggere.
- *Knowledge disponibile* (indice pubblicato) e *usata* (letture servite) sono riportate separatamente: l'offerta non è l'uso. Se il catalogo e stato presentato e il
  modello non ha letto nulla, il report lo dice e non lo conta come successo della Knowledge.
- Confronti: **A→B** (MAF senza Knowledge), **C→D** (MAF con Knowledge), **A→C** (Knowledge senza MAF governato), **B→D** (Knowledge con MAF governato).
  Con meno di tre round per cella la dispersione e ignota e nessuna differenza è un effetto.
- I tool Windows leggono una macchina reale il cui stato cambia fra le prove: il report registra il carico dell'host prima di ogni run. Per un confronto a evidenze
  identiche si usano i meccanismi di replay già esistenti; questa campagna è la parte live.
- Le campagne storiche (Qwen3.6, 64K/Q8, 16K di generazione) restano disponibili ma **non** sono un confronto controllato con questa.
