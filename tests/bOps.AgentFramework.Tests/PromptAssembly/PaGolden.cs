// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bOps.Abstractions;

namespace bOps.AgentFramework.Tests.PromptAssembly;

/// <summary>
/// PA-1: the golden store of the assembled requests. A golden is a pair of files per scenario under <c>Golden/pa1/</c>: a JSON
/// document (compared structurally and canonically: object property order is irrelevant, array order is not) and a Markdown
/// rendering of that same document for prompt review (compared byte for byte, so it can never go stale). Both are snapshots of
/// the CURRENT behaviour at <see cref="Baseline"/>, known defects included; they are changed only by a decision, never to make a
/// test pass. Set <see cref="CaptureVariable"/> to a directory to write the files instead of comparing them.
/// </summary>
internal static partial class PaGolden
{
    internal const string Baseline = "7053ca0";
    internal const string CaptureVariable = "BOPS_PA1_CAPTURE_DIR";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static JsonObject Parse(string text) => JsonNode.Parse(text)!.AsObject();

    /// <summary>
    /// The only normalization: a source checkout with CRLF line endings turns the line breaks of the prompt constants into <c>\r\n</c>,
    /// which the JSON body shows as an escape. Everything else (ids, hashes, order of properties as sent) is compared as produced;
    /// two runs in two processes were byte-identical, so no other mask is needed (<c>docs/benchmarks/pa-1-request-assembly-baseline.md</c>).
    /// </summary>
    internal static string Normalize(string text) => text.Replace("\\r\\n", "\\n", StringComparison.Ordinal);

    /// <summary>Builds the golden document of the picked requests of a run.</summary>
    internal static JsonObject Document(string id, string title, PaRun run, IReadOnlyList<(int Index, string Label)> picks)
    {
        var steps = new JsonArray([.. run.State.Steps.Select(step => (JsonNode)new JsonObject
        {
            ["index"] = step.Index,
            ["description"] = step.Description,
            ["tool"] = step.ToolCall?.ToolName,
            ["outcome"] = step.Result?.Outcome.ToString(),
        })]);
        return new JsonObject
        {
            ["scenario"] = id,
            ["title"] = title,
            ["baseline"] = Baseline,
            ["run"] = new JsonObject
            {
                ["status"] = run.State.Status.ToString(),
                ["requests"] = run.Bodies.Count,
                ["steps"] = steps,
            },
            ["requests"] = new JsonArray([.. picks.Select(pick => (JsonNode)new JsonObject
            {
                ["runRequest"] = pick.Index,
                ["label"] = pick.Label,
                ["body"] = run.Body(pick.Index),
            })]),
        };
    }

    internal static string Serialize(JsonObject document) =>
        document.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";

    /// <summary>The golden directory of the test output.</summary>
    internal static string Directory => Path.Combine(AppContext.BaseDirectory, "Golden", "pa1");

    /// <summary>Compares (or, in capture mode, writes) the golden pair of a scenario.</summary>
    internal static void Verify(string file, JsonObject actual)
    {
        var json = Serialize(actual);
        var markdown = Render(actual);
        if (Environment.GetEnvironmentVariable(CaptureVariable) is { Length: > 0 } capture)
        {
            System.IO.Directory.CreateDirectory(capture);
            File.WriteAllText(Path.Combine(capture, file + ".json"), json, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(capture, file + ".md"), markdown, new UTF8Encoding(false));
            return;
        }

        var jsonPath = Path.Combine(Directory, file + ".json");
        var markdownPath = Path.Combine(Directory, file + ".md");
        Assert.True(File.Exists(jsonPath), $"The golden file {jsonPath} is missing (Golden/pa1/README.md says how it is captured).");
        Assert.True(File.Exists(markdownPath), $"The golden file {markdownPath} is missing (Golden/pa1/README.md says how it is captured).");

        var expected = Parse(File.ReadAllText(jsonPath, new UTF8Encoding(false)));
        if (!JsonNode.DeepEquals(expected, actual))
        {
            Assert.Fail($"{file}: the assembled requests differ from the PA-1 baseline at {FirstDifference(expected, actual, "$")}.");
        }

        var expectedMarkdown = File.ReadAllText(markdownPath, new UTF8Encoding(false)).ReplaceLineEndings("\n");
        Assert.True(
            string.Equals(expectedMarkdown, markdown, StringComparison.Ordinal),
            $"{file}: the Markdown rendering differs from the checked-in review file at character {FirstDifference(expectedMarkdown, markdown)}.");
    }

