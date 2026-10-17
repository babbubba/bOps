// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

// CA2000: ownership of every world and server passes to the test, which disposes it.
#pragma warning disable CA2000

using System.Diagnostics;
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
