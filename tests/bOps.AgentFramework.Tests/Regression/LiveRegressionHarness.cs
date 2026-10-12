// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bOps.Abstractions;
using bOps.Runtime;
using bOps.Runtime.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using RuntimeChatRole = bOps.Abstractions.ChatRole;

namespace bOps.AgentFramework.Tests.Regression;

/// <summary>What one model request of a live-regression fixture was for (the AF-05H token comparison buckets).</summary>
internal enum CallPurpose { Planning, Replanning, Step, EvidenceReadContinuation, Summarization, GroundingCheck, Disclosure }

/// <summary>One planned step of a scripted plan: the tool and its optional equality-only expected arguments.</summary>
internal sealed record ScriptedStep(string Tool, JsonObject? ExpectedArguments = null);

/// <summary>
/// The scripted behaviour of one AF-05H live-regression fixture (DETERMINISTIC FIXTURE, synthetic data: no task database, audit,
/// host name, user path or machine data of the live runs). It reproduces the observed model BEHAVIOUR of LIVE-1/2/3, not their text.
/// </summary>
internal sealed record LiveScript(
    string Name,
    string Goal,
    IReadOnlyList<ScenarioTool> Tools,
    IReadOnlyList<ScriptedStep> InitialPlan,
    IReadOnlyList<IReadOnlyList<ScriptedStep>> Replans,
    string FinalText)
{
    /// <summary>Reads the model asks for, in order, before calling the given tool (the AF-05G pressure of LIVE-2).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ReadsBefore { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The tool at whose FIRST offer the model answers instead of calling it (LIVE-2: the last step; LIVE-3: the second).</summary>
    public string? PrematureFinalAt { get; init; }

    /// <summary>The premature answer.</summary>
    public string? PrematureText { get; init; }

    /// <summary>What the model replies if it is asked to restate its answer (only the pre-AF-05H runtime asks).</summary>
    public string? DisclosureRestatement { get; init; }

    /// <summary>Arguments of the n-th call (1-based) of a tool; empty when not listed.</summary>
    public Func<string, int, JsonObject>? Arguments { get; init; }
}

/// <summary>A scripted model that answers each request from what the request is and what the script says the live model did.</summary>
internal sealed partial class LiveScriptedModel(LiveScript script) : IChatModel
{
    private const string ReadFunction = "runtime.evidence_read";
    private readonly Dictionary<string, int> calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> readsIssued = new(StringComparer.Ordinal);
    private readonly HashSet<string> offeredOnce = new(StringComparer.Ordinal);
    private int replans;
    private bool continuation;

    public ChatModelDescriptor Descriptor { get; } = new("Fixture", "fixture-model");

    public List<(CallPurpose Purpose, int Prompt, int Completion)> Log { get; } = [];

    public List<ModelRequest> Requests { get; } = [];

