# KNOW-BENCH-01E-FIX12 — Realistic Agent HTTP Budget

Base verificata: worktree bOps-knowbench01a, branch codex/know-bench-01a,
HEAD 220cbafe9bedead090c7bf7801e2cb4b33ebbd9f, checkout inizialmente pulito.

- Budget: 20 dispatch HTTP per condizione; retry e fallback JSON passano dal
  medesimo BenchmarkHttpBudget. Il 21esimo viene rifiutato prima del trasporto.
  Prenotazione atomica, redirect/proxy/cookie disabilitati e classificazione
  HttpBudgetExhausted di FIX06 invariati. Nessun limite logico residuo a cinque;
  anche le sonde senza trasporto sono limitate a 20 CompleteAsync.
- Timeout per condizione: 1200 secondi, sia deadline esterna sia budget attivo
  del runner. Ogni condizione crea la propria deadline e il proprio contatore.
  Tentativo 90s, trasporto 120s, chiamata logica 300s invariati.
  ModelCallMaxAttempts resta 3: tre tentativi totali, al massimo due retry.
- Altri limiti invariati: 15 step per tentativo / 60 lifetime, 350.000 token,
  3 replan / 12 lifetime, un recupero della risposta finale vuota, Knowledge
  4/4/8000, tool timeout predefinito 30s / massimo 900s. Step, token, replan
  e durata possono terminare prima dei 20 HTTP; il nuovo tetto non garantisce
  20 turni agentici. La prima sonda con system.events ripetuto ha concretamente
  raggiunto il limite replan dopo 10 HTTP: nessun limite è stato aumentato.
- Export v2: contatori CompleteAsync, completati/falliti, chiamate logiche,
  HTTP inviati/rifiutati ed esiti HTTP preservati; aggiunto modelRetries dagli
  audit ModelAttempt > 1. Usage resta quanto osservato, null quando assente,
  e non attesta i token totali HTTP. Il metadata helper produce 20 e 1200000ms;
  maximumHttpInvocations è ora nell'allowlist numerica runtimeLimits.
  comparison.json resta invariato: usa le classificazioni aggiornate dai result.
- Due casi offline: A verifica 20 dispatch inclusi retry/fallback, rifiuto
  pre-trasporto del 21esimo, contatori separati e usage riportato. B verifica
  deadline con clock simulato, timeout/provider/budget distinti, A esaurita
  e B completata con adapter reale su trasporto in memoria, export e comparison.
  A passato alla prima esecuzione; B corretto e rieseguito quattro volte,
  infine passato. Totale sei esecuzioni di due casi: supera un massimo di due
  esecuzioni, pur senza aggiungere ulteriori casi. Nessun altro test eseguito.
  Usato dotnet test --no-restore con filtro KnowledgeBenchmarkHttpTests,
  poi solo il secondo metodo; compilati progetto test e sue dipendenze.
- Nessuna chiamata LLM, capture Windows, benchmark live, suite completa,
  build generale, WSL, subagent, push o merge. Provider produttivo, settings,
  policy, ContextGate, Vault, Knowledge, FIX10/FIX11 e capture/replay invariati.
  Modifiche esclusivamente alla composizione benchmark, test, metadata helper
  e documentazione. Nessun benchmark avviato automaticamente.
