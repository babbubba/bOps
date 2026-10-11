// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bOps.Abstractions;
using bOps.Runtime;
using bOps.Runtime.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using RuntimeChatRole = bOps.Abstractions.ChatRole;

namespace bOps.AgentFramework.Tests.Regression;

/// <summary>What one scripted-model call was.</summary>
internal enum CallKind { Plan, Step, EvidenceRead, FinalAnswer, GroundingCheck, Summary }

/// <summary>One request the scripted model served, with its synthetic prompt-size proxy.</summary>
internal sealed record ModelCallLog(
    CallKind Kind, string ModelId, int SyntheticPromptTokens, int SyntheticCompletionTokens, int HistoryTurns, int ToolGroupsVisible);

/// <summary>A fake <see cref="ITool"/> returning one fixed, already-persisted result.</summary>
internal sealed class FixtureTool(ScenarioTool tool) : ITool
{
    public int ExecutionCount { get; private set; }

    public ToolManifest Manifest { get; } = new()
    {
        Name = tool.Name,
        Description = "A deterministic regression fixture tool.",
        Risk = RiskLevel.Read,
        Platforms = [CurrentPlatform.Id],
        Requires = [],
        Parameters = [],
    };

    public Task<ToolCallResult> ExecuteAsync(ToolArguments arguments, CancellationToken ct = default)
    {
        ExecutionCount++;
        return Task.FromResult(tool.Result);
    }
}

