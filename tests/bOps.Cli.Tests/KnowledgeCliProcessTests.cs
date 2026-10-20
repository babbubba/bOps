// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every world and server passes to the test, which disposes it.
#pragma warning disable CA2000

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace bOps.Cli.Tests;

/// <summary>
/// ADR-0058 against the real <c>bops</c> executable, in a process of its own: its real configuration providers (environment variables
/// here, as a service or a script would set them), real composition root, real provider adapter and real files. The only thing that is not
/// real is the model: a scripted OpenAI-compatible endpoint on this machine's loopback interface. Nothing live and no network.
/// </summary>
public sealed class KnowledgeCliProcessTests
{
    private const string Goal = CliKnowledgeWorld.Goal;

    // ---- Misconfiguration stops the command; it never degrades to a run without the knowledge ----

    [Fact]
    public async Task EnabledWithNothingElse_StopsBeforeAnyModelCall_WithExitCodeTwoAndTheKeyNamed()
    {
        using var world = new CliKnowledgeWorld();
        using var server = new ScriptedProviderServer([]);

        var (code, output, error) = await RunAsync(world, server, new Dictionary<string, string?> { ["Knowledge:Enabled"] = "true" }, Goal);

        Assert.Equal(2, code);
        Assert.Contains("Knowledge is enabled but its configuration is not usable", error, StringComparison.Ordinal);
        Assert.Contains("PacksDirectory", error, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
        Assert.DoesNotContain("Completed", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMisspeltKey_IsRefusedEvenWhileDisabled_SoItCanNeverReadAsOff()
    {
        using var world = new CliKnowledgeWorld();
        using var server = new ScriptedProviderServer([]);
        var settings = world.Settings();
        settings["Knowledge:Enabeld"] = "true";

        var (code, _, error) = await RunAsync(world, server, settings, Goal);

        Assert.Equal(2, code);
        Assert.Contains("not usable", error, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task ADelegationNeverOpensKnowledge_SoItIsNotStoppedByAnUnusableKnowledgeSection()
    {
        using var world = new CliKnowledgeWorld();
        using var server = new ScriptedProviderServer([]);

        var (code, output, error) = await RunAsync(world, server, new Dictionary<string, string?> { ["Knowledge:Enabled"] = "true" }, "delegate", "Why did nginx stop?");

        // The same outcome as without any Knowledge section (CliProcessTests): denied on the Profile dimension, before any model call.
        Assert.Equal(2, code);
        Assert.Contains("Refused on Profile", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Knowledge is enabled", error, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }

    // ---- The use case end to end ----

    [Fact]
    public async Task TheRealCli_WithAnAuthorisedSignedPack_PublishesTheIndexToTheWire_ServesTheReadTheModelAsks_AndLeavesAnAuditChainThatVerifies()
    {
        using var world = new CliKnowledgeWorld();
        using var server = new ScriptedProviderServer(
        [
            _ => ScriptedProviderServer.Text("{\"rationale\":\"The pack procedure is enough to frame the answer.\",\"steps\":[]}"),
            request => ScriptedProviderServer.ToolCall(
                "call-1", ScriptedProviderServer.WireNameContaining(request, "knowledge_read")!, new JsonObject { ["operation"] = "read_resource", ["ref"] = "K1.R1" }),
            _ => ScriptedProviderServer.Text("Observed facts are separated from possible causes, following the procedure."),
        ]);

        var (code, output, error) = await RunAsync(world, server, world.Settings(), Goal);

        Assert.True(code == 0, $"exit {code}\n{output}\n{error}");
        Assert.Contains("Completed", output, StringComparison.Ordinal);
        Assert.Contains("possible causes", output, StringComparison.Ordinal);
        Assert.Equal(3, server.Requests.Count);

        // Planning carries nothing of the pack.
        Assert.DoesNotContain("knowledge-index", server.Requests[0].ToJsonString(), StringComparison.Ordinal);
        Assert.Null(server.Requests[0]["tools"]);

        // The step request: the control block at the tail of the system message, the index as the first user message, then the goal.
        var messages = server.Requests[1]["messages"]!.AsArray();
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Contains("[knowledge-index-control v1]", messages[0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
        Assert.StartsWith("[knowledge-index v1", messages[1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(CliKnowledgeWorld.Description, messages[1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("user", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal(Goal, messages[2]!["content"]!.GetValue<string>());
        Assert.NotNull(ScriptedProviderServer.WireNameContaining(server.Requests[1], "knowledge_read"));

        // The model's read was served from the pinned snapshot, as a tool message, in the next request.
        var served = server.Requests[2]["messages"]!.AsArray().Where(message => message!["role"]!.GetValue<string>() == "tool").ToArray();
        var read = Assert.Single(served);
        Assert.Contains(CliKnowledgeWorld.ProcedureMarker, read!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain(CliKnowledgeWorld.ProcedureMarker, server.Requests[2]["messages"]![0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);

        // The audit file: the admission, the index, the read, and a hash chain that the product's own verifier accepts.
        var auditFile = Path.Combine(RunDirectory(world), "audit.jsonl");
        var audit = await File.ReadAllTextAsync(auditFile);
        Assert.Contains("KnowledgePackAdmission", audit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("KnowledgeIndex", audit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("KnowledgeRead", audit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CliKnowledgeWorld.ProcedureMarker, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(CliKnowledgeWorld.Description, audit, StringComparison.Ordinal);
        var (verifyCode, verifyOutput, _) = await RunCoreAsync(RunDirectory(world), null, [], "audit", "verify", auditFile);
        Assert.True(verifyCode == 0, verifyOutput);
    }

    // ---- The serialized payload of a Knowledge-enabled step, as a llama.cpp server with a native-tools chat template receives it ----

    [Theory]
    [InlineData(null)]
    [InlineData("8192")]
    public async Task TheStepPayload_WithKnowledge_HasTheRolesOrderSchemaAndLimitAStrictNativeToolsServerNeeds(string? maxOutputTokens)
    {
        using var world = new CliKnowledgeWorld();
        using var server = new ScriptedProviderServer(
        [
            _ => ScriptedProviderServer.Text("{\"rationale\":\"Look at the CPU.\",\"steps\":[{\"description\":\"cpu\",\"expectedTool\":\"system.cpu\"}]}"),
            request => ScriptedProviderServer.ToolCall(
                "call-1", ScriptedProviderServer.WireNameContaining(request, "knowledge_read")!, new JsonObject { ["operation"] = "read_resource", ["ref"] = "K1.R1" }),
            _ => ScriptedProviderServer.Text("Nothing more to add."),
        ]);
        var settings = world.Settings();
        if (maxOutputTokens is not null)
        {
            settings["Agent:MaxOutputTokens"] = maxOutputTokens;
        }

        // The task is allowed to fail once the script ends (an unscripted request is a 500): only the first three payloads are examined.
        await RunAsync(world, server, settings, Goal);

        Assert.True(server.Requests.Count >= 3, $"only {server.Requests.Count} requests reached the server");
        var planning = server.Requests[0];
        var step = server.Requests[1];
        var afterRead = server.Requests[2];

        // Planning: no tools, no tool_choice, nothing of the pack.
        Assert.Null(planning["tools"]);
        Assert.Null(planning["tool_choice"]);
        Assert.DoesNotContain("knowledge-index", planning.ToJsonString(), StringComparison.Ordinal);

        // Request members: exactly the OpenAI chat-completions set the adapter owns, and the output limit only when configured.
        var expectedKeys = new List<string> { "model", "messages", "tools", "tool_choice" };
        if (maxOutputTokens is not null)
        {
            expectedKeys.Add("max_tokens");
        }

        Assert.Equal(expectedKeys.Order(StringComparer.Ordinal), ((JsonObject)step).Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("auto", step["tool_choice"]!.GetValue<string>());
        foreach (var request in server.Requests.Take(3))
        {
            if (maxOutputTokens is null)
            {
                Assert.False(((JsonObject)request).ContainsKey("max_tokens")); // nothing is invented: the server default applies
            }
            else
            {
                Assert.Equal(8192, request["max_tokens"]!.GetValue<int>());
            }

            AssertStrictShape(request);
        }

        // Roles and order: one system message first, the index and then the goal as consecutive user messages, nothing else.
        Assert.Equal(["system", "user", "user"], Roles(step));
        var messages = step["messages"]!.AsArray();
        var system = messages[0]!["content"]!.GetValue<string>();
        Assert.Contains("[knowledge-index-control v1]", system, StringComparison.Ordinal);
        Assert.Contains("never required", system, StringComparison.Ordinal);
        Assert.Contains("send the read by itself", system, StringComparison.Ordinal);
        Assert.StartsWith("[knowledge-index v1", messages[1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(Goal, messages[2]!["content"]!.GetValue<string>());

        // The tool schema: the operational tool and the control function, in that order, with the closed argument set of the manifest.
        var tools = step["tools"]!.AsArray();
        Assert.Equal(["system_cpu", "runtime_knowledge_read"], tools.Select(tool => tool!["function"]!["name"]!.GetValue<string>()));
        var read = tools[1]!["function"]!;
        Assert.Equal("function", tools[1]!["type"]!.GetValue<string>());
        var schema = read["parameters"]!;
        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(["operation"], schema["required"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(
            ["operation", "page", "query", "ref", "resource", "skill"],
            ((JsonObject)schema["properties"]!).Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["discover", "load", "read_resource"], schema["properties"]!["operation"]!["enum"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal("integer", schema["properties"]!["page"]!["type"]!.GetValue<string>());

        // After the read: system, index, goal, the assistant's tool call, and the one tool result that answers it.
        Assert.Equal(["system", "user", "user", "assistant", "tool"], Roles(afterRead));
        var call = afterRead["messages"]![3]!["tool_calls"]!.AsArray().Single()!;
        Assert.Equal("runtime_knowledge_read", call["function"]!["name"]!.GetValue<string>());
        Assert.Equal(JsonValueKind.String, call["function"]!["arguments"]!.GetValueKind()); // a JSON string, as the OpenAI schema says
        Assert.Equal(call["id"]!.GetValue<string>(), afterRead["messages"]![4]!["tool_call_id"]!.GetValue<string>());
        Assert.Contains(CliKnowledgeWorld.ProcedureMarker, afterRead["messages"]![4]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private static string[] Roles(JsonNode request) =>
        [.. request["messages"]!.AsArray().Select(message => message!["role"]!.GetValue<string>())];

    /// <summary>What a strict upstream and a native-tools template reject: explicit nulls, non-string contents, bad function names.</summary>
    private static void AssertStrictShape(JsonNode request)
    {
        Assert.Null(FindNull(request, "$"));
        foreach (var message in request["messages"]!.AsArray())
        {
            Assert.True(message!["content"] is null || message["content"]!.GetValueKind() == JsonValueKind.String, "content must be a string when present");
        }

        foreach (var tool in request["tools"]?.AsArray() ?? [])
        {
            Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", tool!["function"]!["name"]!.GetValue<string>());
        }
    }

    private static string? FindNull(JsonNode? node, string path)
    {
        switch (node)
        {
            case null:
                return path;
            case JsonObject obj:
                return obj.Select(pair => FindNull(pair.Value, $"{path}.{pair.Key}")).FirstOrDefault(found => found is not null);
            case JsonArray array:
                return array.Select((item, index) => FindNull(item, $"{path}[{index}]")).FirstOrDefault(found => found is not null);
            default:
                return null;
        }
    }

    [Fact]
    public async Task TheRealCli_WithAPackThatIsInstalledButNotGranted_RefusesTheTaskAndNeverContactsTheModel()
    {
        using var world = new CliKnowledgeWorld();
        using var server = new ScriptedProviderServer([]);
        var settings = world.Settings();
        settings["Knowledge:Grants:0:Version"] = "9.9.9";

        var (code, output, _) = await RunAsync(world, server, settings, Goal);

        Assert.Equal(1, code);
        Assert.Contains("Knowledge unavailable: NotEntitled", output, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }

    // ---- harness ----

    private static string RunDirectory(CliKnowledgeWorld world)
    {
        var path = Path.Combine(world.Root, "run");
        Directory.CreateDirectory(path);
        return path;
    }

    private static Task<(int ExitCode, string Output, string Error)> RunAsync(
        CliKnowledgeWorld world, ScriptedProviderServer server, Dictionary<string, string?> knowledge, params string[] args) =>
        RunCoreAsync(RunDirectory(world), server, knowledge, args);

    private static async Task<(int ExitCode, string Output, string Error)> RunCoreAsync(
        string workingDirectory, ScriptedProviderServer? server, Dictionary<string, string?> knowledge, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "bops.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var inherited in start.Environment.Keys.Where(key =>
            key.StartsWith("ModelProvider__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Knowledge__", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(inherited);
        }

        if (server is not null)
        {
            start.Environment["ModelProvider__Provider"] = "LlamaCpp";
            start.Environment["ModelProvider__BaseUrl"] = server.BaseUrl;
            start.Environment["ModelProvider__Model"] = "scripted";
            start.Environment["ModelProvider__SupportsNativeToolCalling"] = "true";
            start.Environment["ModelProvider__ApiKeySecret__Provider"] = "environment";
            start.Environment["ModelProvider__ApiKeySecret__Name"] = "BOPS_TEST_KEY";
            start.Environment["BOPS_TEST_KEY"] = "not-a-real-key";
        }

        foreach (var (key, value) in knowledge)
        {
            start.Environment[key.Replace(":", "__", StringComparison.Ordinal)] = value;
        }

        start.Environment["OTEL_SDK_DISABLED"] = "true";

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }
}