    /// <summary>The JSON path of the first structural difference, with the two values shortened.</summary>
    internal static string FirstDifference(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected, actual)
        {
            case (JsonObject left, JsonObject right):
                foreach (var key in left.Select(pair => pair.Key).Union(right.Select(pair => pair.Key)))
                {
                    var l = left[key];
                    var r = right[key];
                    if (!JsonNode.DeepEquals(l, r))
                    {
                        return FirstDifference(l, r, $"{path}.{key}");
                    }
                }

                return path;
            case (JsonArray left, JsonArray right):
                for (var index = 0; index < Math.Max(left.Count, right.Count); index++)
                {
                    var l = index < left.Count ? left[index] : null;
                    var r = index < right.Count ? right[index] : null;
                    if (!JsonNode.DeepEquals(l, r))
                    {
                        return FirstDifference(l, r, $"{path}[{index.ToString(CultureInfo.InvariantCulture)}]");
                    }
                }

                return path;
            case (JsonValue left, JsonValue right) when left.TryGetValue<string>(out var l) && right.TryGetValue<string>(out var r):
                var at = FirstDifference(l, r);
                return $"{path} (character {at.ToString(CultureInfo.InvariantCulture)}: expected \"{Excerpt(l, at)}\", actual \"{Excerpt(r, at)}\")";
            default:
                return $"{path} (expected {Excerpt(expected?.ToJsonString() ?? "<absent>", 0)}, actual {Excerpt(actual?.ToJsonString() ?? "<absent>", 0)})";
        }
    }

    private static string Excerpt(string text, int at) =>
        text[Math.Min(at, text.Length)..][..Math.Min(80, text.Length - Math.Min(at, text.Length))].ReplaceLineEndings("\\n");

    private static int FirstDifference(string expected, string actual)
    {
        var length = Math.Min(expected.Length, actual.Length);
        for (var index = 0; index < length; index++)
        {
            if (expected[index] != actual[index])
            {
                return index;
            }
        }

        return length;
    }

    // ---- the review rendering ----

    /// <summary>A Markdown rendering of a golden document, for prompt review; a pure function of the document.</summary>
    internal static string Render(JsonObject document)
    {
        var text = new StringBuilder();
        var run = document["run"]!.AsObject();
        text.Append(CultureInfo.InvariantCulture, $"# {document["scenario"]} — {document["title"]}\n\n");
        text.Append(CultureInfo.InvariantCulture, $"> PA-1 request-assembly baseline at `{document["baseline"]}`. This is a snapshot of the CURRENT assembled provider request, defects included;\n");
        text.Append("> it is not a statement of what the prompt should say. Generated from the JSON file next to it; never edited by hand.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"Run: status `{run["status"]}`, {run["requests"]} provider request(s). Steps persisted:\n\n");
        foreach (var step in run["steps"]!.AsArray())
        {
            var tool = step!["tool"]?.GetValue<string>();
            text.Append(CultureInfo.InvariantCulture, $"- {step["index"]}: {step["description"]}{(tool is null ? string.Empty : $" — `{tool}` → {step["outcome"]}")}\n");
        }

        foreach (var request in document["requests"]!.AsArray())
        {
            var body = request!["body"]!.AsObject();
            text.Append(CultureInfo.InvariantCulture, $"\n---\n\n## Provider request #{request["runRequest"]} — {request["label"]}\n\n");
            var known = new[] { "model", "messages", "tools", "tool_choice", "max_tokens" };
            text.Append(CultureInfo.InvariantCulture,
                $"`model` = `{body["model"]}` · `tool_choice` = `{body["tool_choice"]?.ToJsonString() ?? "(absent)"}` · `max_tokens` = `{body["max_tokens"]?.ToJsonString() ?? "(absent)"}`");
            var extra = body.Select(pair => pair.Key).Except(known).ToList();
            text.Append(extra.Count == 0 ? string.Empty : $" · other properties: {string.Join(", ", extra)}");
            text.Append("\n\n");

            var tools = body["tools"]?.AsArray();
            text.Append(CultureInfo.InvariantCulture, $"### tools ({tools?.Count ?? 0})\n\n");
            if (tools is null || tools.Count == 0)
            {
                text.Append("(none offered; the request has no `tools` property)\n\n");
            }
            else
            {
                foreach (var tool in tools)
                {
                    var function = tool!["function"]!.AsObject();
                    text.Append(CultureInfo.InvariantCulture, $"- `{function["name"]}` — {function["description"]}\n");
                    text.Append(CultureInfo.InvariantCulture, $"  parameters: `{function["parameters"]!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })}`\n");
                }

                text.Append('\n');
            }

            var messages = body["messages"]!.AsArray();
            text.Append(CultureInfo.InvariantCulture, $"### messages ({messages.Count})\n");
            for (var index = 0; index < messages.Count; index++)
            {
                var message = messages[index]!.AsObject();
                text.Append(CultureInfo.InvariantCulture, $"\n#### [{index}] {message["role"]}");
                if (message["tool_call_id"]?.GetValue<string>() is { } answers)
                {
                    text.Append(CultureInfo.InvariantCulture, $" (answers `{answers}`)");
                }

                text.Append('\n');
                if (message["tool_calls"] is JsonArray calls)
                {
                    foreach (var call in calls)
                    {
                        var function = call!["function"]!;
                        text.Append(CultureInfo.InvariantCulture, $"\ntool call `{call["id"]}` → `{function["name"]}({function["arguments"]})`\n");
                    }
                }

                if (message["content"]?.GetValue<string>() is { } content)
                {
                    text.Append('\n').Append(Fence(content)).Append('\n');
                }
            }
        }

        return text.ToString().ReplaceLineEndings("\n");
    }

    private static string Fence(string content)
    {
        var longest = Backticks().Matches(content).Select(match => match.Length).DefaultIfEmpty(0).Max();
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}text\n{content}\n{fence}";
    }

    [GeneratedRegex("`+")]
    private static partial Regex Backticks();
}
