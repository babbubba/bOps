// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using bOps.Abstractions;

namespace bOps.AgentFramework.Tests.PromptAssembly;

/// <summary>
/// PA-1, level B: the properties that are already true of every request the product assembles and must stay true, whatever a later
/// prompt change does to the wording. They say nothing about what a prompt should say.
/// </summary>
internal static partial class PaInvariants
{
    private static readonly string[] Roles = ["system", "user", "assistant", "tool"];
    private static readonly string[] BodyProperties = ["model", "messages", "tools", "tool_choice", "max_tokens"];
    private static readonly ContextOrigin[] ToolTurnOrigins = [ContextOrigin.ToolResult, ContextOrigin.Knowledge, ContextOrigin.Runtime];
    private static readonly ContextOrigin[] UserTurnOrigins = [ContextOrigin.Task, ContextOrigin.Runtime, ContextOrigin.Knowledge, ContextOrigin.ToolResult];

    /// <summary>The wire name the adapter publishes for a canonical tool name.</summary>
    internal static string Wire(string canonical) => canonical.Replace('.', '_');

    /// <summary>
    /// The checks that hold for every provider request of every scenario run. <paramref name="expectedStatus"/> is the status the run
    /// must end in (PA-1 scenarios all complete; AE-1 also runs that end in a bounded failure, and the request checks hold for them too).
    /// </summary>
    internal static void AssertEveryRequestIsAValidGovernedBody(PaRun run, AgentTaskStatus expectedStatus = AgentTaskStatus.Completed)
    {
        Assert.Equal(expectedStatus, run.State.Status);
        Assert.Equal(0, run.Unconsumed);
        Assert.NotEmpty(run.Bodies);
        Assert.Equal(run.Bodies.Count, run.Seen.Count); // one HTTP body per request the adapter received: no hidden retry, no extra call

        for (var index = 0; index < run.Bodies.Count; index++)
        {
            var wire = run.Wire(index);
            var request = run.Seen[index];

            // The body is exactly the properties the adapter emits, with the model it was configured with.
            Assert.All(wire.Root.Select(pair => pair.Key), key => Assert.Contains(key, BodyProperties));
            Assert.Equal("pa1-fixture-model", wire.Model);
            Assert.Null(wire.MaxTokens); // the product default requests no output limit (no Agent:MaxOutputTokens in appsettings.json)

            // tools and tool_choice are present together or not at all.
            if (wire.ToolNames.Count == 0)
            {
                Assert.Null(wire.Root["tools"]);
                Assert.Null(wire.ToolChoice);
            }
            else
            {
                Assert.Equal("auto", wire.ToolChoice);
                Assert.Equal(wire.ToolNames.Count, wire.ToolNames.Distinct(StringComparer.Ordinal).Count());
                Assert.All(wire.ToolNames, name => Assert.Matches("^[A-Za-z0-9_-]{1,64}$", name));
                Assert.Equal(request.AvailableTools.Select(tool => Wire(tool.Name)), wire.ToolNames); // same tools, same order, one alias each
            }

            // The conversation starts with the system prompt, carries only the four wire roles, and every tool message answers a call.
            Assert.Equal("system", wire.Role(0));
            Assert.Equal(request.SystemPrompt, wire.System);
            Assert.All(wire.Roles, role => Assert.Contains(role, Roles));
            Assert.Equal(1 + request.History.Count, wire.Messages.Count);
            var calls = new HashSet<string>(StringComparer.Ordinal);
            foreach (var message in wire.Messages)
            {
                foreach (var call in message["tool_calls"]?.AsArray() ?? [])
                {
                    Assert.True(calls.Add(call!["id"]!.GetValue<string>()), "a tool call id is used twice in one request");
                    Assert.NotNull(JsonNode.Parse(call["function"]!["arguments"]!.GetValue<string>())); // arguments travel as a JSON string
                }

                if (message["role"]!.GetValue<string>() == "tool")
                {
                    Assert.Contains(message["tool_call_id"]!.GetValue<string>(), calls);
                }
            }

            AssertProvenance(request);
        }
    }

    /// <summary>
    /// Every turn the adapter receives has an established origin and the origin matches its role: the operator's goal is Task, tool
    /// output is ToolResult, knowledge is Knowledge, the model's own output is Model, and a runtime instruction is Runtime.
    /// </summary>
    internal static void AssertProvenance(ModelRequest request)
    {
        foreach (var turn in request.History)
        {
            Assert.NotNull(turn.Provenance);
            var origin = turn.Provenance.Origin;
            switch (turn.Role)
            {
                case ChatRole.Assistant:
                    Assert.Equal(ContextOrigin.Model, origin);
                    break;
                case ChatRole.Tool:
                    Assert.Contains(origin, ToolTurnOrigins);
                    break;
                case ChatRole.User:
                    Assert.Contains(origin, UserTurnOrigins);
                    break;
                default:
                    Assert.Fail($"unexpected role {turn.Role} in the history");
                    break;
            }
        }
    }

    /// <summary>The operational tools of a request, by canonical name (runtime control functions excluded).</summary>
    internal static IReadOnlyList<string> Operational(ModelRequest request) =>
        [.. request.AvailableTools.Select(tool => tool.Name).Where(name => !name.StartsWith("runtime.", StringComparison.Ordinal))];

    /// <summary>The runtime control functions of a request, by canonical name.</summary>
    internal static IReadOnlyList<string> Control(ModelRequest request) =>
        [.. request.AvailableTools.Select(tool => tool.Name).Where(name => name.StartsWith("runtime.", StringComparison.Ordinal))];

    /// <summary>
    /// <paramref name="later"/> is <paramref name="earlier"/> plus exactly the given trailing messages: same system prompt, same tools, same
    /// messages in the same order, then the new ones. This is what a retry or a continuation is allowed to change.
    /// </summary>
    internal static void AssertExtendsBy(PaRun run, int earlier, int later, params string[] appendedRoles)
    {
        var a = run.Wire(earlier);
        var b = run.Wire(later);
        Assert.Equal(a.System, b.System);
        Assert.True(JsonNode.DeepEquals(a.Root["tools"], b.Root["tools"]), "the offered tools differ");
        Assert.Equal(a.Messages.Count + appendedRoles.Length, b.Messages.Count);
        for (var index = 0; index < a.Messages.Count; index++)
        {
            Assert.True(JsonNode.DeepEquals(a.Messages[index], b.Messages[index]), $"message {index} differs");
        }

        Assert.Equal(appendedRoles, b.Roles.Skip(a.Messages.Count));
    }

    /// <summary>The text between two markers, inclusive.</summary>
    internal static string Block(string text, string open, string close)
    {
        var start = text.IndexOf(open, StringComparison.Ordinal);
        var end = text.IndexOf(close, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"the block {open} … {close} is missing");
        return text[start..(end + close.Length)];
    }

    [GeneratedRegex(@"^\d+\. run (?<tool>\S+) \[(?<expected>\S+)\]$", RegexOptions.Multiline)]
    internal static partial Regex PlanLine();

    /// <summary>The canonical tools the plan section of a system prompt lists, in order.</summary>
    internal static IReadOnlyList<string> PlanTools(string systemPrompt) =>
        [.. PlanLine().Matches(systemPrompt).Select(match => match.Groups["expected"].Value)];
}
