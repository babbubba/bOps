// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;

namespace bOps.DiagnosticEvaluation.Tests;

/// <summary>
/// Writes a minimal campaign directory in the layout BOPS-0125 captures (campaign.json, runs/&lt;dir&gt;/run.json, capture/tasks/&lt;id&gt;/
/// state_json.utf8, audit-full.jsonl). Every value is synthetic; the B02 facts mirror the shape (not the content) of a real capture.
/// </summary>
internal sealed class SyntheticCampaign : IDisposable
{
    private readonly JsonArray plannedRuns = [];

    public SyntheticCampaign()
    {
        Directory = Path.Combine(Path.GetTempPath(), "bops-diag-eval-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path.Combine(Directory, "runs"));
    }

    public string Directory { get; }

    public static string KeysDirectory
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var keys = Path.Combine(dir.FullName, "tests", "bOps.DiagnosticEvaluation", "keys");
                if (System.IO.Directory.Exists(keys))
                {
                    return keys;
                }
            }

            throw new DirectoryNotFoundException("tests/bOps.DiagnosticEvaluation/keys not found above the test output");
        }
    }

    /// <summary>The typed facts of a B02 run: 17 real bugchecks, 27 live dumps (26 with 0x193), 60 unexpected shutdowns (Kernel-Power 41
    /// and EventLog 6008 30 each), 385 hardware errors, a 180-day window.</summary>
    public static JsonArray B02Facts() =>
    [
        Fact("system.stability.category-count", "unexpectedShutdown", 60),
        Fact("system.stability.category-count", "kernelCrash", 10),
        Fact("system.stability.category-count", "hardwareError", 385),
        Fact("system.stability.category-count", "displayFault", 26),
        Fact("system.stability.window-days", "window", 180),
        Fact("system.crashes.kind-count", "kernel-live-dump", 27),
        Fact("system.crashes.kind-count", "kernel-bugcheck", 17),
        Fact("system.crashes.kernel-code-count", "kind=kernel-live-dump;code=0x193", 26),
        Fact("system.crashes.kernel-code-count", "kind=kernel-bugcheck;code=0x1e", 9),
        Fact("system.events.matched-count", "source=Microsoft-Windows-Kernel-Power;eventId=41", 30),
        Fact("system.events.matched-count", "source=EventLog;eventId=6008", 30),
    ];

    public static JsonObject Fact(string type, string key, long value) =>
        new() { ["Type"] = type, ["Key"] = key, ["ValueType"] = 1, ["Value"] = value };

    /// <summary>Adds a run. <paramref name="answer"/> null means the run never wrote a final answer.</summary>
    public void AddRun(
        string blindCode,
        string scenario = "B02",
        string? answer = "Nessuna anomalia.",
        int status = 1,
        JsonArray? facts = null,
        int plannedSteps = 3,
        int maxSteps = 15,
        JsonArray? audit = null,
        JsonArray? answerTurnMessages = null,
        string finalMarker = "Final response; evidence grounding check cited no contradiction",
        int completeness = 0,
        string output = "")
    {
        var runDir = $"{plannedRuns.Count + 1:00}-{scenario}-{blindCode}";
        plannedRuns.Add(new JsonObject
        {
            ["blindCode"] = blindCode, ["scenario"] = scenario, ["variant"] = "X", ["round"] = 1, ["runDir"] = runDir,
        });
        var root = Path.Combine(Directory, "runs", runDir);
        var taskId = Guid.NewGuid().ToString("D");
        var taskDir = Path.Combine(root, "capture", "tasks", taskId);
        System.IO.Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(root, "run.json"), "{\"exitCode\":0}");

        var steps = new JsonArray
        {
            new JsonObject
            {
                ["Index"] = 0,
                ["Description"] = "collect",
                ["ToolCall"] = new JsonObject { ["ToolName"] = "system.crashes", ["Arguments"] = new JsonObject() },
                ["Result"] = new JsonObject
                {
                    ["Outcome"] = 0, ["Output"] = output, ["Completeness"] = completeness, ["Facts"] = facts ?? B02Facts(),
                },
                ["ModelCalls"] = new JsonArray(),
            },
        };
        if (answer is not null)
        {
            steps.Add(new JsonObject
            {
                ["Index"] = 1,
                ["Description"] = finalMarker,
                ["Observation"] = answer,
                ["ModelCalls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["RequestJson"] = new JsonObject { ["messages"] = answerTurnMessages ?? [new JsonObject { ["role"] = "user", ["content"] = "goal" }] }
                            .ToJsonString(),
                        ["ResponseJson"] = "{}",
                    },
                },
            });
        }

        var planSteps = new JsonArray();
        for (var i = 0; i < plannedSteps; i++)
        {
            planSteps.Add(new JsonObject { ["ExpectedTool"] = "system.info" });
        }

        var state = new JsonObject
        {
            ["Id"] = taskId,
            ["Goal"] = "Diagnostica questo PC.",
            ["Status"] = status,
            ["TerminalReason"] = new JsonObject { ["Kind"] = status == 1 ? 0 : 1 },
            ["Plans"] = new JsonArray { new JsonObject { ["Steps"] = planSteps, ["ModelCalls"] = new JsonArray() } },
            ["Steps"] = steps,
        };
        File.WriteAllText(Path.Combine(taskDir, "state_json.utf8"), state.ToJsonString());

        var events = new List<JsonObject> { new() { ["eventType"] = "taskLifecycle", ["MaxSteps"] = maxSteps } };
        events.AddRange((audit ?? []).OfType<JsonObject>().Select(e => (JsonObject)e.DeepClone()));
        var lines = events.Select((e, i) => new JsonObject { ["Seq"] = i + 1, ["EventJson"] = e.ToJsonString() }.ToJsonString());
        File.WriteAllText(Path.Combine(root, "audit-full.jsonl"), string.Join("\n", lines) + "\n");
    }

    public static JsonObject KnowledgeIndex(params (string Ref, string Skill)[] listed) => new()
    {
        ["eventType"] = "knowledgeIndex",
        ["PackId"] = "bsoft.windows-event-runbooks",
        ["PackVersion"] = "2.0.0",
        ["Listed"] = new JsonArray(listed.Select(l => (JsonNode)new JsonObject { ["Ref"] = l.Ref, ["Skill"] = l.Skill }).ToArray()),
    };

    public static JsonObject KnowledgeRead(string skill, int stepIndex, int resultCode = 0) => new()
    {
        ["eventType"] = "knowledgeRead", ["Operation"] = "load", ["Skill"] = skill, ["ResultCode"] = resultCode, ["StepIndex"] = stepIndex,
    };

    public void WriteManifest() =>
        File.WriteAllText(Path.Combine(Directory, "campaign.json"), new JsonObject { ["plannedRuns"] = plannedRuns.DeepClone() }.ToJsonString());

    public async Task<JsonObject> EvaluateAsync(string blindCode)
    {
        WriteManifest();
        await CampaignEvaluator.RunAsync(Directory, KeysDirectory, CancellationToken.None);
        return JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Directory, CampaignEvaluator.OutputDirectoryName, blindCode + ".json")))!
            .AsObject();
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // best effort: a temp directory left behind is harmless
        }
    }
}