    public int ReadsDeniedByExhaustion { get; private set; }

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        var (purpose, response) = Answer(request);
        var prompt = DiagnosticModel.SyntheticTokens(request);
        var completion = (Encoding.UTF8.GetByteCount(response.TextResponse ?? string.Empty)
            + response.ToolCalls.Sum(call => Encoding.UTF8.GetByteCount(call.ToolName) + 24)) * 2 / 7;
        Log.Add((purpose, prompt, completion));
        return Task.FromResult(response with { Usage = new ModelUsage(prompt, completion, null) });
    }

    private (CallPurpose, ModelResponse) Answer(ModelRequest request)
    {
        if (string.Equals(request.SystemPrompt, MafSummarization.Prompt, StringComparison.Ordinal))
        {
            return (CallPurpose.Summarization, Text("Earlier tool results were reviewed. " + new string('s', 400), final: false));
        }

        var lastUser = request.History.LastOrDefault(turn => turn.Role == RuntimeChatRole.User)?.Content ?? string.Empty;
        if (request.History.Count > 0 && request.History[^1].Role == RuntimeChatRole.User
            && string.Equals(lastUser, EvidenceGroundingLedger.CheckInstruction, StringComparison.Ordinal))
        {
            return (CallPurpose.GroundingCheck, Text("{\"contradictions\":[]}"));
        }

        if (request.History.Count > 0 && request.History[^1].Role == RuntimeChatRole.User
            && lastUser.StartsWith("EvidenceDisclosure/v1", StringComparison.Ordinal))
        {
            return (CallPurpose.Disclosure, Text(script.DisclosureRestatement ?? script.FinalText));
        }

        if (request.SystemPrompt.Contains("lay out your plan", StringComparison.Ordinal))
        {
            return (CallPurpose.Planning, Plan(script.InitialPlan));
        }

        if (request.SystemPrompt.Contains("Revise it", StringComparison.Ordinal))
        {
            var revised = replans < script.Replans.Count ? script.Replans[replans] : [];
            replans++;
            return (CallPurpose.Replanning, Plan(revised));
        }

        var purpose = continuation ? CallPurpose.EvidenceReadContinuation : CallPurpose.Step;
        continuation = false;
        return (purpose, Step(request));
    }

    private ModelResponse Step(ModelRequest request)
    {
        var operational = request.AvailableTools.Where(tool => tool.Name != ReadFunction).ToList();
        if (operational.Count == 0)
        {
            return Text(script.FinalText);
        }

        var current = operational[0].Name;
        var offered = request.AvailableTools.Any(tool => tool.Name == ReadFunction);
        var wanted = script.ReadsBefore.TryGetValue(current, out var list) ? list : [];
        while (readsIssued.GetValueOrDefault(current) < wanted.Count)
        {
            var target = wanted[readsIssued.GetValueOrDefault(current)];
            readsIssued[current] = readsIssued.GetValueOrDefault(current) + 1;
            if (!offered)
            {
                ReadsDeniedByExhaustion++;
                continue;
            }

            var match = Regex.Match(request.SystemPrompt, $@"step (\d+) {Regex.Escape(target)}: id ev1:", RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                continue;
            }

            continuation = true;
            return new ModelResponse(null,
            [
                new ModelToolCall($"read-{Requests.Count}", ReadFunction, ToolArguments.FromJson(new JsonObject
                {
                    ["step"] = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    ["source"] = "result",
                    ["offset"] = 0,
                    ["length"] = 4000,
                })),
            ], false, null);
        }

        if (string.Equals(current, script.PrematureFinalAt, StringComparison.Ordinal) && offeredOnce.Add(current))
        {
            return Text(script.PrematureText ?? script.FinalText);
        }

        var n = calls[current] = calls.GetValueOrDefault(current) + 1;
        var arguments = script.Arguments?.Invoke(current, n) ?? [];
        return new ModelResponse(null, [new ModelToolCall($"call-{Requests.Count}", current, ToolArguments.FromJson(arguments))], false, null);
    }

    private static ModelResponse Text(string text, bool final = true) => new(text, [], final, null);

    private static ModelResponse Plan(IReadOnlyList<ScriptedStep> steps) => new(new JsonObject
    {
        ["rationale"] = "Collect the diagnostic evidence.",
        ["steps"] = new JsonArray([.. steps.Select((step, index) =>
        {
            var json = new JsonObject { ["description"] = $"step {index}: {step.Tool}", ["expectedTool"] = step.Tool };
            if (step.ExpectedArguments is { } expected)
            {
                json["expectedArguments"] = expected.DeepClone();
            }

            return (JsonNode)json;
        })]),
    }.ToJsonString(), [], false, null);
}

/// <summary>A fixture tool with declared parameters and an optional tool-level validation rule (like a package's own check).</summary>
internal sealed class ParameterizedFixtureTool(
    string name, ToolCallResult result, IReadOnlyList<ToolParameter> parameters, Func<JsonObject, string?>? reject = null) : ITool
{
    public int Invocations { get; private set; }

    public int Successes { get; private set; }

    public ToolManifest Manifest { get; } = new()
    {
        Name = name,
        Description = "A deterministic AF-05H regression fixture tool.",
        Risk = RiskLevel.Read,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = parameters,
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        Invocations++;
        if (reject?.Invoke(arguments.ToJson()) is { } problem)
        {
            return Task.FromResult(ToolCallResult.Failure(problem) with { FailureKind = ToolFailureKind.Validation });
        }

        Successes++;
        return Task.FromResult(result);
    }
}

/// <summary>Everything one live-regression fixture run produced.</summary>
internal sealed class LiveRegressionRun
{
    public required LiveScript Script { get; init; }
    public required string Configuration { get; init; }
    public required TaskState State { get; init; }
    public required LiveScriptedModel Model { get; init; }
    public required RecordingAuditSink Audit { get; init; }
    public required RecordingProjector Projector { get; init; }
    public required IReadOnlyDictionary<string, ParameterizedFixtureTool> Tools { get; init; }

    public int Tokens(CallPurpose purpose) => Model.Log.Where(call => call.Purpose == purpose).Sum(call => call.Prompt + call.Completion);

    public int Calls(CallPurpose purpose) => Model.Log.Count(call => call.Purpose == purpose);

    public int TotalTokens => Model.Log.Sum(call => call.Prompt + call.Completion);

    public int ReadsServed => Audit.Events.OfType<EvidenceReadAuditEvent>().Count(e => e.ResultCode == EvidenceReadResultCode.Success);

    public int PrematureFinals => State.Steps.Count(step =>
        step.ToolCall is null && step.Description == "Premature final response; plan re-evaluation required");

