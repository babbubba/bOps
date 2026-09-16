> **OBSOLETE HISTORICAL INPUT — DO NOT IMPLEMENT FROM THIS FILE.** It was migrated into
> [`agentic/_plans/2026-09-16-consolidated-roadmap.md`](../../_plans/2026-09-16-consolidated-roadmap.md).
> Coding agents must ignore this archive unless a task explicitly requests historical research.

# bOps — Piano di sviluppo

**Un agent runtime open-source per operare in sicurezza su macchine Windows e Linux tramite tool dichiarativi, policy, planning e verifica.**

> bOps is an open-source agent runtime for safely operating Windows and Linux machines through declarative tools, policies, planning and verification.
> Non è un chatbot con accesso alla shell. È un'infrastruttura: l'LLM propone, il runtime decide ed esegue, le policy autorizzano, la verifica conferma, l'audit registra.

Questo documento sostituisce ed espande i piani precedenti ("ServerOpsAgent" e la prima bozza "bOpenOps"), con il nome definitivo e tre decisioni aggiuntive: **CLI prima di tutto** (la Web UI è Fase 2); **multi-provider LLM** fin dal design — OpenRouter, Ollama e llama.cpp implementati da subito, Anthropic/OpenAI/DeepSeek previsti e implementati in Fase 2; e, la più strutturale delle tre, **tutto oltre al runtime minimo è un pacchetto** (§0 principio 8) — non solo i domini opzionali di terze parti (SQL Server, PostgreSQL, ecc.) ma anche System, Filesystem, Network, Docker, Service e gli stessi provider LLM, tutti dietro lo stesso meccanismo di estensione (§5), scritti e distribuiti separatamente dal core, closed-source o open, first-party o di terze parti indifferentemente.

---

## 0. Visione e principi non negoziabili

1. **L'LLM non tocca mai la macchina.** Può solo restituire un'intenzione strutturata (`{"tool": "...", "arguments": {...}}`). È il runtime .NET a decidere se e come eseguirla.
2. **Ogni tool dichiara il proprio rischio** (`READ`, `LOW`, `MEDIUM`, `HIGH`, `CRITICAL`) e la policy decide la modalità (`automatic`, `approval`, `forbidden`) — non è una blacklist di comandi, è un modello dichiarativo.
3. **Ogni azione con effetti collaterali viene verificata dopo l'esecuzione.** "Ho riavviato nginx" non è una conclusione valida finché `service.status("nginx")` non conferma `ACTIVE`.
4. **Tutto è tracciato**: ogni tool call produce un evento di audit strutturato, indipendentemente dall'esito.
5. **Il core è agnostico rispetto a modello LLM e sistema operativo.** OpenRouter, Ollama, llama.cpp, e in seguito Anthropic/OpenAI/DeepSeek sono adapter intercambiabili dietro un'interfaccia `IChatModel`/`IAgentPlanner` (dettaglio in §3.1); Windows e Linux sono `Platform provider` dietro le stesse interfacce astratte.
6. **Non iniziare con il multi-agent, non iniziare con il vector store.** Sono feature dell'onda 2, non fondamenta.
7. **La CLI è l'interfaccia primaria, non un ripiego.** Si costruisce per prima e resta il modo principale di usare bOps fino a Fase 2. La Web UI (Angular) non è "la versione seria" e la CLI "quella per testare" — sono due client dello stesso `bOps.Api`/runtime, e la CLI arriva prima perché è quella che ti fa validare Runtime/Policy/Verification più rapidamente, senza il costo di costruire anche un frontend.
8. **Tutto, oltre al runtime minimo, è un pacchetto.** System, Filesystem, Process, Network, Docker e Service non sono "tool imposti dal core": sono pacchetti di primo livello che usano esattamente lo stesso meccanismo (§5) di un pacchetto di terze parti come "SQL Server DBA Toolkit". Lo stesso vale per i provider LLM: OpenRouter, Ollama, llama.cpp e in futuro Anthropic/OpenAI/DeepSeek/altri sono pacchetti-provider, non un `switch` hardcoded nel runtime. Il core resta letteralmente: Runtime (loop, registry), Policy, Memory, Audit, Abstractions (il contratto/SDK), e gli host (CLI/API/Worker). Tutto il resto è estensione — la differenza tra un pacchetto "di serie" e uno scritto da un terzo è solo *da dove viene caricato*, mai *come è fatto*.

---

## 1. Architettura concettuale

```
                     ┌────────────────────┐
                     │        User        │
                     └─────────┬──────────┘
                               │  goal / domanda
                               ▼
                     ┌─────────────────────────────┐
                     │        Agent Runtime         │
                     │                              │
                     │  Planner   (crea/aggiorna il piano)
                     │  Reasoner  (LLM: sceglie tool, interpreta risultati)
                     │  Memory    (stato task, contesto, storico)
                     │  Policy    (autorizza/nega/richiede approvazione)
                     │  Tool selection (Tool Registry)
                     │  Verification (conferma l'effetto di un'azione)
                     └─────────────┬───────────────┘
                                   │  ToolCall { name, arguments }
                     ┌─────────────┼─────────────────────────┐
                     ▼             ▼             ▼           ▼
               ┌──────────┐ ┌──────────┐  ┌──────────┐ ┌──────────┐
               │ System   │ │ Network  │  │ Docker   │ │ Filesystem│
               │ Tools    │ │ Tools    │  │ Tools    │ │ Tools     │
               └────┬─────┘ └────┬─────┘  └────┬─────┘ └────┬─────┘
                    │            │             │            │
                    └────────────┴──────┬──────┴────────────┘
                                         ▼
                         Platform provider (Windows / Linux)
                                         ▼
                              Audit log (ogni chiamata)
```

Il ciclo esplicito che guida il Planner (dettaglio in §6):

```
REQUEST → UNDERSTAND → PLAN → EXECUTE → OBSERVE → EVALUATE → (goal raggiunto? NO → REPLAN | YES → FINAL)
```

---

## 2. Struttura del repository

Ho adattato la struttura che avevi già abbozzato, con namespace `bOps.*` (b minuscola, coerente col nome definitivo del progetto — sì, è una deviazione dalla convenzione PascalCase di .NET per i namespace, ma è deliberata: mantiene ovunque nel codice la stessa grafia del brand). La separazione più importante, dopo l'ultima revisione, non è più "runtime / tool / piattaforma" ma **core / pacchetti**: il core è il minimo indispensabile (loop, registry, policy, memoria, audit, contratto), tutto il resto — inclusi i tool "di serie" e i provider LLM — è un pacchetto (§5), first-party o di terze parti.

```
bops/
│
├── src/
│   ├── core/
│   │   ├── bOps.Abstractions/         # Il contratto/SDK: ITool, IPackage, IModelProviderPackage, IAgentPlanner,
│   │   │                              #   IPolicyEngine, ecc. Zero dipendenze — è ciò che ANCHE i pacchetti referenziano.
│   │   ├── bOps.Runtime/              # Agent loop, Tool/Provider Registry, Plugin Loader, esecuzione, orchestrazione
│   │   ├── bOps.Policy/               # Policy engine, risk model, approval flow, ceiling per-pacchetto
│   │   ├── bOps.Memory/               # Task state, contesto conversazionale, persistenza SQLite
│   │   ├── bOps.Audit/                # Audit log strutturato (append-only)
│   │   ├── bOps.Cli/                  # Entry point CLI: `bops "..."` / `bops diagnose` — PRIMO deliverable (Fase 1)
│   │   ├── bOps.Api/                  # ASP.NET Core Minimal API — Fase 2, backend per la UI Angular
│   │   └── bOps.Worker/               # Generic Host: Windows Service / systemd unit
│   │
│   └── packages/                      # Pacchetti FIRST-PARTY — stesso contratto di uno di terze parti (§5.1),
│       │                              #   compilati e referenziati direttamente nella solution in Fase 1
│       │                              #   (dynamic loading via Plugin Loader arriva con V0.10, §5.2)
│       │
│       ├── bOps.Packages.System/          # system.info/cpu/memory/disk/process — il primo pacchetto, serve a bootstrap V0.1
│       ├── bOps.Packages.Filesystem/      # fs.* — permission model incluso (§7)
│       ├── bOps.Packages.Network/         # network.*
│       ├── bOps.Packages.Docker/          # docker.*
│       ├── bOps.Packages.Service.Windows/ # service.* su Windows — ServiceController, EventLog, WMI
│       ├── bOps.Packages.Service.Linux/   # service.* su Linux — /proc, systemctl/D-Bus, journalctl
│       │
│       ├── bOps.Packages.Providers.OpenRouter/  # pacchetto-provider: IModelProviderPackage per "OpenRouter"
│       ├── bOps.Packages.Providers.Ollama/      # idem per "Ollama"
│       ├── bOps.Packages.Providers.LlamaCpp/    # idem per "LlamaCpp"
│       └── bOps.Packages.Providers.Anthropic/   # Fase 2 — adapter nativo, non OpenAI-compatible (§3.1.2)
│
├── tests/
│   ├── bOps.Runtime.Tests/
│   ├── bOps.Policy.Tests/
│   ├── bOps.Packages.Tests/            # un progetto di test per pacchetto, stesso pattern per first-party e terze parti
│   ├── bOps.Packages.Service.Windows.Tests/   # [Trait("Category","RequiresWindows")]
│   ├── bOps.Packages.Service.Linux.Tests/     # [Trait("Category","RequiresLinux")]
│   └── bOps.IntegrationTests/
│
├── web/
│   └── bops-ui/                   # Fase 2: Angular 21 + NgRx SignalStore (vedi §17) — non esiste ancora in Fase 1
│
├── docs/
│   ├── architecture/          # Questo documento + ADR (vedi §12)
│   ├── tools/                 # Un file di documentazione per ogni tool (generato dal manifest, vedi §4)
│   ├── plugins/                # Guida "scrivi il tuo primo pacchetto bOps" per sviluppatori terzi (§5.4)
│   ├── security/              # Policy di default, modello di rischio, threat model
│   └── agents/                # Prompt di sistema, esempi di planning
│
├── examples/                  # Scenari end-to-end registrati (per regression test del planner)
│
└── README.md
```

Fuori da questo repository, a runtime: una cartella `plugins/` (percorso configurabile, non versionata in git) dove atterrano i pacchetti di terze parti installati con `bops plugin install` — è lì che il Plugin Loader (§5.2) li scopre dinamicamente. I pacchetti *first-party* sopra restano nel repository principale e compilati staticamente per tutta la Fase 1: la "prova del nove" che l'astrazione regge è che, una volta pronto il Plugin Loader (V0.10), potresti farli caricare dinamicamente esattamente come uno di terze parti senza toccarne il codice interno — cambia solo *come vengono registrati*, mai *come sono fatti* (principio 8, §0).

Perché questa suddivisione: `bOps.Abstractions` non dipende da nulla (nemmeno da `Microsoft.Extensions.AI`), quindi puoi scrivere e testare `Runtime`, `Policy`, `Memory`, `Audit` senza aver ancora deciso quale libreria LLM, quale SO o quale set di tool usare — sono tutti dietro lo stesso contratto. `bOps.Packages.Providers.OpenRouter/Ollama/LlamaCpp` condividono internamente la stessa implementazione `OpenAiCompatibleChatModel` (§3.1: sono config diverse dello stesso codice), ma restano tre pacchetti distinti così ciascuno può essere abilitato/disabilitato/sostituito indipendentemente, ed è coerente con l'idea che "aggiungere un provider" e "aggiungere un tool" siano la stessa identica operazione architetturale. `bOps.Packages.Providers.Anthropic` è separato da subito anche se vuoto in Fase 1, per segnalare che è strutturalmente diverso (adapter nativo, non OpenAI-compatible, §3.1.2). `bOps.Cli` è il primo eseguibile che costruisci e resta l'interfaccia principale finché non arrivi in Fase 2 — `bOps.Api`/`web/bops-ui` semplicemente non esistono nella solution iniziale.

