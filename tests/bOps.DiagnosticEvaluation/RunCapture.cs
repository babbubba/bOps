// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace bOps.DiagnosticEvaluation;

/// <summary>One typed fact of a persisted tool result: <c>F&lt;step&gt;.&lt;position&gt;</c>, its type, key and numeric value when it has one.</summary>
internal sealed record RunFact(int StepIndex, int Position, string Type, string Key, double? Number)
{
    public string Id => $"F{StepIndex}.{Position}";
}

/// <summary>A planned run of the campaign manifest, keyed by its blind code; the variant is kept only for the caller's own key.</summary>
internal sealed record PlannedRun(string BlindCode, string Scenario, string Variant, string RunDirectory);

/// <summary>
/// What the scorers need from one recorded run, read from <c>capture/tasks/*/state_json.utf8</c> and <c>audit-full.jsonl</c>. The
/// evidence text stays in memory: only counts, fact keys and indexes leave this type.
/// </summary>
internal sealed partial class RunCapture
{
    [GeneratedRegex(@"(?<![\w.])\d{1,12}(?![\w])")]
    private static partial Regex OutputInteger();

    [GeneratedRegex(@"""eventId""\s*:\s*(\d+)")]
    private static partial Regex OutputEventId();

    [GeneratedRegex(@"eventId=(\d+)")]
    private static partial Regex KeyEventId();

    // Task status names, in the order of the persisted AgentTaskStatus enum.
    private static readonly string[] StatusNames =
        ["Running", "Completed", "MaxStepsReached", "BudgetExceeded", "PolicyBlocked", "ReplanLimitReached", "Failed", "Cancelled"];

    private const string FinalMarkerPrefix = "Final response";

    public required PlannedRun Planned { get; init; }

    public bool Executed { get; init; }

    public bool HasState { get; init; }

    public string Goal { get; init; } = string.Empty;

    public string Status { get; init; } = "NoTaskState";

    public int? TerminalKind { get; init; }

    /// <summary>The persisted final answer (the observation of the final step), or empty when the run never wrote one.</summary>
    public string FinalAnswer { get; init; } = string.Empty;

    /// <summary>The final step's marker (<c>Final response…</c>), which carries the disclosure and grounding outcome.</summary>
    public string? FinalMarker { get; init; }

    public int? FinalStepIndex { get; init; }

    public IReadOnlyList<RunFact> Facts { get; init; } = [];

    /// <summary>Every integer that appears in a persisted tool output: the evidence a number of the answer may come from when no typed
    /// fact carries it (a typed fact holds only the aggregate a tool chose to type).</summary>
    public IReadOnlySet<long> OutputNumbers { get; init; } = new HashSet<long>();

    /// <summary>Event ids found in tool outputs (<c>"eventId": N</c>) and in fact keys (<c>eventId=N</c>).</summary>
    public IReadOnlySet<long> EventIds { get; init; } = new HashSet<long>();

    public int PartialSources { get; init; }

    public int PlanRevisions { get; init; }

    public int PlannedSteps { get; init; }

    public int? MaxSteps { get; init; }

    public int StepsPersisted { get; init; }

    public int UnexecutedToolCalls { get; init; }

    public int ModelCalls { get; init; }

    public int ModelCallFailures { get; init; }

    public IReadOnlyList<JsonObject> AuditEvents { get; init; } = [];

    /// <summary>The request of the first model call of the final step (the answer turn), parsed; null when there is none.</summary>
    public JsonObject? AnswerTurnRequest { get; init; }

    /// <summary>Every model-call request of the run, parsed, in persisted order (plan calls first).</summary>
    public IReadOnlyList<JsonObject> Requests { get; init; } = [];

    public string? StateSha256 { get; init; }

    public string? AuditSha256 { get; init; }

    public static IReadOnlyList<PlannedRun> ReadManifest(string campaignDirectory)
    {
        var manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(campaignDirectory, "campaign.json")))!.AsObject();
        var runs = new List<PlannedRun>();
        foreach (var entry in manifest["plannedRuns"]?.AsArray() ?? [])
        {
            if (entry is not JsonObject run)
            {
                continue;
            }

            runs.Add(new PlannedRun(
                (string?)run["blindCode"] ?? throw new InvalidDataException("A planned run has no blindCode"),
                (string?)run["scenario"] ?? string.Empty,
                (string?)run["variant"] ?? string.Empty,
                Path.Combine(campaignDirectory, "runs", (string?)run["runDir"] ?? string.Empty)));
        }

        return runs;
    }

    public static RunCapture Read(PlannedRun planned)
    {
        if (!File.Exists(Path.Combine(planned.RunDirectory, "run.json")))
        {
            return new RunCapture { Planned = planned };
        }

        var auditPath = Path.Combine(planned.RunDirectory, "audit-full.jsonl");
        var (audit, auditSha) = ReadAudit(auditPath);
        var tasks = Path.Combine(planned.RunDirectory, "capture", "tasks");
        var statePath = Directory.Exists(tasks)
            ? Directory.EnumerateFiles(tasks, "state_json.utf8", SearchOption.AllDirectories).Order(StringComparer.Ordinal).FirstOrDefault()
            : null;
        if (statePath is null)
        {
            return new RunCapture { Planned = planned, Executed = true, AuditEvents = audit, AuditSha256 = auditSha };
        }

        var stateBytes = File.ReadAllBytes(statePath);
        var state = JsonNode.Parse(stateBytes)!.AsObject();
        var steps = state["Steps"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        var plans = state["Plans"]?.AsArray().OfType<JsonObject>().ToList() ?? [];

        var facts = new List<RunFact>();
        var outputNumbers = new HashSet<long>();
        var eventIds = new HashSet<long>();
        var partial = 0;
        var unexecuted = 0;
        foreach (var step in steps)
        {
            var index = (int?)step["Index"] ?? 0;
            unexecuted += step["UnexecutedToolCalls"]?.AsArray().Count ?? 0;
            if (step["Result"] is not JsonObject result)
            {
                continue;
            }

            if (ReadInt(result["Completeness"]) is 2 or 3)
            {
                partial++;
            }

            if ((string?)result["Output"] is { } output)
            {
                foreach (Match match in OutputInteger().Matches(output))
                {
                    if (long.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    {
                        outputNumbers.Add(n);
                    }
                }

                foreach (Match match in OutputEventId().Matches(output))
                {
                    eventIds.Add(long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
                }
            }

            var position = 0;
            foreach (var fact in result["Facts"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                position++;
                var factKey = (string?)fact["Key"] ?? string.Empty;
                facts.Add(new RunFact(index, position, (string?)fact["Type"] ?? string.Empty, factKey, ReadNumber(fact["Value"])));
                foreach (Match match in KeyEventId().Matches(factKey))
                {
                    eventIds.Add(long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
                }
            }
        }

        var finalStep = steps.LastOrDefault(s => s["ToolCall"] is null
            && ((string?)s["Description"])?.StartsWith(FinalMarkerPrefix, StringComparison.Ordinal) == true);
        var requests = new List<JsonObject>();
        foreach (var call in ModelCallsOf(plans, steps))
        {
            if (TryParseObject((string?)call["RequestJson"]) is { } request)
            {
                requests.Add(request);
            }
        }

        var answerCall = finalStep?["ModelCalls"]?.AsArray().OfType<JsonObject>().FirstOrDefault();
        var allCalls = ModelCallsOf(plans, steps).ToList();
        return new RunCapture
        {
            Planned = planned,
            Executed = true,
            HasState = true,
            Goal = (string?)state["Goal"] ?? string.Empty,
            Status = StatusName(state["Status"]),
            TerminalKind = ReadInt(state["TerminalReason"]?["Kind"]),
            FinalAnswer = (string?)finalStep?["Observation"] ?? string.Empty,
            FinalMarker = (string?)finalStep?["Description"],
            FinalStepIndex = (int?)finalStep?["Index"],
            Facts = facts,
            OutputNumbers = outputNumbers,
            EventIds = eventIds,
            PartialSources = partial,
            PlanRevisions = plans.Count,
            PlannedSteps = plans.LastOrDefault()?["Steps"]?.AsArray().Count ?? 0,
            MaxSteps = audit.Where(e => (string?)e["eventType"] == "taskLifecycle").Select(e => ReadInt(e["MaxSteps"])).FirstOrDefault(),
            StepsPersisted = steps.Count,
            UnexecutedToolCalls = unexecuted,
            ModelCalls = allCalls.Count,
            ModelCallFailures = allCalls.Count(c => !string.IsNullOrEmpty((string?)c["ErrorMessage"])),
            AuditEvents = audit,
            AnswerTurnRequest = TryParseObject((string?)answerCall?["RequestJson"]),
            Requests = requests,
            StateSha256 = Convert.ToHexStringLower(SHA256.HashData(stateBytes)),
            AuditSha256 = auditSha,
        };
    }

    private static IEnumerable<JsonObject> ModelCallsOf(List<JsonObject> plans, List<JsonObject> steps)
    {
        foreach (var plan in plans)
        {
            foreach (var call in plan["ModelCalls"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                yield return call;
            }
        }

        foreach (var step in steps)
        {
            foreach (var call in step["ModelCalls"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                yield return call;
            }

            foreach (var call in step["EarlyFinalReplanCalls"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                yield return call;
            }
        }
    }

    private static (List<JsonObject> Events, string? Sha) ReadAudit(string path)
    {
        var events = new List<JsonObject>();
        if (!File.Exists(path))
        {
            return (events, null);
        }

        var bytes = File.ReadAllBytes(path);
        foreach (var line in System.Text.Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(line)?["EventJson"]?.GetValue<string>() is { } eventJson
                    && JsonNode.Parse(eventJson) is JsonObject e)
                {
                    events.Add(e);
                }
            }
            catch (JsonException)
            {
                // An unparseable line is the audit-chain metric's business (maf_cli_analysis.py); it carries nothing to score.
            }
        }

        return (events, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static JsonObject? TryParseObject(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StatusName(JsonNode? node) => node switch
    {
        null => "NoTaskState",
        _ when ReadInt(node) is { } i && i >= 0 && i < StatusNames.Length => StatusNames[i],
        _ when node.GetValueKind() == JsonValueKind.String => node.GetValue<string>(),
        _ => "Unknown",
    };

    internal static int? ReadInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var i) ? i
        : node is JsonValue v && v.TryGetValue<long>(out var l) ? (int)l
        : null;

    private static double? ReadNumber(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<double>() : null;
}