    public IReadOnlyList<string> SucceededTools => [.. Tools.Where(tool => tool.Value.Successes > 0).Select(tool => tool.Key).OrderBy(name => name, StringComparer.Ordinal)];

    public string FinalText => State.Steps.LastOrDefault(step => step.ToolCall is null)?.Observation ?? string.Empty;
}

/// <summary>Builds the three AF-05H fixtures (synthetic data shaped like the live tasks) and runs them on the real runner and MAF projector.</summary>
internal static class LiveRegression
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("regression");

    private static readonly string[] LongDiagnosticOrder =
        ["system.info", "system.stability", "system.crashes", "system.events", "system.cpu", "system.memory", "storage.health", "system.time"];

    private static ToolParameter[] CrashParameters() =>
    [
        new ToolParameter("mode", ToolParameterType.String, "Output mode.") { AllowedValues = ["raw", "aggregate"] },
        new ToolParameter("sinceDays", ToolParameterType.Integer, "Window in days.", Required: false),
        new ToolParameter("sinceMinutes", ToolParameterType.Integer, "Window in minutes.", Required: false),
    ];

    private static ScenarioTool Find(RegressionScenario scenario, string name) => scenario.Tools.Single(tool => tool.Name == name);

    /// <summary>LIVE-1 equivalent (task 34579793…): a short CPU/memory diagnostic — the negative control.</summary>
    internal static LiveScript ShortDiagnostic()
    {
        var c508 = RegressionScenarios.C508Equivalent();
        return new LiveScript(
            "LIVE-1 short cpu/memory",
            "Is this machine short on CPU or memory right now?",
            [Find(c508, "system.cpu"), Find(c508, "system.memory"), Find(c508, "system.info")],
            [new("system.cpu"), new("system.memory"), new("system.info")],
            [],
            "CPU usage is 4.7% on 16 cores; 28,464 of 32,475 MB of memory are in use. Nothing indicates a shortage right now.");
    }

    /// <summary>
    /// LIVE-2 equivalent (task 3f5b57ac…): the long stability diagnostic under AF-05G pressure. The model re-reads large partial evidence
    /// (more than the four reads one logical call may be served), runs CPU, memory and storage, and then answers while the last planned
    /// step (a clock check) is still unresolved. Its answer states 26 dumps of one code and 1 of another; a restatement would say 27.
    /// </summary>
    internal static LiveScript LongStabilityDiagnostic()
    {
        var c508 = RegressionScenarios.C508Equivalent();
        var tools = new List<ScenarioTool>(c508.Tools)
        {
            new("system.time", ToolCallResult.Success("{\"utc\":\"2026-10-10T12:00:00Z\",\"synchronized\":true}") with
            {
                Completeness = ToolResultCompleteness.Complete,
            }),
        };
        const string answer =
            "The stability history shows 26 × 0x193 and 1 × 0x1a1 kernel live dumps; unexpected shutdowns and corrected hardware "
            + "errors are recorded. CPU and memory are not saturated. A hardware or driver cause is a hypothesis, not a finding.";
        return new LiveScript(
            "LIVE-2 long stability",
            c508.Goal,
            tools,
            [.. LongDiagnosticOrder.Select(tool => new ScriptedStep(tool))],
            [[]],
            answer)
        {
            ReadsBefore = new Dictionary<string, IReadOnlyList<string>>
            {
                ["system.cpu"] = ["system.stability", "system.crashes", "system.events", "system.stability", "system.crashes"],
            },
            PrematureFinalAt = "system.time",
            PrematureText = answer,
            DisclosureRestatement = answer.Replace("26 × 0x193 and 1 × 0x1a1", "27 × 0x193", StringComparison.Ordinal)
                + "\n\n## Evidence limitations\n- Some event and crash results were partial.",
        };
    }

    /// <summary>
    /// LIVE-3 equivalent (task 4888fea6…): the plan constrains the crash query to a combination the tool rejects; the model's valid
    /// correction no longer matches the plan; after the first successful crash query the model answers although five diagnostics remain.
    /// </summary>
    internal static LiveScript FocusedCrashDiagnosis()
    {
        var c508 = RegressionScenarios.C508Equivalent();
        var raw7Days = new JsonObject { ["mode"] = "raw", ["sinceDays"] = 7 };
        var rawMinutes = new JsonObject { ["mode"] = "raw", ["sinceMinutes"] = 10_080 };
        string[] rest = ["system.stability", "system.events", "process.list", "system.memory", "system.cpu"];
        return new LiveScript(
            "LIVE-3 focused crash",
            "Why did this machine crash this week? Find the root cause.",
            [
                Find(c508, "system.crashes"), Find(c508, "system.stability"), Find(c508, "system.events"),
                new ScenarioTool("process.list", ToolCallResult.Success("{\"processes\":212,\"topCpu\":\"svc-a\",\"topMemoryMb\":2100}") with
                {
                    Completeness = ToolResultCompleteness.Complete,
                }),
                Find(c508, "system.memory"), Find(c508, "system.cpu"),
            ],
            [new("system.crashes", raw7Days), .. rest.Select(tool => new ScriptedStep(tool))],
            [
                [new("system.crashes", rawMinutes), .. rest.Select(tool => new ScriptedStep(tool))],
                [.. rest.Select(tool => new ScriptedStep(tool))],
            ],
            "Findings: the crash records, stability history, events, processes, memory and CPU were all collected; a driver "
            + "fault is the leading hypothesis, with the evidence for and against listed.")
        {
            PrematureFinalAt = "system.stability",
            PrematureText = "Crash records were found, but the root cause cannot be determined without further analysis.",
            Arguments = (tool, n) => tool == "system.crashes" ? (JsonObject)(n == 1 ? raw7Days : rawMinutes).DeepClone() : [],
        };
    }

    internal static async Task<LiveRegressionRun> RunAsync(LiveScript script, string configuration, AgentRunnerOptions options)
    {
        var model = new LiveScriptedModel(script);
        var audit = new RecordingAuditSink();
        var tools = script.Tools.ToDictionary(
            tool => tool.Name,
            tool => tool.Name == "system.crashes" && script.InitialPlan[0].ExpectedArguments is not null
                ? new ParameterizedFixtureTool(tool.Name, tool.Result, CrashParameters(), arguments =>
                    arguments["mode"]?.GetValue<string>() == "raw" && arguments.ContainsKey("sinceDays")
                        ? "mode 'raw' accepts only 'sinceMinutes'."
                        : null)
                : new ParameterizedFixtureTool(tool.Name, tool.Result, []),
            StringComparer.Ordinal);
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools.Values)
        {
            registry.Register(new PackageId("regression.package"), tool);
        }

        var projector = new RecordingProjector(new MafContextProjector(options, NullLogger.Instance));
        var broker = new ModelInvocationBroker(model, audit, TimeProvider.System, NullLogger.Instance, options, contextProjector: projector);
        var runner = new AgentRunner(
            model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), audit, new InMemoryTaskStore(),
            TimeProvider.System, NullLogger<AgentRunner>.Instance, options, invocationBroker: broker);
        var state = await runner.RunAsync(script.Goal, Actor);
        return new LiveRegressionRun
        {
            Script = script,
            Configuration = configuration,
            State = state,
            Model = model,
            Audit = audit,
            Projector = projector,
            Tools = tools,
        };
    }

    /// <summary>The per-purpose token table row (synthetic proxy, DETERMINISTIC FIXTURE).</summary>
    internal static string Row(string label, LiveRegressionRun run) => string.Create(CultureInfo.InvariantCulture,
        $"| {label} | {run.Script.Name} | {run.Configuration} | {run.State.Status} | {Math.Max(0, run.State.Plans.Count - 1)} | {run.PrematureFinals} | {run.SucceededTools.Count} | {run.ReadsServed} | "
        + $"{run.Tokens(CallPurpose.Planning)} | {run.Tokens(CallPurpose.Replanning)} | {run.Tokens(CallPurpose.Step)} | {run.Tokens(CallPurpose.EvidenceReadContinuation)} | "
        + $"{run.Tokens(CallPurpose.Summarization)} | {run.Tokens(CallPurpose.GroundingCheck)} | {run.Calls(CallPurpose.Disclosure)} / {run.Tokens(CallPurpose.Disclosure)} | **{run.TotalTokens}** | {Check(run)} |");

    /// <summary>The functional check the live run failed: the exact count (LIVE-2) or the executed diagnostic suffix (LIVE-3).</summary>
    internal static string Check(LiveRegressionRun run)
    {
        if (run.Script.Name.StartsWith("LIVE-2", StringComparison.Ordinal))
        {
            return run.FinalText.Contains("26 × 0x193", StringComparison.Ordinal) && !run.FinalText.Contains("27 × 0x193", StringComparison.Ordinal)
                ? "count kept (26 × 0x193)"
                : "FACT DRIFTED (27 × 0x193)";
        }

        if (run.Script.Name.StartsWith("LIVE-3", StringComparison.Ordinal))
        {
            return run.SucceededTools.Count == run.Tools.Count
                ? $"suffix executed ({run.SucceededTools.Count}/{run.Tools.Count})"
                : $"SUFFIX ABANDONED ({run.SucceededTools.Count}/{run.Tools.Count})";
        }

        return "n/a";
    }

    internal const string Header =
        "| Build | Fixture | Config | Status | Replans | Premature finals | Tools succeeded | ER served | Planning | Replanning | Step inference | ER continuation | Summarization | Grounding check | Disclosure calls / tokens | Total | Check |\n"
        + "|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|---|";
}