---

## 3. Le interfacce core (`bOps.Abstractions`)

Questo è il cuore del progetto: tutto il resto è implementazione di queste interfacce.

```csharp
namespace bOps.Abstractions;

// ---- Tool ----

public enum RiskLevel { Read, Low, Medium, High, Critical }

public sealed record ToolParameter(string Name, string Type, string Description, bool Required = true);

public sealed record ToolManifest(
    string Name,                       // "docker.logs"
    string Description,
    RiskLevel Risk,
    IReadOnlyList<string> Platforms,   // ["linux", "windows"]
    IReadOnlyList<string> Requires,    // ["docker"] — capability richieste, per il discovery
    IReadOnlyList<ToolParameter> Parameters);

public sealed record ToolCallRequest(string ToolName, IReadOnlyDictionary<string, object?> Arguments);

public sealed record ToolCallResult(bool Success, string? Output, string? ErrorMessage, TimeSpan Duration);

public interface ITool
{
    ToolManifest Manifest { get; }
    Task<ToolCallResult> ExecuteAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default);
}

// ---- Tool Registry (discovery) ----

public interface IToolRegistry
{
    void Register(ITool tool);
    IReadOnlyList<ToolManifest> GetAvailableManifests();   // filtrati per piattaforma corrente + capability disponibili
    ITool? Resolve(string toolName);
}

// ---- Policy Engine ----

public enum PolicyMode { Automatic, Approval, Forbidden }

public interface IPolicyEngine
{
    PolicyMode Evaluate(ToolManifest manifest, IReadOnlyDictionary<string, object?> arguments);
}

// ---- Approval (human-in-the-loop) ----

public interface IApprovalProvider
{
    Task<bool> RequestApprovalAsync(ToolManifest manifest, IReadOnlyDictionary<string, object?> arguments, string reason, CancellationToken ct = default);
}

// ---- Verification ----

public interface IVerificationService
{
    // Ogni tool "non-READ" può registrare come verificare il proprio effetto (spesso: rieseguire una tool READ correlata)
    Task<ToolCallResult> VerifyAsync(ToolCallRequest originalCall, ToolCallResult executionResult, CancellationToken ct = default);
}

// ---- Memory ----

public interface IAgentMemory
{
    Task<TaskState> CreateTaskAsync(string goal, CancellationToken ct = default);
    Task SaveStepAsync(Guid taskId, PlanStep step, CancellationToken ct = default);
    Task<TaskState?> GetTaskAsync(Guid taskId, CancellationToken ct = default);
}

public sealed record PlanStep(int Index, string Description, ToolCallRequest? ToolCall, ToolCallResult? Result, string? Observation);
public sealed record TaskState(Guid Id, string Goal, string Status, List<PlanStep> Steps, DateTimeOffset CreatedAtUtc);

// ---- Planner / Reasoner (LLM-agnostic) ----

public interface IChatModel
{
    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default);
}

public sealed record ModelRequest(string SystemPrompt, IReadOnlyList<ChatTurn> History, IReadOnlyList<ToolManifest> AvailableTools);
public sealed record ChatTurn(string Role, string Content);
public sealed record ModelResponse(string? TextResponse, ToolCallRequest? RequestedToolCall, bool IsFinal);

public interface IAgentPlanner
{
    Task<TaskState> RunAsync(string goal, CancellationToken ct = default);
}

// ---- Audit ----

public sealed record AuditEvent(
    DateTimeOffset TimestampUtc,
    Guid TaskId,
    string Goal,
    string Tool,
    IReadOnlyDictionary<string, object?> Arguments,
    RiskLevel Risk,
    string Authorization,     // "automatic" | "user-approved" | "user-rejected"
    string Result,            // "success" | "failure"
    string? Verification);

public interface IAuditSink
{
    Task WriteAsync(AuditEvent evt, CancellationToken ct = default);
}
```

Nota di design: `IChatModel` è deliberatamente minimale e non dipende da `Microsoft.Extensions.AI` o da un SDK specifico — è l'interfaccia *tua*. Gli adapter concreti (`OpenRouterChatModel`, `AnthropicChatModel`, `OllamaChatModel`) vivono in `bOps.Models` e traducono da/verso l'SDK reale. Questo è esattamente il punto "il tuo progetto deve essere LLM-agnostic": se domani `Microsoft.Agents.AI` cambia API (probabile, è ancora in preview attiva), cambi solo l'adapter.

> Puoi comunque *usare* `Microsoft.Extensions.AI`/`Microsoft.Agents.AI` internamente dentro l'adapter `OpenRouterChatModel`, se ti velocizza lo sviluppo (gestisce già serializzazione dei tool-call in stile OpenAI) — l'importante è che il resto del sistema (`Runtime`, `Policy`, `Memory`) non lo veda mai direttamente.

### 3.1 Model Provider Layer — multi-provider fin dal design, multi-provider incrementale nell'implementazione

Requisito: bOps deve prevedere fin da subito **tutti** i provider rilevanti, ma implementarne solo un sottoinsieme nella prima fase — e, come i tool, ogni provider è un **pacchetto** (§0 principio 8, dettaglio completo del meccanismo di caricamento in §5), mai un'entry hardcoded nel runtime. Questo significa anche che un domani un terzo potrà scrivere un pacchetto-provider per un motore che oggi non è nemmeno in questa lista (Azure AI Foundry, AWS Bedrock, Google Vertex AI, Groq, ecc.) senza toccare bOps stesso — esattamente come "SQL Server DBA Toolkit" per i tool.

| Provider | Stato | Fase |
|---|---|---|
| **OpenRouter** | Implementato | V0.1 |
| **Ollama** (server locale) | Implementato | V0.1 |
| **llama.cpp** (`llama-server`) | Implementato | V0.1 |
| OpenAI (diretto) | Previsto, adapter da scrivere | Fase 2 (insieme alla UI) |
| Anthropic | Previsto, adapter da scrivere | Fase 2 (insieme alla UI) |
| DeepSeek | Previsto, adapter da scrivere | Fase 2 (insieme alla UI) — assumo che il "ds4" dei tuoi appunti indichi DeepSeek; correggimi se intendevi altro |

**L'osservazione che rende questo facile**: OpenRouter, Ollama, llama.cpp, OpenAI e DeepSeek espongono tutti un endpoint HTTP **compatibile con lo schema "OpenAI Chat Completions"** (`POST /v1/chat/completions`, stesso formato di richiesta/risposta, stesso schema per i `tool_calls`). Questo significa che non servono 5 adapter diversi: ne basta **uno generico**, parametrizzato per provider, più eventuali "quirk" minimi.

```csharp
namespace bOps.Models;

public sealed record ChatModelOptions(
    string Provider,       // "OpenRouter" | "Ollama" | "LlamaCpp" | "OpenAI" | "DeepSeek"
    string BaseUrl,
    string? ApiKey,        // null per Ollama/llama.cpp in locale
    string Model,
    bool SupportsNativeToolCalling = true);   // false → attiva il fallback JSON strutturato, vedi sotto

// Un solo adapter per tutti i provider "OpenAI-compatible"
public sealed class OpenAiCompatibleChatModel(ChatModelOptions options, HttpClient http) : IChatModel
{
    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        var payload = options.SupportsNativeToolCalling
            ? BuildNativeToolCallPayload(request, options.Model)      // usa "tools"/"tool_choice" come da spec OpenAI
            : BuildJsonSchemaFallbackPayload(request, options.Model); // vedi §3.1.1 — forza output JSON via prompt/grammar

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(payload)
        };
        if (!string.IsNullOrEmpty(options.ApiKey))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        var httpResponse = await http.SendAsync(httpRequest, ct);
        httpResponse.EnsureSuccessStatusCode();
        var body = await httpResponse.Content.ReadFromJsonAsync<OpenAiChatCompletionResponse>(cancellationToken: ct);

        return ParseResponse(body!, options.SupportsNativeToolCalling);
    }

    // BuildNativeToolCallPayload / BuildJsonSchemaFallbackPayload / ParseResponse: dettaglio implementativo,
    // ma concettualmente identico per tutti i provider di questa famiglia.
}

// ---- Provider come pacchetto (§0 principio 8, §5): niente switch hardcoded ----
// Un pacchetto-provider dichiara quali "Provider" id sa gestire, esattamente come
// un pacchetto-tool dichiara i propri ToolManifest.
public interface IModelProviderPackage
{
    IReadOnlyList<string> SupportedProviderIds { get; }   // es. ["OpenRouter"], o ["Bedrock","BedrockClaude"] per un terzo
    IChatModel Create(ChatModelOptions options);
}

public interface IChatModelRegistry
{
    void Register(IModelProviderPackage package);
    IChatModel Create(ChatModelOptions options);   // risolve in base a options.Provider tra i pacchetti registrati
}

public sealed class ChatModelRegistry : IChatModelRegistry
{
    private readonly Dictionary<string, IModelProviderPackage> _byProviderId = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IModelProviderPackage package)
    {
        foreach (var id in package.SupportedProviderIds)
            _byProviderId[id] = package;
    }

    public IChatModel Create(ChatModelOptions options) =>
        _byProviderId.TryGetValue(options.Provider, out var package)
            ? package.Create(options)
            : throw new NotSupportedException(
                $"Provider '{options.Provider}' non riconosciuto — nessun pacchetto-provider registrato lo dichiara. " +
                "Verifica che il pacchetto sia installato/abilitato (§5).");
}

// Registrazione in Program.cs / DI — identica nello spirito al foreach di IToolProvider in §4:
// ogni pacchetto-provider (first-party o di terze parti) si registra da solo, il Runtime non conosce
// mai un elenco statico di provider.
foreach (var package in serviceProvider.GetServices<IModelProviderPackage>())
    chatModelRegistry.Register(package);
```

I pacchetti-provider first-party di Fase 1 sono tre righe di codice ciascuno, perché condividono tutti `OpenAiCompatibleChatModel`:

```csharp
namespace bOps.Packages.Providers.OpenRouter;

public sealed class OpenRouterProviderPackage(IHttpClientFactory httpFactory) : IModelProviderPackage
{
    public IReadOnlyList<string> SupportedProviderIds { get; } = ["OpenRouter"];

    public IChatModel Create(ChatModelOptions options) =>
        new OpenAiCompatibleChatModel(options, httpFactory.CreateClient("bOps.Models"));
}
// bOps.Packages.Providers.Ollama.OllamaProviderPackage e bOps.Packages.Providers.LlamaCpp.LlamaCppProviderPackage
// sono identici, cambia solo SupportedProviderIds → ["Ollama"] / ["LlamaCpp"].
```

`appsettings.json` — puoi cambiare provider senza ricompilare, e in futuro persino a runtime da UI:

```json
{
  "ModelProvider": {
    "Provider": "OpenRouter",
    "BaseUrl": "https://openrouter.ai/api/v1",
    "ApiKey": "",
    "Model": "anthropic/claude-sonnet-4.5",
    "SupportsNativeToolCalling": true
  }
}
```

```json
{
  "ModelProvider": {
    "Provider": "Ollama",
    "BaseUrl": "http://localhost:11434/v1",
    "ApiKey": null,
    "Model": "qwen2.5:14b",
    "SupportsNativeToolCalling": true
  }
}
```

```json
{
  "ModelProvider": {
    "Provider": "LlamaCpp",
    "BaseUrl": "http://localhost:8080/v1",
    "ApiKey": null,
    "Model": "loaded-model",
    "SupportsNativeToolCalling": false
  }
}
```

Note pratiche sui tre provider della prima fase:

- **Ollama** espone sia la sua API nativa (`/api/chat`) sia un endpoint di compatibilità OpenAI (`/v1/chat/completions`), e supporta il tool-calling nativo per i modelli che lo implementano (Llama 3.1+, Qwen2.5, Mistral Nemo, ecc. — non tutti i modelli scaricabili lo supportano allo stesso modo). Usa sempre l'endpoint `/v1/...` per restare sull'adapter generico. Riferimento: [Tool support — Ollama Blog](https://ollama.com/blog/tool-support).
- **llama.cpp** (`llama-server`, il server incluso nel progetto `ggml-org/llama.cpp`) espone anch'esso `/v1/chat/completions` compatibile OpenAI, e supporta il function-calling tramite grammatica GBNF generata dallo schema dei tool — ma la qualità dipende molto dal modello/template di chat caricato: alcuni modelli/quantizzazioni non seguono bene lo schema. Per questo nella configurazione sopra ho messo `SupportsNativeToolCalling: false` come default prudente per llama.cpp: usa il fallback JSON strutturato (§3.1.1) finché non verifichi che il modello specifico che usi supporta bene il tool-calling nativo. Riferimento: [llama.cpp — function-calling.md](https://github.com/ggml-org/llama.cpp/blob/master/docs/function-calling.md) e [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md).
- **OpenRouter** instrada verso decine di modelli diversi: il supporto al tool-calling varia per modello a monte (i modelli Anthropic/OpenAI dietro OpenRouter lo supportano bene; alcuni modelli open-source instradati potrebbero no) — vale la stessa cautela di llama.cpp se usi modelli meno mainstream tramite OpenRouter.

#### 3.1.1 Fallback: tool-calling via JSON strutturato (per modelli/provider senza function-calling nativo affidabile)

Quando `SupportsNativeToolCalling = false`, l'adapter non usa il campo `tools` della request, ma:

1. Inietta nel system prompt lo schema dei tool disponibili (già hai `ToolManifest` — serializzalo in JSON dentro il prompt) e istruzioni esplicite: *"Rispondi SOLO con un oggetto JSON `{\"tool\": \"...\", \"arguments\": {...}}`, oppure `{\"final\": \"...\"}` se hai concluso. Nessun altro testo."*
2. Se il server lo supporta, vincola l'output con una **grammatica** (llama.cpp: parametro `grammar`/`json_schema` nella request; Ollama: parametro `format` con uno schema JSON) — questo è il modo più affidabile per evitare che il modello "rompa" il JSON con testo libero.
3. Fa il parsing dell'output; se non è JSON valido, **un solo retry** con un messaggio di correzione ("il tuo output non era JSON valido, riprova rispettando lo schema"), poi fallisce lo step in modo esplicito (mai eseguire un tool "indovinato" da un parsing fuzzy).

Questo fallback è ciò che rende bOps realmente utilizzabile anche con modelli locali più piccoli/meno capaci, dove il tool-calling nativo del provider è inaffidabile o assente — è un tassello importante quanto Policy/Verification per la robustezà del runtime, non un dettaglio secondario.

#### 3.1.2 Fase 2: Anthropic, OpenAI diretto, DeepSeek

- **OpenAI diretto** e **DeepSeek**: espongono anch'essi API compatibili OpenAI (DeepSeek è compatibile 1:1 con lo schema Chat Completions) — quando li implementi, sono altri due pacchetti-provider da tre righe come `OpenRouterProviderPackage` sopra, senza nuovo codice nell'adapter (`OpenAiCompatibleChatModel` li copre già).
- **Anthropic** resta il caso a parte: l'API nativa (Messages API) ha uno schema di richiesta/risposta e di tool-use diverso da quello OpenAI (blocchi di contenuto tipizzati, `tool_use`/`tool_result` invece di `tool_calls`). Anthropic offre anche un layer di compatibilità OpenAI SDK (beta) che potresti usare come scorciatoia per restare sull'adapter generico, ma per affidabilità/fedeltà completa (in particolare su tool-calling e streaming) conviene scrivere un `AnthropicChatModel` nativo dedicato in Fase 2, quando estendi i provider insieme alla UI. Verifica lo stato della compatibility layer al momento in cui implementi: [OpenAI SDK compatibility — Claude Platform Docs](https://platform.claude.com/docs/en/cli-sdks-libraries/libraries/openai-sdk).

---

## 4. Tool Registry e Tool Discovery

Ogni tool si descrive con un `ToolManifest` (vedi sopra), che è anche serializzabile in JSON — questo è il "protocollo" di discovery:

```json
{
  "name": "docker.logs",
  "description": "Reads logs from a Docker container",
  "risk": "READ",
  "platforms": ["linux", "windows"],
  "requires": ["docker"],
  "parameters": [
    { "name": "container", "type": "string", "description": "Container name or ID", "required": true },
    { "name": "tail", "type": "integer", "description": "Number of lines to return", "required": false }
  ]
}
```

`IToolRegistry.GetAvailableManifests()` filtra automaticamente per:
- **piattaforma corrente** (`Environment.OSVersion` / `RuntimeInformation.IsOSPlatform`) rispetto a `Platforms`;
- **capability disponibili** rispetto a `Requires` (es. `docker.*` sparisce dai tool disponibili se il Docker daemon non risponde — vedi `ICapabilityProbe` sotto).

```csharp
public interface ICapabilityProbe
{
    Task<bool> IsAvailableAsync(string capability, CancellationToken ct = default);
    // "docker" → prova a connettersi al Docker daemon
    // "systemd" → verifica se /run/systemd/system esiste
}
```

Questo è ciò che rende il sistema **estendibile senza toccare il core**: un pacchetto (`bOps.Packages.Network`, `bOps.Packages.Docker`, o un domani `bops-plugin-proxmox` di terze parti — non c'è alcuna differenza di trattamento) si registra da solo alla startup (via DI scanning o un semplice `IToolProvider` per assembly), dichiara i propri manifest, e il Planner li vede automaticamente nella lista di tool disponibili — nessuna modifica a `Runtime` o al prompt di sistema statico. `network.*` e `docker.*`, in particolare, **non sono tool "imposti dal core"**: sono pacchetti come qualunque altro (§0 principio 8), semplicemente ci sono già dal primo giorno perché sono utili quasi ovunque — non perché il runtime li conosca in modo speciale.

```csharp
public interface IToolProvider
{
    IEnumerable<ITool> GetTools();
}

// Registrazione in Program.cs / DI — in Fase 1 i pacchetti first-party sono referenziati direttamente
// (project reference), quindi questo foreach vede sia bOps.Packages.System/Network/Docker/Filesystem/Service
// sia, più avanti, ogni pacchetto scoperto dinamicamente dal Plugin Loader (§5.2):
foreach (var provider in serviceProvider.GetServices<IToolProvider>())
    foreach (var tool in provider.GetTools())
        toolRegistry.Register(tool);
```

Il meccanismo completo di impacchettamento (manifest di pacchetto, caricamento dinamico, trust/sicurezza per pacchetti di terze parti anche closed-source) è il tema centrale di **§5** — qui vale solo notare che il contratto (`ITool`/`IToolProvider`/`ToolManifest`) è già, da solo, sufficiente a rendere ogni categoria di tool un pacchetto a pieno titolo.

---

## 5. Plugin & Package Ecosystem — pacchetti di funzionalità, first-party e di terze parti

Requisito architetturale di prima classe, non un "extra" da fine roadmap: bOps deve poter crescere per dominio (SQL Server, PostgreSQL, Kubernetes, Proxmox, Azure, un nuovo provider LLM, ecc.) tramite **pacchetti sviluppati e distribuiti separatamente** dal repository principale — anche closed-source, anche di terze parti — senza toccare una riga del core. E, come specificato, questo vale sia per i tool (System, Filesystem, Network, Docker, Service sono già pacchetti, §4) sia per i provider LLM (OpenRouter, Ollama, llama.cpp sono già pacchetti-provider, §3.1) — un pacchetto può contribuire l'uno, l'altro, o entrambi.

### 5.1 Cosa è un "pacchetto"

Un pacchetto bOps è:
- Una o più assembly .NET che referenziano **solo** `bOps.Abstractions` (il "Plugin SDK" — l'unico contratto stabile e versionato che un pacchetto deve conoscere) e implementano `IToolProvider` e/o `IModelProviderPackage` per contribuire tool e/o provider.
- Un **manifest di pacchetto** (`bops-plugin.json`), distinto dal `ToolManifest` di ogni singolo tool (che resta interno), con i metadati del pacchetto nel suo complesso e cosa contribuisce (`contributes`).
- Un repository/progetto **separato** da `bops/` per i pacchetti di terze parti (tipicamente `bops-plugin-sqlserver/`, `bops-plugin-postgres/`, ecc., ciascuno con la propria solution, versionamento, CI, e licenza — bOps non impone open-source ai pacchetti). I pacchetti *first-party* (§2) vivono invece nello stesso repository per comodità di CI/versionamento coordinato, ma sono già isolati internamente esattamente come lo sarebbe uno esterno.

```json
{
  "id": "bops-plugin-sqlserver",
  "displayName": "SQL Server DBA Toolkit",
  "publisher": "AcmeCorp",
  "version": "1.2.0",
  "license": "Commercial",
  "minHostAbstractionsVersion": "1.0.0",
  "maxDeclaredRisk": "High",
  "requires": ["sqlserver"],
  "contributes": {
    "tools": ["sqlserver.info", "sqlserver.wait_stats", "sqlserver.missing_indexes", "..."],
    "modelProviders": []
  }
}
```

```json
{
  "id": "bops-plugin-bedrock",
  "displayName": "AWS Bedrock Provider",
  "publisher": "una terza parte qualunque",
  "version": "0.3.0",
  "license": "MIT",
  "minHostAbstractionsVersion": "1.0.0",
  "maxDeclaredRisk": "Read",
  "requires": [],
  "contributes": {
    "tools": [],
    "modelProviders": ["Bedrock", "BedrockClaude"]
  }
}
```

Il secondo esempio è la dimostrazione diretta di "anche i provider si implementano separatamente e si caricano come plugin": nessuno in bOps deve scrivere l'adapter per AWS Bedrock — chiunque può farlo come pacchetto esterno, esattamente con lo stesso meccanismo di "SQL Server DBA Toolkit".

### 5.2 Caricamento a runtime — isolamento delle dipendenze

Un pacchetto di terze parti può portarsi dietro le proprie dipendenze (es. una versione di `Microsoft.Data.SqlClient` diversa da quella usata altrove) — serve isolamento, non un semplice `Assembly.LoadFrom`. Usa [`McMaster.NETCore.Plugins`](https://github.com/natemcmaster/DotNetCorePlugins) (libreria pensata esattamente per questo: plugin .NET con `AssemblyLoadContext` isolato, condivisione selettiva dei tipi "pubblici" come `bOps.Abstractions`), invece di scrivere a mano la gestione di `AssemblyLoadContext`.

```csharp
namespace bOps.Runtime.Plugins;

public sealed class PluginLoader(PluginDiscoveryOptions options, ILogger<PluginLoader> logger)
{
    public IEnumerable<LoadedPlugin> DiscoverAndLoad()
    {
        foreach (var pluginDir in Directory.GetDirectories(options.PluginsRootPath))
        {
            var manifestPath = Path.Combine(pluginDir, "bops-plugin.json");
            if (!File.Exists(manifestPath)) continue;

            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath))!;
            if (!IsCompatible(manifest))
            {
                logger.LogWarning("Pacchetto {Id} incompatibile con questa versione di bOps, saltato", manifest.Id);
                continue;
            }

            var mainDll = Path.Combine(pluginDir, $"{manifest.Id}.dll");
            var loader = McMaster.NETCore.Plugins.PluginLoader.CreateFromAssemblyFile(
                mainDll,
                sharedTypes: [typeof(ITool), typeof(IToolProvider), typeof(IModelProviderPackage), typeof(ToolManifest)]);

            var assembly = loader.LoadDefaultAssembly();
            foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract))
            {
                if (typeof(IToolProvider).IsAssignableFrom(type))
                    yield return LoadedPlugin.OfToolProvider(manifest, (IToolProvider)Activator.CreateInstance(type)!);
                if (typeof(IModelProviderPackage).IsAssignableFrom(type))
                    yield return LoadedPlugin.OfModelProvider(manifest, (IModelProviderPackage)Activator.CreateInstance(type)!);
            }
        }
    }

    private bool IsCompatible(PluginManifest manifest) =>
        Version.Parse(manifest.MinHostAbstractionsVersion) <= HostAbstractionsVersion.Current;
}
```

Convenzione di deploy: cartella `plugins/<plugin-id>/` accanto all'eseguibile (o percorso configurabile), ciascuna con la propria DLL + dipendenze + `bops-plugin.json`. `bops plugin install <path-o-url>`, `bops plugin list`, `bops plugin enable/disable <id>`, `bops plugin remove <id>` sono i comandi CLI che gestiscono questa cartella (V0.10 in roadmap).

> Alternativa per la *distribuzione* (non per il *caricamento*, che resta `McMaster.NETCore.Plugins`): impacchetta il pacchetto come **pacchetto NuGet** invece che come zip di file. `bops plugin install AcmeCorp.BOps.SqlServer` scarica da un feed (pubblico o privato aziendale) e lo estrae in `plugins/`. Utile soprattutto per pacchetti closed-source distribuiti da terze parti a pagamento, dove un feed NuGet privato con autenticazione è già lo standard de facto.

### 5.3 Trust e sicurezza — un pacchetto di terze parti nel path di un runtime con permessi su un server di produzione

Qui la barra di sicurezza si alza, non si abbassa: un pacchetto può essere closed-source (non lo puoi auditare a codice) e sviluppato da terzi (non ti fidi quanto del tuo stesso codice). Regole:

1. **Il manifest dichiara un `maxDeclaredRisk`, ma il host non si fida ciecamente**: la Policy Engine (§7) applica un **ceiling per-pacchetto** indipendente da cosa il singolo `ToolManifest` dichiara — un pacchetto non certificato non può auto-elevarsi a `Critical` anche se un suo tool lo dichiarasse (bug o malizia). Estendi `policy.yaml`:
   ```yaml
   packages:
     "bops-plugin-sqlserver":
       trustLevel: verified       # unverified | community | verified | official
       maxRisk: High              # ceiling indipendente dal manifest del pacchetto stesso
     "bops-plugin-bedrock":
       trustLevel: community
       maxRisk: Low               # un pacchetto-provider non dovrebbe MAI aver bisogno di rischio alto:
                                   # espone solo IChatModel, non esegue azioni sulla macchina
   ```
2. **Opt-in esplicito, mai auto-discovery silenziosa**: un pacchetto nuovo trovato in `plugins/` non si attiva da solo — va abilitato esplicitamente (`bops plugin enable <id>`) dopo revisione dell'operatore, soprattutto se `trustLevel` è `unverified`/`community`.
3. **Isolamento di processo per i pacchetti non ufficiali** (evoluzione, non richiesto dal giorno 1): per pacchetti `unverified`, valuta di eseguirli in un processo separato (comunicazione via gRPC/named pipe verso il processo host) invece che in-process — un crash o un comportamento anomalo non porta giù il runtime principale e non condivide lo stesso spazio di memoria/permessi. I pacchetti `official`/`verified` possono restare in-process per performance.
4. **Firma dei pacchetti**: per la distribuzione via NuGet, richiedi pacchetti firmati (NuGet package signing) prima dell'installazione in produzione.
5. **Audit invariato**: ogni tool call di un pacchetto passa dallo stesso `AuditEvent` (§9) con in più il campo `packageId` — nessuna scorciatoia per i tool di terze parti. Anche le chiamate al modello fatte tramite un pacchetto-provider di terze parti (es. `bops-plugin-bedrock`) vanno tracciate: quale provider ha servito quella risposta, non solo quali tool sono stati chiamati.

### 5.4 Il "Plugin SDK" — cosa espone bOps a chi scrive un pacchetto

`bOps.Abstractions` **è** l'SDK: nessun pacchetto aggiuntivo da mantenere separatamente. Le regole di compatibilità però diventano più stringenti nel momento in cui esistono consumer esterni:

- **Semantic versioning rigoroso** su `bOps.Abstractions`: un breaking change a `ITool`/`ToolManifest`/`RiskLevel`/`IModelProviderPackage` è un major bump, comunicato con adeguato preavviso — è il contratto con chi ha già pubblicato un pacchetto.
- Distribuisci `bOps.Abstractions` come **pacchetto NuGet pubblico indipendente** (anche se il resto del core non lo fosse), proprio perché terzi devono poterlo referenziare senza clonare il repository principale.
- Fornisci un template di scaffolding, `dotnet new bops-plugin` (con un flag per generare uno scheletro tool, provider, o entrambi), che genera già la struttura minima — abbassa parecchio la barriera d'ingresso per sviluppatori terzi.
- Documenta l'SDK in `docs/plugins/` con una guida "Scrivi il tuo primo pacchetto bOps", non solo il reference tecnico.

### 5.5 Esempio: pacchetto `bops-plugin-sqlserver` (SQL Server DBA Toolkit)

Repository separato, referenzia solo `bOps.Abstractions`:

```
bops-plugin-sqlserver/                     # repo indipendente, licenza propria
├── src/AcmeCorp.BOps.SqlServer/
│   ├── AcmeCorp.BOps.SqlServer.csproj      # <PackageReference Include="bOps.Abstractions" Version="1.*" />
│   ├── bops-plugin.json
│   ├── SqlServerToolProvider.cs            # implementa IToolProvider
│   └── Tools/
│       ├── SqlServerInfoTool.cs            # sqlserver.info — versione, edition, uptime, config generale
│       ├── WaitStatsTool.cs                # sqlserver.wait_stats — sys.dm_os_wait_stats, individua colli di bottiglia
│       ├── MissingIndexesTool.cs           # sqlserver.missing_indexes — sys.dm_db_missing_index_details
│       ├── BlockingSessionsTool.cs         # sqlserver.blocking — sessioni bloccate/bloccanti in tempo reale
│       ├── QueryStoreTopTool.cs            # sqlserver.query_store_top — query più costose da Query Store
│       ├── ConfigGetTool.cs                # sqlserver.config_get — sp_configure, max server memory, MAXDOP, ecc.
│       └── TuningRecommendationsTool.cs    # sqlserver.tune_recommendations — sintetizza le tool sopra in una diagnosi
└── README.md
```

```csharp
namespace AcmeCorp.BOps.SqlServer;

public sealed class WaitStatsTool(ISqlConnectionFactory connections) : ITool
{
    public ToolManifest Manifest { get; } = new(
        "sqlserver.wait_stats",
        "Analizza sys.dm_os_wait_stats per individuare i colli di bottiglia principali (I/O, lock, CPU, memoria)",
        RiskLevel.Read, ["windows", "linux"], ["sqlserver"],
        [new ToolParameter("database", "string", "Nome dell'istanza/connessione configurata", Required: false)]);

    public async Task<ToolCallResult> ExecuteAsync(IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        await using var conn = await connections.OpenAsync(args, ct);
        const string query = """
            SELECT TOP 10 wait_type, wait_time_ms, waiting_tasks_count
            FROM sys.dm_os_wait_stats
            WHERE wait_type NOT IN ('SLEEP_TASK','BROKER_TASK_STOP','CLR_AUTO_EVENT') -- rumore noto
            ORDER BY wait_time_ms DESC;
            """;
        var rows = await conn.QueryAsync(query, ct);
        return new ToolCallResult(true, FormatAsTable(rows), null, TimeSpan.Zero);
    }
}
```

Notare i livelli di rischio coerenti col resto del sistema: tutte le tool di analisi/diagnosi (`wait_stats`, `missing_indexes`, `blocking`, `query_store_top`, `config_get`, `tune_recommendations`) sono `READ` — **il pacchetto di base non modifica nulla**, produce solo diagnosi e raccomandazioni (esattamente il tuo scenario "valuta miglioramenti e tuning"). Se in futuro il pacchetto aggiunge tool che *applicano* una modifica (es. `sqlserver.apply_index_recommendation`, `sqlserver.set_max_memory`), quelle sono `Medium`/`High` con verifica post-azione dedicata (es. rileggere `sp_configure` dopo la modifica) — stesso pattern di §7/§8, nessuna eccezione per il fatto di essere un pacchetto.

### 5.6 Esempio: pacchetto `bops-plugin-postgres` (in breve, stesso pattern)

```
postgres.info                postgres.stat_activity        postgres.locks
postgres.vacuum_stats        postgres.slow_queries          postgres.config_get
postgres.replication_status  postgres.bloat_estimate
```

Stessa struttura di repository, stesso `IToolProvider`, libreria client `Npgsql` invece di `Microsoft.Data.SqlClient`. Non c'è un'astrazione comune forzata tra i due pacchetti (`ISqlServerProvider`/`IPostgresProvider` restano concettualmente separati, non un fittizio `IDatabaseProvider` unico) perché DBA su motori diversi significa DMV/cataloghi di sistema/tuning knob completamente diversi — forzare un'interfaccia comune qui produrrebbe la classica astrazione "a foglia di fico" che non aiuta nessuno. Ogni pacchetto resta libero di avere la propria struttura interna; l'unico contratto condiviso è `ITool`/`IToolProvider` (e, per un pacchetto-provider, `IModelProviderPackage`).

### 5.7 Cosa questo cambia nella UI (Fase 2)

La pagina Settings (§17, `features/settings/`) diventa anche il pannello di gestione pacchetti: elenco installati (tool e provider insieme, con `trustLevel`, versione, publisher, cosa contribuiscono), enable/disable, e — quando un pacchetto dichiara uno schema di configurazione (es. connection string SQL Server, o API key di un provider terzo) — un form generato dinamicamente. Non serve progettare questo in Fase 1, ma tienilo presente nella forma di `bOps.Api`: `GET /api/packages` e `POST /api/packages/{id}/enable` sono naturali da subito nel contratto API anche se la UI arriva dopo.

---

## 6. Il loop di planning (Runtime)

Non un semplice "LLM → tool → LLM → tool", ma uno state loop esplicito e ispezionabile:

```
REQUEST → UNDERSTAND → PLAN → EXECUTE → OBSERVE → EVALUATE → goal raggiunto?
                                                                 NO → REPLAN (torna a PLAN)
                                                                 YES → FINAL
```

Implementazione di riferimento (`bOps.Runtime/AgentPlanner.cs`), volutamente esplicita invece di nascosta dentro un framework:

```csharp
namespace bOps.Runtime;

public sealed class AgentPlanner(
    IChatModel model,
    IToolRegistry registry,
    IPolicyEngine policy,
    IApprovalProvider approval,
    IVerificationService verification,
    IAgentMemory memory,
    IAuditSink audit,
    ILogger<AgentPlanner> logger) : IAgentPlanner
{
    private const int MaxSteps = 15; // guard-rail anti-loop-infinito

    public async Task<TaskState> RunAsync(string goal, CancellationToken ct = default)
    {
        var task = await memory.CreateTaskAsync(goal, ct);
        var history = new List<ChatTurn> { new("user", goal) };

        for (var i = 0; i < MaxSteps; i++)
        {
            // PLAN / UNDERSTAND: il modello decide il prossimo passo (o dichiara di aver finito)
            var response = await model.CompleteAsync(
                new ModelRequest(SystemPrompt.Value, history, registry.GetAvailableManifests()), ct);

            if (response.IsFinal || response.RequestedToolCall is null)
            {
                await memory.SaveStepAsync(task.Id, new PlanStep(i, "Diagnosi finale", null, null, response.TextResponse), ct);
                return task with { Status = "completed" };
            }

            var call = response.RequestedToolCall;
            var manifest = registry.Resolve(call.ToolName)?.Manifest
                ?? throw new InvalidOperationException($"Tool sconosciuto: {call.ToolName}");

            // POLICY: automatic / approval / forbidden
            var mode = policy.Evaluate(manifest, call.Arguments);
            if (mode == PolicyMode.Forbidden)
            {
                history.Add(new ChatTurn("tool", $"[DENIED] '{call.ToolName}' non è permesso dalla policy corrente."));
                continue;
            }
            if (mode == PolicyMode.Approval)
            {
                var approved = await approval.RequestApprovalAsync(manifest, call.Arguments,
                    reason: $"Step {i}: {call.ToolName}", ct);
                if (!approved)
                {
                    await audit.WriteAsync(BuildAuditEvent(task, manifest, call, "user-rejected", "n/a", null), ct);
                    history.Add(new ChatTurn("tool", $"[REJECTED] Operatore ha rifiutato '{call.ToolName}'."));
                    continue;
                }
            }

            // EXECUTE
            var tool = registry.Resolve(call.ToolName)!;
            var result = await tool.ExecuteAsync(call.Arguments, ct);

            // VERIFY (solo per tool non-READ)
            string? verificationOutcome = null;
            if (manifest.Risk != RiskLevel.Read && result.Success)
            {
                var verifyResult = await verification.VerifyAsync(call, result, ct);
                verificationOutcome = verifyResult.Success ? "success" : "failure";
            }

            await audit.WriteAsync(BuildAuditEvent(task, manifest, call,
                mode == PolicyMode.Approval ? "user-approved" : "automatic",
                result.Success ? "success" : "failure", verificationOutcome), ct);

            // OBSERVE: il risultato torna nel contesto del modello
            var observation = FormatObservation(call, result, verificationOutcome);
            history.Add(new ChatTurn("tool", observation));
            await memory.SaveStepAsync(task.Id, new PlanStep(i, call.ToolName, call, result, observation), ct);

            // EVALUATE avviene al giro successivo del loop: è il modello stesso, guardando
            // l'observation, a decidere se replanare (nuova ipotesi) o proseguire il piano.
        }

        logger.LogWarning("Task {TaskId} ha raggiunto il limite di {MaxSteps} step senza concludere", task.Id, MaxSteps);
        return task with { Status = "max-steps-reached" };
    }

    private static string FormatObservation(ToolCallRequest call, ToolCallResult result, string? verification) =>
        $"Tool: {call.ToolName}\nResult: {(result.Success ? "OK" : "ERROR: " + result.ErrorMessage)}\n" +
        $"Output: {result.Output}\n" + (verification is null ? "" : $"Verification: {verification}");

    private static AuditEvent BuildAuditEvent(TaskState task, ToolManifest manifest, ToolCallRequest call,
        string authorization, string outcome, string? verification) =>
        new(DateTimeOffset.UtcNow, task.Id, task.Goal, manifest.Name, call.Arguments, manifest.Risk, authorization, outcome, verification);
}
```

Questo è deliberatamente un loop "dumb ma trasparente": ogni transizione di stato è un metodo che puoi loggare, testare e ispezionare — niente "magia" nascosta in un framework di terze parti. Il *replanning* non è un blocco separato: emerge naturalmente perché ad ogni iterazione il modello rivede l'intera history (incluse le observation più recenti) e può cambiare idea sul prossimo tool da chiamare, esattamente come nell'esempio che hai fatto tu ("ipotesi invalidata → nuovo piano").

Se in futuro vuoi un planner più esplicito (piano dichiarato *prima* dell'esecuzione, non passo-passo), puoi far restituire al modello un piano completo in JSON (`PlanStep[]`) al primo turno, poi eseguire step-by-step e permettere una "PLAN_REVISION" esplicita quando un'osservazione contraddice un'ipotesi — è un'estensione naturale di questo stesso loop, utile da introdurre in **V0.2** (vedi roadmap).

---

## 7. Policy Engine e Safety

Configurazione dichiarativa (`policy.yaml`), non hardcoded:

```yaml
policy:
  Read:
    mode: automatic
  Low:
    mode: automatic
  Medium:
    mode: approval
  High:
    mode: approval
  Critical:
    mode: forbidden

overrides:
  # Puoi affinare per singolo tool, non solo per risk level
  - tool: "fs.delete"
    mode: approval
    conditions:
      pathPrefix: ["/tmp/**", "C:\\Temp\\**"]   # solo qui è "approval"; altrove eredita Critical → forbidden
  - tool: "process.kill"
    mode: forbidden
    conditions:
      pidIn: [1, 4]   # mai il PID 1 (init) o 4 (System su Windows)
```

```csharp
namespace bOps.Policy;

public sealed class YamlPolicyEngine(PolicyOptions options) : IPolicyEngine
{
    public PolicyMode Evaluate(ToolManifest manifest, IReadOnlyDictionary<string, object?> arguments)
    {
        var overrideRule = options.Overrides.FirstOrDefault(o => o.Tool == manifest.Name && Matches(o, arguments));
        if (overrideRule is not null)
            return overrideRule.Mode;

        return options.Levels[manifest.Risk];
    }

    private static bool Matches(PolicyOverride rule, IReadOnlyDictionary<string, object?> args) =>
        // valuta le condizioni dichiarate (pathPrefix, pidIn, ecc.) — implementazione incrementale
        true;
}
```

Il permission model sul filesystem (letto/scritto per pattern glob, come nel tuo esempio) è un caso specifico di questo stesso meccanismo — lo implementi come `overrides` con `conditions.pathPrefix`, non come sistema separato:

```yaml
filesystem:
  read:
    - "/var/log/**"
    - "/etc/**"
  write:
    - "/tmp/**"
```

**Checklist di sicurezza aggiuntiva** (rispetto al piano precedente, qui è ancora più centrale):

1. Nessun tool generico "esegui comando" — solo funzioni tipizzate con parametri validati (già garantito dal `ToolManifest.Parameters` + validazione argomenti prima dell'`ExecuteAsync`).
2. Prompt injection via log/output di comandi: qualunque testo che arriva da fonti esterne (log, output di `docker logs`, contenuto di file) è **dati**, mai istruzioni — il system prompt lo dichiara esplicitamente e il Planner non deve mai interpretare testo proveniente da una tool `Result` come una nuova richiesta dell'utente.
3. Minimo privilegio del processo host: separare, quando possibile, il processo "read-only" (system.*, network.*, fs.read) da quello con permessi per azioni `MEDIUM+` (service restart, docker restart), magari come due Windows Service / systemd unit distinte con account diversi.
4. `CRITICAL` è **sempre** `forbidden` di default (mai eseguibile dall'agente, nemmeno con approvazione) — riservalo a operazioni come `fs.delete` su path di sistema, format, o simili; se davvero serve un giorno, va fuori da questo runtime (azione manuale dell'operatore).
5. Timeout su ogni `ExecuteAsync` (usa `CancellationTokenSource` con timeout per tool, es. 30s per operazioni di sistema, più lungo per query Docker/log).

---

## 8. Verification

Ogni tool `non-READ` dichiara (in fase di registrazione) quale tool `READ` la verifica:

```csharp
namespace bOps.Runtime;

public sealed class VerificationService(IToolRegistry registry) : IVerificationService
{
    // mappa dichiarativa: tool eseguito → tool di verifica + come interpretare il risultato
    private static readonly Dictionary<string, (string VerifyTool, Func<string, bool> IsSuccess)> Map = new()
    {
        ["service.restart"] = ("service.status", output => output.Contains("ACTIVE") || output.Contains("Running")),
        ["docker.restart"]  = ("docker.inspect", output => output.Contains("\"Status\":\"running\"")),
        ["process.kill"]    = ("process.list",   output => true /* verifica assenza del PID, logica dedicata */),
    };

    public async Task<ToolCallResult> VerifyAsync(ToolCallRequest original, ToolCallResult executionResult, CancellationToken ct = default)
    {
        if (!Map.TryGetValue(original.ToolName, out var rule))
            return new ToolCallResult(true, "Nessuna verifica dichiarata per questo tool", null, TimeSpan.Zero);

        var verifyTool = registry.Resolve(rule.VerifyTool)
            ?? throw new InvalidOperationException($"Tool di verifica mancante: {rule.VerifyTool}");

        // Riusa gli argomenti rilevanti (es. il nome del servizio) per la query di verifica
        var verifyArgs = ExtractVerificationArguments(original);
        var verifyResult = await verifyTool.ExecuteAsync(verifyArgs, ct);

        var success = verifyResult.Success && rule.IsSuccess(verifyResult.Output ?? string.Empty);
        return verifyResult with { Success = success };
    }

    private static IReadOnlyDictionary<string, object?> ExtractVerificationArguments(ToolCallRequest original) =>
        original.Arguments; // spesso lo stesso identificatore (service name, container name) basta
}
```

Esempio di traccia risultante (esattamente come nel tuo scenario nginx):

```
ACTION:        service.restart(nginx)
EXECUTE:       exit code 0
VERIFY:        service.status(nginx)
VERIFY RESULT: ACTIVE
CONCLUSION:    restart completato con successo (verificato)
```

Se la verifica fallisce, il Planner **non deve dichiarare successo all'utente** — deve rientrare nel loop con un'observation di tipo "verification failed", che tipicamente porta a un replan (es. "il servizio non è ripartito, controllo i log per capire perché").

---

## 9. Audit log

Ogni `AuditEvent` (interfaccia già in §3) viene scritto in append-only, indipendentemente da successo/fallimento:

```json
{
  "timestamp": "2026-09-14T10:32:11Z",
  "taskId": "5b6e...",
  "goal": "Il server è lento, trova il problema",
  "tool": "service.restart",
  "arguments": { "service": "nginx" },
  "risk": "Medium",
  "authorization": "user-approved",
  "result": "success",
  "verification": "success"
}
```

Implementazione iniziale — file JSON-lines, poi migrabile a tabella SQLite senza cambiare l'interfaccia `IAuditSink`:

```csharp
namespace bOps.Audit;

public sealed class JsonLinesAuditSink(string filePath) : IAuditSink
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WriteAsync(AuditEvent evt, CancellationToken ct = default)
    {
        var line = JsonSerializer.Serialize(evt, AuditJsonContext.Default.AuditEvent);
        await _lock.WaitAsync(ct);
        try { await File.AppendAllTextAsync(filePath, line + Environment.NewLine, ct); }
        finally { _lock.Release(); }
    }
}
```

> Usa `System.Text.Json` con **source generation** (`JsonSerializerContext`) per performance e per compatibilità con eventuale Native AOT del binario singolo (vedi §13).

Da questo log derivano gratis: audit/compliance, debug ("cosa ha fatto l'agente ieri alle 14?"), **replay** (rigiocare una sequenza di tool call registrata come test di regressione — utile insieme a `examples/` in §2), e in futuro una dashboard "Agent activity" nella Web UI.

---

## 10. Memory e storage

Segui esattamente la progressione che avevi in mente — parti semplice:

| Tipo di memoria | V0.1–V0.6 | Evoluzione futura |
|---|---|---|
| Short-term (contesto conversazione corrente) | in-memory (`List<ChatTurn>` nel loop) | invariato, è per natura volatile |
| Task state (piano, step, risultati) | **SQLite** (`Microsoft.Data.Sqlite` o EF Core + provider Sqlite) | PostgreSQL se serve concorrenza multi-host |
| Audit | **SQLite** / file JSON-lines | PostgreSQL, o export verso un SIEM |
| Semantic memory (ricerca su task passati per similarità) | **non implementata** | vector store opzionale (Qdrant/pgvector) — solo se emerge un bisogno reale, es. "hai già visto questo pattern di errore?" |

Schema minimo SQLite per `TaskState`/`PlanStep` (via EF Core, `bOps.Memory`):

```csharp
public sealed class bOpsDbContext(DbContextOptions<bOpsDbContext> options) : DbContext(options)
{
    public DbSet<TaskRecord> Tasks => Set<TaskRecord>();
    public DbSet<StepRecord> Steps => Set<StepRecord>();
}

public sealed class TaskRecord
{
    public Guid Id { get; set; }
    public string Goal { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class StepRecord
{
    public int Id { get; set; }
    public Guid TaskId { get; set; }
    public int Index { get; set; }
    public string Description { get; set; } = "";
    public string? ToolCallJson { get; set; }
    public string? ResultJson { get; set; }
    public string? Observation { get; set; }
}
```

Questo è anche ciò che abilita **V0.7 (task persistenti)**: un task interrotto (crash, riavvio del servizio, timeout) può essere ricaricato da SQLite e ripreso dal loop `AgentPlanner` esattamente dallo step successivo.

---

## 11. Tool — catalogo iniziale per categoria

Riprendo la tua suddivisione, con nota su dove vive l'implementazione — ogni categoria è un pacchetto a sé (§5), astratto in `bOps.Abstractions` e implementato per OS in `bOps.Packages.Service.Windows`/`bOps.Packages.Service.Linux` dove serve differenziare.

### System (`bOps.Packages.System`, astratto — implementazione per OS)

```
system.info          system.uptime        system.cpu
system.memory        system.disk          system.processes
system.services      system.environment
```

Interfaccia astratta (analoga a quella che avevi già proposto tu):

```csharp
namespace bOps.Abstractions;

public interface ISystemProvider
{
    Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default);
    Task<double> GetCpuUsagePercentAsync(CancellationToken ct = default);
    Task<MemoryInfo> GetMemoryInfoAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DiskInfo>> GetDisksAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(string sortBy, int limit, CancellationToken ct = default);
}
```

**Windows** (`bOps.Packages.Service.Windows`): come nel piano precedente — `PerformanceCounter`/WMI per CPU e RAM, `DriveInfo` per i dischi, `Process.GetProcesses()` per i processi (arricchito con `WorkingSet64`, `TotalProcessorTime`).

**Linux** (`bOps.Packages.Service.Linux`): niente `PerformanceCounter` (Windows-only) — si legge direttamente `/proc`:

```csharp
namespace bOps.Packages.Service.Linux;

public sealed class LinuxSystemProvider : ISystemProvider
{
    public async Task<double> GetCpuUsagePercentAsync(CancellationToken ct = default)
    {
        // /proc/stat, riga "cpu ": user nice system idle iowait irq softirq
        var first = await ReadCpuTimesAsync(ct);
        await Task.Delay(500, ct);
        var second = await ReadCpuTimesAsync(ct);

        var idleDelta = second.Idle - first.Idle;
        var totalDelta = second.Total - first.Total;
        return totalDelta == 0 ? 0 : 100.0 * (1 - (double)idleDelta / totalDelta);
    }

    public async Task<MemoryInfo> GetMemoryInfoAsync(CancellationToken ct = default)
    {
        // /proc/meminfo: MemTotal, MemAvailable in kB
        var lines = await File.ReadAllLinesAsync("/proc/meminfo", ct);
        long totalKb = 0, availKb = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("MemTotal:")) totalKb = ParseKb(line);
            else if (line.StartsWith("MemAvailable:")) availKb = ParseKb(line);
        }
        return new MemoryInfo(totalKb / 1024, availKb / 1024);
    }

    private static long ParseKb(string line) =>
        long.Parse(line.Split(':', StringSplitOptions.TrimEntries)[1].Replace(" kB", ""));

    // ... ReadCpuTimesAsync legge "/proc/stat" e somma i campi rilevanti
}
```

Per lo **swap** e l'**I/O disco** (esplicitamente richiesti nel tuo scenario "server lento"): `/proc/meminfo` (`SwapTotal`/`SwapFree`) e `/proc/diskstats` (settori letti/scritti per device, da cui derivi IOPS/throughput con due campionamenti come per la CPU). Da mettere in `system.swap` e `system.io` come tool dedicati fin da subito, dato che sono centrali nel tuo esempio di diagnosi.

Riferimenti: [`/proc` filesystem — man7.org proc(5)](https://man7.org/linux/man-pages/man5/proc.5.html), [D-Bus per .NET Core — Red Hat Developer](https://developers.redhat.com/blog/2017/09/18/connecting-net-core-d-bus) (per capire le alternative a shellare comandi, vedi §Services sotto).

### Filesystem (`bOps.Packages.Filesystem`, cross-platform via `System.IO`)

```
fs.list    fs.stat    fs.search    fs.read    fs.hash    fs.delete    fs.move
```

`System.IO` è già cross-platform; la parte da costruire davvero è il **permission model** (§7) — ogni tool fs.* valida il path richiesto contro i pattern glob di `read`/`write` **prima** di toccare il filesystem:

```csharp
public sealed class FsDelete(IPathPolicy pathPolicy) : ITool
{
    public ToolManifest Manifest { get; } = new(
        "fs.delete", "Elimina un file o una directory", RiskLevel.High,
        ["windows", "linux"], [], [new ToolParameter("path", "string", "Path da eliminare")]);

    public Task<ToolCallResult> ExecuteAsync(IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        var path = (string)args["path"]!;
        if (!pathPolicy.IsAllowed(path, PathAccess.Write))
            return Task.FromResult(new ToolCallResult(false, null, $"Path non autorizzato dalla policy: {path}", TimeSpan.Zero));

        File.Delete(path); // o Directory.Delete con ricorsione controllata
        return Task.FromResult(new ToolCallResult(true, $"Eliminato: {path}", null, TimeSpan.Zero));
    }
}
```

`fs.hash` è utile in un modo che spesso si dimentica: confrontare l'hash di un file di configurazione prima/dopo un'azione, per l'audit ("ho verificato che nginx.conf non sia cambiato durante il restart").

### Process (`bOps.Packages.System`, cross-platform via `System.Diagnostics.Process` + arricchimento OS-specific)

```
process.list    process.inspect    process.start    process.stop    process.kill
```

Livelli di rischio come da tuo modello: `list`/`inspect` = `READ`, `stop` (segnale "gentile") = `MEDIUM`, `kill` (forzato, `SIGKILL`/`TerminateProcess`) = `HIGH`, mai `CRITICAL` a meno di whitelist esplicite su PID di sistema come *forbidden* (vedi override in §7).

### Network (`bOps.Packages.Network`)

```
network.interfaces    network.connections    network.dns    network.ping    network.port_check    network.route
```

Cross-platform quasi "gratis" con `System.Net.NetworkInformation` (`NetworkInterface.GetAllNetworkInterfaces()`, `Ping`, `IPGlobalProperties.GetActiveTcpConnections()`); `network.route` è l'unico che tipicamente richiede uno shell-out (`route print` / `ip route`) parsificato — isolalo dietro l'astrazione così il parsing resta confinato al pacchetto Windows/Linux del provider (§5).

### Services (`bOps.Packages.Service.Windows` / `bOps.Packages.Service.Linux`) — l'astrazione più importante da progettare bene

```csharp
namespace bOps.Abstractions;

public interface IServiceProvider2   // nome di comodo per non collidere con IServiceProvider di .NET
{
    Task<IReadOnlyList<ServiceInfo>> ListAsync(CancellationToken ct = default);
    Task<ServiceInfo> GetStatusAsync(string name, CancellationToken ct = default);
    Task StartAsync(string name, CancellationToken ct = default);
    Task StopAsync(string name, CancellationToken ct = default);
    Task RestartAsync(string name, CancellationToken ct = default);
}
```

- **Windows**: `System.ServiceProcess.ServiceController` (già scritto nel piano precedente).
- **Linux**: due opzioni.
  1. **Pragmatica (consigliata per V0.5)**: shell-out a `systemctl` (`Process.Start("systemctl", "restart nginx")`, parsing di `systemctl show <unit> --property=ActiveState`). Semplice, robusto, ciò che fanno anche molti tool "seri" (es. Ansible di default fa lo stesso).
  2. **"Native" (evoluzione)**: D-Bus via [Tmds.DBus](https://github.com/tmds/Tmds.DBus), parlando direttamente con `org.freedesktop.systemd1` — nessun processo figlio, più veloce, ma più codice da mantenere e da un'API meno documentata per i casi limite. Riferimento: [Connecting .NET Core to D-Bus — Red Hat Developer](https://developers.redhat.com/blog/2017/09/18/connecting-net-core-d-bus).

  Parti con l'opzione 1, isola il parsing dentro `LinuxServiceProvider`, e valuta la migrazione a D-Bus solo se emergono problemi reali di performance/robustezza.

Il tool esposto all'agente resta unico e astratto (`service.list`, `service.status`, `service.start`, `service.stop`, `service.restart`) — la scelta Windows/Linux avviene per DI in base al SO rilevato all'avvio, non con `if` sparsi nel codice del tool.

### Docker (`bOps.Packages.Docker`)

```
docker.containers    docker.inspect    docker.logs    docker.images
docker.networks      docker.restart    docker.stop    docker.start
```

Usa [`Docker.DotNet`](https://github.com/dotnet/Docker.DotNet) ([NuGet](https://www.nuget.org/packages/Docker.DotNet)), il client ufficiale .NET per l'API Docker (parla con il daemon via named pipe su Windows o socket Unix su Linux — quindi anche qui l'astrazione a monte resta identica sui due SO):

```csharp
namespace bOps.Packages.Docker;

public sealed class DockerLogsTool(IDockerClientFactory clientFactory) : ITool
{
    public ToolManifest Manifest { get; } = new(
        "docker.logs", "Reads recent logs from a Docker container", RiskLevel.Read,
        ["linux", "windows"], ["docker"],
        [
            new ToolParameter("container", "string", "Nome o ID del container"),
            new ToolParameter("tail", "integer", "Numero di righe", Required: false)
        ]);

    public async Task<ToolCallResult> ExecuteAsync(IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        using var client = clientFactory.Create();
        var container = (string)args["container"]!;
        var tail = args.TryGetValue("tail", out var t) ? Convert.ToInt32(t) : 200;

        await using var stream = await client.Containers.GetContainerLogsAsync(container,
            new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Tail = tail.ToString() }, ct);

        using var reader = new StreamReader(stream);
        var output = await reader.ReadToEndAsync(ct);
        return new ToolCallResult(true, output, null, TimeSpan.Zero);
    }
}
```

`docker.restart` è `MEDIUM` (approvazione), verificato da `docker.inspect` che controlla `State.Status == "running"` (esattamente come nella tabella di verifica in §8).

> Nota su `requires: ["docker"]`: il `ICapabilityProbe` per `docker` prova una `client.System.PingAsync()` all'avvio (con timeout breve) — se fallisce, tutti i `docker.*` spariscono dai tool disponibili e il Planner non li vede nemmeno, invece di fallire a runtime.

---

## 12. Architecture Decision Records (ADR)

Data la posta in gioco (è un progetto "vero", non un esercizio), tieni un ADR per ogni decisione strutturale in `docs/architecture/adr/NNNN-titolo.md` — usa la skill `engineering:architecture` per generarli in modo consistente quando arrivi a un bivio importante. Le prime decisioni che meritano un ADR fin da subito:

1. **ADR-0001**: perché un `IChatModel` custom invece di dipendere direttamente da `Microsoft.Agents.AI`/`Microsoft.Extensions.AI` in tutto il runtime.
2. **ADR-0002**: modello di rischio a 5 livelli e semantica di `Forbidden` (mai bypassabile, nemmeno con approvazione esplicita).
3. **ADR-0003**: `systemctl` shell-out vs D-Bus per la gestione servizi Linux in V0.5.
4. **ADR-0004**: formato dell'audit log (JSON-lines file vs SQLite fin dal V0.1).
5. **ADR-0005**: un adapter `OpenAiCompatibleChatModel` generico condiviso da OpenRouter/Ollama/llama.cpp/OpenAI/DeepSeek, con Anthropic come unico adapter nativo separato (§3.1) — invece di un adapter per provider fin da subito.
6. **ADR-0006**: tool, categorie di tool (System/Filesystem/Network/Docker/Service) e provider LLM sono tutti "pacchetti" dietro lo stesso contratto (`IToolProvider`/`IModelProviderPackage`), first-party e di terze parti trattati identicamente — invece di un core che conosce direttamente le categorie native (§0 principio 8, §5).
7. **ADR-0007**: pacchetti first-party compilati staticamente (project reference) in Fase 1, caricamento dinamico via `McMaster.NETCore.Plugins`/`AssemblyLoadContext` isolato introdotto solo in V0.10 per i pacchetti di terze parti — invece di costruire subito l'infrastruttura di plugin dinamico prima di avere qualcosa da caricare (§5.2).
8. **ADR-0008**: ceiling di rischio per-pacchetto nella Policy Engine, indipendente dal `maxDeclaredRisk` che il pacchetto stesso dichiara nel manifest — un pacchetto di terze parti (specie closed-source) non decide da solo quanto può essere pericoloso (§5.3).

---

## 13. Distribuzione

Coerente con quanto avevi già in mente:

```
Windows  → bOps.Worker come Windows Service (sc.exe create / New-Service, o installer .msi in fasi avanzate)
Linux    → bops-agent.service (systemd unit)
Docker   → immagine "bops-agent" (utile soprattutto per testare i tool Docker/Network in isolamento)
CLI      → binario singolo via `dotnet publish -r <rid> --self-contained -p:PublishAot=true` (Native AOT, se tutte le dipendenze lo supportano — verifica compatibilità di EF Core/Docker.DotNet prima di impegnarti su AOT)
```

Per il Generic Host cross-platform (`bOps.Worker`), il pattern `dotnet new worker` + `UseWindowsService()` + `UseSystemd()` è ormai standard e gestisce da solo il rilevamento del contesto di hosting:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "bOps Agent");
builder.Services.AddSystemd();
builder.Services.AddHostedService<AgentWorker>();
// ... registra Runtime, Policy, Memory, Audit, Tools come nei paragrafi precedenti
var host = builder.Build();
await host.RunAsync();
```

Riferimento: [dotnet new worker - Windows Services or Linux systemd services in .NET — Scott Hanselman](https://www.hanselman.com/blog/dotnet-new-worker-windows-services-or-linux-systemd-services-in-net-core) e [Running a .NET application as a service on Linux with Systemd — Maarten Balliauw](https://blog.maartenballiauw.be/posts/2021-05-25-running-a-net-application-as-a-service-on-linux-with-systemd/).

---

## 14. Roadmap incrementale

Ricalca esattamente la tua, con qualche nota implementativa per fase. Ho raggruppato le versioni in due fasi, come richiesto: **Fase 1 = solo CLI**, provider OpenRouter/Ollama/llama.cpp; **Fase 2 = Web UI Angular 21 + estensione provider** (Anthropic/OpenAI/DeepSeek), che arrivano insieme.

| Fase | Versione | Obiettivo | Contenuto | Note |
|---|---|---|---|---|
| **1 — CLI** | **V0.1** | Agent Runtime minimo | `IChatModel` via `OpenAiCompatibleChatModel` (§3.1) configurabile su OpenRouter, Ollama o llama.cpp da `appsettings` (come pacchetti-provider referenziati direttamente, §5), `IToolRegistry`, esecuzione diretta (no policy, no verify), primo pacchetto tool `bOps.Packages.System` con 5 tool: `system.info`, `system.cpu`, `system.memory`, `process.list`, `fs.list` | Tutto `READ`, nessuna azione con effetti collaterali ancora — obiettivo: "vedo dati reali della macchina in chat", indipendentemente dal provider scelto. Pacchetti e provider esistono già concettualmente come tali (principio 8, §0): sono solo compilati staticamente finché non arriva il loader dinamico (V0.10) |
| **1 — CLI** | **V0.2** | Agent loop esplicito | `AgentPlanner` completo (§6): PLAN → EXECUTE → OBSERVE → EVALUATE → REPLAN, `IAgentMemory` in-memory (non ancora SQLite) | Testa il replanning sia con OpenRouter (tool-calling nativo) sia con llama.cpp in fallback JSON (§3.1.1) — è il primo punto in cui le differenze tra provider si vedono davvero |
| **1 — CLI** | **V0.3** | Policy Engine | `IPolicyEngine` + `policy.yaml`, `IApprovalProvider` (CLI: prompt `[Approve]/[Reject]`) | Introduci `service.restart` come primo tool `MEDIUM` per testare il flusso di approvazione end-to-end |
| **1 — CLI** | **V0.4** | Verification | `IVerificationService` (§8) + mappa tool→verifica | Il caso nginx del tuo esempio come test di riferimento |
| **1 — CLI** | **V0.5** | Windows + Linux, pacchetto Filesystem/Network | `bOps.Packages.Service.Windows` completo (già scritto nel piano precedente) + `bOps.Packages.Service.Linux` (§11: `/proc`, `systemctl`), `bOps.Packages.Filesystem`, `bOps.Packages.Network`, CI su entrambi i SO (GitHub Actions matrix `windows-latest`/`ubuntu-latest`) | Aggiungi `system.swap`, `system.io` a `bOps.Packages.System` qui |
| **1 — CLI** | **V0.6** | Pacchetto Docker | `bOps.Packages.Docker` via `Docker.DotNet`, `ICapabilityProbe` per il discovery condizionale | Qui il progetto diventa "molto utile davvero", come dicevi |
| **1 — CLI** | **V0.7** | Task persistenti | `IAgentMemory` su SQLite (EF Core), possibilità di interrompere/riprendere un task (`bops task resume <id>`) | Chiude la Fase 1: a questo punto la CLI è un prodotto completo e collaudato da solo |
| **2 — UI + provider** | **V0.8** | Estensione provider | Pacchetto-provider nativo `bOps.Packages.Providers.Anthropic` (§3.1.2) + pacchetti `OpenAI`/`DeepSeek` (già coperti da `OpenAiCompatibleChatModel`, solo configurazione/test) | Da fare per prima nella Fase 2: la UI ha bisogno di un selettore provider completo fin dal primo giorno in cui esiste |
| **2 — UI + provider** | **V0.9** | Web UI Angular 21 | `bOps.Api` (ASP.NET Core Minimal API, `POST /api/agents/tasks` + SSE/WebSocket per lo streaming "Agent activity") + `web/bops-ui` in Angular 21 con NgRx SignalStore (dettaglio in §17) | Il mockup che hai disegnato (attività + reasoning + approve/reject) è la vista che costruisci sopra audit log + approval provider + selettore provider di V0.8 |
| **2 — UI + provider** | **V0.10** | Plugin/Package SDK — caricamento dinamico | `PluginLoader` via `McMaster.NETCore.Plugins` (§5.2), `bops plugin install/list/enable/remove`, template `dotnet new bops-plugin`, `bOps.Abstractions` pubblicato come NuGet indipendente e versionato in semver — a questo punto anche i pacchetti first-party POSSONO migrare a caricamento dinamico (dogfooding) senza cambiare una riga del loro codice interno | Qui verifichi davvero che l'astrazione pacchetto sia "giusta": prova a scrivere/caricare `bops-plugin-sqlserver` o `bops-plugin-postgres` (§5.5/§5.6) come primo pacchetto esterno reale, o inviti terzi a farlo |
| **2 — UI + provider** | **V1.0** | Hardening e pubblicazione | Threat model scritto (`docs/security/`), trust levels/firma dei pacchetti (§5.3) applicati sul serio, fuzzing sugli argomenti dei tool, rate limiting, secrets management (Key Vault/Vault), README e docs pubblicabili | Pronto per essere mostrato/usato da altri — anche come piattaforma per pacchetti di terze parti, non solo come prodotto finito |

**Non partire da**: multi-agent (Supervisor + agenti specializzati) e vector store per memoria semantica. Sono naturali estensioni *dopo* V1.0, quando il single-agent runtime è collaudato — a quel punto il Supervisor è "solo" un `IAgentPlanner` che invece di chiamare tool chiama altri `IAgentPlanner` (System Agent, Docker Agent, ecc.), riusando esattamente Policy/Memory/Audit già costruiti. È un buon segno architetturale se in quel momento non devi toccare quasi nulla del core.

---

## 15. Come lo usi tu, fin da subito

```
bops "il server è lento, trova il problema"
bops "controlla tutti i container Docker e dimmi se qualcosa non va"
bops "perché questa macchina usa così tanta RAM?"
bops "controlla se qualche servizio è fallito nelle ultime 24 ore"
bops "trova file più grandi di 5GB non acceduti da 90 giorni"
bops "pulisci quello che hai trovato"     # ← passa da fs.search (READ) a fs.delete (HIGH), con approvazione
```

Il tuo esempio dei 17 GB di log oltre retention è un ottimo primo scenario end-to-end da registrare in `examples/` non appena hai V0.4 (verification) pronto — copre discovery (`fs.search`/`fs.stat`), stima di impatto (dimensione totale, quanti file), policy (`fs.delete` = `High` → approval), e un messaggio di proposta strutturato come nel tuo mockup (impatto/rischio/rollback) che puoi generare come parte della `ToolCallRequest` prima ancora di eseguire, non solo come testo libero del modello.

---

## 16. Articoli e risorse

**Microsoft Agent Framework (per l'adapter `OpenRouterChatModel` e ispirazione sul tool-calling)**
1. [Microsoft Agent Framework — Overview](https://learn.microsoft.com/en-us/agent-framework/overview/)
2. [Using function tools with an agent](https://learn.microsoft.com/en-us/agent-framework/agents/tools/function-tools)
3. [GitHub - microsoft/agent-framework](https://github.com/microsoft/agent-framework) — samples `02-agents`, `03-workflows`
4. [Workflow orchestrations in Agent Framework](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/) — utile quando arrivi al Supervisor multi-agent (post V1.0)

**Docker**
5. [Docker.DotNet — GitHub](https://github.com/dotnet/Docker.DotNet) e [pacchetto NuGet](https://www.nuget.org/packages/Docker.DotNet)
6. [How to Use Docker with .NET Applications](https://oneuptime.com/blog/post/2026-01-27-docker-dotnet/view)

**Linux internals**
7. [`proc(5)` — man7.org](https://man7.org/linux/man-pages/man5/proc.5.html) — riferimento definitivo per `/proc/stat`, `/proc/meminfo`, `/proc/diskstats`
8. [Connecting .NET Core to D-Bus — Red Hat Developer](https://developers.redhat.com/blog/2017/09/18/connecting-net-core-d-bus)
9. [Tmds.DBus — GitHub](https://github.com/tmds/Tmds.DBus) (se in futuro sostituisci lo shell-out a `systemctl`)

**Hosting cross-platform**
10. [dotnet new worker - Windows Services or Linux systemd services — Scott Hanselman](https://www.hanselman.com/blog/dotnet-new-worker-windows-services-or-linux-systemd-services-in-net-core)
11. [Running a .NET application as a service on Linux with Systemd — Maarten Balliauw](https://blog.maartenballiauw.be/posts/2021-05-25-running-a-net-application-as-a-service-on-linux-with-systemd/)

**Windows (dal piano precedente, restano validi)**
12. [`EventLogReader` Class — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.eventing.reader.eventlogreader)
13. [Reading and Querying EventViewer Efficiently With C#](https://www.c-sharpcorner.com/UploadFile/d551d3/reading-and-querying-eventviewer-efficiently-with-C-Sharp/)

**OpenRouter**
14. [How to Integrate OpenAI Models via OpenRouter in C# Using the Microsoft Agent Framework — Void Geeks](https://www.voidgeeks.com/tutorial/How-to-Integrate-OpenAI-Models-via-OpenRouter-in-C-Using-the-Microsoft-Agent-Framework/26)

**Ollama e llama.cpp (provider locali, Fase 1)**
15. [Tool support — Ollama Blog](https://ollama.com/blog/tool-support) — annuncio e semantica del tool-calling nativo in Ollama
16. [Ollama Tool Calling: The Practical Function Calling Guide](https://localaimaster.com/blog/ollama-function-calling-tools)
17. [llama.cpp — function-calling.md](https://github.com/ggml-org/llama.cpp/blob/master/docs/function-calling.md)
18. [llama.cpp/tools/server/README.md](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md) — avvio di `llama-server` e opzioni dell'endpoint OpenAI-compatible

**Anthropic (Fase 2)**
19. [OpenAI SDK compatibility — Claude Platform Docs](https://platform.claude.com/docs/en/cli-sdks-libraries/libraries/openai-sdk)

**Angular 21 + state management (Fase 2, vedi §17)**
20. [NgRx: From the Classic Store to the Signal Store — Fabio Cabiddu, Medium](https://medium.com/@fabio.cabi/ngrx-from-the-classic-store-to-the-signal-store-what-changes-for-angular-developers-816c8d05f18d)
21. [State Management in Angular: NgRx vs. SignalStore — John Kavanagh](https://johnkavanagh.co.uk/articles/state-management-in-angular-ngrx-vs-signalstore/)
22. [Angular State Management in 2026 — NgRx, Signals, NGXS, Akita Compared — DEV Community](https://dev.to/kirandeepjassalcrypto/angular-state-management-in-2026-ngrx-signals-ngxs-akita-compared-with-bundle-loc-numbers-4amj)

**Plugin & Package Ecosystem (§5)**
23. [McMaster.NETCore.Plugins — GitHub](https://github.com/natemcmaster/DotNetCorePlugins) — la libreria per caricamento dinamico di plugin .NET con `AssemblyLoadContext` isolato, usata dal `PluginLoader` in §5.2
24. [AssemblyLoadContext — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/understanding-assemblyloadcontext) — riferimento di base se in futuro serve andare oltre ciò che `McMaster.NETCore.Plugins` copre già
25. [NuGet package signing — Microsoft Learn](https://learn.microsoft.com/en-us/nuget/create-packages/sign-a-package) — utile quando arrivi a richiedere pacchetti firmati per l'installazione in produzione (§5.3)

---

## 17. UI Angular 21 (Fase 2)

Arriva dopo la CLI e insieme all'estensione provider (V0.9 in roadmap), non prima. Quando ci arrivi, questa è l'impostazione consigliata.

### 17.1 Perché NgRx SignalStore e non NgRx "classico"

Hai chiesto "un gestore di stati tipo Redux": in ecosistema Angular questo storicamente vuol dire **NgRx** (store/reducer/effects/selectors, pattern Redux puro). Con Angular 21, però, tutto il framework (change detection, `input()`/`output()`, template control flow) è costruito attorno ai **Signals**, e NgRx lo ha seguito con **`@ngrx/signals`** (SignalStore): stesso principio Redux — stato centralizzato, immutabile, letto tramite selettori, modificato solo tramite operazioni dichiarate — ma senza boilerplate di action/reducer/dispatch separati, e con reattività a grana fine (si ridisegna solo cosa dipende dal pezzo di stato che cambia).

**Consiglio**: usa **NgRx SignalStore** come default. Tieni in mente **NgRx classico** (`@ngrx/store` + `@ngrx/effects`) come alternativa se, guardando l'audit log che stai già costruendo lato backend (§9), vuoi una simmetria esplicita "ogni interazione utente in UI = un'action loggata" con un DevTools a corredo (`@ngrx/store-devtools` mostra la timeline delle action, utile in debug) — è un pattern più verboso ma più "audit-friendly" per un prodotto che parla già il linguaggio di eventi tracciati. Per un dashboard operativo come questo, SignalStore resta la scelta più moderna e con meno codice a parità di funzionalità; puoi sempre migrare più avanti, l'API dei selettori è concettualmente simile.

### 17.2 Struttura del progetto Angular

```
web/bops-ui/
├── src/app/
│   ├── core/
│   │   ├── api/                    # client HTTP verso bOps.Api (generato da OpenAPI, vedi 16.4)
│   │   └── streaming/               # client SSE/WebSocket per l'"Agent activity" in tempo reale
│   │
│   ├── state/                       # SignalStore, uno per bounded context
│   │   ├── tasks.store.ts           # elenco task, stato corrente, storico
│   │   ├── agent-activity.store.ts  # stream di step/observation del task attivo (§9, alimentato da SSE)
│   │   ├── approvals.store.ts       # coda di richieste di approvazione pendenti
│   │   └── providers.store.ts       # provider LLM configurati/disponibili (V0.8: OpenRouter/Ollama/llama.cpp/Anthropic/OpenAI/DeepSeek)
│   │
│   ├── features/
│   │   ├── dashboard/                # vista principale: stato server + attività agente (il mockup che hai disegnato)
│   │   ├── task-detail/              # timeline di un singolo task con step/reasoning/tool call
│   │   ├── approvals/                # coda approvazioni con [Approve]/[Reject]
│   │   └── settings/                 # selezione/configurazione provider LLM
│   │
│   └── shared/                       # componenti UI riutilizzabili (badge di rischio, timeline step, ecc.)
│
└── angular.json
```

### 17.3 Un esempio di SignalStore — coda di approvazione

Questo è il pezzo di stato più direttamente collegato al backend (§7, `IApprovalProvider`): il mockup "Proposta di azione — [Approve]/[Reject]" che hai disegnato è essenzialmente questo store più un componente sopra.

```typescript
import { signalStore, withState, withMethods, withComputed, patchState } from '@ngrx/signals';
import { computed, inject } from '@angular/core';
import { BOpsApiClient } from '../core/api/bops-api-client';

export interface ApprovalRequest {
  id: string;
  taskId: string;
  tool: string;
  arguments: Record<string, unknown>;
  risk: 'Low' | 'Medium' | 'High' | 'Critical';
  reason: string;
  requestedAtUtc: string;
}

interface ApprovalsState {
  pending: ApprovalRequest[];
  loading: boolean;
}

export const ApprovalsStore = signalStore(
  { providedIn: 'root' },
  withState<ApprovalsState>({ pending: [], loading: false }),
  withComputed(({ pending }) => ({
    highRiskCount: computed(() => pending().filter(a => a.risk === 'High' || a.risk === 'Critical').length),
  })),
  withMethods((store, api = inject(BOpsApiClient)) => ({
    // Chiamato quando arriva un evento SSE "approval-requested" dal backend
    enqueue(request: ApprovalRequest) {
      patchState(store, state => ({ pending: [...state.pending, request] }));
    },
    async approve(id: string) {
      await api.respondToApproval(id, true);
      patchState(store, state => ({ pending: state.pending.filter(a => a.id !== id) }));
    },
    async reject(id: string) {
      await api.respondToApproval(id, false);
      patchState(store, state => ({ pending: state.pending.filter(a => a.id !== id) }));
    },
  })),
);
```

Uso nel componente (Angular 21, standalone, signals-first — niente `async` pipe da gestire manualmente):

```typescript
@Component({
  selector: 'bops-approvals-panel',
  template: `
    @for (req of store.pending(); track req.id) {
      <bops-approval-card [request]="req"
        (approve)="store.approve(req.id)"
        (reject)="store.reject(req.id)" />
    } @empty {
      <p>Nessuna approvazione in sospeso.</p>
    }
  `,
})
export class ApprovalsPanelComponent {
  protected readonly store = inject(ApprovalsStore);
}
```

### 17.4 Comunicazione con il backend

- **REST** (`bOps.Api`, ASP.NET Core Minimal API) per comandi puntuali: `POST /api/agents/tasks` (avvia un goal), `POST /api/approvals/{id}/respond`, `GET /api/providers` (elenco provider configurati, per il pannello Settings).
- **Streaming** per l'"Agent activity" in tempo reale del mockup: Server-Sent Events è la scelta più semplice per un flusso unidirezionale server→client di eventi (uno per step del Planner, uno per ogni `AuditEvent`) — più leggero di un WebSocket quando non serve comunicazione bidirezionale sullo stesso canale (le approvazioni restano una REST call separata).
- Genera il client TypeScript da uno schema OpenAPI esposto da `bOps.Api` (Minimal API con `Microsoft.AspNetCore.OpenApi` + un generatore come `openapi-typescript`), invece di scriverlo a mano: mantiene UI e backend sincronizzati quando aggiungi tool/endpoint.

### 17.5 Cosa NON fare in Fase 2

- Non introdurre NgRx classico *e* SignalStore insieme "per sicurezza" — scegli SignalStore (§17.1) e resta coerente in tutta l'app.
- Non duplicare in `state/` ciò che è già disponibile via query REST cache-abili (usa `httpResource()`/`resource()` di Angular per dati che non cambiano in tempo reale, es. l'elenco tool disponibili — riservare i SignalStore allo stato che l'utente modifica o che arriva via stream).
- Non bloccare l'avvio della Fase 2 sull'estensione di *tutti* i provider: implementa Anthropic per primo (è il gap più probabile da colmare per un uso "serio" da UI), poi OpenAI/DeepSeek che nella Fase 1 hai già coperto architetturalmente con `OpenAiCompatibleChatModel` — è solo configurazione e test.

---

## 18. Prossimi passi concreti

1. Nome, namespace e stack ora sono confermati: **bOps** (namespace `bOps.*`), CLI-first in Fase 1 con provider OpenRouter/Ollama/llama.cpp, Web UI Angular 21 + NgRx SignalStore in Fase 2 insieme all'estensione ad Anthropic/OpenAI/DeepSeek. Architetturalmente, **tutto oltre al runtime minimo è un pacchetto** (§0 principio 8, §5): System, Filesystem, Network, Docker, Service e ogni provider LLM, first-party o di terze parti — il caricamento dinamico (V0.10) arriva dopo, ma il *contratto* a pacchetto è presente fin da V0.1.
2. Scaffolding della solution secondo §2 — posso generarti gli `.csproj` e gli scheletri di classe vuoti pronti da compilare (solo i progetti di Fase 1: `bOps.Cli`, `bOps.Runtime`, `bOps.Abstractions`, e i pacchetti `bOps.Packages.System`/`Filesystem`/`Providers.OpenRouter`/`Providers.Ollama`/`Providers.LlamaCpp` — niente `bOps.Api`/`web/bops-ui` finché non arrivi in Fase 2, niente `PluginLoader` dinamico finché non arrivi a V0.10).
3. Implementa **V0.1** seguendo §3, §3.1 e §4 e la sezione System di §11: l'obiettivo concreto immediato è "chiedo `bops "come sta questo server?"` e ricevo CPU/RAM/disco/processi reali, con l'LLM che sceglie autonomamente quali tool chiamare" — verificalo con almeno due dei tre provider di Fase 1 (es. OpenRouter e Ollama) per essere sicuro che l'astrazione `IChatModel`/`IModelProviderPackage` regga davvero.
4. Scrivi ADR-0001, ADR-0002, ADR-0005 e ADR-0006 (§12) prima di scrivere troppo codice di `Policy`/pacchetti — sono le decisioni che, se sbagliate, costano di più da correggere dopo.
5. Conferma se "ds4" nei tuoi appunti originali indica **DeepSeek** (assunzione fatta in §3.1) o un altro provider — non blocca la Fase 1, ma vale la pena chiarirlo prima di arrivare a V0.8.

Se vuoi, nel prossimo passaggio ti genero lo scaffolding completo della solution V0.1 (tutti i `.csproj`, le interfacce di `bOps.Abstractions`, i pacchetti-provider OpenRouter/Ollama/llama.cpp basati su `OpenAiCompatibleChatModel`, e il pacchetto `bOps.Packages.System` con i 5 tool `READ`), pronto da aprire e compilare.