/// <summary>
/// A scripted, deterministic stand-in for a diagnostic model. It is DETERMINISTIC FIXTURE evidence only:
/// token counts are a synthetic proxy (UTF-8 bytes / 3.5 of everything offered), never a provider's tokenizer.
/// It decides each reply from what the request actually contains, so the evidence a context configuration keeps (or loses) is
/// what the final answer can cite.
/// </summary>
internal sealed class DiagnosticModel(
    RegressionScenario scenario, string provider = "Fixture", string model = "fixture-model", int summaryCharacters = 600) : IChatModel
{
    private const string ReadFunction = "runtime.evidence_read";
    private const string SummaryPrefix = "Earlier tool results were reviewed.";

    private readonly Dictionary<string, int> readsIssued = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> nextOffset = new(StringComparer.Ordinal);
    private int callSequence;

    public ChatModelDescriptor Descriptor { get; } = new(provider, model);

    public List<ModelCallLog> Log { get; } = [];

    /// <summary>Every request served, in order, for tool-surface and content assertions.</summary>
    public List<ModelRequest> Requests { get; } = [];

    /// <summary>Reads the scripted model wanted but could not address (the context named no step index for the tool).</summary>
    public int UnaddressableReads { get; private set; }

    /// <summary>Reads the scripted model wanted after the control function stopped being offered.</summary>
    public int ReadsDeniedByExhaustion { get; private set; }

    /// <summary>Whether each expected fact was visible to the final call, by label.</summary>
    public ModelRequest? FinalRequest { get; private set; }

    public Dictionary<string, bool> FinalFactVisibility { get; } = [];

    /// <summary>Whether each expected limitation was visible to the final call, by label.</summary>
    public Dictionary<string, bool> FinalLimitationVisibility { get; } = [];

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        callSequence++;
        Requests.Add(request);
        var kind = Classify(request, out var response);
        response ??= kind switch
        {
            _ => throw new InvalidOperationException("unreachable"),
        };
        Log.Add(new ModelCallLog(
            kind, model, SyntheticTokens(request), SyntheticCompletion(response), request.History.Count, ToolGroups(request.History)));
        return Task.FromResult(response with
        {
            Usage = new ModelUsage(Log[^1].SyntheticPromptTokens, Log[^1].SyntheticCompletionTokens, null),
        });
    }

    private CallKind Classify(ModelRequest request, out ModelResponse? response)
    {
        if (string.Equals(request.SystemPrompt, MafSummarization.Prompt, StringComparison.Ordinal))
        {
            response = new ModelResponse(SummaryPrefix + " " + new string('s', Math.Max(0, summaryCharacters - SummaryPrefix.Length - 1)), [], false, null);
            return CallKind.Summary;
        }

        if (request.History.Count > 0
            && request.History[^1] is { Role: RuntimeChatRole.User } last
            && string.Equals(last.Content, EvidenceGroundingLedger.CheckInstruction, StringComparison.Ordinal))
        {
            response = new ModelResponse("{\"contradictions\":[]}", [], true, null);
            return CallKind.GroundingCheck;
        }

        if (request.SystemPrompt.Contains("Respond with ONLY a single JSON object", StringComparison.Ordinal))
        {
            response = PlanResponse();
            return CallKind.Plan;
        }

        var operational = request.AvailableTools.Where(tool => tool.Name != ReadFunction).ToList();
        if (operational.Count == 0)
        {
            response = FinalAnswer(request);
            return CallKind.FinalAnswer;
        }

        var current = operational[0].Name;
        var offered = request.AvailableTools.Any(tool => tool.Name == ReadFunction);
        var wanted = scenario.ReadsBefore.TryGetValue(current, out var list) ? list : [];
        while (readsIssued.GetValueOrDefault(current) < wanted.Count)
        {
            var target = wanted[readsIssued.GetValueOrDefault(current)];
            if (!offered && !scenario.ModelIgnoresExhaustion)
            {
                ReadsDeniedByExhaustion += wanted.Count - readsIssued.GetValueOrDefault(current);
                readsIssued[current] = wanted.Count;
                break;
            }

            readsIssued[current] = readsIssued.GetValueOrDefault(current) + 1;
            if (FindStepIndex(request, target) is not { } stepIndex)
            {
                UnaddressableReads++;
                continue;
            }

            var offset = nextOffset.GetValueOrDefault(target);
            nextOffset[target] = offset + 4000;
            response = new ModelResponse(null,
            [
                new ModelToolCall($"read-{callSequence}", ReadFunction, ToolArguments.FromJson(new JsonObject
                {
                    ["step"] = stepIndex, ["source"] = "result", ["offset"] = offset, ["length"] = 4000,
                })),
            ], false, null);
            return CallKind.EvidenceRead;
        }

        response = new ModelResponse(null, [new ModelToolCall($"call-{callSequence}", current, ToolArguments.Empty)], false, null);
        return CallKind.Step;
    }

    private ModelResponse PlanResponse()
    {
        var steps = new JsonArray(scenario.Tools.Select((tool, index) => (JsonNode)new JsonObject
        {
            ["description"] = $"step {index}",
            ["expectedTool"] = tool.Name,
        }).ToArray());
        return new ModelResponse(
            new JsonObject { ["rationale"] = "Collect the diagnostic evidence.", ["steps"] = steps }.ToJsonString(), [], false, null);
    }

    /// <summary>The step index the context itself names for <paramref name="tool"/>: the limitations digest or a history record.</summary>
    private static int? FindStepIndex(ModelRequest request, string tool)
    {
        var escaped = Regex.Escape(tool);
        var digest = Regex.Match(request.SystemPrompt, $@"step (\d+): {escaped} ", RegexOptions.CultureInvariant);
        if (digest.Success)
        {
            return int.Parse(digest.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        foreach (var turn in request.History)
        {
            if (turn.Content is not { } text)
            {
                continue;
            }

            var record = Regex.Match(text, $@"s=(\d+);e=ev1:[0-9a-f]+:\d+;(?:r=\d+;)?t={escaped};", RegexOptions.CultureInvariant);
            if (record.Success)
            {
                return int.Parse(record.Groups[1].Value, CultureInfo.InvariantCulture);
            }

            var index = Regex.Match(text, $@"step={{?(\d+)}}? tool={escaped}\b", RegexOptions.CultureInvariant);
            if (index.Success)
            {
                return int.Parse(index.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private ModelResponse FinalAnswer(ModelRequest request)
    {
        FinalRequest = request;
        var everything = Everything(request);
        var text = new StringBuilder("Diagnostic findings (scripted fixture model):\n");
        foreach (var fact in scenario.ExpectedFacts)
        {
            var visible = fact.Markers.Any(marker => everything.Contains(marker, StringComparison.Ordinal));
            FinalFactVisibility[fact.Label] = visible;
            if (visible)
            {
                text.Append("- ").Append(fact.Label).Append('\n');
            }
        }

        var limited = false;
        foreach (var limitation in scenario.ExpectedLimitations)
        {
            var visible = limitation.Markers.Any(marker => everything.Contains(marker, StringComparison.Ordinal));
            FinalLimitationVisibility[limitation.Label] = visible;
            limited |= visible;
        }

        if (limited)
        {
            text.Append("\nEvidence limitations\n");
            foreach (var limitation in scenario.ExpectedLimitations.Where(l => FinalLimitationVisibility[l.Label]))
            {
                text.Append("- ").Append(limitation.Label).Append('\n');
            }
        }

        return new ModelResponse(text.ToString(), [], true, null);
    }

    private static string Everything(ModelRequest request)
    {
        var builder = new StringBuilder(request.SystemPrompt);
        foreach (var turn in request.History)
        {
            builder.Append('\n').Append(turn.Content);
            foreach (var call in turn.ToolCalls ?? [])
            {
                builder.Append('\n').Append(call.ToolName);
            }
        }

        return builder.ToString();
    }

    /// <summary>A synthetic prompt-size proxy: UTF-8 bytes of everything offered / 3.5. Not a provider tokenizer.</summary>
    internal static int SyntheticTokens(ModelRequest request)
    {
        long bytes = Encoding.UTF8.GetByteCount(request.SystemPrompt);
        foreach (var turn in request.History)
        {
            bytes += Encoding.UTF8.GetByteCount(turn.Content ?? string.Empty);
            foreach (var call in turn.ToolCalls ?? [])
            {
                bytes += Encoding.UTF8.GetByteCount(call.ToolName) + Encoding.UTF8.GetByteCount(call.Arguments.ToJson().ToJsonString());
            }
        }

        bytes += Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request.AvailableTools));
        return (int)(bytes * 2 / 7);
    }

    private static int SyntheticCompletion(ModelResponse response) =>
        (Encoding.UTF8.GetByteCount(response.TextResponse ?? string.Empty)
         + response.ToolCalls.Sum(call => Encoding.UTF8.GetByteCount(call.ToolName) + 24)) * 2 / 7;

    private static int ToolGroups(IReadOnlyList<ChatTurn> history) =>
        history.Count(turn => turn.Role == RuntimeChatRole.Assistant && turn.ToolCalls is { Count: > 0 });
}

/// <summary>Decorates a projector so a run keeps every projection's non-content stats (no global meters, no cross-test noise).</summary>
internal sealed class RecordingProjector(IModelContextProjector inner) : IModelContextProjector
{
    public List<(string Provider, string Model, ModelContextProjectionStats Stats, TimeSpan Elapsed)> Projections { get; } = [];

    public async Task<ModelContextProjectionResult> ProjectAsync(
        ModelContextRequest request, ChatModelDescriptor descriptor, ModelSummarizationAccess? summarization = null, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var result = await inner.ProjectAsync(request, descriptor, summarization, ct);
        Projections.Add((descriptor.ProviderId, descriptor.ModelId, result.Stats, watch.Elapsed));
        return result;
    }
}

/// <summary>One named configuration of a regression run.</summary>
internal sealed record RegressionConfiguration(
    string Name,
    AgentRunnerOptions Options,
    bool UseProjector = true,
    bool DedicatedSummarizer = false,
    int SummaryCharacters = 1200);

/// <summary>Everything one regression run produced.</summary>
internal sealed class RegressionRun
{
    public required RegressionScenario Scenario { get; init; }
    public required RegressionConfiguration Configuration { get; init; }
    public required TaskState State { get; init; }
    public required DiagnosticModel Model { get; init; }
    public DiagnosticModel? SummaryModel { get; init; }
    public required RecordingAuditSink Audit { get; init; }
    public required RecordingProjector? Projector { get; init; }
    public required IReadOnlyList<FixtureTool> Tools { get; init; }
    public required TimeSpan WallClock { get; init; }

    public IEnumerable<ModelCallLog> AllCalls => Model.Log.Concat(SummaryModel?.Log ?? []);
    public IEnumerable<ModelCallLog> PrimaryCalls => Model.Log.Where(call => call.Kind != CallKind.Summary);
    public IEnumerable<ModelCallLog> SummaryCalls => Model.Log.Where(call => call.Kind == CallKind.Summary).Concat(SummaryModel?.Log ?? []);

    public int PrimaryPromptTokens => PrimaryCalls.Sum(call => call.SyntheticPromptTokens);
    public int PrimaryCompletionTokens => PrimaryCalls.Sum(call => call.SyntheticCompletionTokens);
    public int SummaryPromptTokens => SummaryCalls.Sum(call => call.SyntheticPromptTokens);
    public int SummaryCompletionTokens => SummaryCalls.Sum(call => call.SyntheticCompletionTokens);
    public int AllInTokens => PrimaryPromptTokens + PrimaryCompletionTokens + SummaryPromptTokens + SummaryCompletionTokens;

    public int EvidenceReadServed => Audit.Events.OfType<EvidenceReadAuditEvent>().Count(e => e.ResultCode == EvidenceReadResultCode.Success);
    public int EvidenceReadExhausted => Audit.Events.OfType<EvidenceReadAuditEvent>().Count(e => e.ResultCode == EvidenceReadResultCode.LimitExceeded);
    public int EvidenceReadRejected => Audit.Events.OfType<EvidenceReadAuditEvent>().Count(e =>
        e.ResultCode is not (EvidenceReadResultCode.Success or EvidenceReadResultCode.LimitExceeded));
    public int OperationalSteps => State.Steps.Count(step => step.ToolCall is not null && !FinalResponse.IsFinalStep(step));
    public int Replans => Math.Max(0, State.Plans.Count - 1);
}

/// <summary>Builds and runs a scenario on the real runner, broker and (optionally) MAF projector.</summary>
internal static class RegressionRunner
{
    private static readonly ActorIdentity Actor = ActorIdentity.FromOperatingSystemUser("regression");

    internal static AgentRunnerOptions BaseOptions(
        int? maxContextTokens, int reservedOutput = 1024, SummarizationOptions? summarization = null, ModelContextProfile[]? extraProfiles = null,
        Func<ModelContextOptions, ModelContextOptions>? context = null)
    {
        var profiles = new List<ModelContextProfile>(extraProfiles ?? []);
        if (maxContextTokens is { } max)
        {
            profiles.Add(new ModelContextProfile
            {
                Provider = "Fixture", Model = "fixture-model", MaxContextTokens = max, ReservedOutputTokens = reservedOutput,
            });
        }

        var contextOptions = new ModelContextOptions { Profiles = profiles };
        return new AgentRunnerOptions
        {
            ModelCallMaxAttempts = 1,
            MaxSteps = 40,
            MaxLifetimeSteps = 60,
            MaxTotalTokens = null,
            Summarization = summarization ?? new SummarizationOptions(),
            Context = context is null ? contextOptions : context(contextOptions),
        };
    }

    internal static async Task<RegressionRun> RunAsync(RegressionScenario scenario, RegressionConfiguration configuration)
    {
        var model = new DiagnosticModel(scenario, summaryCharacters: configuration.SummaryCharacters);
        var audit = new RecordingAuditSink();
        var tools = scenario.Tools.Select(tool => new FixtureTool(tool)).ToList();
        var registry = new ToolRegistry(new AlwaysAvailableCapabilityProbe());
        foreach (var tool in tools)
        {
            registry.Register(new PackageId("regression.package"), tool);
        }

        DiagnosticModel? summaryModel = null;
        ModelInvocationBroker? dedicated = null;
        if (configuration.DedicatedSummarizer)
        {
            summaryModel = new DiagnosticModel(scenario, "Sum", "small", configuration.SummaryCharacters);
            dedicated = new ModelInvocationBroker(summaryModel, audit, TimeProvider.System, NullLogger.Instance, configuration.Options);
        }

        RecordingProjector? projector = configuration.UseProjector
            ? new RecordingProjector(new MafContextProjector(configuration.Options, NullLogger.Instance))
            : null;
        var broker = new ModelInvocationBroker(
            model, audit, TimeProvider.System, NullLogger.Instance, configuration.Options,
            contextProjector: projector, dedicatedSummarizer: dedicated);
        var runner = new AgentRunner(
            model, registry, new DefaultTestPolicyEngine(), new NeverCalledApprovalProvider(), audit, new InMemoryTaskStore(),
            TimeProvider.System, NullLogger<AgentRunner>.Instance, configuration.Options, invocationBroker: broker);

        var watch = Stopwatch.StartNew();
        var state = await runner.RunAsync(scenario.Goal, Actor);
        watch.Stop();
        return new RegressionRun
        {
            Scenario = scenario,
            Configuration = configuration,
            State = state,
            Model = model,
            SummaryModel = summaryModel,
            Audit = audit,
            Projector = projector,
            Tools = tools,
            WallClock = watch.Elapsed,
        };
    }
}
